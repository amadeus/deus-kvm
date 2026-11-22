using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class PolishTests
{
    [Theory]
    [InlineData(-1920, -200, 1920, 1080, -960, 340)]
    [InlineData(100, 200, 101, 201, 150, 300)]
    public void CenterUsesSelectedMonitorPhysicalCoordinates(int x, int y, int w, int h, int expectedX, int expectedY)
    {
        var monitor = new MonitorInfo("selected", x, y, w, h, 144, false);
        Assert.Equal((expectedX, expectedY), monitor.Center);
    }
    [Theory]
    [InlineData(null, 3)] [InlineData("stop", 1)] [InlineData("unregister", 2)] [InlineData("files", 3)]
    public async Task RemovalStopsAtFailureAndPreservesLaterRetrySteps(string? failure, int calls)
    {
        var steps = new RemovalSteps(failure);
        if (failure is null) await RemovalWorkflow.Run(steps);
        else await Assert.ThrowsAsync<IOException>(() => RemovalWorkflow.Run(steps));
        Assert.Equal(new[] { "stop", "unregister", "files" }.Take(calls), steps.Calls);
    }
    private sealed class RemovalSteps(string? failure) : IRemovalSteps
    {
        public List<string> Calls { get; } = [];
        private Task Step(string name) { Calls.Add(name); if (failure == name) throw new IOException(name); return Task.CompletedTask; }
        public Task Stop() => Step("stop");
        public Task Unregister() => Step("unregister");
        public Task FinishFiles() => Step("files");
    }
}
