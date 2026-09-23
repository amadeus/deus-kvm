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
        using var session = new FileClipboardSession(new(1, 2, "file", 10), new TestFileReader(_ => { requests++; return new byte[10]; }));
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
        var session = new FileClipboardSession(new(1, 2, "file", source.Length),
            new TestFileReader(offset => source.Skip((int)offset).Take(1024).ToArray()));
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
        using var session = new FileClipboardSession(new(1, 2, "empty", 0), new TestFileReader(_ => throw new Exception("Unexpected request")));
        var stream = new RemoteFileStream(session, () => { });
        var count = Marshal.AllocCoTaskMem(4);
        try { stream.Read(new byte[10], 10, count); Assert.Equal(0, Marshal.ReadInt32(count)); }
        finally { Marshal.FreeCoTaskMem(count); }
    }
    [Fact]
    public void TwoMegabyteTransferSurvivesLargeConsumerReads()
    {
        // Slightly over the reported 1.9 MB failure, with a partial final block.
        var source = new byte[2 * 1024 * 1024 + 37];
        new Random(1234).NextBytes(source);
        var requests = 0;
        var session = new FileClipboardSession(new(1, 2, "file", source.Length), new TestFileReader(offset => {
            requests++;
            return source.AsSpan((int)offset, Math.Min(FileNetworkCrypto.MaximumBlock, source.Length - (int)offset)).ToArray();
        }));
        using (session)
        {
            var stream = new RemoteFileStream(session, () => { });
            var received = new byte[source.Length];
            var buffer = new byte[1024 * 1024];
            var count = Marshal.AllocCoTaskMem(4);
            try
            {
                for (var position = 0; position < received.Length;)
                {
                    stream.Read(buffer, buffer.Length, count);
                    var n = Marshal.ReadInt32(count);
                    Assert.InRange(n, 1, Math.Min(buffer.Length, received.Length - position));
                    Array.Copy(buffer, 0, received, position, n); position += n;
                }
            }
            finally { Marshal.FreeCoTaskMem(count); }
            Assert.Equal(source, received);
            Assert.Equal((source.Length + FileNetworkCrypto.MaximumBlock - 1) / FileNetworkCrypto.MaximumBlock, requests);
        }
    }

}
