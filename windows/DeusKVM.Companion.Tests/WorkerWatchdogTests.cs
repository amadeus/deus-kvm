using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class WorkerWatchdogTests
{
    [Fact]
    public void HeartbeatsCannotHideHungDiscovery()
    {
        var clock = new Clock();
        var watchdog = new WorkerWatchdog(clock);
        watchdog.Observe("Discovering");
        for (var i = 0; i < 7; i++) { clock.Advance(10); watchdog.Observe("Discovering"); }
        Assert.True(watchdog.IsExpired());
    }

    [Theory]
    [InlineData("Discovered")]
    [InlineData("Waiting")]
    [InlineData("Connected")]
    public void HealthyIdleConnectionSurvivesWhileDeadWorkerExpires(string state)
    {
        var clock = new Clock();
        var watchdog = new WorkerWatchdog(clock);
        watchdog.Observe(state);
        for (var i = 0; i < 60; i++) { clock.Advance(10); watchdog.Observe(state); }
        Assert.False(watchdog.IsExpired());
        clock.Advance(61);
        Assert.True(watchdog.IsExpired());
    }

    [Fact]
    public void NewOperationGetsItsOwnDeadline()
    {
        var clock = new Clock();
        var watchdog = new WorkerWatchdog(clock);
        watchdog.Observe("Opening");
        clock.Advance(50);
        watchdog.Observe("Discovering");
        clock.Advance(20);
        Assert.False(watchdog.IsExpired());
        clock.Advance(41);
        Assert.True(watchdog.IsExpired());
    }

    private sealed class Clock : TimeProvider
    {
        private long seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => seconds;
        public void Advance(int value) => seconds += value;
    }
}
