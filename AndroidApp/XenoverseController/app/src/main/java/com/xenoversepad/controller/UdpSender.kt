package com.xenoversepad.controller

import android.util.Log
import kotlinx.coroutines.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * Sends XenoversePad UDP packets to the PC receiver.
 *
 * - A heartbeat loop fires every 50 ms so the receiver knows we're alive.
 * - [sendInput] pushes an immediate input-state packet; it can be called from
 *   any thread (the actual I/O is dispatched to [Dispatchers.IO]).
 */
class UdpSender(
    private val targetIp: String,
    private val targetPort: Int = GamepadProtocol.PORT
) {

    companion object {
        private const val TAG = "UdpSender"
        private const val HEARTBEAT_INTERVAL_MS = 50L
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private var socket: DatagramSocket? = null
    private var address: InetAddress? = null
    private var heartbeatJob: Job? = null

    /**
     * Open the socket and start the heartbeat loop.
     */
    fun start() {
        scope.launch {
            try {
                address = InetAddress.getByName(targetIp)
                socket = DatagramSocket()
                Log.i(TAG, "Socket opened → $targetIp:$targetPort")
                startHeartbeat()
            } catch (e: Exception) {
                Log.e(TAG, "Failed to open socket", e)
            }
        }
    }

    /**
     * Send an input-state packet immediately.
     * Safe to call from the main thread.
     */
    fun sendInput(
        buttons: Int,
        lx: Short,
        ly: Short,
        rx: Short = 0,
        ry: Short = 0,
        lt: Int = 0,
        rt: Int = 0
    ) {
        val data = GamepadProtocol.buildInputPacket(buttons, lx, ly, rx, ry, lt, rt)
        scope.launch { transmit(data) }
    }

    /**
     * Stop the heartbeat and close the socket.
     */
    fun stop() {
        heartbeatJob?.cancel()
        heartbeatJob = null
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
        address = null
        Log.i(TAG, "Stopped")
    }

    /**
     * Cancel the entire coroutine scope (call from Activity onDestroy).
     */
    fun destroy() {
        stop()
        scope.cancel()
    }

    // ── internals ────────────────────────────────────────────────────────

    private fun startHeartbeat() {
        heartbeatJob = scope.launch {
            while (isActive) {
                val data = GamepadProtocol.buildHeartbeat()
                transmit(data)
                delay(HEARTBEAT_INTERVAL_MS)
            }
        }
    }

    private fun transmit(data: ByteArray) {
        try {
            val addr = address ?: return
            val sock = socket ?: return
            val packet = DatagramPacket(data, data.size, addr, targetPort)
            sock.send(packet)
        } catch (e: Exception) {
            Log.w(TAG, "transmit failed: ${e.message}")
        }
    }
}
