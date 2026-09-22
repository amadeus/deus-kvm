using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

// An epoch belongs to the Mac's enabled state, not to a Bluetooth connection.
public sealed class MacAvailability
{
    public bool Negotiated { get; private set; }
    public bool Available { get; private set; }
    public uint Epoch { get; private set; }
    public bool Requested { get; private set; }

    public void Hello(bool negotiated, uint epoch, bool available)
    {
        Negotiated = negotiated; Epoch = epoch; Available = available; Requested = false;
    }

    public void Receive(byte[] payload)
    {
        if (!Negotiated || payload.Length != 5 || payload[4] > 2)
            throw new InvalidDataException("Invalid Mac availability");
        Epoch = BinaryPrimitives.ReadUInt32LittleEndian(payload); Available = payload[4] != 0; Requested = payload[4] == 2;
    }

    public byte[] Selection(bool active)
    {
        var payload = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Epoch);
        payload[4] = active && Available ? (byte)1 : (byte)0;
        return payload;
    }
}
