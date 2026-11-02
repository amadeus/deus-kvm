using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class EdgeConfirmTests
{
    // A 4K display at 125%: 3840 physical pixels wide, 3072 when scaled.
    private static readonly MonitorInfo Live = new(@"\\.\DISPLAY1", 0, 0, 3840, 2160, 120, true);
    private static readonly EdgeConfiguration RightEdge = new(1, @"\\.\DISPLAY1");

    [Theory]
    [InlineData(1920, 1080)] // a fallback mode cached while the display slept: returns at 50%
    [InlineData(3072, 1728)] // scaled bounds against a physical cursor: returns at 80%
    public void StaleOrScaledLayoutCannotReturnPartwayAcross(int width, int height)
    {
        var cached = Live with { W = width, H = height };
        var x = width - 1;
        Assert.True(EdgeDetector.Observe(cached, [cached], RightEdge, x, 500, 4, 0, false, false), "the cached test alone would return");
        Assert.Equal(EdgeVerdict.StaleLayout, EdgeDetector.Confirm(cached, Live, [cached], RightEdge, x, 500, 4, 0));
    }

    [Fact]
    public void TheRealEdgeStillReturns()
    {
        Assert.Equal(EdgeVerdict.Leave, EdgeDetector.Confirm(Live, Live, [Live], RightEdge, 3839, 500, 4, 0));
        Assert.Equal(EdgeVerdict.Leave, EdgeDetector.Confirm(Live, Live with { Dpi = 96 }, [Live], RightEdge, 3839, 500, 1, 0));
    }

    [Fact]
    public void LiveLayoutAlsoBlocksAnAdjacentMonitorAndUnknownMonitors()
    {
        var neighbour = new MonitorInfo(@"\\.\DISPLAY2", 3840, 0, 1920, 1080, 96, false);
        Assert.Equal(EdgeVerdict.StaleLayout, EdgeDetector.Confirm(Live, Live, [Live, neighbour], RightEdge, 3839, 500, 4, 0));
        Assert.Equal(EdgeVerdict.OtherMonitor, EdgeDetector.Confirm(Live, neighbour, [Live], RightEdge, 3839, 500, 4, 0));
        Assert.Equal(EdgeVerdict.Unavailable, EdgeDetector.Confirm(Live, null, [Live], RightEdge, 3839, 500, 4, 0));
    }

    [Fact]
    public void HistoryIsBoundedTruncatedAndOldestFirst()
    {
        var clock = new DateTime(2026, 11, 1, 21, 34, 11, 617, DateTimeKind.Utc);
        var history = new DiagnosticHistory(3, 10, () => clock);
        foreach (var entry in new[] { "one", "two", "three", "four", "a very long entry" }) history.Add(entry);
        Assert.Equal(["21:34:11.617Z three", "21:34:11.617Z four", "21:34:11.617Z a very lon"], history.Lines());
    }
}
