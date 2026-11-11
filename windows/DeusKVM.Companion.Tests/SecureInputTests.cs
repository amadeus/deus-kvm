using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class SecureInputTests
{
    [Fact]
    public void BatchCutsFramesOnlyBetweenRecordsAndUnderTheLimit()
    {
        var records = new List<byte>();
        for (var i = 0; i < 2000; i++)
            records.AddRange(i % 7 == 0 ? SecureInputRecord.EncodeButton(1, i % 2 == 0) : SecureInputRecord.EncodeMotion(i, -i));
        var bytes = records.ToArray();

        var frames = SecureFrame.Batch(bytes, bytes.Length);
        var bodies = SecureFrame.Parse(frames, frames.Length, out var consumed).ToList();

        Assert.Equal(frames.Length, consumed);
        Assert.True(bodies.Count > 1);
        Assert.All(bodies, body => Assert.InRange(body.Length, 1, SecureFrame.MaxFrame));
        Assert.Equal(bytes, bodies.SelectMany(body => body).ToArray());
        foreach (var body in bodies)
        {
            var offset = 0;
            while (offset < body.Length) offset += SecureInputRecord.Length(body[offset]);
            Assert.Equal(body.Length, offset); // no record split across frames
        }
    }

    [Fact]
    public void BatchDropsATruncatedOrUnknownTail()
    {
        var motion = SecureInputRecord.EncodeMotion(3, 4);
        var truncated = motion.Concat(new byte[] { SecureInputRecord.Motion, 1 }).ToArray();
        Assert.Equal(SecureFrame.Wrap(motion), SecureFrame.Batch(truncated, truncated.Length));
        var unknown = motion.Concat(new byte[] { 9, 9, 9 }).ToArray();
        Assert.Equal(SecureFrame.Wrap(motion), SecureFrame.Batch(unknown, unknown.Length));
        Assert.Empty(SecureFrame.Batch(motion, 0));
    }

    [Fact]
    public void MotionAndButtonRecordsRoundTrip()
    {
        var motion = SecureInputRecord.EncodeMotion(-2, 300);
        Assert.Equal(new byte[] { 1, 0xFE, 0xFF, 0x2C, 0x01 }, motion);
        Assert.True(SecureInputRecord.TryParse(motion, out var type, out var dx, out var dy, out _, out _));
        Assert.Equal(SecureInputRecord.Motion, type);
        Assert.Equal(-2, dx);
        Assert.Equal(300, dy);
        Assert.Equal(new byte[] { 1, 0xFF, 0x7F, 0x00, 0x80 }, SecureInputRecord.EncodeMotion(40000, -40000));

        var button = SecureInputRecord.EncodeButton(2, true);
        Assert.Equal(new byte[] { 2, 2, 1 }, button);
        Assert.True(SecureInputRecord.TryParse(button, out type, out _, out _, out var which, out var downState));
        Assert.Equal(SecureInputRecord.Button, type);
        Assert.Equal(2, which);
        Assert.True(downState);
        Assert.Equal(5, SecureInputRecord.Length(SecureInputRecord.Motion));
        Assert.Equal(3, SecureInputRecord.Length(SecureInputRecord.Button));
        Assert.Equal(0, SecureInputRecord.Length(9));
    }

    [Fact]
    public void MarksAndReleasesTravelInBatchesWithTheirRecords()
    {
        var mark = SecureInputRecord.EncodeMark(0x0102030405060708);
        Assert.Equal(9, mark.Length);
        Assert.Equal(0x0102030405060708, SecureInputRecord.ReadMark(mark));
        Assert.Equal(0, SecureInputRecord.ReadMark(SecureInputRecord.EncodeMotion(1, 1)));
        Assert.False(SecureInputRecord.TryParse(mark, out _, out _, out _, out _, out _)); // not an injection
        var bytes = mark.Concat(SecureInputRecord.EncodeMotion(5, 6)).Concat(SecureInputRecord.ReleaseRecord).ToArray();
        Assert.Equal(SecureFrame.Wrap(bytes), SecureFrame.Batch(bytes, bytes.Length));
    }

    [Fact]
    public void RelayStatsRoundTripAndRejectOtherFrames()
    {
        var stats = new SecureRelayStats(new LatencySummary(10, 1, 2, 3, 4), new LatencySummary(10, 5, 6, 7, 8),
            new LatencySummary(9, 9, 10, 11, 12), 13, 14, 15, 16);
        var frame = stats.Encode();
        Assert.Equal(SecureRelayStats.Length, frame.Length);
        Assert.Equal(stats, SecureRelayStats.Decode(frame));
        Assert.Null(SecureRelayStats.Decode([1]));
        Assert.Null(SecureRelayStats.Decode(null));
        frame[0] = 0;
        Assert.Null(SecureRelayStats.Decode(frame));
    }

    [Fact]
    public void LatencyPercentilesComeFromTheWindowAndResetIt()
    {
        var samples = new LatencySamples();
        Assert.Equal(default, samples.Take());
        for (var i = 100; i >= 1; i--) samples.Add(i);
        samples.Add(-5); // clamped to zero
        var summary = samples.Take();
        Assert.Equal(new LatencySummary(101, 50, 90, 99, 100), summary);
        Assert.Equal(0, samples.Count);
        Assert.Equal("n=0", default(LatencySummary).ToString());
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0 })]
    [InlineData(new byte[] { 2, 4, 1 })]
    [InlineData(new byte[] { 2, 1, 2 })]
    [InlineData(new byte[] { 9, 0, 0, 0, 0 })]
    [InlineData(new byte[0])]
    public void MalformedRecordsAreRejected(byte[] bytes)
    {
        Assert.False(SecureInputRecord.TryParse(bytes, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void FramesParseInOrderAndLeaveTheTail()
    {
        var a = SecureInputRecord.EncodeMotion(1, 2);
        var b = SecureInputRecord.EncodeButton(1, true);
        var wrapped = SecureFrame.Wrap(a).Concat(SecureFrame.Wrap(b)).ToArray();
        var buffer = new byte[64];
        // All of the first frame and all but one byte of the second: only the first comes out.
        wrapped.AsSpan(0, wrapped.Length - 1).CopyTo(buffer);
        var frames = SecureFrame.Parse(buffer, wrapped.Length - 1, out var consumed).ToList();
        Assert.Single(frames);
        Assert.Equal(a, frames[0]);
        Assert.Equal(2 + a.Length, consumed);
        // The rest of the buffer arrives: the second frame completes.
        wrapped.CopyTo(buffer, 0);
        frames = SecureFrame.Parse(buffer, wrapped.Length, out consumed).ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal(b, frames[1]);
        Assert.Equal(wrapped.Length, consumed);
    }

    [Fact]
    public void AnOversizedFrameIsRejected()
    {
        var buffer = new byte[8];
        buffer[0] = 0xFF; buffer[1] = 0xFF; // claims 65535 bytes
        Assert.Throws<InvalidDataException>(() => SecureFrame.Parse(buffer, buffer.Length, out _).ToList());
    }
}
