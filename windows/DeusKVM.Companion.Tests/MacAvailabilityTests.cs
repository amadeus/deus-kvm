using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class MacAvailabilityTests
{
    [Fact]
    public void PausingReleasesPriorityAndReenableJoinsBehindTheOtherMac()
    {
        var policy = new MacPriority(requireAvailability: true); policy.CompleteEnumeration();
        policy.Observe("a", "A", "01", true); policy.Observe("b", "B", "02", true);
        var a = policy.Connections[0]; var b = policy.Connections[1];
        Assert.Null(policy.Next);
        policy.SetAvailable(a, true); policy.SetAvailable(b, true);
        Assert.True(policy.Activate(a));
        policy.SetAvailable(a, false);
        Assert.Equal(b, policy.Next);
        Assert.True(policy.Activate(b));
        policy.SetAvailable(a, true);
        Assert.Equal(b, policy.Next);
        policy.SetAvailable(b, false);
        Assert.Equal(a, policy.Next);
        Assert.Equal(2, policy.Connections.Length); // no Bluetooth disconnect needed
    }

    [Fact]
    public void EpochAndAvailabilityAreEchoedAndDisabledMacCannotReceiveAGrant()
    {
        var state = new MacAvailability();
        state.Hello(true, 10, true);
        Assert.Equal(new byte[] { 10, 0, 0, 0, 1 }, state.Selection(true));
        state.Receive([11, 0, 0, 0, 0]);
        Assert.False(state.Available);
        Assert.Equal(new byte[] { 11, 0, 0, 0, 0 }, state.Selection(true));
        state.Receive([12, 0, 0, 0, 1]);
        Assert.True(state.Available);
        Assert.Equal(new byte[] { 12, 0, 0, 0, 1 }, state.Selection(true));
        state.Receive([13, 0, 0, 0, 2]);
        Assert.True(state.Available);
        Assert.True(state.Requested);
        Assert.Equal(new byte[] { 13, 0, 0, 0, 1 }, state.Selection(true));
        Assert.Throws<InvalidDataException>(() => state.Receive([1]));
        Assert.Throws<InvalidDataException>(() => state.Receive([12, 0, 0, 0, 3]));
    }

    [Fact]
    public void StaleAvailabilityCannotChangeAReplacementConnection()
    {
        var policy = new MacPriority(requireAvailability: true); policy.CompleteEnumeration();
        policy.Observe("a", "A", "01", true); var old = policy.Connections[0];
        policy.Remove("a"); policy.Observe("a", "A", "01", true);
        policy.SetAvailable(old, true);
        Assert.Null(policy.Next);
        policy.SetAvailable(policy.Connections[0], true);
        Assert.NotNull(policy.Next);
    }
}
