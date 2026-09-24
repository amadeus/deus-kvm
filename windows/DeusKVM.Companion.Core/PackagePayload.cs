using System.Text.Json;

namespace DeusKVM.Companion.Core;

// Transaction for the complete Framework payload. Callers stop service/workers before Activate.
public sealed class PackagePayload : IDisposable
{
    public const string ManifestName = "package.json";
    private readonly string destination, staged, backup;
    private readonly string[] names;
    private readonly List<string> saved = [], installed = [];
    private bool finished;

    private static Dictionary<string, string> ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) throw new IOException("Extract the complete DeusKVM ZIP before installing (package.json is missing).");
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Invalid package manifest");
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty package manifest");
        if (entries.Count > 64 || entries.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
            !entries.ContainsKey("DeusKVM.Companion.exe") || !entries.ContainsKey("DeusKVM.Companion.exe.config") ||
            !entries.ContainsKey("DeusKVM.Companion.Core.dll")) throw new InvalidDataException("Incomplete package manifest");
        foreach (var pair in entries)
            if (pair.Key.Length > 128 || pair.Key.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')) ||
                !(pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || pair.Key == "DeusKVM.Companion.exe" || pair.Key == "DeusKVM.Companion.exe.config") ||
                pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid package entry");
        return entries;
    }
    public static bool Matches(string source, string destination)
    {
        var expected = ReadManifest(source);
        if (!File.Exists(Path.Combine(destination, ManifestName))) return false;
        var current = ReadManifest(destination);
        return expected.Count == current.Count && expected.All(pair => current.TryGetValue(pair.Key, out var hash) &&
            string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase) && HasHash(Path.Combine(destination, pair.Key), pair.Value));
    }
    private static bool HasHash(string path, string hash)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(RuntimeCompat.Hex(RuntimeCompat.Sha256(stream)), hash, StringComparison.OrdinalIgnoreCase);
    }
    public PackagePayload(string source, string destination)
    {
        this.destination = destination;
        var entries = ReadManifest(source);
        var previous = File.Exists(Path.Combine(destination, ManifestName)) ? ReadManifest(destination).Keys : Enumerable.Empty<string>();
        names = entries.Keys.Concat(previous).Append("DeusKVM.Companion.exe").Append(ManifestName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        staged = Path.Combine(destination, "update-" + Guid.NewGuid().ToString("N"));
        backup = Path.Combine(destination, "previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            foreach (var pair in entries)
            {
                // New files inherit the protected destination ACL, never source metadata.
                var target = Path.Combine(staged, pair.Key);
                using (var input = File.OpenRead(Path.Combine(source, pair.Key)))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                if (!HasHash(target, pair.Value)) throw new IOException($"Package file is damaged: {pair.Key}. Extract the ZIP again.");
            }
            File.WriteAllText(Path.Combine(staged, ManifestName), JsonSerializer.Serialize(entries));
        }
        catch { Directory.Delete(staged, true); throw; }
    }
    public void Activate()
    {
        Directory.CreateDirectory(backup);
        foreach (var name in names)
        {
            var current = Path.Combine(destination, name);
            if (File.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation file: " + name);
                File.Move(current, Path.Combine(backup, name)); saved.Add(name);
            }
            var next = Path.Combine(staged, name);
            if (File.Exists(next)) { File.Move(next, current); installed.Add(name); }
        }
    }
    public void RollBack()
    {
        // Keep records and backup if rollback itself fails, so no recovery data is discarded.
        foreach (var name in installed.ToArray())
        { File.Delete(Path.Combine(destination, name)); installed.Remove(name); }
        foreach (var name in saved.ToArray())
        { File.Move(Path.Combine(backup, name), Path.Combine(destination, name)); saved.Remove(name); }
        finished = true;
        Cleanup();
    }
    public void Complete() { finished = true; Cleanup(); }
    private void Cleanup()
    {
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }
    public void Dispose()
    {
        // Explicit rollback must happen while the service is stopped. Never delete its backup here.
        if (!finished && Directory.Exists(staged)) Directory.Delete(staged, true);
    }
}
