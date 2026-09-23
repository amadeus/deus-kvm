using System.Buffers.Binary;
using System.Text;

namespace DeusKVM.Companion.Core;

// One requested block at a time keeps clipboard traffic below the BLE queue cap.
// Epochs isolate desktop/link lifetimes; sequences isolate individual copies.
public sealed class ClipboardTransfer(Action<Protocol.Message, byte[]> send, Action<byte[]> apply)
{
    public const int MaximumBytes = 65536, BlockBytes = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private uint epoch, sequence;
    public uint Sequence => sequence;
    private byte[]? local;
    private uint? seenOffer;
    private (uint Sequence, int Size)? offer;
    private MemoryStream? incoming;
    private bool active;
    private double requestedAt;
    private int retries;

    public static byte[]? Text(string? value)
    {
        if (value is null || value.Length > MaximumBytes * 2 || value.Contains('\0')) return null;
        try
        {
            var bytes = Utf8.GetBytes(value.Replace("\r\n", "\n").Replace('\r', '\n'));
            return bytes.Length <= MaximumBytes ? bytes : null;
        }
        catch (EncoderFallbackException) { return null; }
    }
    public static string? Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) return null;
        try { var text = Utf8.GetString(bytes); return text.Contains('\0') || text.Contains('\r') ? null : text; }
        catch (DecoderFallbackException) { return null; }
    }
    public void Reset(uint nextEpoch)
    {
        epoch = nextEpoch; seenOffer = null; local = null; offer = null; incoming?.Dispose(); incoming = null;
        sequence++; retries = 0;
    }
    public void Observe(byte[]? text, bool announce = true)
    {
        local = text is not null && Decode(text) is not null ? text.ToArray() : null;
        sequence++; offer = null; incoming?.Dispose(); incoming = null;
        if (announce) Yield();
    }
    public void Yield() => send(Protocol.Message.ClipGrab, Header(epoch, sequence, local is null ? uint.MaxValue : (uint)local.Length));
    public void SetActive(bool value, double now)
    {
        active = value;
        if (active && offer is not null && incoming is null) Request(now);
    }
    public void Tick(double now)
    {
        if (incoming is null || now - requestedAt < 5) return;
        if (++retries > 3) { offer = null; incoming.Dispose(); incoming = null; return; }
        Request(now);
    }
    public void Receive(Protocol.Message type, byte[] payload, double now)
    {
        if (payload.Length < 12 || (type != Protocol.Message.ClipData && payload.Length != 12))
            throw new InvalidDataException("Invalid clipboard packet length");
        var scope = Read(payload, 0); var id = Read(payload, 4); var value = Read(payload, 8);
        if (scope != epoch) return;
        switch (type)
        {
            case Protocol.Message.ClipGrab:
                if (value != uint.MaxValue && value > MaximumBytes) throw new InvalidDataException("Clipboard too large");
                if (seenOffer == id) return;
                seenOffer = id;
                incoming?.Dispose(); incoming = null;
                offer = value == uint.MaxValue ? null : (id, (int)value);
                retries = 0;
                if (active && offer is not null) Request(now);
                break;
            case Protocol.Message.ClipGet:
                if (id != sequence || local is null) { Yield(); return; }
                if (value > local.Length || value % BlockBytes != 0) throw new InvalidDataException("Invalid clipboard offset");
                send(Protocol.Message.ClipData, Header(epoch, id, value)
                    .Concat(local.Skip((int)value).Take(BlockBytes)).ToArray());
                break;
            case Protocol.Message.ClipData:
                if (payload.Length > BlockBytes + 12) throw new InvalidDataException("Clipboard block too large");
                if (offer is not { } current || current.Sequence != id || incoming is null || value != incoming.Length) return;
                var count = payload.Length - 12;
                if (count != Math.Min(BlockBytes, current.Size - incoming.Length)) throw new InvalidDataException("Invalid clipboard block");
                incoming.Write(payload, 12, count); retries = 0;
                if (incoming.Length < current.Size) { Request(now); return; }
                var bytes = incoming.ToArray();
                incoming.Dispose(); incoming = null; offer = null;
                if (Decode(bytes) is null) throw new InvalidDataException("Invalid clipboard UTF-8");
                local = null; sequence++; // Imported text is never offered back.
                apply(bytes);
                break;
            default: throw new InvalidDataException("Unknown clipboard packet");
        }
    }
    private void Request(double now)
    {
        if (offer is not { } current) return;
        incoming ??= new MemoryStream(current.Size);
        requestedAt = now;
        send(Protocol.Message.ClipGet, Header(epoch, current.Sequence, (uint)incoming.Length));
    }
    public static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    public static byte[] Header(uint scope, uint id, uint value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, scope);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return bytes;
    }
}
