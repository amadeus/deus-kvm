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
    public void TwoGigabyteBoundaryAndTailRemainExact()
    {
        var offer = new FileClipboardOffer(1, 2, "file", 2_000_000_000);
        Assert.Equal(offer, FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(offer)));
        Assert.Throws<InvalidDataException>(() => FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(offer with { Size = 2_000_000_001 })));
        uint? requested = null;
        using var session = new FileClipboardSession(offer, new TestFileReader(offset => { requested = offset; return [1, 2, 3]; }));
        var stream = new RemoteFileStream(session, () => { });
        stream.Seek(-3, 2, IntPtr.Zero); var tail = new byte[8]; stream.Read(tail, tail.Length, IntPtr.Zero);
        Assert.Equal(1_999_999_997u, requested); Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, tail);
        Assert.Empty(session.Read(2_000_000_000));
    }
    [Fact]
    public void MissingNetworkFailsWithoutBluetoothFallback()
    {
        using var session = new FileClipboardSession(new(1, 2, "file", 4));
        Assert.Throws<NetworkUnavailableException>(() => session.Read(0));
    }
    [Fact]
    public async Task CancellationUnblocksAnOutstandingRead()
    {
        using var stop = new CancellationTokenSource();
        var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new FileClipboardSession(new(1, 2, "file", 4), new TestFileReader(_ => {
            requested.SetResult(true); stop.Token.WaitHandle.WaitOne(); throw new IOException("Canceled");
        }, stop.Cancel));
        var read = Task.Run(() => Assert.Throws<IOException>(() => session.Read(0)));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2)); session.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public void InvalidBlocksFailAndExpiredOffersStayExpired()
    {
        using var empty = new FileClipboardSession(new(1, 2, "file", 4), new TestFileReader(_ => []));
        Assert.Throws<IOException>(() => empty.Read(0));
        using var large = new FileClipboardSession(new(1, 2, "file", 4), new TestFileReader(_ => new byte[5]));
        Assert.Throws<IOException>(() => large.Read(0));
        large.Dispose(); Assert.Throws<IOException>(() => large.Read(0));
    }
    [Fact]
    public void ConsumerFailureLogsNoNamesOrContents()
    {
        List<string> events = [];
        using var session = new FileClipboardSession(new(1, 2, "private-name", 4), new TestFileReader(_ => throw new IOException("Unavailable")));
        var stream = new RemoteFileStream(session, () => { }, events.Add);
        Assert.Throws<IOException>(() => stream.Read(new byte[4], 4, IntPtr.Zero));
        Assert.Contains(events, e => e.StartsWith("consumer-read-failed hr="));
        Assert.DoesNotContain(events, e => e.Contains("private-name"));
    }
}
