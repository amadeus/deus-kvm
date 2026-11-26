using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class DesktopInputReadinessTests
{
    private static readonly InputDesktopAccess LockedDefault = new(true, true, false, SessionLockState.Locked);
    private static readonly InputDesktopAccess UnlockedDefault = new(true, true, false, SessionLockState.Unlocked);
    private static readonly InputDesktopAccess Winlogon = new(true, false, true, SessionLockState.Unknown);

    [Fact]
    public void LockedDefaultPlacesAndAcknowledgesWithoutClipboard()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0;
        state.Begin(1, 10, () => acknowledged++);
        Assert.True(state.Complete(1, 10, LockedDefault, () => true));
        Assert.True(state.ReadyWithoutClipboard);
        Assert.Equal(1, acknowledged);
        Assert.True(state.Complete(1, 10, LockedDefault, () => true));
        Assert.Equal(1, acknowledged);
    }

    [Fact]
    public void UnavailableAttachmentRetriesIntoWinlogonWithoutLosingEntry()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0;
        state.Begin(1, 10, () => acknowledged++);
        Assert.False(state.Complete(1, 10, default, () => throw new InvalidOperationException()));
        Assert.Equal(1, state.Count);
        Assert.True(state.Complete(1, 10, Winlogon, () => true));
        Assert.Equal(1, acknowledged);
    }

    [Fact]
    public void UnlockRequiresFreshClipboardBeforeInputOrRepeat()
    {
        var state = new DesktopInputReadiness(); var inputs = 0;
        state.Begin(1, 10);
        Assert.True(state.ClipboardInstalled(1, 10));
        Assert.True(state.Complete(1, 10, LockedDefault, () => true));
        Assert.True(state.NeedsRenewal(UnlockedDefault));
        state.Renew(1, 11);
        state.Submit(1, 11, () => inputs++);
        Assert.False(state.ClipboardInstalled(1, 10));
        Assert.False(state.Complete(1, 11, UnlockedDefault, () => true));
        Assert.False(state.Ready);
        Assert.Equal(0, inputs);
        Assert.True(state.ClipboardInstalled(1, 11));
        Assert.True(state.Complete(1, 11, UnlockedDefault, () => true));
        Assert.Equal(1, inputs);
    }

    [Fact]
    public void ClipboardHelperRestartPreservesOnlyUnacknowledgedEntry()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0; var paste = 0;
        state.Begin(1, 10, () => acknowledged++);
        state.Submit(1, 10, () => paste++);
        state.Renew(1, 11);
        Assert.Equal(1, state.Count);
        Assert.False(state.ClipboardInstalled(1, 10));
        Assert.True(state.ClipboardInstalled(1, 11));
        Assert.True(state.Complete(1, 11, UnlockedDefault, () => true));
        Assert.Equal(1, acknowledged); Assert.Equal(0, paste);
        state.Renew(1, 12);
        Assert.True(state.Complete(1, 12, Winlogon, () => true));
        Assert.Equal(1, acknowledged);
    }

    [Fact]
    public void TakeoverExitAndResetDoNotReviveCancelledEntry()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0;
        state.Begin(1, 10, () => acknowledged++);
        state.Begin(2, 11);
        Assert.False(state.Complete(1, 10, Winlogon, () => true));
        state.Renew(2, 12);
        Assert.True(state.Complete(2, 12, Winlogon, () => true));
        state.Begin(1, 13, () => acknowledged++); state.Cancel(); state.Renew(1, 14);
        Assert.True(state.Complete(1, 14, Winlogon, () => true));
        state.Begin(1, 15, () => acknowledged++); state.DiscardPending(); state.Renew(1, 16);
        Assert.True(state.Complete(1, 16, Winlogon, () => true));
        Assert.Equal(0, acknowledged);
    }

    [Fact]
    public void UnknownSessionAndOtherDesktopDoNotGrantBypass()
    {
        var state = new DesktopInputReadiness(); state.Begin(1, 10); state.ClipboardInstalled(1, 10);
        Assert.False(state.Complete(1, 10, new(true, true, false, SessionLockState.Unknown), () => true));
        Assert.False(state.Complete(1, 10, new(true, false, false, SessionLockState.Unlocked), () => true));
        Assert.True(state.Complete(1, 10, UnlockedDefault, () => true));
    }

    [Fact]
    public void AttachmentFailureDuringDrainRetriesEntryOnce()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0;
        state.Begin(1, 10, () => acknowledged++);
        Assert.False(state.Complete(1, 10, Winlogon, () => true, () => false));
        Assert.False(state.Ready); Assert.Equal(1, state.Count);
        Assert.True(state.Complete(1, 10, Winlogon, () => true, () => true));
        Assert.Equal(1, acknowledged);
    }

    [Fact]
    public void UnlockBetweenQueuedActionsStopsOldDrain()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0; var paste = 0;
        state.Begin(1, 10, () => acknowledged++); state.Submit(1, 10, () => paste++);
        var checkedActions = 0;
        Assert.False(state.Complete(1, 10, LockedDefault, () => true, () =>
        {
            if (++checkedActions == 1) return true;
            Assert.True(state.NeedsRenewal(UnlockedDefault));
            state.Renew(1, 11); return false;
        }));
        Assert.Equal(1, acknowledged); Assert.Equal(0, paste);
        Assert.False(state.Ready); Assert.Equal(11, state.Generation);
    }

    [Fact]
    public void FailedEntryAfterSecureGrantStillRequiresFreshClipboardOnUnlock()
    {
        var state = new DesktopInputReadiness(); var acknowledged = 0;
        state.Begin(1, 10, () => acknowledged++); state.ClipboardInstalled(1, 10);
        Assert.False(state.Complete(1, 10, LockedDefault, () => true, () => false));
        Assert.False(state.Ready);
        Assert.True(state.NeedsRenewal(UnlockedDefault));
        state.Renew(1, 11);
        Assert.False(state.ClipboardInstalled(1, 10));
        Assert.False(state.Complete(1, 11, UnlockedDefault, () => true));
        Assert.Equal(0, acknowledged);
        state.ClipboardInstalled(1, 11);
        Assert.True(state.Complete(1, 11, UnlockedDefault, () => true));
        Assert.Equal(1, acknowledged);
    }
}
