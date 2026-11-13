package com.xenoversepad.controller

import android.util.Log
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Manages network transport for XenoversePad, supporting both USB (TCP via
 * ADB reverse tunnel) and Wi-Fi (UDP).
 *
 * **Priority**: USB/TCP to 127.0.0.1:27016 is preferred. If that connection
 * fails or drops, traffic falls back to UDP aimed at the user-entered IP.
 * When the TCP link becomes available again, it automatically switches over.
 *
 * The sequence number in [GamepadProtocol] is continuous and never reset
 * across transport switches.
 *
 * Heartbeats are sent every 50ms over UDP only; on TCP, heartbeats are suppressed
 * because the 60 Hz input loop already serves as a heartbeat and maintains framing.
 * Rumble packets ("XVR1", 6 bytes) and Haptic Vocabulary Effect packets ("XVE1", 6 bytes)
 * from the PC receiver are received on the active transport and forwarded to
 * [hapticSink] and [effectSink].
 */
class NetworkManager {

    // ── Public state ────────────────────────────────────────────────────

    enum class Transport { DISCONNECTED, USB_TCP, WIFI_UDP }

    private val _transport = MutableStateFlow(Transport.DISCONNECTED)
    val transport: StateFlow<Transport> = _transport

    @Volatile var hapticSink: ((left: Int, right: Int) -> Unit)? = null
    @Volatile var effectSink: ((effectId: Int, intensity: Int) -> Unit)? = null

    // ── Configuration ───────────────────────────────────────────────────

    companion object {
        private const val TAG = "NetworkManager"
        private const val TCP_HOST = "127.0.0.1"
        private const val PORT = GamepadProtocol.PORT           // 27016
        private const val HEARTBEAT_MS = 50L
        private const val TCP_CONNECT_TIMEOUT_MS = 600
        private const val TCP_PROBE_INTERVAL_MS = 1_500L
        private const val TCP_STABILITY_MS = 500L
        private const val TCP_MAX_FAILURES = 1
        private const val SWITCH_COOLDOWN_MS = 1_000L
        private const val RUMBLE_PACKET_SIZE = 6
    }

    // ── Internals ───────────────────────────────────────────────────────

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    // TCP
    private val tcpLock = Any()
    @Volatile private var tcpSocket: Socket? = null
    @Volatile private var tcpOut: OutputStream? = null
    @Volatile private var tcpIn: InputStream? = null
    @Volatile private var consecutiveTcpFailures = 0
    @Volatile private var lastTcpError = ""
    @Volatile private var lastSwitchTimeMs = 0L

    // UDP
    private val udpLock = Any()
    @Volatile private var udpSocket: DatagramSocket? = null
    @Volatile private var udpAddress: InetAddress? = null

    // Jobs
    private var heartbeatJob: Job? = null
    private var tcpProbeJob: Job? = null
    private var rumbleListenJob: Job? = null

    // Wi-Fi target IP (set from the UI)
    @Volatile private var wifiIp: String = ""

    // ── Public API ──────────────────────────────────────────────────────

    /**
     * Begin the connection lifecycle.
     *
     * @param fallbackIp the user-entered PC IP for UDP fallback.
     */
    fun start(fallbackIp: String) {
        stop()
        wifiIp = fallbackIp

        scope.launch {
            // Open UDP first so fallback is immediately ready
            openUdp()

            // Initialize to UDP; probe loop will verify and switch to TCP
            switchToUdp("init-fallback")

            // Start heartbeat loop (active on UDP, suppressed on TCP)
            startHeartbeat()

            // Start probe loop to connect/switch to TCP when USB cable is present
            startTcpProbeLoop()
        }
    }

    /**
     * Update the Wi-Fi fallback IP at runtime.
     */
    fun updateFallbackIp(ip: String) {
        wifiIp = ip
        scope.launch {
            try {
                val addr = InetAddress.getByName(ip)
                synchronized(udpLock) {
                    udpAddress = addr
                }
            } catch (e: Exception) {
                Log.w(TAG, "Bad fallback IP: $ip", e)
            }
        }
    }

    /**
     * Send one input-state packet over the active transport.
     * Safe to call from any thread.
     */
    fun sendInput(
        buttons: Int,
        lx: Short, ly: Short,
        rx: Short = 0, ry: Short = 0,
        lt: Int = 0, rt: Int = 0
    ) {
        val data = GamepadProtocol.buildInputPacket(buttons, lx, ly, rx, ry, lt, rt)
        scope.launch { transmit(data) }
    }

