using System.Text.Json;

namespace DeusKVM.Companion.Core;

// A complete answer to an ownership-scoped clipboard request. Empty and unavailable are
// final answers, not an absence of a response. File/image contents remain on their source.
public sealed record ClipboardSnapshot(uint Epoch, ulong Generation, uint Revision, string Kind,
    byte[]? Text = null, FileClipboardOffer? File = null)
{
    public const int MaximumBytes = 96 * 1024;

    public byte[] Encode()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, FileClipboardOffer.WireJson);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Clipboard snapshot too large");
        return bytes;
    }

    public static ClipboardSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Clipboard snapshot too large");
        try
        {
            var snapshot = JsonSerializer.Deserialize<ClipboardSnapshot>(bytes, FileClipboardOffer.WireJson)
                ?? throw new InvalidDataException("Empty clipboard snapshot");
            snapshot.Validate();
            return snapshot;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid clipboard snapshot", error); }
    }

    private void Validate()
    {
        if (Generation == 0) throw new InvalidDataException("Missing clipboard ownership generation");
        switch (Kind)
        {
            case "empty" or "unavailable" when Text is null && File is null:
                return;
            case "text" when File is null && Text is not null && ClipboardTransfer.Decode(Text) is not null:
                return;
            case "file" or "image" when Text is null && File is not null:
                var validated = FileClipboardOffer.Parse(JsonSerializer.SerializeToUtf8Bytes(File, FileClipboardOffer.WireJson));
                if (validated.Epoch == Epoch && validated.Sequence == Revision && validated.Network?.Valid == true &&
                    validated.IsImage == (Kind == "image")) return;
                break;
        }
        throw new InvalidDataException("Invalid clipboard snapshot content");
    }
}

public sealed record ClipboardSnapshotRequest(uint Epoch, ulong Generation, bool Request = true)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, FileClipboardOffer.WireJson);

    public static ClipboardSnapshotRequest Parse(byte[] bytes)
    {
        if (bytes.Length > 256) throw new InvalidDataException("Clipboard snapshot request too large");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("request", out var marker) || marker.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Missing clipboard snapshot request marker");
            var request = JsonSerializer.Deserialize<ClipboardSnapshotRequest>(bytes, FileClipboardOffer.WireJson);
            return request is { Request: true, Generation: > 0 } ? request
                : throw new InvalidDataException("Invalid clipboard snapshot request");
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid clipboard snapshot request", error); }
    }
}

// Revocation runs on the pipe thread, even if the clipboard STA is blocked rendering a
// requested image. A lease is never reused when the same Mac takes control again.
public sealed class RemoteClipboardLease(long source, long ownership, uint epoch)
{
    private readonly CancellationTokenSource lifetime = new();
    public long Source { get; } = source;
    public long Ownership { get; } = ownership;
    public uint Epoch { get; } = epoch;
    public bool Valid => !lifetime.IsCancellationRequested;
    public CancellationToken Token => lifetime.Token;
    public bool Matches(long source, long ownership, uint epoch) =>
        Valid && Source == source && Ownership == ownership && Epoch == epoch;
    public void Revoke() => lifetime.Cancel();
}

// A local Windows copy made after selection wins over a delayed remote snapshot. OLE may
// advance the sequence while rendering our object, which is not a new local copy.
public sealed class RemoteClipboardSelection(uint revision)
{
    private uint expected = revision;
    private uint? completed;
    public bool CanInstall(uint sourceRevision, uint current, bool ownsRemote, uint receivedAtRevision) =>
        completed != sourceRevision && (ownsRemote || current == (completed is null ? expected : receivedAtRevision));
    public void Complete(uint sourceRevision, uint revision) { completed = sourceRevision; expected = revision; }
}
