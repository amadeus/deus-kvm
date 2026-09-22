using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ControlArbiterTests
{
    [Fact]
    public void EnableCannotStealUntilPreviousMacAcknowledgesRelease()
    {
        var state = new ControlArbiter();
        Assert.True(state.Grant(1));
        Assert.False(state.Grant(2)); // connecting alone cannot steal
        state.Request(2);
        state.BeginRelease(40);
        Assert.Equal(1L, state.Owner);
        Assert.False(state.Grant(2));
        Assert.False(state.Acknowledge(2, 40));
        Assert.False(state.Acknowledge(1, 39));
        Assert.False(state.Grant(2));
        Assert.True(state.Acknowledge(1, 40));
        Assert.True(state.Grant(2));
        Assert.False(state.Acknowledge(1, 40));
        Assert.Equal(2L, state.Owner);
    }

    [Fact]
    public void LatestEnableWinsWhileReleaseIsPending()
    {
        var state = new ControlArbiter();
        state.Grant(1); state.Request(2); state.BeginRelease(40);
        state.Request(3); state.BeginRelease(41);
        Assert.Equal(40U, state.ReleaseEpoch);
        Assert.True(state.Acknowledge(1, 40));
        Assert.False(state.Grant(2));
        Assert.True(state.Grant(3));
    }

    [Fact]
    public void RapidReenableOfOldOwnerStillWaitsForItsRelease()
    {
        var state = new ControlArbiter();
        state.Grant(1); state.Request(2); state.BeginRelease(40);
        state.Request(1);
        Assert.False(state.Grant(1));
        Assert.True(state.Acknowledge(1, 40));
        Assert.True(state.Grant(1));
        state.Request(1);
        Assert.Null(state.Requested);
    }

    [Fact]
    public void PhysicalDisconnectReleasesOwnerButUnrelatedDisconnectDoesNot()
    {
        var state = new ControlArbiter();
        state.Grant(1); state.Request(2); state.BeginRelease(40);
        state.Disconnected(3);
        Assert.False(state.Grant(2));
        state.Disconnected(1);
        Assert.Null(state.Releasing);
        Assert.True(state.Grant(2));
    }

    [Fact]
    public void CancelledOrDisconnectedRequesterCannotHoldUpFutureSelection()
    {
        var state = new ControlArbiter();
        state.Grant(1); state.Request(2); state.BeginRelease(40);
        state.CancelRequest();
        Assert.True(state.Acknowledge(1, 40));
        Assert.Null(state.Owner);
        state.Request(2); state.Disconnected(2);
        Assert.True(state.Grant(3));
    }
}