    /**
     * Tear down all sockets, jobs, and reset transport to DISCONNECTED.
     */
    fun stop() {
        heartbeatJob?.cancel()
        heartbeatJob = null
        tcpProbeJob?.cancel()
        tcpProbeJob = null
        rumbleListenJob?.cancel()
        rumbleListenJob = null

        closeTcp()
        closeUdp()

        _transport.value = Transport.DISCONNECTED
        Log.i(TAG, "Stopped")
    }

    /**
     * Cancel the coroutine scope permanently (call from Activity onDestroy).
     */
    fun destroy() {
        stop()
        scope.cancel()
    }

    // ── Transport switching ─────────────────────────────────────────────

    private fun switchToTcp(reason: String) {
        synchronized(this) {
            val now = System.currentTimeMillis()
            if (now - lastSwitchTimeMs < SWITCH_COOLDOWN_MS) return
            lastSwitchTimeMs = now
            consecutiveTcpFailures = 0
            // Note: do not close UDP socket so fallback remains instantaneous
            _transport.value = Transport.USB_TCP
            startRumbleListener()
            Log.d(TAG, "switch → TCP (reason=$reason)")
        }
    }

    private fun switchToUdp(reason: String) {
        synchronized(this) {
            lastSwitchTimeMs = System.currentTimeMillis()
            _transport.value = Transport.WIFI_UDP
            startRumbleListener()
            Log.d(TAG, "switch → UDP (reason=$reason)")
        }
    }

    private fun handleTcpFailure(reason: String = "tcp-dead") {
        synchronized(this) {
            if (_transport.value != Transport.USB_TCP) return
            closeTcp()
            openUdp()
            switchToUdp(reason)
        }
    }

    // ── TCP ─────────────────────────────────────────────────────────────

    private fun connectTcpAndVerify(): Boolean {
        closeTcp()
        val sock = Socket()
        try {
            sock.connect(InetSocketAddress(TCP_HOST, PORT), TCP_CONNECT_TIMEOUT_MS)
            sock.tcpNoDelay = true
            sock.soTimeout = 500  // read timeout for rumble receiver
            val out = sock.getOutputStream()
            val inp = sock.getInputStream()

            synchronized(tcpLock) {
                tcpSocket = sock
                tcpOut = out
                tcpIn = inp
            }
            Log.d(TAG, "verify connect OK")
        } catch (e: Exception) {
            Log.d(TAG, "verify connect FAIL: ${e.message}")
            try { sock.close() } catch (_: Exception) {}
            return false
        }

        try {
            val verify = GamepadProtocol.buildInputPacket(
                buttons = 0, lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0
            )
            val len = verify.size
            val out = tcpOut ?: return false
            synchronized(tcpLock) {
                out.write(len and 0xFF)
                out.write((len shr 8) and 0xFF)
                out.write(verify)
                out.flush()
            }
            Log.d(TAG, "verify write OK (${len + 2} bytes)")
            return true
        } catch (e: Exception) {
            Log.d(TAG, "verify write FAIL: ${e.message}")
            closeTcp()
            return false
        }
    }

    private fun closeTcp() {
        synchronized(tcpLock) {
            try { tcpOut?.close() } catch (_: Exception) {}
            try { tcpIn?.close() } catch (_: Exception) {}
            try { tcpSocket?.close() } catch (_: Exception) {}
            tcpSocket = null
            tcpOut = null
            tcpIn = null
        }
    }

    private fun transmitTcp(data: ByteArray): Boolean {
        return try {
            synchronized(tcpLock) {
                val out = tcpOut ?: return false
                val len = data.size
                out.write(len and 0xFF)
                out.write((len shr 8) and 0xFF)
                out.write(data)
                out.flush()
            }
            true
        } catch (ex: Exception) {
            Log.d(TAG, "TCP write failed: ${ex.javaClass.simpleName}: ${ex.message}")
            lastTcpError = "${ex.javaClass.simpleName}: ${ex.message}"
            false
        }
    }

    // ── UDP ─────────────────────────────────────────────────────────────

