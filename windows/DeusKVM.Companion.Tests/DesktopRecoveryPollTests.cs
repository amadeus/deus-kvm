using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class DesktopRecoveryPollTests
{
    [Fact]
    public void MouseFloodCannotProduceAStatusMessageForEveryPacketDuringRecovery()
    {
        var poll = new DesktopRecoveryPoll();
        var refreshes = new List<long>();
        // Five seconds at 1,000 events/second while the Mac's HID device is absent.
        for (long now = 0; now < 5000; now++)
            if (poll.ShouldRefresh(true, true, now)) refreshes.Add(now);
        Assert.Equal(20, refreshes.Count);
        Assert.Equal(0, refreshes[0]);
        Assert.All(refreshes.Zip(refreshes.Skip(1), (first, second) => (First: first, Second: second)), pair => Assert.True(pair.Second - pair.First >= 250));
    }

    [Fact]
    public void FirstProbeIsImmediateAndRetriesResumeWithoutWaitingForTheOneSecondTimer()
    {
        var poll = new DesktopRecoveryPoll();
        Assert.True(poll.ShouldRefresh(true, true, 1000));
        Assert.False(poll.ShouldRefresh(true, true, 1249));
        Assert.True(poll.ShouldRefresh(true, true, 1250));
        Assert.True(poll.ShouldRefresh(true, true, 9000));
    }

    [Fact]
    public void HealthyOrLocalInputDoesNotProbeAndNextOutageStartsImmediately()
    {
        var poll = new DesktopRecoveryPoll();
        Assert.True(poll.ShouldRefresh(true, true, 1000));
        Assert.False(poll.ShouldRefresh(false, true, 1001));
        Assert.True(poll.ShouldRefresh(true, true, 1002));
        Assert.False(poll.ShouldRefresh(true, false, 1003));
        Assert.True(poll.ShouldRefresh(true, true, 1004));
    }
}
