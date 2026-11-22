import CryptoKit
import Darwin
import XCTest

/// The real listener on loopback, with a client doing the PC's half of ControlLink.
@MainActor
final class ControlServerTests: XCTestCase {
    private func connect(port: UInt16) throws -> Int32 {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        let result = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        XCTAssertEqual(result, 0)
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        return fd
    }

    private func write(_ fd: Int32, _ data: Data) {
        _ = data.withUnsafeBytes { Darwin.send(fd, $0.baseAddress, data.count, 0) }
    }

    private func read(_ fd: Int32, _ count: Int) throws -> Data {
        var buffer = [UInt8](repeating: 0, count: count)
        var got = 0
        while got < count {
            let received = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress!.advanced(by: got), count - got, 0) }
            guard received > 0 else { throw CompanionProtocol.Failure.malformed }
            got += received
        }
        return Data(buffer)
    }

    func testAPCConnectsProvesItsIdAndExchangesFrames() throws {
        let server = ControlServer()
        let mac = ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey())
        let pc = ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey())
        let connected = expectation(description: "connected")
        let received = expectation(description: "packet")
        var connectedID: UUID?
        var packet: CompanionProtocol.Packet?
        server.onConnect = { id, _ in
            connectedID = id
            server.send(CompanionProtocol.Packet(stream: 1, type: 1, payload: Data("hello".utf8)), to: id)
            connected.fulfill()
        }
        server.onPacket = { _, _, value in packet = value; received.fulfill() }
        server.start(identity: mac)
        defer { server.stop() }
        let port = try XCTUnwrap(server.port)

        let fd = try connect(port: port)
        defer { Darwin.close(fd) }
        let nonce = Data(repeating: 3, count: 32)
        write(fd, ControlLink.hello(identity: pc.publicKey, nonce: nonce))
        let hello = try XCTUnwrap(ControlLink.readHello(read(fd, ControlLink.helloLength)))
        XCTAssertEqual(hello.identity, mac.publicKey)
        let secret = try pc.agree(with: hello.identity)
        var outgoing = FileNetworkCrypto(secret: secret, client: nonce, server: hello.nonce, direction: "client", purpose: "control")
        var incoming = FileNetworkCrypto(secret: secret, client: nonce, server: hello.nonce, direction: "server", purpose: "control")

        wait(for: [connected], timeout: 5)
        XCTAssertEqual(connectedID, ControlLink.peerID(pc.publicKey), "the PC is named by the key it proved")
        let length = try XCTUnwrap(ControlLink.sealedLength(read(fd, 4)))
        let first = try ControlLink.open(&incoming, read(fd, length))
        XCTAssertEqual(first.payload, Data("hello".utf8))

        try write(fd, ControlLink.frame(&outgoing, stream: 0, type: 2, payload: Data([1, 2, 3, 4])))
        wait(for: [received], timeout: 5)
        XCTAssertEqual(packet, CompanionProtocol.Packet(stream: 0, type: 2, payload: Data([1, 2, 3, 4])))
    }

    func testAWrongKeyEndsTheConnection() throws {
        let server = ControlServer()
        let disconnected = expectation(description: "disconnected")
        server.onDisconnect = { _, _ in disconnected.fulfill() }
        server.start(identity: ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey()))
        defer { server.stop() }
        let fd = try connect(port: XCTUnwrap(server.port))
        defer { Darwin.close(fd) }
        let pc = ControlIdentity(privateKey: P256.KeyAgreement.PrivateKey())
        write(fd, ControlLink.hello(identity: pc.publicKey, nonce: Data(count: 32)))
        _ = try read(fd, ControlLink.helloLength)
        // A frame sealed with some other secret: the Mac cannot open it and drops the link.
        var wrong = FileNetworkCrypto(
            secret: Data(count: 32),
            client: Data(count: 32),
            server: Data(count: 32),
            direction: "client",
            purpose: "control"
        )
        try write(fd, ControlLink.frame(&wrong, stream: 0, type: 2, payload: Data([0, 0, 0, 0])))
        wait(for: [disconnected], timeout: 5)
    }
}
