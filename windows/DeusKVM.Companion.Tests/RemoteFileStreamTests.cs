using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class RemoteFileStreamTests
{
    [Fact]
    public void InspectingStreamDoesNotDownloadAndReadRequiresPastePermission()
    {
        var requests = 0;
        using var session = new FileClipboardSession(new(1, 2, "file", 10), _ => requests++);
        var stream = new RemoteFileStream(session, () => throw new UnauthorizedAccessException());
        stream.Stat(out var stat, 0);
        stream.Seek(0, 0, IntPtr.Zero);
        stream.Clone(out _);
        Assert.Equal(10, stat.cbSize);
        Assert.Throws<UnauthorizedAccessException>(() => stream.Read(new byte[10], 10, IntPtr.Zero));
        Assert.Equal(0, requests);
    }

    [Fact]
    public void StreamPreservesBinaryContentsAcrossBlocksSeeksAndClones()
    {
        var source = Enumerable.Range(0, 2500).Select(i => (byte)(i % 251)).ToArray();
        FileClipboardSession? session = null;
        session = new(new(1, 2, "file", source.Length), request =>
        {
            var offset = ClipboardTransfer.Read(request, 8);
            session!.Receive(ClipboardTransfer.Header(1, 2, offset).Concat(source.Skip((int)offset).Take(1024)).ToArray());
        });
        using (session)
        {
            var stream = new RemoteFileStream(session, () => { });
            var bytes = new byte[source.Length];
            stream.Read(bytes, bytes.Length, IntPtr.Zero);
            Assert.Equal(source, bytes);
            stream.Seek(1000, 0, IntPtr.Zero);
            stream.Clone(out var clone);
            var slice = new byte[1100];
            clone.Read(slice, slice.Length, IntPtr.Zero);
            Assert.Equal(source.Skip(1000).Take(1100), slice);
            var one = new byte[1]; stream.Read(one, 1, IntPtr.Zero);
            Assert.Equal(source[1000], one[0]);
            Assert.Throws<IOException>(() => stream.Seek(-1, 0, IntPtr.Zero));
        }
    }

    [Fact]
    public void EmptyFileAndEofDoNotRequestContent()
    {
        using var session = new FileClipboardSession(new(1, 2, "empty", 0), _ => Assert.Fail("Unexpected request"));
        var stream = new RemoteFileStream(session, () => { });
        var count = Marshal.AllocCoTaskMem(4);
        try { stream.Read(new byte[10], 10, count); Assert.Equal(0, Marshal.ReadInt32(count)); }
        finally { Marshal.FreeCoTaskMem(count); }
    }
}
