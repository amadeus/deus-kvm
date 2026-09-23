namespace DeusKVM.Companion.Core;

// Metadata only until paste; no held file handle or content prefetch on copy.
public sealed record ClipboardFileSource(string Path, string Name, int Size, DateTime Modified, DateTime Created)
{
    public static ClipboardFileSource? Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                info.Length < 0 || info.Length > FileClipboardOffer.MaximumBytes) return null;
            FileClipboardOffer.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new FileClipboardOffer(0, 0, info.Name, (int)info.Length)));
            return new(info.FullName, info.Name, (int)info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public byte[]? Read(uint offset, int count)
    {
        if (offset > Size || count is <= 0 or > FileNetworkCrypto.MaximumBlock || Capture(Path) != this) return null;
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != Size) return null;
            file.Position = offset;
            var data = new byte[Math.Min(count, Size - (int)offset)]; file.ReadExactly(data);
            return Capture(Path) == this ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
