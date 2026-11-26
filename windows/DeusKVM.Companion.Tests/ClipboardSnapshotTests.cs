using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ClipboardSnapshotTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("unavailable")]
    public void ExplicitNoContentRoundTrips(string kind)
    {
        var snapshot = new ClipboardSnapshot(7, 9, 12, kind);
        Assert.Equal(snapshot, ClipboardSnapshot.Parse(snapshot.Encode()));
    }

    [Fact]
    public void MaximumTextFitsBothSnapshotAndDesktopEnvelope()
    {
        var text = Enumerable.Repeat((byte)'a', ClipboardTransfer.MaximumBytes).ToArray();
        var snapshot = new ClipboardSnapshot(7, 9, 12, "text", text);
        var bytes = snapshot.Encode();
        Assert.Equal(text, ClipboardSnapshot.Parse(bytes).Text);
        Assert.True(bytes.Length <= ClipboardSnapshot.MaximumBytes);
        var message = new DesktopMessage("clipboard-snapshot", Source: 1, Ownership: 9, Epoch: 7, Clipboard: bytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(message).Length <= DesktopPipe.MaximumMessageBytes);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unavailable")]
    [InlineData("image")]
    [InlineData("file")]
    [InlineData("unknown")]
    public void ContentCannotMasqueradeAsAnotherKind(string kind) =>
        Assert.Throws<InvalidDataException>(() => new ClipboardSnapshot(7, 9, 12, kind, [65]).Encode());

    [Fact]
    public void FileMustBelongToSnapshotRevisionAndEpoch()
    {
        var network = new FileNetworkOffer(["192.168.77.24"], 53401, Convert.ToBase64String(new byte[32]));
        var file = new FileClipboardOffer(7, 12, "example.txt", 8, Network: network);
        Assert.Equal("file", ClipboardSnapshot.Parse(new ClipboardSnapshot(7, 9, 12, "file", File: file).Encode()).Kind);
        Assert.Throws<InvalidDataException>(() => new ClipboardSnapshot(8, 9, 12, "file", File: file).Encode());
        Assert.Throws<InvalidDataException>(() => new ClipboardSnapshot(7, 9, 13, "file", File: file).Encode());
        Assert.Throws<InvalidDataException>(() => new ClipboardSnapshot(7, 9, 12, "image", File: file).Encode());
    }

    [Fact]
    public void RevokingLeaseCancelsBlockedWorkAndCannotReviveSameSource()
    {
        var first = new RemoteClipboardLease(4, 10, 1);
        var canceled = false;
        using var registration = first.Token.Register(() => canceled = true);
        first.Revoke();
        var second = new RemoteClipboardLease(4, 11, 2);
        Assert.True(canceled);
        Assert.False(first.Matches(4, 10, 1));
        Assert.False(second.Matches(4, 10, 1));
        Assert.True(second.Matches(4, 11, 2));
    }

    [Fact]
    public void RequestRequiresOwnershipAndCannotBeAResponse()
    {
        var request = new ClipboardSnapshotRequest(7, 9);
        Assert.Equal(request, ClipboardSnapshotRequest.Parse(request.Encode()));
        Assert.Throws<InvalidDataException>(() => ClipboardSnapshotRequest.Parse(new ClipboardSnapshotRequest(7, 0).Encode()));
        Assert.Throws<InvalidDataException>(() => ClipboardSnapshotRequest.Parse(new ClipboardSnapshot(7, 9, 12, "empty").Encode()));
    }

    [Fact]
    public void LocalWindowsCopyWinsPendingSnapshotButLaterMacCopyCanReplaceIt()
    {
        var selection = new RemoteClipboardSelection(20);
        Assert.False(selection.CanInstall(50, 21, false, 21));
        selection.Complete(50, 21);
        Assert.False(selection.CanInstall(50, 21, false, 21)); // duplicate old snapshot
        Assert.True(selection.CanInstall(51, 21, false, 21)); // a fresh copy on this Mac
        Assert.False(selection.CanInstall(51, 22, false, 21)); // Windows copied during installation
    }

    [Fact]
    public void RenderingDoesNotLookLikeALocalCopyButReplacementDoes()
    {
        var selection = new RemoteClipboardSelection(20);
        Assert.True(selection.CanInstall(50, 20, false, 20));
        selection.Complete(50, 21);
        Assert.True(selection.CanInstall(51, 22, true, 21));
        selection.Complete(51, 23);
        Assert.False(selection.CanInstall(52, 24, false, 23));
    }
}
