using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ControlLinkTests
{
    // Produced by the Mac's ControlLink.swift with the same keys and nonces (see the Swift test):
    // both ends must agree byte for byte.
    private const string MacPublic = "04515c3d6eb9e396b904d3feca7f54fdcd0cc1e997bf375dca515ad0a6c3b4035f4536be3a50f318fbf9a5475902a221502bef0d57e08c53b2cc0a56f17d9f9354";
    private const string PcPublic = "041f140146bfb1b251f84f4ddbe0d4cdcfd77afd984a9520e35794021f8312bb9eec995a08b1fa7704df3dcc0b50a9665263fb7711f95f9f8a449c5096e47c892b";
    private const string Secret = "2d3fdc966ff636cbaf27e7b8b67ecc69b9f5ce75a694c0705a0d0b0cc2a8bcdd";
    private const string ServerFrame = "1600000057ca202adc8104415ad5aeed1934b04cfc44996b6d37";

    private static byte[] Hex(string hex) => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    private static byte[] Range(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void TheHandshakeMatchesTheMac()
    {
        using var pc = ControlIdentity.Import([.. Range(33), .. Hex(PcPublic)]);
        Assert.Equal(Hex(PcPublic), pc.Public);
        Assert.Equal(Hex(Secret), pc.Agree(Hex(MacPublic)));
        Assert.Equal(new Guid("fc0e14f6-8853-7123-8229-c03b61bdac22"), ControlLink.PeerId(Hex(PcPublic)));
        Assert.Equal("F6140EFC", ControlLink.Fingerprint(Hex(PcPublic)));
        var (outgoing, incoming) = ControlLink.ClientKeys(Hex(Secret), Enumerable.Repeat((byte)0xC1, 32).ToArray(), Enumerable.Repeat((byte)0x5E, 32).ToArray());
        using (outgoing)
        using (incoming)
        {
            var frame = Hex(ServerFrame);
            Assert.Equal(frame.Length - 4, ControlLink.SealedLength(frame));
            var packet = ControlLink.Open(incoming, frame.AsSpan(4).ToArray());
            Assert.Equal(0, packet.Stream);
            Assert.Equal(2, packet.Type);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, packet.Payload);
        }
    }

    [Fact]
    public void IdentitiesRoundTripAndAgree()
    {
        using var a = ControlIdentity.Create();
        using var b = ControlIdentity.Create();
        using var restored = ControlIdentity.Import(a.Export());
        Assert.Equal(a.Public, restored.Public);
        Assert.Equal(restored.Agree(b.Public), b.Agree(a.Public));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => a.Agree(new byte[65]));
    }

    [Fact]
    public void HellosAndFramesAreChecked()
    {
        var nonce = Range(0);
        var hello = ControlLink.Hello(Hex(MacPublic), nonce);
        Assert.Equal(ControlLink.HelloLength, hello.Length);
        Assert.True(ControlLink.TryReadHello(hello, out var identity, out var read));
        Assert.Equal(Hex(MacPublic), identity);
        Assert.Equal(nonce, read);
        hello[4] = 2;
        Assert.False(ControlLink.TryReadHello(hello, out _, out _));
        Assert.Equal(-1, ControlLink.SealedLength([0, 0, 0, 0]));
        Assert.Equal(-1, ControlLink.SealedLength([0xFF, 0xFF, 0xFF, 0x7F]));
        var secret = Range(9);
        var (pcOut, pcIn) = ControlLink.ClientKeys(secret, Range(1), Range(2));
        using var macIn = new FileNetworkCrypto(secret, Range(1), Range(2), "client", "control");
        var frame = ControlLink.Frame(pcOut, 1, 0x04, new byte[70000 - 4500]);
        Assert.Equal(0x04, ControlLink.Open(macIn, frame.AsSpan(4).ToArray()).Type);
        Assert.Throws<InvalidDataException>(() => ControlLink.Frame(pcOut, 2, 1, []));
        pcOut.Dispose(); pcIn.Dispose();
    }
}
