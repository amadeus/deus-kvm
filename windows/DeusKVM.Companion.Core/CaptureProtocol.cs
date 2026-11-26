using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

public readonly record struct CaptureEntry(byte SwitchId, byte Edge, ushort Fraction, bool Center, ulong Capture);

/// <summary>Capture-scoped control records. Exact lengths reject legacy or partial messages.</summary>
public static class CaptureProtocol
{
    public static bool TryReadEnter(Protocol.Message type, ReadOnlySpan<byte> payload, out CaptureEntry entry)
    {
        entry = default;
        var center = type == Protocol.Message.EnterCenter;
        if (type != Protocol.Message.Enter && !center) return false;
        var prefix = center ? 2 : 4;
        if (payload.Length != prefix + 8 || payload[1] >= 4) return false;
        var capture = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(prefix));
        if (capture == 0) return false;
        entry = new CaptureEntry(payload[0], payload[1], center ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2)),
            center, capture);
        return true;
    }

    public static bool TryReadExit(ReadOnlySpan<byte> payload, out byte switchId, out ulong capture)
    {
        switchId = 0; capture = 0;
        if (payload.Length != 9) return false;
        capture = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(1));
        if (capture == 0) return false;
        switchId = payload[0];
        return true;
    }

    public static bool TryReadResume(ReadOnlySpan<byte> payload, out byte switchId, out byte edge, out ulong capture)
    {
        switchId = edge = 0; capture = 0;
        if (payload.Length != 10 || payload[1] >= 4) return false;
        capture = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(2));
        if (capture == 0) return false;
        switchId = payload[0]; edge = payload[1];
        return true;
    }

    public static byte[] Acknowledge(byte switchId, bool ok, int x, int y, byte blind, ulong capture)
    {
        if (capture == 0) throw new ArgumentOutOfRangeException(nameof(capture));
        var payload = new byte[15];
        payload[0] = switchId; payload[1] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), (short)RuntimeCompat.Clamp(x, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), (short)RuntimeCompat.Clamp(y, short.MinValue, short.MaxValue));
        payload[6] = blind;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(7), capture);
        return payload;
    }
}
