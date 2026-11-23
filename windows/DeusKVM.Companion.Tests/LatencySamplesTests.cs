using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class LatencySamplesTests
{
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
}
