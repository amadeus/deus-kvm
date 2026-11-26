using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class InputOwnershipTests
{
    private static InputOwnership Ready()
    {
        var state = new InputOwnership();
        state.Connect(1, 10); state.Connect(2, 20);
        return state;
    }

    private static InputEvent Motion(ulong capture, int dx = 1) => new(InputRecord.Motion, 1, 0, Dx: dx, Capture: capture);
    private static InputEvent Keys(ulong capture, byte modifiers = 0, params byte[] keys) =>
        new(InputRecord.Keyboard, 1, 0, Modifiers: modifiers, Keys: keys, Capture: capture);

    [Fact]
    public void LocalActivityCannotAcquireAndBothRemoteSourcesRemainReady()
    {
        var state = Ready();
        Assert.Equal(InputDisposition.Ignore, state.Observe(2, 20, Motion(200)).Disposition);
        state.Enter(1, 10, 1, 100);
        state.Enter(2, 20, 2, 200);
        Assert.True(state.IsRemote(1, 10));
        Assert.True(state.IsRemote(2, 20));
        var first = state.Observe(1, 10, Motion(100));
        Assert.True(first.OwnershipChanged);
        Assert.Equal(1L, state.Owner);
        Assert.Equal(InputDisposition.Forward, first.Disposition);
        Assert.True(state.Observe(2, 20, Motion(200)).OwnershipChanged);
        Assert.Equal(2L, state.Owner);
    }

    [Fact]
    public void KeepaliveEmptyMotionAndUnchangedStateNeverAcquire()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100);
        state.Observe(1, 10, Keys(100, 0, 4));
        state.Enter(2, 20, 2, 200);
        var generation = state.Generation;
        foreach (var record in new[] { new InputEvent(InputRecord.Ping, 1, 0), Motion(100, 0), Keys(100, 0, 4) })
            Assert.Equal(InputDisposition.Passive, state.Observe(1, 10, record).Disposition);
        Assert.Equal(2L, state.Owner);
        Assert.Equal(generation, state.Generation);
    }

    [Fact]
    public void PhysicalReleaseAcquiresButSyntheticResetDoesNot()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100);
        state.Observe(1, 10, Keys(100, 0, 4));
        state.Enter(2, 20, 2, 200);
        Assert.True(state.Observe(1, 10, Keys(100)).OwnershipChanged);
        state.Observe(2, 20, Motion(200));
        var reset = state.Observe(1, 10, new InputEvent(InputRecord.Reset, 2, 0, Capture: 100));
        Assert.Equal(InputDisposition.Passive, reset.Disposition);
        Assert.Equal(2L, state.Owner);
        Assert.True(state.IsRemote(1, 10)); // EXIT controls mode; reset only releases input.
    }

    [Fact]
    public void TakeoverRestoresModifiersWithoutReplayingHeldKeysOrButtons()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100);
        state.Observe(1, 10, Keys(100, 2, 0x39)); // Shift + Caps Lock
        state.Observe(1, 10, new InputEvent(InputRecord.Button, 2, 0, Button: 1, Down: true, Capture: 100));
        state.Enter(2, 20, 2, 200);
        var motion = state.Observe(1, 10, Motion(100));
        Assert.Equal((byte)2, motion.RestoreModifiers);
        Assert.True(motion.Record.IsMotion); // No synthesized mouse down.
        var key = state.Observe(1, 10, Keys(100, 2, 0x39, 4));
        Assert.Equal(new byte[] { 4 }, key.Record.Keys); // No Caps Lock toggle replay.
        state.Observe(1, 10, Keys(100, 2, 4));
        Assert.Equal(new byte[] { 4, 0x39 }, state.Observe(1, 10, Keys(100, 2, 4, 0x39)).Record.Keys);
    }

    [Fact]
    public void SupersededStreamAndCaptureCannotResetOrReclaim()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100);
        state.Enter(1, 10, 2, 101);
        Assert.Equal(InputDisposition.Ignore, state.Observe(1, 10, new InputEvent(InputRecord.Reset, 1, 0, Capture: 100)).Disposition);
        Assert.Equal(InputDisposition.Ignore, state.Observe(1, 10, Motion(100)).Disposition);
        Assert.False(state.Exit(1, 10, 1, 100));
        state.Connect(1, 11);
        state.Enter(1, 11, 3, 102);
        Assert.False(state.Disconnect(1, 10));
        Assert.Equal(InputDisposition.Ignore, state.Observe(1, 10, Motion(102)).Disposition);
        Assert.Equal(1L, state.Owner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetAndExitKeepClipboardPartnerInEitherArrivalOrder(bool resetFirst)
    {
        var state = Ready(); state.Enter(1, 10, 1, 100);
        var generation = state.Generation;
        var reset = new InputEvent(InputRecord.Reset, 1, 0, Capture: 100);
        if (resetFirst) state.Observe(1, 10, reset);
        Assert.True(state.Exit(1, 10, 1, 100));
        if (!resetFirst) state.Observe(1, 10, reset);
        Assert.Equal(1L, state.Owner); Assert.Equal(generation, state.Generation);
        Assert.False(state.IsRemote(1, 10));
        Assert.Equal(InputDisposition.Ignore, state.Observe(1, 10, Motion(100)).Disposition);
    }

    [Fact]
    public void OwnerGenerationAdvancesAndUnrelatedExitCannotReleaseOwner()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100);
        var first = state.Generation;
        state.Enter(2, 20, 2, 200);
        Assert.True(state.Generation > first);
        var second = state.Generation;
        Assert.True(state.Exit(1, 10, 1, 100));
        Assert.Equal(second, state.Generation);
        Assert.Equal(2L, state.Owner);
        Assert.True(state.Disconnect(2, 20));
        Assert.Null(state.Owner);
        Assert.True(state.Generation > second);
        Assert.False(state.Enter(1, 10, 1, 0));
    }

    [Fact]
    public void BackgroundResumeDoesNotTakeOverAndFreshCaptureInvalidatesPendingWork()
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100); state.Enter(2, 20, 2, 200);
        var generation = state.Generation;
        Assert.True(state.Resume(1, 10, 1, 100));
        Assert.Equal(2L, state.Owner);
        Assert.Equal(generation, state.Generation);
        state.Observe(1, 10, Motion(100));
        var reclaimed = state.Generation;
        state.Enter(1, 10, 2, 101);
        Assert.True(state.Generation > reclaimed);
        Assert.False(state.Resume(1, 10, 1, 100));
    }

    [Theory]
    [InlineData(InputRecord.Button)]
    [InlineData(InputRecord.Consumer)]
    [InlineData(InputRecord.Scroll)]
    [InlineData(InputRecord.Keyboard)]
    public void FirstActualInputAcquiresBeforeItIsForwarded(byte kind)
    {
        var state = Ready();
        state.Enter(1, 10, 1, 100); state.Enter(2, 20, 2, 200);
        var record = new InputEvent(kind, 1, 0, Button: 1, Down: true, Usage: 0xE9, Wheel: 1, Keys: [0x19], Capture: 100);
        var result = state.Observe(1, 10, record);
        Assert.True(result.OwnershipChanged);
        Assert.Equal(InputDisposition.Forward, result.Disposition);
        Assert.Equal(1L, state.Owner);
        Assert.Equal(state.Generation, result.Generation);
    }
}
