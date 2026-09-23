using System.Runtime.InteropServices;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class FileClipboardOperationTests
{
    [Fact]
    public void SynchronousNativeReadFetchesWithoutOptionalAsyncStart()
    {
        var requests = 0;
        List<string> diagnostics = [];
        var session = new FileClipboardSession(new(1, 2, "file", 3), new TestFileReader(_ => { requests++; return [7, 8, 9]; }));
        using (session)
        {
            var operation = new FileClipboardOperation(() => true, () => true, session.Dispose, diagnostics.Add);
            var stream = new RemoteFileStream(session, operation.CheckRead);
            operation.Check(); stream.Stat(out _, 0); stream.Seek(0, 0, IntPtr.Zero); stream.Clone(out _);
            Assert.Equal(0, requests);
            Assert.False(operation.Operating);
            var bytes = new byte[3]; stream.Read(bytes, 3, IntPtr.Zero);
            Assert.Equal(new byte[] { 7, 8, 9 }, bytes);
            Assert.Equal(1, requests);
            Assert.Equal(new[] { "stream-read synchronous" }, diagnostics);
        }
    }

    [Fact]
    public void AsyncLifecycleStillWorksAndCancellationRevokesAccess()
    {
        var canceled = false;
        var operation = new FileClipboardOperation(() => true, () => true, () => canceled = true, _ => { });
        operation.Start(); Assert.True(operation.Operating); operation.CheckRead();
        operation.End(0); Assert.False(operation.Operating); operation.CheckRead();
        Assert.False(canceled);
        operation.Start(); operation.End(unchecked((int)0x80004004));
        Assert.True(canceled);
        Assert.Throws<COMException>(operation.CheckRead);
    }

    [Theory]
    [InlineData(false, true, "session-unavailable")]
    [InlineData(true, false, "clipboard-replaced")]
    public void SessionAndClipboardOwnershipStillGateSynchronousReads(bool permitted, bool owns, string reason)
    {
        List<string> diagnostics = [];
        var requests = 0;
        using var session = new FileClipboardSession(new(1, 2, "file", 3), new TestFileReader(_ => { requests++; return [7, 8, 9]; }));
        var operation = new FileClipboardOperation(() => permitted, () => owns, session.Dispose, diagnostics.Add);
        var stream = new RemoteFileStream(session, operation.CheckRead);
        Assert.Throws<COMException>(() => stream.Read(new byte[3], 3, IntPtr.Zero));
        Assert.Throws<COMException>(operation.CheckRead);
        Assert.Equal(0, requests);
        Assert.Equal(new[] { "read-denied " + reason }, diagnostics);
    }
}
