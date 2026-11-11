using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

/// <summary>
/// The compact records the user desktop worker forwards to the SYSTEM secure-input worker, which
/// injects every network pointer step on whatever desktop has input. No
/// sequence or crypto: this is a local, length-prefixed channel, not the network stream. Motion
/// is relative; a button is one of 1 left, 2 right, 3 middle. A mark carries the QPC timestamp
/// (Stopwatch ticks, comparable across processes on one PC) at which the records after it reached
/// the desktop worker, for latency measurement; release lets go of every button the SYSTEM
/// worker holds.
/// </summary>
public static class SecureInputRecord
{
    public const byte Motion = 1, Button = 2, Mark = 3, Release = 4;
    public static readonly byte[] ReleaseRecord = [Release];

    public static byte[] EncodeMark(long ticks)
    {
        var record = new byte[9];
        record[0] = Mark;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), ticks);
        return record;
    }

    /// <summary>The timestamp of a well-formed mark record, or 0.</summary>
    public static long ReadMark(ReadOnlySpan<byte> record) =>
        record.Length == 9 && record[0] == Mark ? BinaryPrimitives.ReadInt64LittleEndian(record.Slice(1)) : 0;

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
    public static int Length(byte type) => type switch { Motion => 5, Button => 3, Mark => 9, Release => 1, _ => 0 };

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

/// <summary>
/// Latency samples in microseconds for one reporting window: exact percentiles over the window
/// (sorted at report time), at most <see cref="Capacity"/> samples kept. Safe from any thread.
/// </summary>
public sealed class LatencySamples
{
    public const int Capacity = 16384;
    private readonly object gate = new();
    private readonly int[] values = new int[Capacity];
    private int count;

    public int Count { get { lock (gate) return count; } }

    public void Add(long micros)
    {
        lock (gate) if (count < Capacity) values[count++] = (int)Math.Max(0, Math.Min(micros, int.MaxValue));
    }

    /// <summary>The window's summary; the window starts over.</summary>
    public LatencySummary Take()
    {
        int[] sorted;
        lock (gate)
        {
            sorted = new int[count];
            Array.Copy(values, sorted, count);
            count = 0;
        }
        if (sorted.Length == 0) return default;
        Array.Sort(sorted);
        int At(int percent) => sorted[(sorted.Length - 1) * percent / 100];
        return new LatencySummary(sorted.Length, At(50), At(90), At(99), sorted[sorted.Length - 1]);
    }

    public static long Micros(long ticks) => ticks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}

public readonly record struct LatencySummary(int Count, int P50, int P90, int P99, int Max)
{
    public override string ToString() => Count == 0 ? "n=0" : $"n={Count} p50Us={P50} p90Us={P90} p99Us={P99} maxUs={Max}";
}

/// <summary>
/// What the SYSTEM worker measured over one window, sent back to the service as one frame:
/// transit (desktop worker received the step → SYSTEM worker read it), SendInput itself, and the
/// total (received → injected); records it could not inject, the longest such outage, and how
/// often the input desktop changed under it.
/// </summary>
public sealed record SecureRelayStats(LatencySummary Transit, LatencySummary Inject, LatencySummary Total,
    int Failed, int LongestFailMs, int Switches, int Replayed)
{
    public const byte Tag = 0x53; // 'S'
    public const int Length = 1 + 4 * (3 * 5 + 4);

    public byte[] Encode()
    {
        var frame = new byte[Length];
        frame[0] = Tag;
        var offset = 1;
        foreach (var summary in new[] { Transit, Inject, Total })
            foreach (var value in new[] { summary.Count, summary.P50, summary.P90, summary.P99, summary.Max }) Put(value);
        Put(Failed); Put(LongestFailMs); Put(Switches); Put(Replayed);
        return frame;

        void Put(int value) { BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset), value); offset += 4; }
    }

    public static SecureRelayStats? Decode(byte[]? frame)
    {
        if (frame is not { Length: Length } || frame[0] != Tag) return null;
        var offset = 1;
        LatencySummary Summary() => new(Get(), Get(), Get(), Get(), Get());
        int Get() { var value = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(offset)); offset += 4; return value; }
        var transit = Summary();
        var inject = Summary();
        var total = Summary();
        return new SecureRelayStats(transit, inject, total, Get(), Get(), Get(), Get());
    }
}
