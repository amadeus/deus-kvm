using System.Text;
using System.Text.Json;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ProtocolTests
{
    private sealed record Vector(byte Stream, byte Type, string Payload, byte Sequence, string[] Frames);
    [Fact]
    public void SharedWireFixturesMatchBothLanguages()
    {
        var vectors = JsonSerializer.Deserialize<Vector[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var vector in vectors)
        {
            var payload = TestBytes.FromHex(vector.Payload);
            var encoder = new Protocol.Encoder { Sequence = vector.Sequence };
            var frames = encoder.Encode(new(vector.Stream, vector.Type, payload));
            Assert.Equal(vector.Frames, frames.Select(frame => RuntimeCompat.Hex(frame).ToLowerInvariant()));
            var decoder = new Protocol.Decoder();
            Protocol.Packet? result = null;
            foreach (var frame in frames) result = decoder.Receive(frame);
            Assert.NotNull(result); Assert.Equal(payload, result.Payload);
            Assert.Equal(vector.Stream, result.Stream); Assert.Equal(vector.Type, result.Type);
        }
        Assert.Equal(0xe3069283u, Protocol.Crc(Encoding.ASCII.GetBytes("123456789")));
    }
    [Fact]
    public void CorruptionSequenceGapAndOversizeAreRejected()
    {
        List<byte[]> Frames() => new Protocol.Encoder().Encode(new(1, 4, Enumerable.Range(0, 40).Select(i => (byte)i).ToArray()));
        var frames = Frames(); var decoder = new Protocol.Decoder();
        decoder.Receive(frames[0]); Assert.Throws<InvalidDataException>(() => decoder.Receive(frames[2]));
        frames = Frames(); decoder = new(); frames[^1][^1] ^= 1;
        foreach (var frame in frames.Take(frames.Count - 1)) decoder.Receive(frame);
        Assert.Throws<InvalidDataException>(() => decoder.Receive(frames[^1]));
        Assert.Throws<InvalidDataException>(() => new Protocol.Decoder().Receive([0, 0x13, 4, 1, 0, 1, 0]));
        Assert.Throws<InvalidDataException>(() => new Protocol.Decoder().Receive([0, 0x17, 4, 0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => new Protocol.Encoder().Encode(new(0, 2, new byte[14])));
    }
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(256)]
    [InlineData(65536)]
    public void RoundTripFragmentBoundaries(int size)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var frames = new Protocol.Encoder().Encode(new(1, 4, payload));
        var decoder = new Protocol.Decoder(); Protocol.Packet? result = null;
        foreach (var frame in frames) { Assert.InRange(frame.Length, 2, 20); result = decoder.Receive(frame); }
        Assert.Equal(payload, result!.Payload);
    }
}
