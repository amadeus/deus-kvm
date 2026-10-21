using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

public static class ClipboardImageContent
{
    public const int MaximumBytes = 32 * 1024 * 1024, MaximumPixels = 40_000_000;

    // Check the PNG header before a native decoder allocates the bitmap.
    public static bool HasSafeDimensions(byte[] png)
    {
        if (png.Length is < 33 or > MaximumBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)) != 13 ||
            !png.AsSpan(12, 4).SequenceEqual(new byte[] { 73, 72, 68, 82 })) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        return width > 0 && height > 0 && (ulong)width * height <= MaximumPixels;
    }

    public static byte[] Read(FileClipboardSession session, Func<bool> permitted)
    {
        if (!session.Offer.IsImage || session.Offer.Size is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid image offer");
        var bytes = new byte[session.Offer.Size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!permitted()) throw new OperationCanceledException();
            var block = session.Read((uint)offset);
            Buffer.BlockCopy(block, 0, bytes, offset, block.Length);
            offset += block.Length;
        }
        if (!permitted()) throw new OperationCanceledException();
        if (!HasSafeDimensions(bytes)) throw new InvalidDataException("Invalid clipboard PNG");
        return bytes;
    }

    // Explorer consumes the already-downloaded PNG as a virtual file. It must not
    // reopen the network source or require a temporary screenshot on disk.
    public static FileClipboardSession CreateFileSession(FileClipboardOffer offer, byte[] png)
    {
        if (!offer.IsImage || offer.Size != png.Length || ClipboardFileSource.FromPng(png) is not { } source)
            throw new InvalidDataException("Invalid cached clipboard image");
        return new FileClipboardSession(offer with { Network = null }, new CachedImageReader(source));
    }

    private sealed class CachedImageReader(ClipboardFileSource source) : IFileBlockReader
    {
        public byte[] Read(uint offset) => source.Read(offset, FileNetworkCrypto.MaximumBlock)
            ?? throw new IOException("Invalid image position");
        public void Dispose() { }
    }
}
