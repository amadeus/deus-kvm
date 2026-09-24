using System.Buffers.Binary;

namespace DeusKVM.Companion.Core;

public static class Protocol
{
    public const int Version = 1, ChunkSize = 20, MaximumPayload = 65536;
    public enum Message : byte
    {
        Hello = 1, Ping = 2, Pong = 3, Screens = 4, Config = 5,
        Enter = 0x11, EnterAck = 0x12, Leave = 0x13, State = 0x14, Exit = 0x15, Resume = 0x16, EnterCenter = 0x17,
        Availability = 0x18, Selection = 0x19, RequestControl = 0x1A, ReleaseAck = 0x1B,
        ClipGrab = 0x20, ClipGet = 0x21, ClipData = 0x22, ClipState = 0x23, FileOffer = 0x24, FileGet = 0x25, FileData = 0x26, FileAccept = 0x27, Nack = 0x7F
    }
    public sealed record Packet(byte Stream, byte Type, byte[] Payload);
    public static Guid Uuid(int id) => new($"d5df{id:x4}-fd35-4b5c-8fc9-39dd1c43cb1d");
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint result = uint.MaxValue;
        foreach (var value in data)
        {
            result ^= value;
            for (var bit = 0; bit < 8; bit++) result = (result >> 1) ^ ((result & 1) != 0 ? 0x82F63B78u : 0);
        }
        return ~result;
    }
    public sealed class Encoder
    {
        public byte Sequence { get; set; }
        public List<byte[]> Encode(Packet packet)
        {
            if (packet.Payload.Length > MaximumPayload || packet.Stream > 1 || (packet.Stream == 0 && packet.Payload.Length > 13)) throw new InvalidDataException("Oversized packet");
            var multi = packet.Payload.Length > ChunkSize - 7;
            var first = true;
            var offset = 0;
            List<byte[]> frames = [];
            while (true)
            {
                var capacity = ChunkSize - (first ? 7 : 2);
                var remaining = packet.Payload.Length - offset;
                var last = multi ? !first && remaining <= capacity - 4 : true;
                var count = last ? remaining : Math.Min(capacity, first ? remaining : Math.Max(0, remaining - (ChunkSize - 6)));
                var frame = new byte[(first ? 7 : 2) + count + (last && multi ? 4 : 0)];
                frame[0] = Sequence++;
                frame[1] = (byte)((packet.Stream << 4) | (first ? 1 : 0) | (last ? 2 : 0));
                if (first) { frame[2] = packet.Type; BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), (uint)packet.Payload.Length); }
                packet.Payload.AsSpan(offset, count).CopyTo(frame.AsSpan(first ? 7 : 2));
                if (last && multi) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4), Crc(packet.Payload));
                frames.Add(frame); offset += count; first = false;
                if (last) return frames;
            }
        }
    }
    public sealed class Decoder
    {
        private byte? expected;
        private Packet? partial;
        private int length;
        public Packet? Receive(byte[] frame)
        {
            if (frame.Length is < 2 or > ChunkSize || (frame[1] & 0xCC) != 0) throw new InvalidDataException("Invalid frame");
            var first = (frame[1] & 1) != 0;
            var last = (frame[1] & 2) != 0;
            var stream = (byte)(frame[1] >> 4);
            if (stream > 1) throw new InvalidDataException("Invalid stream");
            var wanted = expected;
            expected = unchecked((byte)(frame[0] + 1));
            if (wanted.HasValue && wanted != frame[0]) { partial = null; throw new InvalidDataException("Sequence gap"); }
            if (first)
            {
                if (frame.Length < 7 || partial is not null) { partial = null; throw new InvalidDataException("Invalid first frame"); }
                var declared = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(3));
                if (declared > MaximumPayload) throw new InvalidDataException("Oversized payload");
                length = (int)declared;
                var payload = frame.AsSpan(7).ToArray();
                if (last)
                {
                    if (payload.Length != length) throw new InvalidDataException("Invalid length");
                    return new Packet(stream, frame[2], payload);
                }
                if (stream == 0 || payload.Length > length) throw new InvalidDataException("Invalid multipart frame");
                partial = new Packet(stream, frame[2], payload); return null;
            }
            if (partial is null || partial.Stream != stream || (last && frame.Length < 6)) throw new InvalidDataException("Missing first frame");
            var combined = partial.Payload.Concat(frame.AsSpan(2, frame.Length - 2 - (last ? 4 : 0)).ToArray()).ToArray();
            if (combined.Length > length) { partial = null; throw new InvalidDataException("Invalid length"); }
            var packet = partial with { Payload = combined };
            if (last)
            {
                partial = null;
                if (combined.Length != length || Crc(combined) != BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(frame.Length - 4)))
                    throw new InvalidDataException("Invalid checksum or length");
                return packet;
            }
            partial = packet; return null;
        }
    }
}
