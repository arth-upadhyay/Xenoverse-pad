using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PCReceiver;

internal static class Program
{
    private const int DefaultPort = 27016;
    private const int DefaultHttpEffectPort = 8080;
    private const int SequenceLogEvery = 20;

    // ── Shared controller & socket state ────────────────────────────────

    private static readonly Lock s_stateLock = new();
    private static readonly object _vigemLock = new();

    private enum Transport { None, Udp, Tcp }
    private static Transport s_activeTransport = Transport.None;

    private static volatile NetworkStream? _activeTcpStream;
    private static volatile TcpClient? _activeTcpClient;
    private static volatile IPEndPoint? _lastUdpEndpoint;
    private static Socket? _udpSocket;
    private static TcpListener? _tcpListener;
    private static TcpListener? _httpListener;

    // Keep a single ViGEmClient instance alive across Start/Stop cycles so native
    // vigem_alloc / vigem_free never races with SubmitReport or FeedbackReceived!
    private static ViGEmClient? _sharedVigemClient;
    private static IXbox360Controller? _controller;
    private static Action<string, Color>? _statusUpdater;

    // Send synchronization & rate-limiting state for SendRumble & SendEffect.
    private static readonly object _tcpSendLock = new();
    private static readonly object _udpSendLock = new();
    private static readonly object _rumbleLock = new();
    private static DateTime _lastRumbleSentUtc = DateTime.MinValue;
    private static DateTime _lastRumbleLogUtc = DateTime.MinValue;
    private static DateTime _lastNonZeroRumbleUtc = DateTime.MinValue;
    private static byte _lastLeft;
    private static byte _lastRight;
    private static bool _hasSentRumble;

    // ── Xenoverse 1 Real-Time Combat Haptic State ───────────────────────
    private static readonly object _combatLock = new();
    private static uint _prevButtons;
    private static bool _wasSuperCharging;
    private static bool _wasUltimateCharging;
    private static DateTime _superChargeStartUtc = DateTime.MinValue;
    private static DateTime _lastChargePulseUtc = DateTime.MinValue;
    private static DateTime _lastCombatEffectUtc = DateTime.MinValue;
    private static bool _combatHapticsEnabled = true;

    // Sequence tracking (shared across both transports).
    private static uint s_lastSequence;
    private static bool s_hasSequence;
    private static int s_acceptedPackets;
    private static bool s_degraded;
    private static DateTime s_lastHeartbeatUtc = DateTime.UtcNow;

