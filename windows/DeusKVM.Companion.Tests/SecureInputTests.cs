using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class SecureInputTests
{
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
