using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PCReceiver;

namespace TestSender;

internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 27016;
    private const int InputIntervalMs = 16;   // ~60 Hz
    private const int StepDurationMs = 400;    // duration of one button script step

    // Dummy button script: exercises every button and D-pad direction once per cycle.
    private static readonly (uint Mask, string Name)[] ButtonScript =
    [
        (PacketProtocol.ButtonA, "A"),
        (PacketProtocol.ButtonB, "B"),
        (PacketProtocol.ButtonX, "X"),
        (PacketProtocol.ButtonY, "Y"),
        (PacketProtocol.ButtonLB, "LB"),
        (PacketProtocol.ButtonRB, "RB"),
        (PacketProtocol.ButtonBack, "Back"),
        (PacketProtocol.ButtonStart, "Start"),
        (PacketProtocol.ButtonDPadUp, "DPadUp"),
        (PacketProtocol.ButtonDPadDown, "DPadDown"),
        (PacketProtocol.ButtonDPadLeft, "DPadLeft"),
        (PacketProtocol.ButtonDPadRight, "DPadRight"),
        (0u, "none"),
    ];

    private static int Main(string[] args)
    {
        string host = DefaultHost;
        int port = DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.Equals("--host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], out int parsedPort) && parsedPort is > 0 and <= 65535)
            {
                port = parsedPort;
                i++;
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (arg.Equals("--host", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[warn] --host needs a value");
            }
            else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[warn] --port needs a value");
            }
            else if (int.TryParse(arg, out int positionalPort) && positionalPort is > 0 and <= 65535)
            {
                port = positionalPort;
            }
            else
            {
                host = arg;
            }
        }

        IPAddress address;

        try
        {
            address = Dns.GetHostAddresses(host)
                .First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] cannot resolve '{host}': {ex.Message}");
            return 1;
        }

        var target = new IPEndPoint(address, port);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.ReceiveTimeout = 100;

        Console.WriteLine($"TestSender -> {target.Address}:{target.Port} (local port {((IPEndPoint)socket.LocalEndPoint!).Port})");
        Console.WriteLine($"Input packets every {InputIntervalMs} ms, heartbeat every {PacketProtocol.HeartbeatIntervalMs} ms.");
        Console.WriteLine("Press Ctrl+C to exit.");

        using var shutdown = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var rumbleListener = new Thread(() => ListenForRumble(socket, shutdown.Token))
        {
            IsBackground = true,
            Name = "rumble-listener"
        };
        rumbleListener.Start();

        var clock = Stopwatch.StartNew();
        uint sequence = 0;
        long nextInputDueMs = 0;
        long nextHeartbeatDueMs = 0;
        long nextStatusDueMs = 0;
        int sentPackets = 0;
        string activeButton = string.Empty;

        while (!shutdown.IsCancellationRequested)
        {
            long now = clock.ElapsedMilliseconds;

            if (now >= nextInputDueMs)
            {
                nextInputDueMs = now + InputIntervalMs;

                double t = clock.Elapsed.TotalSeconds;
                int step = (int)(now / StepDurationMs) % ButtonScript.Length;
                uint buttons = ButtonScript[step].Mask;

                if (ButtonScript[step].Name != activeButton)
                {
                    activeButton = ButtonScript[step].Name;
                    Console.WriteLine($"[buttons] {activeButton}");
                }

                var packet = new InputPacket
                {
                    Sequence = sequence++,
                    Buttons = buttons,
                    LeftStickX = (short)(Math.Sin(t * 1.5) * 32000),
                    LeftStickY = (short)(Math.Cos(t * 1.2) * 32000),
                    RightStickX = (short)(Math.Sin(t * 2.1) * 30000),
                    RightStickY = (short)(Math.Cos(t * 1.7) * 30000),
                    LeftTrigger = (byte)Math.Clamp(128 + 127 * Math.Sin(t * 1.3), 0, 255),
                    RightTrigger = (byte)Math.Clamp(128 + 127 * Math.Cos(t * 1.1), 0, 255),
                };

                Send(socket, target, packet.Serialize());
                sentPackets++;
            }

            if (now >= nextHeartbeatDueMs)
            {
                nextHeartbeatDueMs = now + PacketProtocol.HeartbeatIntervalMs;

                var heartbeat = new InputPacket
                {
                    Sequence = sequence++,
                    Buttons = PacketProtocol.ButtonHeartbeat
                };

                Send(socket, target, heartbeat.Serialize());
                sentPackets++;
            }

            if (now >= nextStatusDueMs)
            {
                nextStatusDueMs = now + 1000;
                Console.WriteLine($"[status] sent {sentPackets} packets, last seq={sequence - 1}");
            }

            Thread.Sleep(1);
        }

        Console.WriteLine("TestSender stopped.");
        return 0;
    }

    private static void Send(Socket socket, EndPoint target, byte[] payload)
    {
        try
        {
            socket.SendTo(payload, 0, payload.Length, SocketFlags.None, target);
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[warn] send failed: {ex.SocketErrorCode}");
        }
        catch (ObjectDisposedException)
        {
            // Socket closed while shutting down.
        }
    }

    private static void ListenForRumble(Socket socket, CancellationToken token)
    {
        var buffer = new byte[256];

        while (!token.IsCancellationRequested)
        {
            try
            {
                EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(buffer, ref remoteEp);

                if (received <= 0)
                {
                    continue;
                }

                if (RumblePacket.TryDeserialize(buffer.AsSpan(0, received), out RumblePacket rumble))
                {
                    Console.WriteLine($"[rumble] left={rumble.LeftMotor} right={rumble.RightMotor}");
                }
                else
                {
                    Console.WriteLine($"[warn] ignored {received} byte datagram from the PC (bad magic or short packet)");
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                // No rumble within the timeout, keep waiting.
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[warn] rumble receive failed: {ex.SocketErrorCode}");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TestSender - sends dummy gamepad packets to the PCReceiver.");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run --project TestSender -- [<host>] [<port>]");
        Console.WriteLine("       dotnet run --project TestSender -- --host <host> --port <port>");
        Console.WriteLine();
        Console.WriteLine($"  <host>      target address (default {DefaultHost})");
        Console.WriteLine($"  <port>      target port (default {DefaultPort})");
    }
}