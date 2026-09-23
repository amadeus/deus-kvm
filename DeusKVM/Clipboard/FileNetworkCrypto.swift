import CryptoKit
import Foundation

struct FileNetworkOffer: Codable, Sendable {
    let hosts: [String]
    let port: UInt16
    let key: String
}

/// Directional session keys; counters are implicit and never reused in a session.
struct FileNetworkCrypto {
    static let maximumBlock = 256 * 1024
    let key: SymmetricKey
    private var counter: UInt64 = 0

    init(secret: Data, client: Data, server: Data, direction: String) {
        key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: secret),
            salt: client + server,
            info: Data("DeusKVM file v1 \(direction)".utf8),
            outputByteCount: 32
        )
    }

    static func random() -> Data {
        SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }
    }

    private mutating func nonce() throws -> AES.GCM.Nonce {
        guard counter < UInt64.max else { throw CompanionProtocol.Failure.sequence }
        let data = Data(repeating: 0, count: 4) + Data((0 ..< 8).reversed().map { UInt8(truncatingIfNeeded: counter >> ($0 * 8)) })
        counter += 1
        return try AES.GCM.Nonce(data: data)
    }

    mutating func seal(_ data: Data) throws -> Data {
        let box = try AES.GCM.seal(data, using: key, nonce: nonce())
        return box.ciphertext + box.tag
    }

    mutating func open(_ data: Data) throws -> Data {
        guard data.count >= 16 else { throw CompanionProtocol.Failure.malformed }
        let box = try AES.GCM.SealedBox(nonce: nonce(), ciphertext: data.dropLast(16), tag: data.suffix(16))
        return try AES.GCM.open(box, using: key)
    }
}
