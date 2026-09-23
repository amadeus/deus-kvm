using System.Text.Json;

namespace DeusKVM.Companion.Core;

public sealed record FileClipboardOffer(uint Epoch, uint Sequence, string Name, int Size, uint ClipboardSequence = 0)
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public static FileClipboardOffer Parse(byte[] data)
    {
        if (data.Length > 1036) throw new InvalidDataException("Oversized file offer");
        var offer = JsonSerializer.Deserialize<FileClipboardOffer>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (offer is null || offer.Size is < 0 or > MaximumBytes || string.IsNullOrEmpty(offer.Name) || offer.Name.Length > 240 ||
            offer.Name is "." or ".." || offer.Name.EndsWith('.') || offer.Name.EndsWith(' ') ||
            offer.Name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(offer.Name.Split('.')[0].ToUpperInvariant()))
            throw new InvalidDataException("Unsupported file offer");
        return offer;
    }
}

// One bounded block in flight. No request is made until a consumer actually reads.
public sealed class FileClipboardSession(FileClipboardOffer offer, Action<byte[]> request, Action<string>? diagnostic = null) : IDisposable
{
    private readonly object gate = new(), reader = new();
    private bool canceled;
    private long started, lastProgress, receivedBytes;
    private int blocks;
    private string failure = "none";
    private void Trace(string message) => diagnostic?.Invoke(message);
    private uint? waiting;
    private byte[]? response;
    public FileClipboardOffer Offer { get; } = offer;
    public void Dispose()
    {
        lock (gate)
        {
            if (!canceled && started != 0) Trace($"transfer-released received={receivedBytes} blocks={blocks} waiting={waiting?.ToString() ?? "none"}");
            canceled = true; Monitor.PulseAll(gate);
        }
    }
    public void Receive(byte[] payload)
    {
        if (payload.Length is < 12 or > 1036 || ClipboardTransfer.Read(payload, 0) != Offer.Epoch ||
            ClipboardTransfer.Read(payload, 4) != Offer.Sequence) return;
        lock (gate)
        {
            if (canceled || waiting is null) return;
            var offset = ClipboardTransfer.Read(payload, 8);
            if (offset == uint.MaxValue) { failure = "source-unavailable"; canceled = true; Monitor.PulseAll(gate); return; }
            if (offset != waiting) return;
            if (payload.Length - 12 != Math.Min(1024, Offer.Size - (int)offset)) { failure = "invalid-block-length"; canceled = true; Monitor.PulseAll(gate); return; }
            response = payload[12..]; Monitor.PulseAll(gate);
        }
    }
    public byte[] Read(uint offset)
    {
        lock (reader)
        lock (gate)
        {
            if (canceled) throw new IOException("File offer expired; copy the file again on the Mac.");
            if (offset > Offer.Size) throw new IOException("Invalid file position");
            if (offset == Offer.Size) return [];
            var now = Environment.TickCount64;
            if (started == 0) { started = now; Trace($"transfer-start size={Offer.Size} block=1024"); }
            waiting = offset; response = null;
            request(ClipboardTransfer.Header(Offer.Epoch, Offer.Sequence, offset));
            var deadline = Environment.TickCount64 + 15000;
            try
            {
                while (!canceled && response is null && Environment.TickCount64 < deadline)
                    Monitor.Wait(gate, (int)Math.Max(1, deadline - Environment.TickCount64));
                if (canceled || response is null)
                {
                    var reason = canceled ? failure == "none" ? "canceled" : failure : "block-timeout";
                    Trace($"transfer-failed reason={reason} offset={offset} received={receivedBytes} elapsedMs={Environment.TickCount64 - started}");
                    canceled = true; throw new IOException("File transfer interrupted or timed out.");
                }
                receivedBytes += response.Length; blocks++;
                now = Environment.TickCount64;
                if (blocks == 1 || now - lastProgress >= 1000 || offset + response.Length == Offer.Size)
                {
                    lastProgress = now;
                    Trace($"transfer-progress received={receivedBytes} through={offset + response.Length} size={Offer.Size} blocks={blocks} elapsedMs={now - started}");
                }
                return response;
            }
            finally { waiting = null; response = null; }
        }
    }
}
