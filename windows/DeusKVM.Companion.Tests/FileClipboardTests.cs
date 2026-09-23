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
}
