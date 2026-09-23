import XCTest

final class FileNetworkCryptoTests: XCTestCase {
    private struct Vector: Decodable {
        let secret: String
        let client: String
        let server: String
        let direction: String
        let plain: [String]
        let sealed: [String]
    }

    private func bytes(_ value: String) -> Data {
        let chars = Array(value)
        return Data(stride(from: 0, to: chars.count, by: 2).map { UInt8(String(chars[$0 ..< $0 + 2]), radix: 16)! })
    }

    func testSharedEncryptedRecordsAndSequenceAuthentication() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("protocol/file-network-fixtures.json")
        for vector in try JSONDecoder().decode([Vector].self, from: Data(contentsOf: url)) {
            var sender = FileNetworkCrypto(
                secret: bytes(vector.secret),
                client: bytes(vector.client),
                server: bytes(vector.server),
                direction: vector.direction
            )
            var receiver = sender
            for index in vector.plain.indices {
                XCTAssertEqual(try sender.seal(bytes(vector.plain[index])), bytes(vector.sealed[index]))
                XCTAssertEqual(try receiver.open(bytes(vector.sealed[index])), bytes(vector.plain[index]))
            }
            XCTAssertThrowsError(try receiver.open(bytes(vector.sealed[0])))
        }
    }

    func testTamperingWrongKeyAndNewConnectionRejectOldRecords() throws {
        let key = Data(repeating: 7, count: 32), client = Data(repeating: 8, count: 32), server = Data(repeating: 9, count: 32)
        let sender = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client")
        var encoder = sender
        let encrypted = try encoder.seal(Data([1, 2, 3]))
        var damaged = encrypted; damaged[damaged.startIndex] ^= 1
        var receiver = sender
        XCTAssertThrowsError(try receiver.open(damaged))
        var wrong = FileNetworkCrypto(secret: Data(repeating: 0, count: 32), client: client, server: server, direction: "client")
        XCTAssertThrowsError(try wrong.open(encrypted))
        var next = FileNetworkCrypto(secret: key, client: Data(repeating: 1, count: 32), server: server, direction: "client")
        XCTAssertThrowsError(try next.open(encrypted))
        var reflected = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server")
        XCTAssertThrowsError(try reflected.open(encrypted))
    }
}
