using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeusKVM.Companion.Core;

/// <summary>
/// A long-term P-256 key that identifies this PC to Macs (and a Mac to this PC). The public key
/// travels as its 65-byte uncompressed X9.63 form; its SHA-256 names the peer.
/// </summary>
public sealed class ControlIdentity : IDisposable
{
    private readonly ECDiffieHellman key;

    private ControlIdentity(ECDiffieHellman key)
    {
        this.key = key;
        var parameters = key.ExportParameters(false);
        Public = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
    }

    public byte[] Public { get; }

    public static ControlIdentity Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The private scalar then the public key (32 + 65 bytes), for storage.</summary>
    public byte[] Export() => [.. key.ExportParameters(true).D!, .. Public];

    public static ControlIdentity Import(byte[] stored)
    {
        if (stored.Length != 97 || stored[32] != 0x04) throw new CryptographicException("Invalid identity");
        return new(ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = stored.AsSpan(0, 32).ToArray(),
            Q = new ECPoint { X = stored.AsSpan(33, 32).ToArray(), Y = stored.AsSpan(65, 32).ToArray() }
        }));
    }

    /// <summary>The shared secret with a peer: SHA-256 over a label and the ECDH result.</summary>
    public byte[] Agree(byte[] peerPublic)
    {
        if (!ValidPublic(peerPublic)) throw new CryptographicException("Invalid peer key");
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPublic.AsSpan(1, 32).ToArray(), Y = peerPublic.AsSpan(33, 32).ToArray() }
        };
        using var peer = ECDiffieHellman.Create(parameters);
        return key.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, ControlLink.SecretLabel, null);
    }

    public static bool ValidPublic(byte[] value) => value is { Length: 65 } && value[0] == 0x04;

    public void Dispose() => key.Dispose();
}

/// <summary>
/// The companion protocol over TCP, PC (client) to Mac (server). Each side first sends a
/// 102-byte hello: "DKVM", version 1, its 65-byte identity public key and a 32-byte nonce. Both
/// derive the shared secret from their identities (static ECDH, so only the holders of both
/// private keys can read or write the link) and directional AES-GCM keys from it and the two
/// nonces (<see cref="FileNetworkCrypto"/> purpose "control"). Every frame after that is a u32
/// LE sealed length then the sealed bytes of: stream (0 control, 1 bulk), message type, payload.
/// Whether the peer is trusted is decided by its identity, after the handshake.
/// </summary>
public static class ControlLink
{
    public const int Version = 1, HelloLength = 4 + 1 + 65 + 32, MaximumFrame = Protocol.MaximumPayload + 2 + 16;
    public static readonly byte[] Magic = "DKVM"u8.ToArray();
    public static readonly byte[] SecretLabel = "DeusKVM control v1"u8.ToArray();
    /// <summary>The Mac's Bonjour service type; the Mac listens on this port when it is free.</summary>
    public const string ServiceType = "_deuskvm._tcp", ServiceDomain = "local";
    public const int DefaultPort = 53400;

    public static byte[] Hello(byte[] identity, byte[] nonce)
    {
        if (!ControlIdentity.ValidPublic(identity) || nonce.Length != 32) throw new ArgumentException("Invalid hello");
        return [.. Magic, Version, .. identity, .. nonce];
    }

    public static bool TryReadHello(ReadOnlySpan<byte> hello, out byte[] identity, out byte[] nonce)
    {
        identity = nonce = [];
        if (hello.Length != HelloLength || !hello.Slice(0, 4).SequenceEqual(Magic) || hello[4] != Version) return false;
        identity = hello.Slice(5, 65).ToArray();
        nonce = hello.Slice(70, 32).ToArray();
        return ControlIdentity.ValidPublic(identity);
    }

    /// <summary>A stable id for a peer: the first 16 bytes of its public key's SHA-256.</summary>
    public static Guid PeerId(byte[] identity)
    {
        using var sha = SHA256.Create();
        return new Guid(sha.ComputeHash(identity).AsSpan(0, 16).ToArray());
    }

    /// <summary>Short fingerprint for logs and the allow prompt.</summary>
    public static string Fingerprint(byte[] identity)
    {
        using var sha = SHA256.Create();
        return RuntimeCompat.Hex(sha.ComputeHash(identity).AsSpan(0, 4).ToArray());
    }

    /// <summary>The PC's (client's) send and receive keys.</summary>
    public static (FileNetworkCrypto Outgoing, FileNetworkCrypto Incoming) ClientKeys(byte[] secret, byte[] clientNonce, byte[] serverNonce) =>
        (new FileNetworkCrypto(secret, clientNonce, serverNonce, "client", "control"),
         new FileNetworkCrypto(secret, clientNonce, serverNonce, "server", "control"));

    public static byte[] Frame(FileNetworkCrypto outgoing, byte stream, byte type, ReadOnlySpan<byte> payload)
    {
        if (stream > 1 || payload.Length > Protocol.MaximumPayload) throw new InvalidDataException("Oversized frame");
        var plain = new byte[2 + payload.Length];
        plain[0] = stream; plain[1] = type;
        payload.CopyTo(plain.AsSpan(2));
        var sealedBytes = outgoing.Seal(plain);
        var frame = new byte[4 + sealedBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)sealedBytes.Length);
        sealedBytes.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>A frame's sealed length from its 4-byte prefix, or -1 if it is out of range.</summary>
    public static int SealedLength(ReadOnlySpan<byte> prefix)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        return length is >= 18 and <= MaximumFrame ? (int)length : -1;
    }

    public static Protocol.Packet Open(FileNetworkCrypto incoming, byte[] sealedBytes)
    {
        var plain = incoming.Open(sealedBytes);
        if (plain.Length < 2 || plain[0] > 1) throw new InvalidDataException("Invalid control frame");
        return new Protocol.Packet(plain[0], plain[1], plain.AsSpan(2).ToArray());
    }
}
