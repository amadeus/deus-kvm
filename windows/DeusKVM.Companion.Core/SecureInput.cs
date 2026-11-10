using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

/// <summary>
/// The compact records the user desktop worker forwards to the SYSTEM secure-input worker so it
/// can inject on Windows' secure desktop (NETWORK_POINTER_PLAN.md). No sequence, capture time or
/// crypto: this is a local, SYSTEM-only, length-prefixed channel, not the network stream. Motion
/// is relative; a button is one of 1 left, 2 right, 3 middle.
/// </summary>
public static class SecureInputRecord
{
    public const byte Motion = 1, Button = 2;

    public static byte[] EncodeMotion(int dx, int dy)
    {
        var record = new byte[5];
        record[0] = Motion;
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(1), Clamp16(dx));
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(3), Clamp16(dy));
        return record;
    }

    public static byte[] EncodeButton(byte button, bool down) => [Button, button, (byte)(down ? 1 : 0)];

    private static short Clamp16(int value) => (short)(value < short.MinValue ? short.MinValue : value > short.MaxValue ? short.MaxValue : value);

    /// <summary>The length of a record given its leading type byte, or 0 if the type is unknown.</summary>
    public static int Length(byte type) => type switch { Motion => 5, Button => 3, _ => 0 };

    public static bool TryParse(ReadOnlySpan<byte> record, out byte type, out int dx, out int dy, out byte button, out bool down)
    {
        type = 0; dx = dy = 0; button = 0; down = false;
        if (record.Length < 1) return false;
        type = record[0];
        switch (type)
        {
            case Motion when record.Length == 5:
                dx = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(1));
                dy = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(3));
                return true;
            case Button when record.Length == 3 && record[1] is >= 1 and <= 3 && record[2] <= 1:
                button = record[1]; down = record[2] == 1;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Length-prefixed framing for the SYSTEM pipe: a u16 byte count then that many bytes. The
/// parser pulls complete frames out of a growing buffer, like the network client's bulk reader.
/// </summary>
public static class SecureFrame
{
    public const int MaxFrame = 4096;

    public static byte[] Wrap(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxFrame) throw new ArgumentOutOfRangeException(nameof(body));
        var frame = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)body.Length);
        body.CopyTo(frame.AsSpan(2));
        return frame;
    }

    /// <summary>
    /// Frames for a run of records: as few as possible, each at most <see cref="MaxFrame"/> and cut
    /// only between records. A truncated or unknown record ends the run (it is dropped).
    /// </summary>
    public static byte[] Batch(byte[] records, int count)
    {
        using var output = new MemoryStream(count + 16);
        int start = 0, offset = 0;
        while (offset < count)
        {
            var length = SecureInputRecord.Length(records[offset]);
            if (length == 0 || offset + length > count) break;
            if (offset + length - start > MaxFrame) { Append(start, offset); start = offset; }
            offset += length;
        }
        if (offset > start) Append(start, offset);
        return output.ToArray();

        void Append(int from, int to)
        {
            var frame = Wrap(records.AsSpan(from, to - from));
            output.Write(frame, 0, frame.Length);
        }
    }

    /// <summary>
    /// Complete frame bodies in <paramref name="buffer"/> up to <paramref name="filled"/>; the
    /// returned offset is how many bytes were consumed, which the caller compacts away.
    /// </summary>
    public static IEnumerable<byte[]> Parse(byte[] buffer, int filled, out int consumed)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (filled - offset >= 2)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
            if (length > MaxFrame) throw new InvalidDataException("Oversized secure frame");
            if (filled - offset - 2 < length) break;
            frames.Add(buffer.AsSpan(offset + 2, length).ToArray());
            offset += 2 + length;
        }
        consumed = offset;
        return frames;
    }
}
