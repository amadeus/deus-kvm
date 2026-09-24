using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;
namespace DeusKVM.Companion.Tests;
public sealed class ClipboardFileSourceTests
{
    [Fact]
    public void SparseTwoGigabyteSourceReadsTailAndRejectsChangesAndOversize()
    {
        var path = Path.GetTempFileName();
        try {
            using (var file = File.OpenWrite(path)) { file.SetLength(FileClipboardOffer.MaximumBytes); file.Position = file.Length - 3; file.Write(new byte[] { 4, 5, 6 }, 0, 3); }
            var source = ClipboardFileSource.Capture(path)!;
            Assert.Equal(2_000_000_000, source.Size);
            Assert.Equal(new byte[] { 4, 5, 6 }, source.Read(1_999_999_997, FileNetworkCrypto.MaximumBlock));
            Assert.Null(source.Read(0, FileNetworkCrypto.MaximumBlock + 1));
            using (var file = File.OpenWrite(path)) file.SetLength(2_000_000_001);
            Assert.Null(source.Read(0, 4)); Assert.Null(ClipboardFileSource.Capture(path));
        } finally { File.Delete(path); }
    }
    [Fact]
    public void SourceCaptureRejectsDirectoriesLinksAndRemovedFiles()
    {
        var path = Path.GetTempFileName(); var link = path + ".link";
        try {
            var source = ClipboardFileSource.Capture(path)!;
            TestBytes.CreateSymbolicLink(link, path);
            Assert.Null(ClipboardFileSource.Capture(link)); Assert.Null(ClipboardFileSource.Capture(Path.GetDirectoryName(path)!));
            File.Delete(path); Assert.Null(source.Read(0, 1));
        } finally { File.Delete(link); File.Delete(path); }
    }
    [Fact]
    public void WireMetadataMatchesSwiftKeysAndPreservesTwoGigabytes()
    {
        var offer = new FileClipboardOffer(1, 2, "file", 2_000_000_000, 3);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(offer, FileClipboardOffer.WireJson);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(2_000_000_000, document.RootElement.GetProperty("size").GetInt32());
        Assert.Equal(3u, document.RootElement.GetProperty("clipboardSequence").GetUInt32());
        Assert.Equal(offer, FileClipboardOffer.Parse(bytes));
    }
}
