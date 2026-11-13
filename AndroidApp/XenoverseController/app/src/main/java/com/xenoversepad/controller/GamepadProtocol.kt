package com.xenoversepad.controller

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Builds the 22-byte XenoversePad protocol packets.
 *
 * Wire format (little-endian):
 *   Offset  Size  Field
 *   0       4     Magic "XVP1" (0x58, 0x56, 0x50, 0x31)
 *   4       4     Sequence number (uint32)
 *   8       4     Buttons bitmask (uint32) — bit 31 = heartbeat flag
 *   12      2     Left stick X (int16, −32768 … 32767)
 *   14      2     Left stick Y (int16)
 *   16      2     Right stick X (int16)
 *   18      2     Right stick Y (int16)
 *   20      1     Left trigger (uint8, 0 … 255)
 *   21      1     Right trigger (uint8, 0 … 255)
 *   ─────────────
 *   Total: 22 bytes
 */
object GamepadProtocol {

    const val PACKET_SIZE = 22
    const val PORT = 27016

    /** Magic bytes: ASCII "XVP1" */
    private val MAGIC = byteArrayOf(0x58, 0x56, 0x50, 0x31)

    // Button bit positions (Xbox 360 compatible)
    const val BTN_A: Int         = 0
    const val BTN_B: Int         = 1
    const val BTN_X: Int         = 2
    const val BTN_Y: Int         = 3
    const val BTN_LB: Int        = 4
    const val BTN_RB: Int        = 5
    const val BTN_BACK: Int      = 6
    const val BTN_START: Int     = 7
    const val BTN_DPAD_UP: Int   = 8
    const val BTN_DPAD_DOWN: Int = 9
    const val BTN_DPAD_LEFT: Int = 10
    const val BTN_DPAD_RIGHT: Int= 11
    const val BTN_LS: Int        = 12  // Left stick click
    const val BTN_RS: Int        = 13  // Right stick click
    const val BTN_GUIDE: Int     = 14

    /** Bit 31 — set on heartbeat-only packets so the receiver knows it's a keep-alive. */
    const val FLAG_HEARTBEAT: Int = 31

    private var sequence: Long = 0   // wraps as uint32

    /**
     * Build a full input-state packet.
     */
    fun buildInputPacket(
        buttons: Int,
        lx: Short,
        ly: Short,
        rx: Short,
        ry: Short,
        lt: Int,   // 0–255
        rt: Int    // 0–255
    ): ByteArray {
        val seq = nextSequence()
        return encode(seq, buttons, lx, ly, rx, ry, lt, rt)
    }

    /**
     * Build a heartbeat packet (all axes centered, no buttons, heartbeat flag set).
     */
    fun buildHeartbeat(): ByteArray {
        val seq = nextSequence()
        val buttons = 1 shl FLAG_HEARTBEAT   // only bit 31
        return encode(seq, buttons, 0, 0, 0, 0, 0, 0)
    }

    // ── internals ────────────────────────────────────────────────────────

    @Synchronized
    private fun nextSequence(): Int {
        val s = (sequence and 0xFFFFFFFFL).toInt()
        sequence++
        return s
    }

    private fun encode(
        seq: Int,
        buttons: Int,
        lx: Short,
        ly: Short,
        rx: Short,
        ry: Short,
        lt: Int,
        rt: Int
    ): ByteArray {
        val buf = ByteBuffer.allocate(PACKET_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        buf.put(MAGIC)               // 4 bytes
        buf.putInt(seq)              // 4 bytes
        buf.putInt(buttons)          // 4 bytes
        buf.putShort(lx)             // 2 bytes
        buf.putShort(ly)             // 2 bytes
        buf.putShort(rx)             // 2 bytes
        buf.putShort(ry)             // 2 bytes
        buf.put((lt and 0xFF).toByte())  // 1 byte
        buf.put((rt and 0xFF).toByte())  // 1 byte
        return buf.array()
    }
}
