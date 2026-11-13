using System.Buffers.Binary;

namespace PCReceiver;

/// <summary>
/// Binary wire format shared by the PC receiver and the test sender.
/// All fields are little-endian. See PROTOCOL.md for the contract used by the Android app.
/// </summary>
public static class PacketProtocol
{
    /// <summary>Size in bytes of an input packet (phone -> PC).</summary>
    public const int InputPacketSize = 22;

    /// <summary>Size in bytes of a rumble packet (PC -> phone).</summary>
    public const int RumblePacketSize = 6;

    /// <summary>Expected interval between heartbeat packets, in milliseconds.</summary>
    public const int HeartbeatIntervalMs = 50;

    /// <summary>Input channel is considered degraded after this many milliseconds without a heartbeat.</summary>
    public const int HeartbeatTimeoutMs = 500;

    // Buttons bitmask (32 bit field at offset 8).
    public const uint ButtonA = 1u << 0;
    public const uint ButtonB = 1u << 1;
    public const uint ButtonX = 1u << 2;
    public const uint ButtonY = 1u << 3;
    public const uint ButtonLB = 1u << 4;
    public const uint ButtonRB = 1u << 5;
    public const uint ButtonBack = 1u << 6;
    public const uint ButtonStart = 1u << 7;

    // D-pad is carried in the same bitmask (no separate byte in the 22 byte layout).
    public const uint ButtonDPadUp = 1u << 8;
    public const uint ButtonDPadDown = 1u << 9;
    public const uint ButtonDPadLeft = 1u << 10;
    public const uint ButtonDPadRight = 1u << 11;

    /// <summary>Set on heartbeat packets: sequence number only, controller state is not changed.</summary>
    public const uint ButtonHeartbeat = 1u << 31;

    /// <summary>Magic bytes of an input packet: ASCII 'XVP1'.</summary>
    public static ReadOnlySpan<byte> InputMagic => "XVP1"u8;

    /// <summary>Magic bytes of a rumble packet: ASCII 'XVR1'.</summary>
    public static ReadOnlySpan<byte> RumbleMagic => "XVR1"u8;

    /// <summary>
    /// True when the buffer starts with the given magic. Longer buffers are accepted,
    /// the trailing bytes are ignored by the caller.
    /// </summary>
    public static bool HasMagic(ReadOnlySpan<byte> data, ReadOnlySpan<byte> magic)
        => data.Length >= magic.Length && data[..magic.Length].SequenceEqual(magic);
}

/// <summary>Input state packet sent from the phone (or the test sender) to the PC.</summary>
public struct InputPacket
{
    public uint Sequence;
    public uint Buttons;
    public short LeftStickX;
    public short LeftStickY;
    public short RightStickX;
    public short RightStickY;
    public byte LeftTrigger;
    public byte RightTrigger;

    /// <summary>True for heartbeat packets, which carry a sequence number but no state change.</summary>
    public bool IsHeartbeat => (Buttons & PacketProtocol.ButtonHeartbeat) != 0;

    public byte[] Serialize()
    {
        var buffer = new byte[PacketProtocol.InputPacketSize];

        PacketProtocol.InputMagic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(12, 2), LeftStickX);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(14, 2), LeftStickY);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(16, 2), RightStickX);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(18, 2), RightStickY);
        buffer[20] = LeftTrigger;
        buffer[21] = RightTrigger;

        return buffer;
    }

    /// <summary>
    /// Parses an input packet. Returns false when the buffer is too short or the magic does not match.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out InputPacket packet)
    {
        packet = default;

        if (data.Length < PacketProtocol.InputPacketSize)
        {
            return false;
        }

        if (!PacketProtocol.HasMagic(data, PacketProtocol.InputMagic))
        {
            return false;
        }

        packet.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        packet.Buttons = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
        packet.LeftStickX = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(12, 2));
        packet.LeftStickY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2));
        packet.RightStickX = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(16, 2));
        packet.RightStickY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(18, 2));
        packet.LeftTrigger = data[20];
        packet.RightTrigger = data[21];

        return true;
    }
}

/// <summary>Rumble packet sent from the PC back to the phone (or the test sender).</summary>
public struct RumblePacket
{
    public byte LeftMotor;
    public byte RightMotor;

    public byte[] Serialize()
    {
        var buffer = new byte[PacketProtocol.RumblePacketSize];

        PacketProtocol.RumbleMagic.CopyTo(buffer);
        buffer[4] = LeftMotor;
        buffer[5] = RightMotor;

        return buffer;
    }

    /// <summary>
    /// Parses a rumble packet. Returns false when the buffer is too short or the magic does not match.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out RumblePacket packet)
    {
        packet = default;

        if (data.Length < PacketProtocol.RumblePacketSize)
        {
            return false;
        }

        if (!PacketProtocol.HasMagic(data, PacketProtocol.RumbleMagic))
        {
            return false;
        }

        packet.LeftMotor = data[4];
        packet.RightMotor = data[5];

        return true;
    }
}
