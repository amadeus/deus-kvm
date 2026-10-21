using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ClipboardImageTests
{
    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");

    [Fact]
    public void SourceOwnsItsBytesAndServesOnlyBoundedRanges()
    {
        var png = Png(); var original = (byte[])png.Clone();
        var source = ClipboardFileSource.FromPng(png)!;
        png[0] = 0;
        Assert.Equal("image/png", source.ContentType);
        Assert.Equal(original, source.Read(0, original.Length));
        Assert.Empty(source.Read((uint)source.Size, 1)!);
        Assert.Null(source.Read((uint)source.Size + 1, 1));
        Assert.Null(source.Read(0, FileNetworkCrypto.MaximumBlock + 1));
        Assert.Null(ClipboardFileSource.FromPng(png));
    }

    [Fact]
    public void DimensionsAreBoundedBeforeNativeDecoding()
    {
        var png = Png();
        Assert.True(ClipboardImageContent.HasSafeDimensions(png));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), uint.MaxValue);
        Assert.False(ClipboardImageContent.HasSafeDimensions(png));
        Assert.False(ClipboardImageContent.HasSafeDimensions(new byte[32]));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 0);
        Assert.False(ClipboardImageContent.HasSafeDimensions(png));
    }

    [Fact]
    public void WireMetadataSeparatesImagesFromFilesAndRejectsUnknownOrOversizedImages()
    {
        var offer = new FileClipboardOffer(1, 3, "Screenshot.png", Png().Length, 2, ContentType: "image/png");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(offer, FileClipboardOffer.WireJson);
        Assert.Equal(offer, FileClipboardOffer.Parse(bytes));
        Assert.True(FileClipboardOffer.Parse(bytes).IsImage);
        Assert.False(FileClipboardOffer.Parse(Encoding.UTF8.GetBytes(
            """{"epoch":1,"sequence":3,"name":"file.png","size":2000000000,"clipboardSequence":2}""")).IsImage);
        foreach (var invalid in new[] { offer with { Size = 0 }, offer with { Size = ClipboardImageContent.MaximumBytes + 1 },
            offer with { ContentType = "image/jpeg" } })
            Assert.Throws<InvalidDataException>(() => FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid, FileClipboardOffer.WireJson)));
    }

    [Fact]
    public void MultiBlockImageReceivePreservesBytesAndCancellationDiscardsIt()
    {
        var bytes = new byte[FileNetworkCrypto.MaximumBlock + 37];
        Png().CopyTo(bytes, 0);
        var offer = new FileClipboardOffer(1, 2, "Screenshot.png", bytes.Length, ContentType: "image/png");
        using var session = new FileClipboardSession(offer, new ImageReader(bytes));
        Assert.Equal(bytes, ClipboardImageContent.Read(session, () => true));
        var checks = 0;
        Assert.Throws<OperationCanceledException>(() => ClipboardImageContent.Read(session, () => ++checks < 2));
        session.Dispose();
        Assert.Throws<IOException>(() => ClipboardImageContent.Read(session, () => true));
    }

    private sealed class ImageReader(byte[] bytes) : IFileBlockReader
    {
        public byte[] Read(uint offset) => bytes.Skip((int)offset).Take(FileNetworkCrypto.MaximumBlock).ToArray();
        public void Dispose() { }
    }

    [Fact]
    public void DownloadedImageCanBePastedAsAFileRepeatedlyWithoutTheNetworkSource()
    {
        var png = new byte[FileNetworkCrypto.MaximumBlock + 37];
        Png().CopyTo(png, 0);
        var expected = (byte[])png.Clone();
        var offer = new FileClipboardOffer(1, 2, "Screenshot.png", png.Length, ContentType: "image/png",
            Network: new FileNetworkOffer(["127.0.0.1"], 1, Convert.ToBase64String(new byte[32])));
        using var session = ClipboardImageContent.CreateFileSession(offer, png);
        Assert.Null(session.Offer.Network);
        png[0] = 0; // The cached file owns its bytes, independently of the caller.
        var operation = new FileClipboardOperation(() => true, () => true, session.Dispose, _ => { });
        for (var paste = 0; paste < 2; paste++)
        {
            operation.Start();
            var stream = new RemoteFileStream(session, operation.CheckRead);
            stream.Stat(out var stat, 0);
            Assert.Equal("Screenshot.png", stat.pwcsName);
            Assert.Equal(expected.Length, stat.cbSize);
            var result = new byte[expected.Length];
            stream.Read(result, result.Length, IntPtr.Zero);
            Assert.Equal(expected, result);
            stream.Seek(FileNetworkCrypto.MaximumBlock - 5, 0, IntPtr.Zero);
            stream.Clone(out var clone);
            var tail = new byte[42]; clone.Read(tail, tail.Length, IntPtr.Zero);
            Assert.Equal(expected.Skip(FileNetworkCrypto.MaximumBlock - 5), tail);
            operation.End(0);
        }
        session.Dispose();
        Assert.Throws<IOException>(() => new RemoteFileStream(session, () => { }).Read(new byte[1], 1, IntPtr.Zero));
    }

    [Fact]
    public void CachedImageFileRejectsWrongMetadataAndRespectsClipboardReplacement()
    {
        var png = Png();
        var offer = new FileClipboardOffer(1, 2, "Screenshot.png", png.Length, ContentType: "image/png");
        Assert.Throws<InvalidDataException>(() => ClipboardImageContent.CreateFileSession(offer with { Size = png.Length + 1 }, png));
        Assert.Throws<InvalidDataException>(() => ClipboardImageContent.CreateFileSession(offer with { ContentType = null }, png));
        using var session = ClipboardImageContent.CreateFileSession(offer, png);
        var owns = true;
        var operation = new FileClipboardOperation(() => true, () => owns, session.Dispose, _ => { });
        var stream = new RemoteFileStream(session, operation.CheckRead);
        stream.Read(new byte[1], 1, IntPtr.Zero);
        owns = false;
        Assert.Throws<System.Runtime.InteropServices.COMException>(() => stream.Read(new byte[1], 1, IntPtr.Zero));
    }
}
