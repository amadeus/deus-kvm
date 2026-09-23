using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class FileClipboardTests
{
    [Theory]
    [InlineData("../secret")]
    [InlineData("x:y")]
    [InlineData("CON.txt")]
    [InlineData("x\\y")]
    [InlineData("file.")]
    public void RejectsUnsafeNames(string name) => Assert.Throws<InvalidDataException>(() =>
        FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(new FileClipboardOffer(1, 2, name, 10))));

    [Fact]
    public void AdvertisementDoesNotFetchAndReadsUseExactOffsets()
    {
        List<byte[]> requests = [];
        FileClipboardSession? session = null;
        session = new(new(1, 2, "file.txt", 1500), request =>
        {
            requests.Add(request);
            var offset = ClipboardTransfer.Read(request, 8);
            session!.Receive(ClipboardTransfer.Header(1, 2, offset).Concat(new byte[Math.Min(1024, 1500 - offset)]).ToArray());
        });
        using (session)
        {
            Assert.Empty(requests);
            Assert.Equal(1024, session.Read(0).Length);
            Assert.Single(requests);
            Assert.Equal(476, session.Read(1024).Length);
            Assert.Equal(1024u, ClipboardTransfer.Read(requests[1], 8));
            Assert.Empty(session.Read(1500));
            Assert.Equal(2, requests.Count);
        }
    }

    [Fact]
    public async Task CancellationUnblocksAnOutstandingRead()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new FileClipboardSession(new(1, 2, "file", 4), _ => requested.SetResult());
        var read = Task.Run(() => Assert.Throws<IOException>(() => session.Read(0)));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Dispose();
        await read.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void StaleBlocksAreIgnoredAndSourceErrorsFailTheRead()
    {
        FileClipboardSession? session = null;
        session = new(new(1, 2, "file", 4), _ =>
        {
            session!.Receive(ClipboardTransfer.Header(0, 2, 0).Concat(new byte[4]).ToArray());
            session.Receive(ClipboardTransfer.Header(1, 3, 0).Concat(new byte[4]).ToArray());
            session.Receive(ClipboardTransfer.Header(1, 2, uint.MaxValue));
        });
        using (session) Assert.Throws<IOException>(() => session.Read(0));
    }

    [Fact]
    public void ShortOrOversizedBlocksFailInsteadOfSilentlyTruncating()
    {
        FileClipboardSession? session = null;
        session = new(new(1, 2, "file", 4), _ => session!.Receive(ClipboardTransfer.Header(1, 2, 0).Concat(new byte[3]).ToArray()));
        using (session) Assert.Throws<IOException>(() => session.Read(0));
    }
    [Fact]
    public void DiagnosticsDistinguishDeliveredBytesFromConsumerReadCompletion()
    {
        List<string> events = [];
        FileClipboardSession? session = null;
        session = new(new(1, 2, "private-name", 2500), request =>
        {
            var offset = ClipboardTransfer.Read(request, 8);
            session!.Receive(ClipboardTransfer.Header(1, 2, offset)
                .Concat(new byte[Math.Min(1024, 2500 - offset)]).ToArray());
        }, events.Add);
        using (session)
        {
            var stream = new RemoteFileStream(session, () => { }, events.Add);
            stream.Stat(out _, 0); Assert.Empty(events);
            stream.Read(new byte[3000], 3000, IntPtr.Zero);
            Assert.Contains("consumer-read offset=0 requested=3000", events);
            Assert.Contains("transfer-start size=2500 block=1024", events);
            Assert.Contains(events, e => e.StartsWith("transfer-progress received=2500 through=2500 size=2500 blocks=3 elapsedMs="));
            Assert.Contains(events, e => e.StartsWith("consumer-read-complete returned=2500 elapsedMs="));
            Assert.DoesNotContain(events, e => e.Contains("private-name"));
        }
    }

    [Fact]
    public void DiagnosticsDistinguishSourceFailureFromAnIncompleteConsumerRead()
    {
        List<string> events = [];
        FileClipboardSession? session = null;
        session = new(new(1, 2, "private-name", 2500), _ =>
            session!.Receive(ClipboardTransfer.Header(1, 2, uint.MaxValue)), events.Add);
        using (session)
        {
            var stream = new RemoteFileStream(session, () => { }, events.Add);
            Assert.Throws<IOException>(() => stream.Read(new byte[3000], 3000, IntPtr.Zero));
            Assert.Contains(events, e => e.StartsWith("transfer-failed reason=source-unavailable offset=0 received=0 elapsedMs="));
            Assert.Contains(events, e => e.StartsWith("consumer-read-failed hr="));
            Assert.DoesNotContain(events, e => e.StartsWith("consumer-read-complete"));
        }
    }

}
