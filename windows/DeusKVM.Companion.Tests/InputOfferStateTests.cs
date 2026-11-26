using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class InputOfferStateTests
{
    private static FileNetworkOffer Offer(int port = 1234) => new(["192.168.1.2"], port, Convert.ToBase64String(new byte[32]));

    [Fact]
    public void HelperReplayDoesNotReplaceAConnectedStreamOrPhysicalSnapshot()
    {
        var state = new InputOfferState();
        Assert.Equal(InputOfferChange.Replace, state.Receive(Offer(), false));
        Assert.Equal(InputOfferChange.Unchanged, state.Receive(Offer(), true));
    }

    [Fact]
    public void ChangedLiveOfferRequiresLocalRecoveryAndDoesNotCommitReplacement()
    {
        var state = new InputOfferState(); state.Receive(Offer(), false);
        Assert.Equal(InputOfferChange.Reconnect, state.Receive(Offer(4321), true));
        Assert.Equal(InputOfferChange.Unchanged, state.Receive(Offer(), true));
        Assert.Equal(InputOfferChange.Replace, state.Receive(Offer(4321), false));
    }

    [Fact]
    public void NewHelperAcceptsOfferBeforeResumingCaptureMetadata()
    {
        var state = new InputOfferState();
        Assert.Equal(InputOfferChange.Replace, state.Receive(Offer(), true));
        Assert.Equal(InputOfferChange.Unchanged, state.Receive(Offer(), true));
    }
}
