using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ClipboardTransferTests
{
    [Theory]
    [InlineData("")]
    [InlineData("hello\r\nworld\rline\n😀 café 日本語")]
    public void TextIsNormalizedAndTransferredWithoutEcho(string text)
    {
        var pair = new Pair();
        var expected = ClipboardTransfer.Text(text)!;
        pair.A.Observe(expected);
        pair.Pump();
        Assert.Equal(expected, Assert.Single(pair.AppliedB));
        pair.B.Yield(); pair.Pump();
        Assert.Empty(pair.AppliedA);
        Assert.Single(pair.AppliedB);
    }
    [Theory]
    [InlineData(1023)] [InlineData(1024)] [InlineData(1025)] [InlineData(20480)] [InlineData(65536)]
    public void LargeTransfersStayWithinOneBlockAndWireBudget(int size)
    {
        var pair = new Pair();
        var text = Enumerable.Repeat((byte)'x', size).ToArray();
        pair.A.Observe(text); pair.Pump();
        Assert.Equal(text, Assert.Single(pair.AppliedB));
        Assert.InRange(pair.LargestQueue, 1, 2);
    }
    [Fact]
    public void RepeatedRoundTripsUseTheLatestCopyWithoutEcho()
    {
        var pair = new Pair();
        for (var index = 0; index < 4; index++)
        {
            pair.A.SetActive(false, 0); pair.B.SetActive(true, 0);
            pair.A.Observe(ClipboardTransfer.Text($"Mac {index}")); pair.Pump();
            Assert.Equal($"Mac {index}", ClipboardTransfer.Decode(pair.AppliedB.Last()));
            pair.B.Observe(ClipboardTransfer.Text("superseded"));
            pair.B.Observe(ClipboardTransfer.Text($"PC {index}")); pair.Pump();
            pair.B.SetActive(false, 0); pair.A.SetActive(true, 0); pair.Pump();
            Assert.Equal($"PC {index}", ClipboardTransfer.Decode(pair.AppliedA.Last()));
            pair.A.Yield(); pair.B.Yield(); pair.Pump();
            Assert.Equal(index + 1, pair.AppliedA.Count); Assert.Equal(index + 1, pair.AppliedB.Count);
        }
    }
    [Fact]
    public void SourceCopyChangesMidTransfer()
    {
        var pair = new Pair();
        pair.A.Observe(ClipboardTransfer.Text(new string('x', 3000))); pair.Step(); pair.Step();
        pair.A.Observe(ClipboardTransfer.Text("replacement")); pair.Pump();
        Assert.Equal("replacement", ClipboardTransfer.Decode(Assert.Single(pair.AppliedB)));
    }
    [Fact]
    public void InactiveDestinationDefersUntilSwitchAndInitialSnapshotIsNotAnnounced()
    {
        var pair = new Pair();
        pair.B.SetActive(false, 0);
        pair.A.Observe(ClipboardTransfer.Text("initial"), false);
        Assert.Empty(pair.Queue);
        pair.A.Yield(); pair.Pump();
        Assert.Empty(pair.AppliedB);
        pair.B.SetActive(true, 0); pair.Pump();
        Assert.Equal("initial", ClipboardTransfer.Decode(Assert.Single(pair.AppliedB)));
    }
    [Fact]
    public void NewLocalCopyCancelsAnIncomingTransfer()
    {
        var pair = new Pair();
        pair.A.Observe(ClipboardTransfer.Text(new string('x', 3000)));
        pair.Step(); pair.Step(); pair.Step(); // First block received; next requested.
        pair.B.Observe(ClipboardTransfer.Text("new local copy"));
        pair.Pump();
        Assert.Empty(pair.AppliedB);
    }
    [Fact]
    public void PrivateOrOversizedCopyWithdrawsThePreviousOffer()
    {
        var pair = new Pair();
        pair.B.SetActive(false, 0);
        pair.A.Observe(ClipboardTransfer.Text("old")); pair.Pump();
        pair.A.Observe(null); pair.Pump();
        pair.B.SetActive(true, 0); pair.Pump();
        Assert.Empty(pair.AppliedB);
        Assert.Null(ClipboardTransfer.Text(new string('x', 65537)));
        Assert.Null(ClipboardTransfer.Text(new string('é', 32769)));
        Assert.Null(ClipboardTransfer.Text("a\0b"));
    }
    [Fact]
    public void EpochChangeDiscardsInFlightTextAndImportedCopiesAreNotReoffered()
    {
        var pair = new Pair();
        pair.A.Observe(ClipboardTransfer.Text("old session")); pair.Step(); pair.Step();
        pair.B.Reset(43); pair.Pump();
        Assert.Empty(pair.AppliedB);
        pair.A.Reset(43); pair.A.Observe(ClipboardTransfer.Text("new session")); pair.Pump();
        Assert.Equal("new session", ClipboardTransfer.Decode(Assert.Single(pair.AppliedB)));
    }
    [Fact]
    public void DuplicateAnnouncementsAndBlocksCannotApplyTwice()
    {
        var pair = new Pair();
        pair.A.Observe(ClipboardTransfer.Text("hello"));
        var announcement = pair.Queue.Peek();
        pair.Pump(); pair.Queue.Enqueue(announcement); pair.Pump();
        Assert.Single(pair.AppliedB);
    }
    [Fact]
    public void DroppedRequestIsRetriedAndRetriesAreBounded()
    {
        var pair = new Pair();
        pair.A.Observe(ClipboardTransfer.Text("retry")); pair.Step();
        pair.Queue.Clear(); pair.B.Tick(5); pair.Pump();
        Assert.Single(pair.AppliedB);
        pair.A.Observe(ClipboardTransfer.Text("expire")); pair.Step(); pair.Queue.Clear();
        for (var tick = 1; tick <= 3; tick++) { pair.B.Tick(tick * 5); Assert.Single(pair.Queue); pair.Queue.Clear(); }
        pair.B.Tick(20); Assert.Empty(pair.Queue);
    }
    [Fact]
    public void RejectsMalformedUtf8OversizeAndIncorrectBlockLengths()
    {
        var pair = new Pair();
        Assert.Throws<InvalidDataException>(() => pair.B.Receive(Protocol.Message.ClipGrab, new byte[11], 0));
        Assert.Throws<InvalidDataException>(() => pair.B.Receive(Protocol.Message.ClipGrab, ClipboardTransfer.Header(42, 7, 65537), 0));
        pair.B.Receive(Protocol.Message.ClipGrab, ClipboardTransfer.Header(42, 7, 1), 0);
        Assert.Throws<InvalidDataException>(() => pair.B.Receive(Protocol.Message.ClipData, ClipboardTransfer.Header(42, 7, 0), 0));
        Assert.Throws<InvalidDataException>(() => pair.B.Receive(Protocol.Message.ClipData, ClipboardTransfer.Header(42, 7, 0).Concat(new byte[] { 255 }).ToArray(), 0));
        Assert.Empty(pair.AppliedB);
    }
    private sealed class Pair
    {
        public readonly Queue<(bool ToB, Protocol.Message Type, byte[] Data)> Queue = new();
        public readonly List<byte[]> AppliedA = [], AppliedB = [];
        public readonly ClipboardTransfer A, B;
        public int LargestQueue;
        public Pair()
        {
            A = new((type, bytes) => Queue.Enqueue((true, type, bytes)), AppliedA.Add);
            B = new((type, bytes) => Queue.Enqueue((false, type, bytes)), AppliedB.Add);
            A.Reset(42); B.Reset(42); A.SetActive(false, 0); B.SetActive(true, 0);
        }
        public void Step()
        {
            LargestQueue = Math.Max(LargestQueue, Queue.Count);
            var packet = Queue.Dequeue();
            Assert.InRange(packet.Data.Length, 0, packet.Type == Protocol.Message.ClipData ? 1036 : 13);
            (packet.ToB ? B : A).Receive(packet.Type, packet.Data, 0);
        }
        public void Pump()
        {
            var remaining = 1000;
            while (Queue.Count > 0) { Assert.True(remaining-- > 0, "Clipboard feedback loop"); Step(); }
        }
    }
}
