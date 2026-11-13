using System.Buffers.Binary;
using System.Diagnostics;

namespace DeusKVM.Companion.Core;

/// <summary>One record of the Mac's pointer stream: motion, a button, or a keep-alive ping.</summary>
public readonly record struct InputEvent(byte Type, uint Sequence, uint Captured, int Dx = 0, int Dy = 0, byte Button = 0, bool Down = false)
{
    public bool IsMotion => Type == InputRecord.Motion;
    public bool IsButton => Type == InputRecord.Button;
}

/// <summary>
/// The wire records of the pointer stream, little-endian. The Mac
/// sends each one sealed and prefixed by its sealed length; the PC answers every record with a
/// sealed twelve-byte acknowledgement per read carrying the last sequence, its own clock and
/// flags: bit 0 says the PC cannot inject right now, so the Mac should use the Bluetooth mouse
/// until it clears. Bit 1 is an edge-return request, the same as the Bluetooth leave message:
/// bits 2-3 the edge, 8-15 the switch id, 16-31 the fraction along the edge.
/// </summary>
public static class InputRecord
{
    public const byte Motion = 1, Button = 2, Ping = 3;
    public const int AckLength = 12, SealedAckLength = AckLength + 16;
    /// <summary>
    /// Bit 4: the acknowledgement left later than its record's read (a paced ack), so the Mac
    /// keeps it out of its round-trip figures.
    /// </summary>
    public const uint AckBlocked = 1, AckLeave = 2, AckDeferred = 16;

    public static uint LeaveFlags(byte switchId, byte edge, ushort fraction) =>
        AckLeave | (uint)(edge & 3) << 2 | (uint)switchId << 8 | (uint)fraction << 16;

    public static bool TryParse(ReadOnlySpan<byte> plain, out InputEvent record)
    {
        record = default;
        if (plain.Length < 9) return false;
        var type = plain[0];
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(1));
        var captured = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(5));
        switch (type)
        {
            case Motion when plain.Length == 13:
                record = new InputEvent(type, sequence, captured,
                    BinaryPrimitives.ReadInt16LittleEndian(plain.Slice(9)), BinaryPrimitives.ReadInt16LittleEndian(plain.Slice(11)));
                return true;
            case Button when plain.Length == 11 && plain[9] is >= 1 and <= 3 && plain[10] <= 1:
                record = new InputEvent(type, sequence, captured, Button: plain[9], Down: plain[10] == 1);
                return true;
            case Ping when plain.Length == 9:
                record = new InputEvent(type, sequence, captured);
                return true;
            default:
                return false;
        }
    }

    public static byte[] Ack(uint sequence, uint received, bool blocked, uint leave = 0, bool deferred = false)
    {
        var ack = new byte[AckLength];
        BinaryPrimitives.WriteUInt32LittleEndian(ack, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4), received);
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(8),
            (blocked ? AckBlocked : 0) | (deferred ? AckDeferred : 0) | (leave & ~(AckBlocked | AckDeferred)));
        return ack;
    }

    /// <summary>This PC's clock in 100 µs ticks, wrapping at 32 bits; only its differences matter.</summary>
    public static uint Clock()
    {
        var ticks = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;
        return unchecked((uint)(ticks / frequency * 10000 + ticks % frequency * 10000 / frequency));
    }
}

/// <summary>
/// When the PC acknowledges the pointer stream. Each acknowledgement wakes a thread on the Mac,
/// so routine ones (cumulative) go out at most every <see cref="IntervalMs"/>; one with news
/// goes out at once: a ping (a round-trip probe), a change in whether the PC can inject, or a
/// new edge-return request. A record left unacknowledged is acknowledged by its deadline.
/// </summary>
public sealed class AckPacer
{
    public const int IntervalMs = 16;
    private readonly long interval;
    private long lastSent;
    private bool sentOnce, reportedBlocked;
    private long reportedLeave;

    public AckPacer(long ticksPerSecond) => interval = ticksPerSecond * IntervalMs / 1000;

    /// <summary>The newest record read and not yet acknowledged, with its read clock.</summary>
    public uint? Pending { get; private set; }
    public uint PendingReceived { get; private set; }
    /// <summary>The newest sequence acknowledged; a repeat carries a later request.</summary>
    public uint? LastAcked { get; private set; }

    /// <summary>
    /// A read ended with <paramref name="sequence"/>; true if it is acknowledged now.
    /// <paramref name="leave"/> identifies the edge-return request in force (0 for none).
    /// </summary>
    public bool Read(uint sequence, uint received, bool ping, bool blocked, long leave, long now)
    {
        Pending = sequence;
        PendingReceived = received;
        return !sentOnce || now - lastSent >= interval || ping || blocked != reportedBlocked || Fresh(leave);
    }

    /// <summary>A request raised since the last acknowledgement goes out without waiting.</summary>
    public bool Fresh(long leave) => leave != 0 && leave != reportedLeave;

    /// <summary>When the pending record must be acknowledged, in the same ticks; null if none.</summary>
    public long? Deadline => Pending is null ? null : lastSent + interval;

    public void Sent(uint sequence, bool blocked, long leave, long now)
    {
        sentOnce = true;
        lastSent = now;
        reportedBlocked = blocked;
        reportedLeave = leave;
        LastAcked = sequence;
        if (Pending == sequence) Pending = null;
    }
}

/// <summary>
/// What arrived on the PC and how evenly: the mirror of the Mac's gap statistics, so one
/// problem report shows both ends. Gaps over 100 ms are a lifted finger, not jitter.
/// </summary>
public sealed class InputArrivalStats
{
    public const double StrokeGapMs = 100;
    public int Received { get; private set; }
    public int Injected { get; set; }
    public int Discarded { get; set; }
    public double GapMaxMs { get; private set; }
    private double gapSum, last = -1;
    private int gaps;

    public void Observe(double nowMs)
    {
        Received++;
        if (last >= 0 && nowMs - last <= StrokeGapMs)
        {
            var gap = nowMs - last;
            gaps++; gapSum += gap; GapMaxMs = Math.Max(GapMaxMs, gap);
        }
        last = nowMs;
    }

    public string Summary =>
        $"received={Received} injected={Injected} discarded={Discarded} gapAvgMs={(gaps == 0 ? 0 : gapSum / gaps):0.0} gapMaxMs={GapMaxMs:0.0}";

    public void Reset() { Received = Injected = Discarded = gaps = 0; gapSum = GapMaxMs = 0; last = -1; }
}
