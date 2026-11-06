using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class StateReporterTests
{
    [Fact]
    public void UnchangedStateIsForwardedOnlyAfterTheRefreshInterval()
    {
        var reporter = new StateReporter(30000);
        Assert.True(reporter.ShouldSend([0, 0, 1], 1000));
        Assert.False(reporter.ShouldSend([0, 0, 1], 2000));
        Assert.False(reporter.ShouldSend([0, 0, 1], 30999));
        Assert.True(reporter.ShouldSend([0, 0, 1], 31000));
        Assert.True(reporter.ShouldSend([3, 0, 0], 31001), "a change goes out at once");
        Assert.True(reporter.ShouldSend([0, 0, 1], 31002));
    }

    [Fact]
    public void ResetSendsTheNextStateAndCallersCannotMutateTheCopy()
    {
        var reporter = new StateReporter();
        byte[] state = [4, 2, 0];
        Assert.True(reporter.ShouldSend(state, 0));
        state[0] = 0;
        Assert.True(reporter.ShouldSend(state, 1), "the stored copy is independent of the caller's array");
        Assert.False(reporter.ShouldSend([0, 2, 0], 2));
        reporter.Reset();
        Assert.True(reporter.ShouldSend([0, 2, 0], 3));
    }

    [Theory]
    [InlineData(false, 0, 0, false)]
    [InlineData(true, 0, 0, true)]
    [InlineData(false, 1, 0, true)]
    [InlineData(false, 0, 1, true)]
    public void EdgeHealthIsWrittenOnlyWhileTheMacOwnsControlOrAReturnWasDecided(bool active, int leaves, int rejected, bool expected) =>
        Assert.Equal(expected, EdgeHealth.ShouldWrite(active, leaves, rejected));
}
