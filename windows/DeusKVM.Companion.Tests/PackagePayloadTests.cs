using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class PackagePayloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deuskvm-package-test-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "installed");
    public PackagePayloadTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); }
    public void Dispose() => Directory.Delete(root, true);
    private void WritePackage(string directory, string version, params string[] extra)
    {
        var manifest = new Dictionary<string, string>();
        foreach (var name in new[] { "DeusKVM.Companion.exe", "DeusKVM.Companion.Core.dll", "DeusKVM.Companion.exe.config" }.Concat(extra))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(version + name);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            manifest[name] = RuntimeCompat.Hex(RuntimeCompat.Sha256(bytes));
        }
        File.WriteAllText(Path.Combine(directory, PackagePayload.ManifestName), JsonSerializer.Serialize(manifest));
    }
    [Fact]
    public void LegacySingleExeUpgradeAndRollbackRestoreExactlyTheOriginal()
    {
        File.WriteAllText(Path.Combine(Destination, "DeusKVM.Companion.exe"), "legacy");
        WritePackage(Source, "new");
        using var payload = new PackagePayload(Source, Destination);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(Destination, "DeusKVM.Companion.exe")));
        payload.Activate(); Assert.True(PackagePayload.Matches(Source, Destination));
        payload.RollBack();
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(Destination, "DeusKVM.Companion.exe")));
        Assert.Single(Directory.GetFiles(Destination)); Assert.Empty(Directory.GetDirectories(Destination));
    }
    [Fact]
    public void UpdateReplacesDependenciesRemovesObsoleteDllAndPreservesUnrelatedFiles()
    {
        WritePackage(Destination, "old", "Obsolete.dll"); WritePackage(Source, "new", "Current.dll");
        File.WriteAllText(Path.Combine(Destination, "preference.txt"), "keep");
        using (var payload = new PackagePayload(Source, Destination)) { payload.Activate(); payload.Complete(); }
        Assert.True(PackagePayload.Matches(Source, Destination));
        Assert.False(File.Exists(Path.Combine(Destination, "Obsolete.dll")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Destination, "preference.txt")));
        File.AppendAllText(Path.Combine(Destination, "Current.dll"), "damaged");
        Assert.False(PackagePayload.Matches(Source, Destination));
        Assert.Empty(Directory.GetDirectories(Destination));
    }
    [Fact]
    public void PartialActivationFailureCanRollBackAlreadyReplacedExe()
    {
        WritePackage(Source, "new");
        File.WriteAllText(Path.Combine(Destination, "DeusKVM.Companion.exe"), "old");
        Directory.CreateDirectory(Path.Combine(Destination, "DeusKVM.Companion.Core.dll"));
        using var payload = new PackagePayload(Source, Destination);
        Assert.ThrowsAny<IOException>(() => payload.Activate());
        payload.RollBack();
        Assert.Equal("old", File.ReadAllText(Path.Combine(Destination, "DeusKVM.Companion.exe")));
        Assert.False(File.Exists(Path.Combine(Destination, PackagePayload.ManifestName)));
    }
    [Fact]
    public void DamagedDownloadFailsBeforeAnyInstalledFileChanges()
    {
        WritePackage(Destination, "old"); WritePackage(Source, "new");
        File.AppendAllText(Path.Combine(Source, "DeusKVM.Companion.Core.dll"), "broken");
        Assert.Throws<IOException>(() => new PackagePayload(Source, Destination));
        Assert.Equal("oldDeusKVM.Companion.exe", File.ReadAllText(Path.Combine(Destination, "DeusKVM.Companion.exe")));
        Assert.Empty(Directory.GetDirectories(Destination));
    }
    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:\\outside.dll")]
    [InlineData("DeusKVM.Companion.exe:stream.dll")]
    public void ManifestCannotWriteOutsidePayload(string name)
    {
        WritePackage(Source, "new");
        var path = Path.Combine(Source, PackagePayload.ManifestName);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        manifest[name] = new string('0', 64); File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        Assert.Throws<InvalidDataException>(() => new PackagePayload(Source, Destination));
        Assert.Empty(Directory.GetFileSystemEntries(Destination));
    }
}
