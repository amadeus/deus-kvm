import CryptoKit
import Foundation

/// The Mac's long-term P-256 identity on the companion link; the PC's is the same shape. The
/// public key travels as its 65-byte X9.63 form and its SHA-256 names the peer.
struct ControlIdentity {
    let privateKey: P256.KeyAgreement.PrivateKey

    var publicKey: Data {
        privateKey.publicKey.x963Representation
    }

    static let defaultsKey = "companionIdentity"

    /// The stored identity, created on first use.
    static func load(_ defaults: UserDefaults = .standard) -> ControlIdentity {
        if let data = defaults.data(forKey: defaultsKey), let key = try? P256.KeyAgreement.PrivateKey(rawRepresentation: data) {
            return ControlIdentity(privateKey: key)
        }
        let key = P256.KeyAgreement.PrivateKey()
        defaults.set(key.rawRepresentation, forKey: defaultsKey)
        return ControlIdentity(privateKey: key)
    }

    /// SHA-256 over a label and the ECDH result: the same bytes .NET's DeriveKeyFromHash gives.
    func agree(with peer: Data) throws -> Data {
        let peerKey = try P256.KeyAgreement.PublicKey(x963Representation: peer)
        let shared = try privateKey.sharedSecretFromKeyAgreement(with: peerKey)
        var hash = SHA256()
        hash.update(data: ControlLink.secretLabel)
        shared.withUnsafeBytes { hash.update(bufferPointer: $0) }
        return Data(hash.finalize())
    }
}

/// The companion protocol over TCP, PC (client) to Mac (server). Each side first sends a 102-byte
/// hello: "DKVM", version 1, its 65-byte identity public key and a 32-byte nonce. Both derive the
/// shared secret from their identities (static ECDH: only the holders of both private keys can
/// read or write the link) and directional AES-GCM keys from it and the two nonces (purpose
/// "control"). Every later frame is a u32 LE sealed length, then the sealed bytes of: stream (0
/// control, 1 bulk), message type, payload. Trust is decided by the peer's identity afterwards.
enum ControlLink {
    static let version: UInt8 = 1
    static let helloLength = 4 + 1 + 65 + 32
    static let maximumFrame = CompanionProtocol.maximumPayload + 2 + 16
    static let magic = Data("DKVM".utf8)
    static let secretLabel = Data("DeusKVM control v1".utf8)
    static let serviceType = "_deuskvm._tcp"
    static let defaultPort: UInt16 = 53400

    static func hello(identity: Data, nonce: Data) -> Data {
        magic + Data([version]) + identity + nonce
    }

    static func readHello(_ data: Data) -> (identity: Data, nonce: Data)? {
        guard data.count == helloLength, data.prefix(4) == magic, data[data.startIndex + 4] == version else { return nil }
        let identity = Data(data.dropFirst(5).prefix(65))
        guard identity.first == 0x04 else { return nil }
        return (identity, Data(data.suffix(32)))
    }

    /// A stable id for a peer: the first 16 bytes of its public key's SHA-256, as .NET's Guid
    /// constructor lays them out (so both sides print the same id).
    static func peerID(_ identity: Data) -> UUID {
        let digest = Array(SHA256.hash(data: identity).prefix(16))
        // System.Guid(byte[]) reads the first three fields little-endian.
        let bytes = [digest[3], digest[2], digest[1], digest[0], digest[5], digest[4], digest[7], digest[6]] + digest[8...]
        return UUID(uuid: (
            bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]
        ))
    }

    static func fingerprint(_ identity: Data) -> String {
        SHA256.hash(data: identity).prefix(4).map { String(format: "%02X", $0) }.joined()
    }

    /// The Mac's (server's) send and receive keys.
    static func serverKeys(secret: Data, client: Data, server: Data) -> (outgoing: FileNetworkCrypto, incoming: FileNetworkCrypto) {
        (
            FileNetworkCrypto(secret: secret, client: client, server: server, direction: "server", purpose: "control"),
            FileNetworkCrypto(secret: secret, client: client, server: server, direction: "client", purpose: "control")
        )
    }

    static func frame(_ outgoing: inout FileNetworkCrypto, stream: UInt8, type: UInt8, payload: Data) throws -> Data {
        guard stream <= 1, payload.count <= CompanionProtocol.maximumPayload else { throw CompanionProtocol.Failure.malformed }
        let sealed = try outgoing.seal(Data([stream, type]) + payload)
        return CompanionProtocol.u32(UInt32(sealed.count)) + sealed
    }

    /// A frame's sealed length from its 4-byte prefix, or nil if it is out of range.
    static func sealedLength(_ prefix: Data) -> Int? {
        let length = Int(CompanionProtocol.read32(prefix, 0))
        return (18 ... maximumFrame).contains(length) ? length : nil
    }

    static func open(_ incoming: inout FileNetworkCrypto, _ sealed: Data) throws -> CompanionProtocol.Packet {
        let plain = try incoming.open(sealed)
        guard plain.count >= 2, plain[plain.startIndex] <= 1 else { throw CompanionProtocol.Failure.malformed }
        return CompanionProtocol.Packet(
            stream: plain[plain.startIndex],
            type: plain[plain.startIndex + 1],
            payload: Data(plain.dropFirst(2))
        )
    }
}