    // ── WinForms Application Entry Point ────────────────────────────────

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }

    // ── Start / Stop Receiver Engine (Called by MainForm on Task.Run) ───

    public static async Task StartReceiverAsync(
        string[] args,
        Action<string, Color> updateStatus,
        CancellationToken token)
    {
        _statusUpdater = updateStatus;

        int port = DefaultPort;
        bool verbose = false;
        bool adbEnabled = true;
        bool rumbleTestEnabled = true;
        bool testEffectsEnabled = false;
        _combatHapticsEnabled = true;

        foreach (string arg in args)
        {
            if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }
            else if (arg.Equals("--no-adb", StringComparison.OrdinalIgnoreCase))
            {
                adbEnabled = false;
            }
            else if (arg.Equals("--rumble-test", StringComparison.OrdinalIgnoreCase))
            {
                rumbleTestEnabled = true;
            }
            else if (arg.Equals("--no-rumble-test", StringComparison.OrdinalIgnoreCase))
            {
                rumbleTestEnabled = false;
            }
            else if (arg.Equals("--test-effects", StringComparison.OrdinalIgnoreCase))
            {
                testEffectsEnabled = true;
            }
            else if (arg.Equals("--no-combat-haptics", StringComparison.OrdinalIgnoreCase))
            {
                _combatHapticsEnabled = false;
            }
            else if (int.TryParse(arg, out int parsedPort) && parsedPort is > 0 and <= 65535)
            {
                port = parsedPort;
            }
        }

        // Reset session state
        lock (s_stateLock)
        {
            s_activeTransport = Transport.None;
            s_lastSequence = 0;
            s_hasSequence = false;
            s_acceptedPackets = 0;
            s_degraded = false;
            s_lastHeartbeatUtc = DateTime.UtcNow;
        }
        lock (_combatLock)
        {
            _prevButtons = 0;
            _wasSuperCharging = false;
            _wasUltimateCharging = false;
        }
        Interlocked.Exchange(ref _activeTcpStream, null);
        Interlocked.Exchange(ref _activeTcpClient, null);
        Interlocked.Exchange(ref _lastUdpEndpoint, null);

        IXbox360Controller? sessionController = null;
        Socket? sessionUdpSocket = null;
        TcpListener? sessionTcpListener = null;

        // ── 1. ViGEm virtual controller (thread-safe via _vigemLock) ────
        try
        {
            lock (_vigemLock)
            {
                _sharedVigemClient ??= new ViGEmClient();

                // If an old controller remained connected, disconnect it safely first
                if (_controller is not null)
                {
                    try { _controller.Disconnect(); } catch { }
                    _controller = null;
                }

                sessionController = _sharedVigemClient.CreateXbox360Controller();
                sessionController.AutoSubmitReport = false;
                sessionController.Connect();
                _controller = sessionController;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fatal] could not connect the virtual Xbox 360 controller: {ex.Message}");
            updateStatus("Status: ViGEmBus Error (Install ViGEmBus Driver)", Color.FromArgb(255, 80, 80));
            return;
        }

        Console.WriteLine("Virtual Xbox 360 controller connected.");

        // ── 2. UDP listener (with ReuseAddress so Stop->Start rebinds immediately) ──
        try
        {
            sessionUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sessionUdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sessionUdpSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            sessionUdpSocket.ReceiveTimeout = 25;
            _udpSocket = sessionUdpSocket;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[fatal] could not bind UDP 0.0.0.0:{port}: {ex.Message}");
            updateStatus($"Status: Port {port} Busy (UDP Bind Failed)", Color.FromArgb(255, 80, 80));
            CleanupSessionResources(sessionController, sessionUdpSocket, null);
            return;
        }

        Console.WriteLine($"Listening on UDP 0.0.0.0:{port}  (Wi-Fi)");

        // ── 3. TCP listener (with ReuseAddress so Stop->Start rebinds immediately) ──
        try
        {
            sessionTcpListener = new TcpListener(IPAddress.Any, port);
            sessionTcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sessionTcpListener.Start();
            _tcpListener = sessionTcpListener;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[fatal] could not bind TCP 0.0.0.0:{port}: {ex.Message}");
            updateStatus($"Status: Port {port} Busy (TCP Bind Failed)", Color.FromArgb(255, 80, 80));
            CleanupSessionResources(sessionController, sessionUdpSocket, sessionTcpListener);
            return;
        }

        Console.WriteLine($"Listening on TCP 0.0.0.0:{port}  (USB / ADB)");
        Console.WriteLine($"Heartbeat interval {PacketProtocol.HeartbeatIntervalMs} ms, timeout {PacketProtocol.HeartbeatTimeoutMs} ms.");
        Console.WriteLine("Xenoverse 1 Live Combat Haptics: ENABLED (Supers/Ultimates/Heavy Hits/Transforms auto-trigger XVE1 1..4).");

        updateStatus($"Status: Listening on UDP/TCP (Port {port})", Color.FromArgb(100, 200, 255));

        // ── 4. Rumble callback (ViGEm → phone) ──────────────────────────
        Xbox360FeedbackReceivedEventHandler feedbackHandler = (_, e) =>
        {
            if (token.IsCancellationRequested) return;

            byte large = e.LargeMotor;
            byte small = e.SmallMotor;

            if (large > 0 || small > 0)
            {
                Console.WriteLine($"[GAME RUMBLE] ViGEm FeedbackReceived large={large} small={small}");
            }

            if (large >= 220 && small >= 180)
            {
                SendEffect(2, large);
            }
            else if (large >= 170 || small >= 190)
            {
                SendEffect(3, Math.Max(large, small));
            }
            else
            {
                SendRumble(large, small);
            }
        };
        sessionController.FeedbackReceived += feedbackHandler;

        // ── 5. Startup 5s debug rumble trigger ──────────────────────────
        if (rumbleTestEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, token);
                    Console.WriteLine("[rumble] test sent left=200 right=200");
                    SendRumble(200, 200);

                    await Task.Delay(200, token);
                    Console.WriteLine("[rumble] test sent left=200 right=200");
                    SendRumble(200, 200);
                }
                catch (Exception) { }
            });
        }

        if (testEffectsEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(6500, token);
                    for (byte effectId = 1; effectId <= 4; effectId++)
                    {
                        if (token.IsCancellationRequested) break;
                        Console.WriteLine($"[effect] --test-effects triggering effectId={effectId} intensity=255");
                        SendEffect(effectId, 255);
                        await Task.Delay(1500, token);
                    }
                }
                catch (Exception) { }
            });
        }

        // ── 6. Local function: apply input to ViGEm pad ─────────────────
        void ApplyState(InputPacket packet)
        {
            if (token.IsCancellationRequested) return;

            lock (_vigemLock)
            {
                if (_controller != sessionController || sessionController is null) return;

                try
                {
                    sessionController.SetButtonState(Xbox360Button.A, (packet.Buttons & PacketProtocol.ButtonA) != 0);
                    sessionController.SetButtonState(Xbox360Button.B, (packet.Buttons & PacketProtocol.ButtonB) != 0);
                    sessionController.SetButtonState(Xbox360Button.X, (packet.Buttons & PacketProtocol.ButtonX) != 0);
                    sessionController.SetButtonState(Xbox360Button.Y, (packet.Buttons & PacketProtocol.ButtonY) != 0);
                    sessionController.SetButtonState(Xbox360Button.LeftShoulder, (packet.Buttons & PacketProtocol.ButtonLB) != 0);
                    sessionController.SetButtonState(Xbox360Button.RightShoulder, (packet.Buttons & PacketProtocol.ButtonRB) != 0);
                    sessionController.SetButtonState(Xbox360Button.Back, (packet.Buttons & PacketProtocol.ButtonBack) != 0);
                    sessionController.SetButtonState(Xbox360Button.Start, (packet.Buttons & PacketProtocol.ButtonStart) != 0);
                    sessionController.SetButtonState(Xbox360Button.Up, (packet.Buttons & PacketProtocol.ButtonDPadUp) != 0);
                    sessionController.SetButtonState(Xbox360Button.Down, (packet.Buttons & PacketProtocol.ButtonDPadDown) != 0);
                    sessionController.SetButtonState(Xbox360Button.Left, (packet.Buttons & PacketProtocol.ButtonDPadLeft) != 0);
                    sessionController.SetButtonState(Xbox360Button.Right, (packet.Buttons & PacketProtocol.ButtonDPadRight) != 0);

                    sessionController.SetAxisValue(Xbox360Axis.LeftThumbX, packet.LeftStickX);
                    sessionController.SetAxisValue(Xbox360Axis.LeftThumbY, packet.LeftStickY);
                    sessionController.SetAxisValue(Xbox360Axis.RightThumbX, packet.RightStickX);
                    sessionController.SetAxisValue(Xbox360Axis.RightThumbY, packet.RightStickY);

                    sessionController.SetSliderValue(Xbox360Slider.LeftTrigger, packet.LeftTrigger);
                    sessionController.SetSliderValue(Xbox360Slider.RightTrigger, packet.RightTrigger);

                    sessionController.SubmitReport();
                }
                catch (Exception)
                {
                    return;
                }
            }

            if (_combatHapticsEnabled)
            {
                ProcessXenoverseCombatHaptics(packet);
            }
        }

        // ── 7. Background tasks: TCP accept loop, ADB watchdog, HTTP server ──
        var tcpTask = Task.Run(() => TcpAcceptLoop(sessionTcpListener, ApplyState, verbose, token));
        var httpTask = Task.Run(() => HttpEffectServerLoop(DefaultHttpEffectPort, token));
        Task? adbTask = null;

        if (adbEnabled)
        {
            adbTask = Task.Run(() => AdbWatchdog(port, token));
        }
        else
        {
            Console.WriteLine("[adb] ADB automation disabled (--no-adb).");
        }

        // ── 8. UDP receive loop on current background thread ────────────
        byte[] buffer = new byte[2048];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int received = 0;
                IPEndPoint? sender = null;

                try
                {
                    EndPoint tempSender = new IPEndPoint(IPAddress.Any, 0);
                    received = sessionUdpSocket.ReceiveFrom(buffer, ref tempSender);
                    sender = (IPEndPoint)tempSender;
                }
                catch (SocketException ex) when (
                    ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock or SocketError.Interrupted or SocketError.OperationAborted or SocketError.ConnectionReset)
                {
                    received = 0;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    break;
                }

                if (received > 0 && sender is not null)
                {
                    if (!InputPacket.TryDeserialize(buffer.AsSpan(0, received), out InputPacket packet))
                    {
                        Console.WriteLine($"[warn] ignored {received} byte UDP datagram from {sender}: bad magic or short packet");
                    }
                    else
                    {
                        bool switchedToUdp = false;
                        Interlocked.Exchange(ref _lastUdpEndpoint, sender);

                        lock (s_stateLock)
                        {
                            if (s_activeTransport != Transport.Udp)
                            {
                                s_activeTransport = Transport.Udp;
                                switchedToUdp = true;
                            }
                        }

                        if (switchedToUdp)
                        {
                            updateStatus($"Status: Phone Connected via Wi-Fi ({sender.Address})", Color.FromArgb(255, 214, 0));
                        }

                        ProcessPacket(packet, "UDP", sender.ToString(), ApplyState, verbose);
                    }
                }

                CheckHeartbeat();
            }
        }
        finally
        {
            try { sessionController.FeedbackReceived -= feedbackHandler; } catch { }
            CleanupSessionResources(sessionController, sessionUdpSocket, sessionTcpListener);

            try
            {
                await Task.WhenAny(Task.WhenAll(
                    tcpTask,
                    httpTask,
                    adbTask ?? Task.CompletedTask), Task.Delay(400));
            }
            catch { }

            Console.WriteLine("Receiver stopped.");
        }
    }

    /// <summary>
    /// Called by MainForm on Stop to immediately wake blocking socket calls
    /// without touching native ViGEm pointers from the UI thread.
    /// </summary>
    public static void WakeSocketsForStop()
    {
        try { _tcpListener?.Stop(); } catch { }
        try { _httpListener?.Stop(); } catch { }
        try { _activeTcpStream?.Close(); } catch { }
        try { _activeTcpClient?.Close(); } catch { }
        try { _udpSocket?.Close(); } catch { }
    }

    /// <summary>
    /// Cleans up only the resources belonging to the ending session so an older session's
    /// finally block can never close a newly started session's sockets or controller.
    /// </summary>
    private static void CleanupSessionResources(
        IXbox360Controller? sessionController,
        Socket? sessionUdpSocket,
        TcpListener? sessionTcpListener)
    {
        try { sessionTcpListener?.Stop(); } catch { }
        Interlocked.CompareExchange(ref _tcpListener, null, sessionTcpListener);

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        try
        {
            var stream = Interlocked.Exchange(ref _activeTcpStream, null);
            stream?.Close();
        }
        catch { }

        try
        {
            var client = Interlocked.Exchange(ref _activeTcpClient, null);
            client?.Close();
        }
        catch { }

        try { sessionUdpSocket?.Close(); } catch { }
        Interlocked.CompareExchange(ref _udpSocket, null, sessionUdpSocket);

        lock (_vigemLock)
        {
            if (sessionController is not null)
            {
                try { sessionController.Disconnect(); } catch { }
                if (_controller == sessionController)
                {
                    _controller = null;
                }
            }
        }
    }

    public static void ShutdownViGEmOnExit()
    {
        lock (_vigemLock)
        {
            try { _controller?.Disconnect(); } catch { }
            _controller = null;

            try { _sharedVigemClient?.Dispose(); } catch { }
            _sharedVigemClient = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Xenoverse 1 Real-Time Combat Haptic Engine
    // ═══════════════════════════════════════════════════════════════════

    private static void ProcessXenoverseCombatHaptics(InputPacket packet)
    {
        DateTime now = DateTime.UtcNow;
        uint b = packet.Buttons;
        uint prev;

        lock (_combatLock)
        {
            prev = _prevButtons;
            _prevButtons = b;
        }

        uint newlyPressed = b & ~prev;
        uint newlyReleased = prev & ~b;

        bool ltHeld = packet.LeftTrigger >= 100;
        bool rtHeld = packet.RightTrigger >= 100;
        uint faceMask = PacketProtocol.ButtonA | PacketProtocol.ButtonB | PacketProtocol.ButtonX | PacketProtocol.ButtonY;
        bool anyFaceHeld = (b & faceMask) != 0;
        bool anyFaceNewlyPressed = (newlyPressed & faceMask) != 0;
        bool anyFaceNewlyReleased = (newlyReleased & faceMask) != 0;

        // 1. ULTIMATE / AWOKEN TRANSFORMATION (LT + RT + Face Button A/B/X/Y)
        if (ltHeld && rtHeld)
        {
            if (anyFaceNewlyPressed)
            {
                lock (_combatLock)
                {
                    _wasUltimateCharging = true;
                    _superChargeStartUtc = now;
                    _lastCombatEffectUtc = now;
                }
                Console.WriteLine("[xenoverse] Ultimate / Awoken Skill triggered (LT+RT+Face) → Effect 4 (Transform/Awakening)");
                SendEffect(4, 255);
                return;
            }

            if (_wasUltimateCharging && anyFaceNewlyReleased && !anyFaceHeld)
            {
                lock (_combatLock)
                {
                    _wasUltimateCharging = false;
                    _lastCombatEffectUtc = now;
                }
                Console.WriteLine("[xenoverse] Ultimate Attack Unleashed → Effect 2 (Kamehameha Release)");
                SendEffect(2, 255);
                return;
            }
        }
        else if (_wasUltimateCharging && (!ltHeld || !rtHeld))
        {
            lock (_combatLock) { _wasUltimateCharging = false; }
            SendEffect(2, 255);
            return;
        }

        // 2. SUPER ATTACK / KAMEHAMEHA CHARGE & RELEASE (RT + Face Button X/Y/B/A)
        if (rtHeld && !ltHeld)
        {
            if (anyFaceNewlyPressed)
            {
                lock (_combatLock)
                {
                    _wasSuperCharging = true;
                    _superChargeStartUtc = now;
                    _lastChargePulseUtc = now;
                    _lastCombatEffectUtc = now;
                }
                Console.WriteLine("[xenoverse] Super Attack / Ki Charge started (RT+Face) → Effect 1 (Kamehameha Charge)");
                SendEffect(1, 255);
                return;
            }

            if (_wasSuperCharging && anyFaceHeld && (now - _lastChargePulseUtc).TotalMilliseconds >= 420.0)
            {
                lock (_combatLock) { _lastChargePulseUtc = now; }
                SendEffect(1, 255);
                return;
            }

            if (_wasSuperCharging && anyFaceNewlyReleased && !anyFaceHeld)
            {
                lock (_combatLock)
                {
                    _wasSuperCharging = false;
                    _lastCombatEffectUtc = now;
                }
                Console.WriteLine("[xenoverse] Super Attack Fired → Effect 2 (Kamehameha Release)");
                SendEffect(2, 255);
                return;
            }
        }
        else if (_wasSuperCharging && !rtHeld)
        {
            lock (_combatLock) { _wasSuperCharging = false; }
            Console.WriteLine("[xenoverse] Super Attack Released → Effect 2 (Kamehameha Release)");
            SendEffect(2, 255);
            return;
        }

        // 3. VANISH / GRAB / HEAVY SMASH / MELEE COMBOS
        bool lbHeld = (b & PacketProtocol.ButtonLB) != 0;
        bool rbHeld = (b & PacketProtocol.ButtonRB) != 0;

        if ((lbHeld || rbHeld) && ((newlyPressed & (PacketProtocol.ButtonB | PacketProtocol.ButtonA)) != 0))
        {
            Console.WriteLine("[xenoverse] Grab / Vanish executed → Effect 3 (Heavy Hit)");
            SendEffect(3, 255);
            return;
        }

        if ((newlyPressed & PacketProtocol.ButtonY) != 0)
        {
            Console.WriteLine("[xenoverse] Heavy Smash (Y) → Effect 3 (Heavy Hit)");
            SendEffect(3, 230);
            return;
        }

        if ((newlyPressed & PacketProtocol.ButtonX) != 0)
        {
            SendRumble(165, 165);
            return;
        }

        if ((newlyPressed & PacketProtocol.ButtonB) != 0)
        {
            SendRumble(190, 190);
            return;
        }

        if ((newlyPressed & (PacketProtocol.ButtonA | PacketProtocol.ButtonRB | PacketProtocol.ButtonLB)) != 0)
        {
            SendRumble(135, 135);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 5 — Haptics Return Path (SendRumble)
    // ═══════════════════════════════════════════════════════════════════

    public static void SendRumble(byte left, byte right)
    {
        DateTime now = DateTime.UtcNow;

        lock (_rumbleLock)
        {
            if (_hasSentRumble &&
                (now - _lastRumbleSentUtc).TotalMilliseconds < 20.0 &&
                left == _lastLeft && right == _lastRight)
            {
                return;
            }

            if (left == 0 && right == 0 && (now - _lastNonZeroRumbleUtc).TotalMilliseconds < 60.0)
            {
                return;
            }

            if (left > 0 || right > 0)
            {
                _lastNonZeroRumbleUtc = now;
            }

            _hasSentRumble = true;
            _lastRumbleSentUtc = now;
            _lastLeft = left;
            _lastRight = right;
        }

        byte[] payload = new byte[] { 0x58, 0x56, 0x52, 0x31, left, right };

        Transport activeTransport;
        lock (s_stateLock)
        {
            activeTransport = s_activeTransport;
        }

        var tcpStream = Volatile.Read(ref _activeTcpStream);
        var udpEndpoint = Volatile.Read(ref _lastUdpEndpoint);

        if (tcpStream is not null && (activeTransport == Transport.Tcp || udpEndpoint is null))
        {
            if (TrySendRumbleTcp(tcpStream, payload, left, right, now))
            {
                return;
            }
            udpEndpoint = Volatile.Read(ref _lastUdpEndpoint);
        }

        if (udpEndpoint is not null && _udpSocket is not null)
        {
            if (TrySendRumbleUdp(udpEndpoint, payload, left, right, now))
            {
                return;
            }
        }

        if (tcpStream is not null)
        {
            if (TrySendRumbleTcp(tcpStream, payload, left, right, now))
            {
                return;
            }
        }

        Console.WriteLine("[rumble] dropped (no transport)");
    }

    private static bool TrySendRumbleTcp(NetworkStream stream, byte[] payload, byte left, byte right, DateTime now)
    {
        try
        {
            lock (_tcpSendLock)
            {
                stream.Write(new byte[] { 0x06, 0x00 }, 0, 2);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }

            LogRumbleSend(left, right, "TCP", now);
            return true;
        }
        catch (IOException)
        {
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
        catch (ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] TCP rumble send failed: {ex.Message}");
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
    }

    private static bool TrySendRumbleUdp(IPEndPoint endpoint, byte[] payload, byte left, byte right, DateTime now)
    {
        try
        {
            var sock = _udpSocket;
            if (sock is null) return false;

            lock (_udpSendLock)
            {
                sock.SendTo(payload, 0, 6, SocketFlags.None, endpoint);
            }

            LogRumbleSend(left, right, "UDP", now);
            return true;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[warn] UDP rumble send failed: {ex.SocketErrorCode}");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void LogRumbleSend(byte left, byte right, string transport, DateTime now)
    {
        bool shouldLog = false;
        lock (_rumbleLock)
        {
            if ((now - _lastRumbleLogUtc).TotalMilliseconds >= 100.0)
            {
                _lastRumbleLogUtc = now;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            Console.WriteLine($"[rumble] sending → left={left} right={right} via {transport}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Phase 7 — Haptic Vocabulary Return Path (SendEffect "XVE1")
    // ═══════════════════════════════════════════════════════════════════

    public static void SendEffect(byte effectId, byte intensity)
    {
        byte[] payload = new byte[] { 0x58, 0x56, 0x45, 0x31, effectId, intensity };

        Transport activeTransport;
        lock (s_stateLock)
        {
            activeTransport = s_activeTransport;
        }

        var tcpStream = Volatile.Read(ref _activeTcpStream);
        var udpEndpoint = Volatile.Read(ref _lastUdpEndpoint);

        if (tcpStream is not null && (activeTransport == Transport.Tcp || udpEndpoint is null))
        {
            if (TrySendEffectTcp(tcpStream, payload, effectId, intensity))
            {
                return;
            }
            udpEndpoint = Volatile.Read(ref _lastUdpEndpoint);
        }

        if (udpEndpoint is not null && _udpSocket is not null)
        {
            if (TrySendEffectUdp(udpEndpoint, payload, effectId, intensity))
            {
                return;
            }
        }

        if (tcpStream is not null)
        {
            if (TrySendEffectTcp(tcpStream, payload, effectId, intensity))
            {
                return;
            }
        }

        Console.WriteLine($"[effect] dropped id={effectId} intensity={intensity} (no transport)");
    }

    private static bool TrySendEffectTcp(NetworkStream stream, byte[] payload, byte effectId, byte intensity)
    {
        try
        {
            lock (_tcpSendLock)
            {
                stream.Write(new byte[] { 0x06, 0x00 }, 0, 2);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }

            Console.WriteLine($"[effect] sending → id={effectId} intensity={intensity} via TCP");
            return true;
        }
        catch (IOException)
        {
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
        catch (ObjectDisposedException)
        {
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] TCP effect send failed: {ex.Message}");
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            return false;
        }
    }

    private static bool TrySendEffectUdp(IPEndPoint endpoint, byte[] payload, byte effectId, byte intensity)
    {
        try
        {
            var sock = _udpSocket;
            if (sock is null) return false;

            lock (_udpSendLock)
            {
                sock.SendTo(payload, 0, 6, SocketFlags.None, endpoint);
            }

            Console.WriteLine($"[effect] sending → id={effectId} intensity={intensity} via UDP");
            return true;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[warn] UDP effect send failed: {ex.SocketErrorCode}");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // ── HTTP Effect Server (http://localhost:8080/effect/{id}) ──────────

    private static void HttpEffectServerLoop(int httpPort, CancellationToken token)
    {
        TcpListener? httpListener = null;
        try
        {
            httpListener = new TcpListener(IPAddress.Loopback, httpPort);
            httpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            httpListener.Start();
            _httpListener = httpListener;
            Console.WriteLine($"[effect] HTTP trigger listening on http://localhost:{httpPort}/effect/{{1..4}}");

            while (!token.IsCancellationRequested)
            {
                if (!httpListener.Pending())
                {
                    Thread.Sleep(25);
                    continue;
                }

                using TcpClient client = httpListener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                byte[] reqBuf = new byte[1024];
                int n = stream.Read(reqBuf, 0, reqBuf.Length);
                if (n <= 0) continue;

                string requestText = Encoding.ASCII.GetString(reqBuf, 0, n);
                string firstLine = requestText.Split('\r', '\n')[0];
                string[] parts = firstLine.Split(' ');
                string path = parts.Length >= 2 ? parts[1] : "/";

                string responseBody;
                int statusCode = 200;

                if (TryParseEffectRequest(path, out byte effectId, out byte intensity))
                {
                    SendEffect(effectId, intensity);
                    responseBody = $"{{\"ok\":true,\"effectId\":{effectId},\"intensity\":{intensity}}}\n";
                }
                else
                {
                    statusCode = 400;
                    responseBody = "{\"ok\":false,\"usage\":\"GET /effect/{1..4}?intensity=255\"}\n";
                }

                byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
                string statusText = statusCode == 200 ? "200 OK" : "400 Bad Request";
                string header =
                    $"HTTP/1.1 {statusText}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[warn] HTTP effect server on port {httpPort} unavailable: {ex.Message}");
            }
        }
        finally
        {
            try { httpListener?.Stop(); } catch { }
            Interlocked.CompareExchange(ref _httpListener, null, httpListener);
        }
    }

    private static bool TryParseEffectRequest(string rawPath, out byte effectId, out byte intensity)
    {
        effectId = 0;
        intensity = 255;

        string pathPart = rawPath;
        string queryPart = string.Empty;
        int qIdx = rawPath.IndexOf('?');
        if (qIdx >= 0)
        {
            pathPart = rawPath[..qIdx];
            queryPart = rawPath[(qIdx + 1)..];
        }

        string[] segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("effect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!byte.TryParse(segments[1], out effectId) || effectId == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(queryPart))
        {
            foreach (string kv in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = kv.Split('=', 2);
                if (pair.Length == 2 &&
                    pair[0].Equals("intensity", StringComparison.OrdinalIgnoreCase) &&
                    byte.TryParse(pair[1], out byte parsedIntensity))
                {
                    intensity = parsedIntensity;
                }
            }
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TCP accept loop — runs on a background thread
    // ═══════════════════════════════════════════════════════════════════

    private static void TcpAcceptLoop(
        TcpListener listener,
        Action<InputPacket> applyState,
        bool verbose,
        CancellationToken token)
    {
        Console.WriteLine($"[tcp] accept loop started on port {((IPEndPoint)listener.LocalEndpoint).Port}");
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(20);
                    continue;
                }

                var client = listener.AcceptTcpClient();
                Console.WriteLine($"[tcp] client connected from {client.Client.RemoteEndPoint}");
                _statusUpdater?.Invoke("Status: Phone Connected via USB (TCP)", Color.FromArgb(68, 255, 136));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClient(client, applyState, verbose, token);
                    }
                    finally
                    {
                        Console.WriteLine("[tcp] client disconnected");
                        client.Dispose();
                    }
                });
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"[tcp] accept error: {ex.Message}");
                }
                break;
            }
        }
    }

    private static async Task HandleClient(
        TcpClient tcpClient,
        Action<InputPacket> applyState,
        bool verbose,
        CancellationToken token)
    {
        var remoteLabel = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "tcp";
        NetworkStream stream;
        try
        {
            stream = tcpClient.GetStream();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tcp] reader closed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Interlocked.Exchange(ref _activeTcpClient, tcpClient);
        Interlocked.Exchange(ref _activeTcpStream, stream);
        lock (s_stateLock)
        {
            s_activeTransport = Transport.Tcp;
        }

        byte[] lengthBuffer = new byte[2];

        try
        {
            while (!token.IsCancellationRequested && tcpClient.Connected)
            {
                int readLen = 0;
                while (readLen < 2)
                {
                    int n = await stream.ReadAsync(lengthBuffer.AsMemory(readLen, 2 - readLen), token);
                    if (n == 0)
                    {
                        throw new EndOfStreamException("Connection closed by client");
                    }
                    readLen += n;
                }

                int len = BinaryPrimitives.ReadUInt16LittleEndian(lengthBuffer);

                if (len <= 0 || len > 4096)
                {
                    Console.WriteLine($"[tcp] bad length {len}");
                    return;
                }

                byte[] payload = new byte[len];
                int readPayload = 0;
                while (readPayload < len)
                {
                    int n = await stream.ReadAsync(payload.AsMemory(readPayload, len - readPayload), token);
                    if (n == 0)
                    {
                        throw new EndOfStreamException($"Connection closed prematurely while reading {len}-byte payload (read {readPayload} bytes)");
                    }
                    readPayload += n;
                }

                if (len >= 4)
                {
                    var magic = payload.AsSpan(0, 4);

                    if (magic.SequenceEqual("XVP1"u8))
                    {
                        if (InputPacket.TryDeserialize(payload, out InputPacket packet))
                        {
                            Interlocked.Exchange(ref _activeTcpStream, stream);
                            lock (s_stateLock)
                            {
                                s_activeTransport = Transport.Tcp;
                            }

                            ProcessPacket(packet, "TCP", remoteLabel, applyState, verbose);
                        }
                        else
                        {
                            Console.WriteLine($"[tcp] failed to deserialize XVP1 packet, len={len}");
                        }
                    }
                    else if (magic.SequenceEqual("XVPH"u8))
                    {
                        Interlocked.Exchange(ref _activeTcpStream, stream);
                        lock (s_stateLock)
                        {
                            s_activeTransport = Transport.Tcp;
                            s_lastHeartbeatUtc = DateTime.UtcNow;

                            if (s_degraded)
                            {
                                s_degraded = false;
                                Console.WriteLine("[heartbeat] input channel restored via TCP");
                            }
                        }

                        Console.WriteLine("[heartbeat:TCP]");
                    }
                    else
                    {
                        string hex = Convert.ToHexString(magic);
                        Console.WriteLine($"[tcp] unknown magic 0x{hex} len={len}, skipping");
                    }
                }
                else
                {
                    string hex = Convert.ToHexString(payload);
                    Console.WriteLine($"[tcp] short payload without magic 0x{hex} len={len}, skipping");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[tcp] reader closed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeTcpStream, null, stream);
            Interlocked.CompareExchange(ref _activeTcpClient, null, tcpClient);
            lock (s_stateLock)
            {
                if (s_activeTransport == Transport.Tcp && _activeTcpStream is null)
                {
                    s_activeTransport = _lastUdpEndpoint is not null ? Transport.Udp : Transport.None;
                    if (!token.IsCancellationRequested)
                    {
                        if (s_activeTransport == Transport.Udp)
                        {
                            _statusUpdater?.Invoke("Status: Phone Connected via Wi-Fi (UDP)", Color.FromArgb(255, 214, 0));
                        }
                        else
                        {
                            _statusUpdater?.Invoke($"Status: Listening on UDP/TCP (Port {DefaultPort})", Color.FromArgb(100, 200, 255));
                        }
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unified packet processing — shared by UDP and TCP
    // ═══════════════════════════════════════════════════════════════════

    private static void ProcessPacket(
        InputPacket packet,
        string transport,
        string? senderLabel,
        Action<InputPacket> applyState,
        bool verbose)
    {
        bool accepted = true;

        lock (s_stateLock)
        {
            if (s_hasSequence)
            {
                uint delta = unchecked(packet.Sequence - s_lastSequence);

                if (delta == 0 || delta > 0x8000_0000)
                {
                    Console.WriteLine($"[discard] stale/dup on {transport}: seq={packet.Sequence}, last={s_lastSequence}, from {senderLabel}");
                    accepted = false;
                }
                else if (delta > 1)
                {
                    Console.WriteLine($"[gap] WARNING missed {delta - 1} packet(s) on {transport}: seq={packet.Sequence} after last={s_lastSequence}");
                }
            }

            if (accepted)
            {
                s_lastSequence = packet.Sequence;
                s_hasSequence = true;
                s_acceptedPackets++;
            }
        }

        if (!accepted) return;

        lock (s_stateLock)
        {
            s_lastHeartbeatUtc = DateTime.UtcNow;

            if (s_degraded)
            {
                s_degraded = false;
                Console.WriteLine($"[heartbeat] input channel restored via {transport}");
                if (transport == "TCP")
                {
                    _statusUpdater?.Invoke("Status: Phone Connected via USB (TCP)", Color.FromArgb(68, 255, 136));
                }
                else
                {
                    _statusUpdater?.Invoke("Status: Phone Connected via Wi-Fi (UDP)", Color.FromArgb(255, 214, 0));
                }
            }
        }

        int count;
        lock (s_stateLock) { count = s_acceptedPackets; }

        if (packet.IsHeartbeat)
        {
            if (transport == "TCP")
            {
                Console.WriteLine("[heartbeat:TCP]");
            }
            else if (verbose)
            {
                Console.WriteLine($"[heartbeat] seq={packet.Sequence} via {transport}");
            }
        }
        else
        {
            applyState(packet);

            if (transport == "TCP" || verbose || count == 1 || count % SequenceLogEvery == 0)
            {
                Console.WriteLine(
                    $"[input:{transport}] seq={packet.Sequence} buttons=0x{packet.Buttons:X8} " +
                    $"ls=({packet.LeftStickX},{packet.LeftStickY}) rs=({packet.RightStickX},{packet.RightStickY}) " +
                    $"lt={packet.LeftTrigger} rt={packet.RightTrigger}");
            }
        }
    }

    private static void CheckHeartbeat()
    {
        lock (s_stateLock)
        {
            TimeSpan since = DateTime.UtcNow - s_lastHeartbeatUtc;

            if (!s_degraded && since.TotalMilliseconds >= PacketProtocol.HeartbeatTimeoutMs)
            {
                s_degraded = true;
                Console.WriteLine(
                    $"HEARTBEAT_TIMEOUT: no heartbeat for {since.TotalMilliseconds:F0} ms " +
                    $"(limit {PacketProtocol.HeartbeatTimeoutMs} ms). Input channel DEGRADED.");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ADB watchdog — background thread that auto-sets up reverse port
    // ═══════════════════════════════════════════════════════════════════

    private static void AdbWatchdog(int port, CancellationToken token)
    {
        string? adbPath = ResolveAdbPath();

        if (adbPath is null)
        {
            Console.WriteLine("[adb] adb not found in PATH or common SDK locations. USB fallback disabled.");
            Console.WriteLine("[adb] Install Android SDK Platform-Tools or add adb to your PATH.");
            return;
        }

        Console.WriteLine($"[adb] Using: {adbPath}");

        var previousDevices = new HashSet<string>(StringComparer.Ordinal);
        bool firstCheck = true;

        while (!token.IsCancellationRequested)
        {
            try
            {
                string devicesOutput = RunAdb(adbPath, "devices");
                var currentDevices = ParseAdbDeviceIds(devicesOutput);

                bool hasNewDevice = firstCheck
                    ? currentDevices.Count > 0
                    : currentDevices.Except(previousDevices).Any();

                if (hasNewDevice)
                {
                    string result = RunAdb(adbPath, $"reverse tcp:{port} tcp:{port}");
                    Console.WriteLine("[adb] reverse established");
                    if (!string.IsNullOrWhiteSpace(result) && !result.Trim().Equals(port.ToString()))
                    {
                        Console.WriteLine($"[adb]   {result.Trim()}");
                    }
                }
                else if (!firstCheck && previousDevices.Count > 0 && currentDevices.Count == 0)
                {
                    Console.WriteLine("[adb] reverse removed");
                }

                previousDevices = currentDevices;
                firstCheck = false;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[adb] error: {ex.Message}");
            }

            for (int i = 0; i < 50 && !token.IsCancellationRequested; i++)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static HashSet<string> ParseAdbDeviceIds(string output)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1] == "device")
            {
                set.Add(parts[0]);
            }
        }
        return set;
    }

    private static string? ResolveAdbPath()
    {
        if (TryRunAdb("adb"))
        {
            return "adb";
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"),
            @"C:\Android\platform-tools\adb.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Android", "android-sdk", "platform-tools", "adb.exe"),
        ];

        foreach (string envVar in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            string? val = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(val))
            {
                candidates = [.. candidates, Path.Combine(val, "platform-tools", "adb.exe")];
            }
        }

        foreach (string path in candidates)
        {
            if (File.Exists(path) && TryRunAdb(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool TryRunAdb(string path)
    {
        try
        {
            RunAdb(path, "version");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RunAdb(string adbPath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"Failed to start: {adbPath} {arguments}");

        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(1500);

        return stdout;
    }
}
