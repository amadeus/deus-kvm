using System.Threading.Channels;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class DesktopPipeTests
{
    [Fact]
    public async Task ParentDisconnectFinishesWithoutWaitingForDesktopDispatch()
    {
        using var stream = new MemoryStream();
        await DesktopPipe.Write(stream, new DesktopMessage("exit"), CancellationToken.None);
        stream.Position = 0;
        var pendingDesktop = new List<DesktopMessage>();
        var outgoing = Channel.CreateBounded<DesktopMessage>(32);
        // Queue the desktop message but never execute it, as if the UI were hung.
        // EOF must still stop the pump, including its otherwise idle writer.
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Task.Run(() => DesktopPipe.Pump(stream, outgoing.Reader, pendingDesktop.Add, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(pendingDesktop);
        Assert.Equal("exit", pendingDesktop[0].Kind);
    }

    [Fact]
    public async Task ProblemReportTravelsOutsideTheStatusDetail()
    {
        // The host copies any message's Detail into its status line; a report must not replace it.
        using var stream = new MemoryStream();
        var report = string.Join("\n", Enumerable.Repeat("  12:00:00.000Z leave StaleLayout: cached=1920x1080@120", 100));
        await DesktopPipe.Write(stream, new DesktopMessage("report", Epoch: 42, Report: report), CancellationToken.None);
        stream.Position = 0;
        var message = await DesktopPipe.Read(stream, CancellationToken.None);
        Assert.Equal(("report", 42u, report), (message.Kind, message.Epoch, message.Report));
        Assert.Null(message.Detail);
    }

    [Fact]
    public void ProblemReportMessageValues() =>
        Assert.Equal((0x30, 0x31), ((int)Protocol.Message.ReportRequest, (int)Protocol.Message.Report));

    [Fact]
    public async Task MaximumClipboardFitsInAuthenticatedDesktopMessage()
    {
        using var stream = new MemoryStream();
        var payload = Enumerable.Repeat((byte)255, ClipboardTransfer.MaximumBytes).ToArray();
        await DesktopPipe.Write(stream, new DesktopMessage("clipboard-copy", Epoch: 42, Revision: 7, Clipboard: payload), CancellationToken.None);
        stream.Position = 0;
        var read = await DesktopPipe.Read(stream, CancellationToken.None);
        Assert.Equal(payload, read.Clipboard);
        Assert.Equal(42u, read.Epoch);
        Assert.Equal(7u, read.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DesktopPipe.MaximumMessageBytes + 1)]
    public async Task RejectsInvalidLengthBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => DesktopPipe.Read(stream, CancellationToken.None));
    }
}
