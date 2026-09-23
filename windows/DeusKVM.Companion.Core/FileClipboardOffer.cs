using System.Text.Json;

namespace DeusKVM.Companion.Core;

public sealed record FileClipboardOffer(uint Epoch, uint Sequence, string Name, int Size, uint ClipboardSequence = 0, FileNetworkOffer? Network = null)
{
    public static readonly JsonSerializerOptions WireJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public const int MaximumBytes = 2_000_000_000;
    public static FileClipboardOffer Parse(byte[] data)
    {
        if (data.Length > 4096) throw new InvalidDataException("Oversized file offer");
        var offer = JsonSerializer.Deserialize<FileClipboardOffer>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (offer is null || offer.Size is < 0 or > MaximumBytes || string.IsNullOrEmpty(offer.Name) || offer.Name.Length > 240 ||
            offer.Name is "." or ".." || offer.Name.EndsWith('.') || offer.Name.EndsWith(' ') ||
            offer.Name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(offer.Name.Split('.')[0].ToUpperInvariant()))
            throw new InvalidDataException("Unsupported file offer");
        return offer.Network is not null && !offer.Network.Valid ? offer with { Network = null } : offer;
    }
}

// One bounded network block in flight. Metadata inspection never opens a connection.
public interface IFileBlockReader : IDisposable
{
    byte[] Read(uint offset);
}

public sealed class FileClipboardSession : IDisposable
{
    private readonly object readerGate = new();
    private readonly IFileBlockReader reader;
    private volatile bool canceled;
    public FileClipboardOffer Offer { get; }
    public FileClipboardSession(FileClipboardOffer offer, Action<string>? diagnostic = null)
        : this(offer, new NetworkFileReader(offer, diagnostic)) { }
    public FileClipboardSession(FileClipboardOffer offer, IFileBlockReader reader)
    { Offer = offer; this.reader = reader; }
    public void Dispose() { canceled = true; reader.Dispose(); }
    public byte[] Read(uint offset)
    {
        lock (readerGate)
        {
            if (canceled) throw new IOException("File offer expired; copy the file again.");
            if (offset > Offer.Size) throw new IOException("Invalid file position");
            if (offset == Offer.Size) return [];
            var result = reader.Read(offset);
            if (canceled) throw new IOException("File offer expired; copy the file again.");
            if (result.Length == 0 || result.Length > Math.Min(FileNetworkCrypto.MaximumBlock, Offer.Size - (int)offset))
                throw new IOException("Invalid file block length");
            return result;
        }
    }
}