    private fun openUdp() {
        synchronized(udpLock) {
            try {
                if (udpSocket != null && udpSocket?.isClosed == false) {
                    return
                }
                if (udpAddress == null && wifiIp.isNotEmpty()) {
                    udpAddress = InetAddress.getByName(wifiIp)
                }
                udpSocket = DatagramSocket().apply {
                    soTimeout = 500  // read timeout for rumble receiver
                }
                Log.i(TAG, "UDP socket opened → $wifiIp:$PORT")
            } catch (e: Exception) {
                Log.e(TAG, "Failed to open UDP", e)
            }
        }
    }

    private fun closeUdp() {
        synchronized(udpLock) {
            try { udpSocket?.close() } catch (_: Exception) {}
            udpSocket = null
        }
    }

    private fun transmitUdp(data: ByteArray): Boolean {
        return try {
            val addr = udpAddress ?: synchronized(udpLock) {
                if (wifiIp.isNotEmpty()) InetAddress.getByName(wifiIp).also { udpAddress = it } else null
            } ?: return false

            val sock = udpSocket ?: run {
                openUdp()
                udpSocket ?: return false
            }

            val pkt = DatagramPacket(data, data.size, addr, PORT)
            sock.send(pkt)
            true
        } catch (e: Exception) {
            Log.w(TAG, "UDP transmit failed: ${e.message}")
            false
        }
    }

    // ── Unified transmit ────────────────────────────────────────────────

    private fun transmit(data: ByteArray) {
        when (_transport.value) {
            Transport.USB_TCP -> {
                if (!transmitTcp(data)) {
                    consecutiveTcpFailures++
                    Log.d(TAG, "TCP failure $consecutiveTcpFailures/$TCP_MAX_FAILURES: $lastTcpError")
                    if (consecutiveTcpFailures >= TCP_MAX_FAILURES) {
                        handleTcpFailure("tcp-dead")
                    }
                    transmitUdp(data)
                } else {
                    consecutiveTcpFailures = 0
                }
            }
            Transport.WIFI_UDP -> {
                transmitUdp(data)
            }
            Transport.DISCONNECTED -> { /* drop */ }
        }
    }

    // ── Heartbeat ───────────────────────────────────────────────────────

