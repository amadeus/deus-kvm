using DeusKVM.Companion.Core;
using Xunit;
namespace DeusKVM.Companion.Tests;

public sealed class InputStreamFeedbackTests
{
    [Fact]
    public void OldLeaveCannotBeStampedWithLaterReacquisition()
    {
        var state = new InputStreamFeedback();
        state.SetOwner(100, 1, true); state.Leave(0x123402, 1000);
        var old = state.Read(1);
        state.SetOwner(100, 2, false); state.SetOwner(101, 3, true);
        var current = state.Read(2);
        Assert.Equal(1UL, old.Ownership); Assert.Equal(100UL, old.Capture); Assert.NotEqual(0U, old.Leave);
        Assert.Equal(3UL, current.Ownership); Assert.Equal(101UL, current.Capture); Assert.Equal(0U, current.Leave);
        Assert.Equal(0, current.LeaveSerial);
    }
    [Fact]
    public void NonOwnerAndExpiredLeaveNeverEnterAcknowledgement()
    {
        var state = new InputStreamFeedback(); state.SetOwner(100, 1, false); state.Leave(2, 100);
        Assert.Equal(0U, state.Read(1).Leave);
        state.SetOwner(100, 2, true); state.Leave(2, 100);
        Assert.Equal(2U, state.Read(99).Leave); Assert.Equal(0U, state.Read(100).Leave);
    }
}
