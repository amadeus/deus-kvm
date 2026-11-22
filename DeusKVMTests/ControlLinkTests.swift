import CryptoKit
import XCTest

final class ControlLinkTests: XCTestCase {
    private func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    /// The same keys, nonces and bytes as windows/DeusKVM.Companion.Tests/ControlLinkTests.cs.
    func testTheHandshakeMatchesTheCompanion() throws {
        let mac = try ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey(rawRepresentation: Data((1 ... 32).map { UInt8($0) })))
        let pc = try ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey(rawRepresentation: Data((33 ... 64).map { UInt8($0) })))
        XCTAssertEqual(
            hex(pc.publicKey),
            "041f140146bfb1b251f84f4ddbe0d4cdcfd77afd984a9520e35794021f8312bb9e" +
                "ec995a08b1fa7704df3dcc0b50a9665263fb7711f95f9f8a449c5096e47c892b"
        )
        let secret = try mac.agree(with: pc.publicKey)
        XCTAssertEqual(hex(secret), "2d3fdc966ff636cbaf27e7b8b67ecc69b9f5ce75a694c0705a0d0b0cc2a8bcdd")
        XCTAssertEqual(try pc.agree(with: mac.publicKey), secret)
        XCTAssertEqual(ControlLink.peerID(pc.publicKey).uuidString.lowercased(), "fc0e14f6-8853-7123-8229-c03b61bdac22")
        XCTAssertEqual(ControlLink.fingerprint(pc.publicKey), "F6140EFC")
        var keys = ControlLink.serverKeys(
            secret: secret,
            client: Data(repeating: 0xC1, count: 32),
            server: Data(repeating: 0x5E, count: 32)
        )
        let frame = try ControlLink.frame(&keys.outgoing, stream: 0, type: 2, payload: Data([1, 2, 3, 4]))
        XCTAssertEqual(hex(frame), "1600000057ca202adc8104415ad5aeed1934b04cfc44996b6d37")
    }

    func testHellosAndFramesAreChecked() throws {
        let identity = ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey())
        let nonce = Data(repeating: 7, count: 32)
        var hello = ControlLink.hello(identity: identity.publicKey, nonce: nonce)
        XCTAssertEqual(hello.count, ControlLink.helloLength)
        let read = try XCTUnwrap(ControlLink.readHello(hello))
        XCTAssertEqual(read.identity, identity.publicKey)
        XCTAssertEqual(read.nonce, nonce)
        hello[4] = 2
        XCTAssertNil(ControlLink.readHello(hello))
        XCTAssertNil(ControlLink.sealedLength(Data([0, 0, 0, 0])))
        XCTAssertNil(ControlLink.sealedLength(Data([0xFF, 0xFF, 0xFF, 0x7F])))

        let secret = Data(repeating: 9, count: 32)
        var mac = ControlLink.serverKeys(secret: secret, client: Data(repeating: 1, count: 32), server: Data(repeating: 2, count: 32))
        var pcIn = FileNetworkCrypto(
            secret: secret, client: Data(repeating: 1, count: 32), server: Data(repeating: 2, count: 32), direction: "server",
            purpose: "control"
        )
        let frame = try ControlLink.frame(&mac.outgoing, stream: 1, type: 4, payload: Data(count: 60000))
        XCTAssertEqual(ControlLink.sealedLength(frame.prefix(4)), frame.count - 4)
        let packet = try ControlLink.open(&pcIn, Data(frame.dropFirst(4)))
        XCTAssertEqual(packet.stream, 1)
        XCTAssertEqual(packet.payload.count, 60000)
        XCTAssertThrowsError(try ControlLink.frame(&mac.outgoing, stream: 2, type: 1, payload: Data()))
    }

    func testTheIdentityPersists() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "control-identity-\(UUID())"))
        let first = ControlIdentity.load(defaults)
        XCTAssertEqual(ControlIdentity.load(defaults).publicKey, first.publicKey)
    }
}
