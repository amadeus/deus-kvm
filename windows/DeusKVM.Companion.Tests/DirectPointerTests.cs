using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class DirectPointerTests
{
    [Fact]
    public void MacWireVectorPreservesCoalescedTravel()
    {
        var sample = PointerSample.Parse([7, 0, 244, 1, 0, 0, 6, 255, 255, 0, 0, 0, 1]);
        var state = new DirectPointerState(); state.Begin(7);
        Assert.True(state.Move(sample, out var dx, out var dy));
        Assert.Equal(500, dx); Assert.Equal(-250, dy);
    }
    [Fact]
    public void SubpixelMotionAccumulatesAndWrapsWithoutInt8Clipping()
    {
        var state = new DirectPointerState(); state.Begin(1);
        for (byte serial = 1; serial <= 4; serial++)
        {
            Assert.True(state.Move(new(1, serial * 64, -serial * 64, 0, 0, 0, serial), out var dx, out var dy));
            Assert.Equal(serial == 4 ? 1 : 0, dx); Assert.Equal(serial == 4 ? -1 : 0, dy);
        }
        state.Begin(1);
        var total = 0;
        for (var index = 1; index <= 300; index++)
        {
            total = unchecked(total + 100_000_000);
            Assert.True(state.Move(new(1, total, -total, 0, 0, 0, unchecked((byte)index)), out var dx, out var dy));
            Assert.Equal(390625, dx); Assert.Equal(-390625, dy);
        }
    }
    [Fact]
    public void ExitNewSessionAndWrongSequenceRejectStaleInput()
    {
        var state = new DirectPointerState();
        var sample = new PointerSample(1, 256, 0, 1, 0, 0, 1);
        Assert.False(state.Move(sample, out _, out _));
        state.Begin(1);
        Assert.False(state.Move(sample with { Serial = 2 }, out _, out _));
        Assert.True(state.Move(sample, out _, out _));
        Assert.False(state.Move(sample, out _, out _));
        state.End(); Assert.False(state.Move(sample with { Serial = 2 }, out _, out _));
        state.Begin(2); Assert.False(state.Move(sample, out _, out _));
        Assert.True(state.Move(sample with { Id = 2 }, out var dx, out _));
        Assert.Equal(1, dx);
    }
    [Theory]
    [InlineData(-1920, -1920, 5760, 0)]
    [InlineData(3839, -1920, 5760, 65535)]
    [InlineData(-2000, -1920, 5760, 0)]
    [InlineData(5000, -1920, 5760, 65535)]
    public void AbsoluteCoordinatesCoverNegativeOriginVirtualDesktop(int point, int origin, int size, int expected)
        => Assert.Equal(expected, DirectPointerState.Normalize(point, origin, size));
    [Fact]
    public void RejectInvalidMousePayload()
    {
        Assert.Throws<InvalidDataException>(() => PointerSample.Parse(new byte[12]));
        var bytes = new byte[13]; bytes[9] = 8;
        Assert.Throws<InvalidDataException>(() => PointerSample.Parse(bytes));
    }
}
