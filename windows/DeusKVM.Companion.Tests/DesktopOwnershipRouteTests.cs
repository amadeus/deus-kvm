using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class DesktopOwnershipRouteTests
{
    private static bool Available(long source, long stream) => source is 1 or 2 && stream == source * 10;

    [Fact]
    public void RestartSeedsNewAgentAboveOldOwnershipAndRejectsItsDelayedCallbacks()
    {
        var route = new DesktopOwnershipRoute();
        Assert.True(route.RestartAgent(1));
        Assert.True(route.TrySelect(1, 10, 50, 1, Available));
        Assert.True(route.ExpectClipboard(1, 50, 7));
        Assert.True(route.RestartAgent(2));
        Assert.Null(route.Owner);
        Assert.Equal(51, route.Generation);
        Assert.False(route.MatchesClipboard(1, 50, 7));
        Assert.False(route.TrySelect(2, 20, 900, 1, Available), "old agent cannot win even with a higher ownership counter");
        Assert.False(route.RestartAgent(1));
        Assert.False(route.RestartAgent(2));
        var replacement = new InputOwnership(route.Generation);
        replacement.Connect(2, 20); replacement.Enter(2, 20, 1, 100);
        Assert.True(route.TrySelect(2, 20, replacement.Generation, 2, Available));
        Assert.Equal(2L, route.Owner);
    }

    [Fact]
    public void DisabledSourceReplacedStreamAndDuplicateOwnerMessagesCannotAdvanceRoute()
    {
        var route = new DesktopOwnershipRoute(); route.RestartAgent(1);
        var seed = route.Generation;
        Assert.False(route.TrySelect(3, 30, seed + 1, 1, Available));
        Assert.False(route.TrySelect(1, 11, seed + 1, 1, Available));
        Assert.False(route.TrySelect(1, 10, seed + 1, 2, Available));
        Assert.Equal(seed, route.Generation);
        Assert.True(route.TrySelect(1, 10, seed + 1, 1, Available));
        Assert.False(route.TrySelect(2, 20, seed + 1, 1, Available));
        Assert.Equal(1L, route.Owner);
    }

    [Fact]
    public void ClipboardReadinessRequiresCurrentOwnerGenerationAndEndpointEpoch()
    {
        var route = new DesktopOwnershipRoute(); route.RestartAgent(1);
        route.TrySelect(1, 10, 2, 1, Available);
        Assert.False(route.MatchesClipboard(1, 2, 7), "no selection has installed a clipboard scope yet");
        Assert.True(route.ExpectClipboard(1, 2, 7));
        Assert.True(route.MatchesClipboard(1, 2, 7));
        Assert.False(route.MatchesClipboard(2, 2, 7));
        Assert.False(route.MatchesClipboard(1, 1, 7));
        Assert.True(route.ExpectClipboard(1, 2, 8));
        Assert.False(route.MatchesClipboard(1, 2, 7), "same-owner clipboard restart invalidates the previous ready callback");
        Assert.True(route.MatchesClipboard(1, 2, 8));
        Assert.True(route.TrySelect(2, 20, 3, 1, Available));
        Assert.False(route.ExpectClipboard(1, 2, 9));
        Assert.False(route.MatchesClipboard(1, 2, 8));
        Assert.False(route.MatchesClipboard(2, 3, 8), "a new owner must install its own scope");
    }

    [Fact]
    public void LosingOwnerRevokesClipboardWithoutElectingAnotherMac()
    {
        var route = new DesktopOwnershipRoute(); route.RestartAgent(1);
        route.TrySelect(1, 10, 2, 1, Available); route.ExpectClipboard(1, 2, 7);
        Assert.True(route.TrySelect(0, 0, 3, 1, Available));
        Assert.Null(route.Owner);
        Assert.False(route.MatchesClipboard(1, 2, 7));
        Assert.False(route.ExpectClipboard(2, 3, 7));
        Assert.False(route.ExpectClipboard(0, 3, 7));
    }
}
