using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class RemoteClipboardInboxTests
{
    [Fact]
    public void SnapshotArrivingAfterCommandDrainWaitsForItsSelection()
    {
        var inbox = new RemoteClipboardInbox<ClipboardSnapshot>();
        var previous = new RemoteClipboardLease(1, 10, 7);
        inbox.Select(previous);
        // STA drained its command queue. The pipe now receives both the next select
        // and its only snapshot before the STA tries to take a snapshot.
        var next = new RemoteClipboardLease(2, 11, 8);
        inbox.Select(next); previous.Revoke();
        var snapshot = new ClipboardSnapshot(8, 11, 12, "empty");
        inbox.Publish(next, snapshot);
        Assert.Null(inbox.Take(previous));
        Assert.Null(inbox.Take(null));
        // The next wake consumes the queued select. No clipboard change or retry
        // from the Mac is needed to obtain the original readiness response.
        Assert.Same(snapshot, inbox.Take(next));
        Assert.Null(inbox.Take(next));
    }

    [Fact]
    public void EpochSelectionCannotConsumeSnapshotBeforeItsCommand()
    {
        var inbox = new RemoteClipboardInbox<ClipboardSnapshot>();
        var placeholder = new RemoteClipboardLease(1, 10, 0);
        inbox.Select(placeholder);
        var selected = new RemoteClipboardLease(1, 10, 7);
        inbox.Select(selected); placeholder.Revoke();
        var snapshot = new ClipboardSnapshot(7, 10, 12, "unavailable");
        inbox.Publish(selected, snapshot);
        Assert.Null(inbox.Take(placeholder));
        Assert.Same(snapshot, inbox.Take(selected));
    }

    [Fact]
    public void SupersededProducerCannotReplaceCurrentSnapshot()
    {
        var inbox = new RemoteClipboardInbox<string>();
        var previous = new RemoteClipboardLease(1, 10, 7);
        inbox.Select(previous); inbox.Publish(previous, "old");
        var selected = new RemoteClipboardLease(2, 11, 8);
        inbox.Select(selected);
        inbox.Publish(selected, "first"); inbox.Publish(selected, "latest");
        inbox.Publish(previous, "late old source");
        Assert.Null(inbox.Take(previous));
        Assert.Equal("latest", inbox.Take(selected));
    }

    [Fact]
    public void RevocationAndLocalReturnDiscardPendingSnapshots()
    {
        var inbox = new RemoteClipboardInbox<string>();
        var selected = new RemoteClipboardLease(1, 10, 7);
        inbox.Select(selected); inbox.Publish(selected, "pending");
        selected.Revoke();
        Assert.Null(inbox.Take(selected));
        var replacement = new RemoteClipboardLease(1, 10, 7);
        inbox.Select(replacement); inbox.Publish(replacement, "current");
        Assert.Null(inbox.Take(selected));
        inbox.Clear();
        Assert.Null(inbox.Take(replacement));
    }
}
