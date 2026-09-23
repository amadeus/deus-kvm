using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

public sealed record PointerSample(byte Id, int X, int Y, byte Buttons, sbyte Wheel, sbyte Pan, byte Serial)
{
    public static PointerSample Parse(byte[] payload)
    {
        if (payload.Length != 13 || payload[9] > 7) throw new InvalidDataException("Invalid pointer sample");
        return new(payload[0], BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5)), payload[9],
            unchecked((sbyte)payload[10]), unchecked((sbyte)payload[11]), payload[12]);
    }
}

// Source travel is cumulative signed wrapping 24.8. Rebase on the actual Windows
// cursor every sample so edges, clipping and physical mice cannot build up debt.
public sealed class DirectPointerState
{
    public byte? Active { get; private set; }
    private int sourceX, sourceY;
    private byte serial;
    private double remainderX, remainderY;
    public void Begin(byte id) { End(); Active = id; }
    public void End() { Active = null; sourceX = sourceY = 0; serial = 0; remainderX = remainderY = 0; }
    public bool Move(PointerSample sample, out int dx, out int dy)
    {
        dx = dy = 0;
        if (Active != sample.Id || sample.Serial != unchecked((byte)(serial + 1))) return false;
        var x = unchecked(sample.X - sourceX) / 256d + remainderX;
        var y = unchecked(sample.Y - sourceY) / 256d + remainderY;
        dx = (int)Math.Truncate(x); dy = (int)Math.Truncate(y);
        remainderX = x - dx; remainderY = y - dy;
        sourceX = sample.X; sourceY = sample.Y; serial = sample.Serial;
        return true;
    }
    public static int Normalize(int point, int origin, int size) => size <= 1 ? 0 :
        (int)Math.Clamp(Math.Round(((long)point - origin) * 65535d / (size - 1)), 0, 65535);
}
