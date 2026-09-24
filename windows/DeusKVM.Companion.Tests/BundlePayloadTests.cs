using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeusKVM.Bootstrap;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class BundlePayloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "deuskvm-bundle-test-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Required = ["DeusKVM.Companion.exe", "DeusKVM.Companion.exe.config", "DeusKVM.Companion.Core.dll", "package.json"];
    public BundlePayloadTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static MemoryStream Archive(params string[] extra)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var name in Required.Concat(extra))
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write("contents of " + name);
            }
        stream.Position = 0; return stream;
    }
    private static string Hash(Stream stream)
    {
        using var hash = SHA256.Create();
        var digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        stream.Position = 0; return digest;
    }
    [Fact]
    public void CompletePackageExtractsAndLeavesNoArchiveBesideFiles()
    {
        using var archive = Archive("System.Text.Json.dll");
        BundlePayload.Extract(archive, Hash(archive), directory);
        Assert.Equal(5, Directory.GetFiles(directory).Length);
        foreach (var name in Required.Append("System.Text.Json.dll"))
            Assert.Equal("contents of " + name, File.ReadAllText(Path.Combine(directory, name)));
    }
    [Fact]
    public void TamperedArchiveIsRejectedBeforeWritingFiles()
    {
        using var archive = Archive(); var checksum = Hash(archive);
        archive.Position = 20; archive.WriteByte(99); archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => BundlePayload.Extract(archive, checksum, directory));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\escape.exe")]
    [InlineData("C:\\escape.exe")]
    [InlineData("file.dll:stream")]
    [InlineData("folder/file.dll")]
    [InlineData("DEUSKVM.COMPANION.EXE")]
    public void InvalidOrDuplicateEntryCannotEscapeThePackage(string name)
    {
        using var archive = Archive(name);
        Assert.Throws<InvalidDataException>(() => BundlePayload.Extract(archive, Hash(archive), directory));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }
    [Fact]
    public void DestinationWithExistingFilesIsRejected()
    {
        File.WriteAllText(Path.Combine(directory, "keep"), "original");
        using var archive = Archive();
        Assert.Throws<IOException>(() => BundlePayload.Extract(archive, Hash(archive), directory));
        Assert.Equal("original", File.ReadAllText(Path.Combine(directory, "keep")));
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact]
    public void OversizedInflatedEntryIsRejectedBeforeWritingFiles()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("large.dll").Open())
        {
            var block = new byte[65536];
            for (var i = 0; i <= 256; i++) entry.Write(block, 0, block.Length);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => BundlePayload.Extract(stream, Hash(stream), directory));
        Assert.Empty(Directory.GetFiles(directory));
    }
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("--install", "\"--install\"")]
    [InlineData("space path\\", "\"space path\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void ForwardedArgumentsPreserveQuotesAndSlashes(string argument, string expected) =>
        Assert.Equal(expected, BundlePayload.QuoteArgument(argument));
}