    private fun startHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (isActive) {
                if (_transport.value == Transport.USB_TCP) {
                    delay(HEARTBEAT_MS)
                    continue
                }
                transmit(GamepadProtocol.buildHeartbeat())
                delay(HEARTBEAT_MS)
            }
        }
    }

    // ── TCP probe loop (auto-switch-back) ───────────────────────────────

    private fun startTcpProbeLoop() {
        tcpProbeJob?.cancel()
        tcpProbeJob = scope.launch {
            while (isActive) {
                delay(TCP_PROBE_INTERVAL_MS)
                if (_transport.value == Transport.WIFI_UDP) {
                    if (connectTcpAndVerify()) {
                        delay(TCP_STABILITY_MS)
                        val stabilityPacket = GamepadProtocol.buildInputPacket(0, 0, 0, 0, 0, 0, 0)
                        val len = stabilityPacket.size
                        var stabilityWriteOk = false
                        try {
                            val out = tcpOut
                            if (out != null) {
                                synchronized(tcpLock) {
                                    out.write(len and 0xFF)
                                    out.write((len shr 8) and 0xFF)
                                    out.write(stabilityPacket)
                                    out.flush()
                                }
                                stabilityWriteOk = true
                            }
                        } catch (e: Exception) {
                            Log.d(TAG, "probe stability write failed: ${e.message}")
                        }

                        if (stabilityWriteOk) {
                            Log.d(TAG, "probe: verify ok")
                            switchToTcp("probe-verify")
                        } else {
                            closeTcp()
                        }
                    }
                }
            }
        }
    }

    // ── Rumble & Effect receiver ────────────────────────────────────────

    /**
     * Listen for 6-byte rumble ("XVR1") or haptic effect ("XVE1") packets on the active transport.
     *
     * Standard Rumble format (6 bytes):
     *   [0–3] Magic "XVR1" (0x58, 0x56, 0x52, 0x31)
     *   [4]   Large motor (uint8, 0–255)
     *   [5]   Small motor (uint8, 0–255)
     *
     * Haptic Effect format (6 bytes):
     *   [0–3] Magic "XVE1" (0x58, 0x56, 0x45, 0x31)
     *   [4]   effectId (uint8)
     *   [5]   intensity (uint8, 0–255)
     */
    private fun startRumbleListener() {
        rumbleListenJob?.cancel()
        rumbleListenJob = scope.launch {
            when (_transport.value) {
                Transport.USB_TCP -> listenRumbleTcp()
                Transport.WIFI_UDP -> listenRumbleUdp()
                Transport.DISCONNECTED -> {}
            }
        }
    }

    private suspend fun CoroutineScope.listenRumbleTcp() {
        val buf = ByteArray(RUMBLE_PACKET_SIZE)
        buf[0] = 0x58.toByte() // 'X'
        buf[1] = 0x56.toByte() // 'V'
        buf[2] = 0x52.toByte() // 'R' or 'E' (0x45)
        buf[3] = 0x31.toByte() // '1'

        while (isActive && _transport.value == Transport.USB_TCP) {
            try {
                val inp = tcpIn ?: break

                // Sync on 4-byte magic "XVR1" or "XVE1"
                var matched = 0
                while (matched < 4) {
                    val b = inp.read()
                    if (b == -1) {
                        handleTcpFailure("tcp-dead")
                        return
                    }
                    when (matched) {
                        0 -> if (b == 0x58) matched = 1
                        1 -> if (b == 0x56) matched = 2 else if (b == 0x58) matched = 1 else matched = 0
                        2 -> if (b == 0x52 || b == 0x45) {
                            buf[2] = b.toByte()
                            matched = 3
                        } else if (b == 0x58) {
                            matched = 1
                        } else {
                            matched = 0
                        }
                        3 -> if (b == 0x31) matched = 4 else if (b == 0x58) matched = 1 else matched = 0
                    }
                }

                // Read remaining 2 bytes (motors or effectId/intensity)
                var read = 4
                while (read < RUMBLE_PACKET_SIZE) {
                    val n = inp.read(buf, read, RUMBLE_PACKET_SIZE - read)
                    if (n == -1) {
                        handleTcpFailure("tcp-dead")
                        return
                    }
                    read += n
                }

                parseRumble(buf)
            } catch (e: java.net.SocketTimeoutException) {
                // Timeout when idle — normal
                continue
            } catch (e: Exception) {
                if (_transport.value == Transport.USB_TCP) {
                    Log.d(TAG, "TCP read error: ${e.message}")
                    handleTcpFailure("tcp-dead")
                }
                break
            }
        }
    }

    private suspend fun CoroutineScope.listenRumbleUdp() {
        val buf = ByteArray(RUMBLE_PACKET_SIZE)
        while (isActive && _transport.value == Transport.WIFI_UDP) {
            try {
                val sock = udpSocket ?: break
                val pkt = DatagramPacket(buf, buf.size)
                sock.receive(pkt)
                if (pkt.length >= RUMBLE_PACKET_SIZE) {
                    parseRumble(buf)
                }
            } catch (e: java.net.SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                if (_transport.value == Transport.WIFI_UDP) {
                    Log.w(TAG, "Rumble UDP read error: ${e.message}")
                    delay(100)
                }
                break
            }
        }
    }

    private fun parseRumble(data: ByteArray) {
        if (data.size != 6) {
            Log.d(TAG, "parseRumble: wrong size ${data.size}")
            return
        }

        val isXvr1 = data[0] == 0x58.toByte() && data[1] == 0x56.toByte() &&
            data[2] == 0x52.toByte() && data[3] == 0x31.toByte()
        val isXve1 = data[0] == 0x58.toByte() && data[1] == 0x56.toByte() &&
            data[2] == 0x45.toByte() && data[3] == 0x31.toByte()

        when {
            isXvr1 -> {
                val left = data[4].toInt() and 0xFF
                val right = data[5].toInt() and 0xFF
                Log.i(TAG, "🎮 Rumble received → left=$left right=$right")
                hapticSink?.invoke(left, right)
            }
            isXve1 -> {
                val effectId = data[4].toInt() and 0xFF
                val intensity = data[5].toInt() and 0xFF
                Log.i(TAG, "⚡ Effect received → effectId=$effectId intensity=$intensity")
                effectSink?.invoke(effectId, intensity)
            }
            else -> {
                Log.d(TAG, "parseRumble: bad magic ${data.take(4).joinToString(" ") { "%02X".format(it) }}")
            }
        }
    }
}
