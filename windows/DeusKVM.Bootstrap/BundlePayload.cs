using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DeusKVM.Bootstrap;

// BCL only: the downloadable launcher must not need any DLLs beside itself.
internal static class BundlePayload
{
    private const long MaximumArchive = 4 * 1024 * 1024, MaximumExtracted = 16 * 1024 * 1024;
    internal static void Extract(Stream input, string expectedHash, string destination)
    {
        if (input.Length is <= 0 or > MaximumArchive) throw new InvalidDataException("Invalid embedded package size");
        using (var hash = SHA256.Create())
            if (!string.Equals(BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""), expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download is damaged. Download DeusKVM again.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (archive.Entries.Count is 0 or > 64) throw new InvalidDataException("Invalid embedded package");
        // Validate all entries before writing any files. The package format is flat.
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length is 0 or > 128 ||
                entry.FullName.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')) ||
                entry.FullName is "." or ".." || entry.FullName.EndsWith(".", StringComparison.Ordinal) ||
                !names.Add(entry.FullName) || entry.Length < 0 || entry.Length > MaximumExtracted - total)
                throw new InvalidDataException("Invalid embedded package entry");
            total += entry.Length;
        }
        foreach (var required in new[] { "DeusKVM.Companion.exe", "DeusKVM.Companion.exe.config", "DeusKVM.Companion.Core.dll", "package.json" })
            if (!names.Contains(required)) throw new InvalidDataException("Incomplete embedded package");
        if (!Directory.Exists(destination) || Directory.EnumerateFileSystemEntries(destination).Any() ||
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Package destination must be an empty private directory");
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            using var source = entry.Open();
            using var target = new FileStream(Path.Combine(destination, entry.FullName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Invalid extracted package size");
                target.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated embedded package");
        }
    }

    // Keep this small BCL-only boundary independent of the bundled companion assembly.
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
