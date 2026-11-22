import Darwin
import dnssd
import Foundation
import os

/// The Mac's end of the companion link (ControlLink): a TCP listener the PC's service connects
/// to, published over Bonjour as `_deuskvm._tcp` with the Mac's id in its TXT record. Each
/// connection gets a reading thread (handshake, then frames) and a serial queue for writes, so
/// the main actor never blocks on a socket. Packets reach the main actor in order; a new
/// connection from the same PC replaces the old one.
final class ControlServer: @unchecked Sendable {
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "ControlLink")
    private let lock = NSLock()
    private var identity: ControlIdentity?
    private var listenFD: Int32 = -1
    private var bonjour: DNSServiceRef?
    private var connections: [UUID: Connection] = [:]
    private var serial = 0
    private(set) var port: UInt16?

    /// A PC finished the handshake (main actor): its id and the connection token later callbacks carry.
    var onConnect: (@MainActor (UUID, Int) -> Void)?
    var onPacket: (@MainActor (UUID, Int, CompanionProtocol.Packet) -> Void)?
    var onDisconnect: (@MainActor (UUID, Int) -> Void)?
    /// Listener changes for the UI: whether it is up, and a line for diagnostics.
    var onState: (@MainActor (Bool, String) -> Void)?

    private static let maximumConnections = 8
    /// Bytes waiting to be written before a connection that stopped reading is dropped.
    private static let maximumBacklog = 8 << 20

    private final class Connection: @unchecked Sendable {
        let fd: Int32
        let token: Int
        let queue: DispatchQueue
        var outgoing: FileNetworkCrypto
        var backlog = 0 // under the server lock

        init(fd: Int32, token: Int, outgoing: FileNetworkCrypto) {
            self.fd = fd
            self.token = token
            self.outgoing = outgoing
            queue = DispatchQueue(label: "io.github.amadeus.deuskvm.control-link.\(token)", qos: .userInitiated)
        }
    }

    var id: UUID? {
        lock.withLock { identity.map { ControlLink.peerID($0.publicKey) } }
    }

    // MARK: Lifetime (main actor)

    func start(identity: ControlIdentity) {
        enum Outcome { case running, failed, started(Int32, UInt16, Bool) }
        let outcome: Outcome = lock.withLock {
            guard listenFD < 0 else { return .running }
            self.identity = identity
            guard let (fd, port) = Self.listen(preferred: ControlLink.defaultPort) ?? Self.listen(preferred: 0) else { return .failed }
            listenFD = fd
            self.port = port
            bonjour = Self.publish(port: port, id: ControlLink.peerID(identity.publicKey))
            return .started(fd, port, bonjour != nil)
        }
        switch outcome {
        case .running: return
        case .failed: report(false, "control link: listener unavailable")
        case let .started(fd, port, published):
            let thread = Thread { [weak self] in self?.acceptLoop(fd) }
            thread.name = "io.github.amadeus.deuskvm.control-link"
            thread.start()
            report(true, "control link: listening on port \(port)\(published ? "" : " (Bonjour unavailable)")")
        }
    }

    func stop() {
        let service: DNSServiceRef? = lock.withLock { defer { bonjour = nil }; return bonjour }
        let (fd, open): (Int32, [Connection]) = lock.withLock {
            let state = (listenFD, Array(connections.values))
            listenFD = -1; port = nil; connections.removeAll()
            return state
        }
        if let service { DNSServiceRefDeallocate(service) }
        for connection in open {
            shutdown(connection.fd, SHUT_RDWR)
        }
        if fd >= 0 { Darwin.close(fd) }
        report(false, "control link: stopped")
    }

    /// One packet to a PC; dropped if it is not connected. Never blocks the caller.
    func send(_ packet: CompanionProtocol.Packet, to id: UUID) {
        guard let connection = lock.withLock({ connections[id] }) else { return }
        let size = packet.payload.count + 22
        let overflow: Bool = lock.withLock {
            connection.backlog += size
            return connection.backlog > Self.maximumBacklog
        }
        if overflow {
            log.error("control link backlog full; dropping the connection")
            shutdown(connection.fd, SHUT_RDWR)
            return
        }
        connection.queue.async { [self] in
            defer { lock.withLock { connection.backlog -= size } }
            guard let frame = try? ControlLink.frame(
                      &connection.outgoing,
                      stream: packet.stream,
                      type: packet.type,
                      payload: packet.payload
                  ),
                  Self.sendAll(connection.fd, frame)
            else {
                shutdown(connection.fd, SHUT_RDWR) // the reading thread ends the connection
                return
            }
        }
    }

    /// Ends a PC's connection (a protocol error); its disconnect callback follows.
    func close(_ id: UUID) {
        if let connection = lock.withLock({ connections[id] }) { shutdown(connection.fd, SHUT_RDWR) }
    }

    // MARK: Threads

    private func acceptLoop(_ listen: Int32) {
        while true {
            let fd = accept(listen, nil, nil)
            if fd < 0 {
                if errno == EINTR || errno == ECONNABORTED { continue }
                break // listener closed
            }
            let full = lock.withLock { connections.count >= Self.maximumConnections || listenFD != listen }
            if full { Darwin.close(fd); continue }
            let thread = Thread { [weak self] in self?.serve(fd) }
            thread.name = "io.github.amadeus.deuskvm.control-link.peer"
            thread.start()
        }
    }

    private func serve(_ fd: Int32) {
        defer { Darwin.close(fd) }
        Self.configure(fd)
        guard let identity = lock.withLock({ identity }),
              let hello = Self.recvExact(fd, ControlLink.helloLength), let peer = ControlLink.readHello(hello) else { return }
        let nonce = FileNetworkCrypto.random()
        guard Self.sendAll(fd, ControlLink.hello(identity: identity.publicKey, nonce: nonce)),
              let secret = try? identity.agree(with: peer.identity) else { return }
        var keys = ControlLink.serverKeys(secret: secret, client: peer.nonce, server: nonce)
        // The handshake had a deadline; frames may be minutes apart (keepalive watches the peer).
        var none = timeval(tv_sec: 0, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &none, socklen_t(MemoryLayout<timeval>.size))
        let id = ControlLink.peerID(peer.identity)
        let (connection, replaced): (Connection, Connection?) = lock.withLock {
            serial += 1
            let connection = Connection(fd: fd, token: serial, outgoing: keys.outgoing)
            let replaced = connections[id]
            connections[id] = connection
            return (connection, replaced)
        }
        if let replaced { shutdown(replaced.fd, SHUT_RDWR) }
        let token = connection.token
        log.info("control link: PC \(ControlLink.fingerprint(peer.identity), privacy: .public) connected")
        DispatchQueue.main.async { [self] in MainActor.assumeIsolated { onConnect?(id, token) } }
        while let prefix = Self.recvExact(fd, 4), let length = ControlLink.sealedLength(prefix),
              let sealed = Self.recvExact(fd, length), let packet = try? ControlLink.open(&keys.incoming, sealed)
        {
            DispatchQueue.main.async { [self] in MainActor.assumeIsolated { onPacket?(id, token, packet) } }
        }
        lock.withLock { if connections[id] === connection { connections.removeValue(forKey: id) } }
        DispatchQueue.main.async { [self] in MainActor.assumeIsolated { onDisconnect?(id, token) } }
    }

    private func report(_ up: Bool, _ line: String) {
        DispatchQueue.main.async { [self] in MainActor.assumeIsolated { onState?(up, line) } }
    }

    // MARK: Sockets

    private static func listen(preferred: UInt16) -> (Int32, UInt16)? {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &yes, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_addr.s_addr = 0 // INADDR_ANY
        addr.sin_port = preferred.bigEndian
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0, Darwin.listen(fd, 8) == 0 else { Darwin.close(fd); return nil }
        var local = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        return (fd, UInt16(bigEndian: local.sin_port))
    }

    private static func configure(_ fd: Int32) {
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
        // A PC that vanishes (power loss, cable) is noticed in about 20 s even with no traffic.
        setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &one, socklen_t(MemoryLayout<Int32>.size))
        var idle: Int32 = 10, interval: Int32 = 5, count: Int32 = 2
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPALIVE, &idle, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPINTVL, &interval, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPCNT, &count, socklen_t(MemoryLayout<Int32>.size))
        var handshake = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &handshake, socklen_t(MemoryLayout<timeval>.size))
        var write = timeval(tv_sec: 10, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &write, socklen_t(MemoryLayout<timeval>.size))
    }

    /// Registers the service under the Mac's computer name (the system's default instance name).
    private static func publish(port: UInt16, id: UUID) -> DNSServiceRef? {
        var text = TXTRecordRef()
        TXTRecordCreate(&text, 0, nil)
        defer { TXTRecordDeallocate(&text) }
        for (key, value) in [("v", "1"), ("id", id.uuidString.lowercased())] {
            let bytes = Array(value.utf8)
            _ = TXTRecordSetValue(&text, key, UInt8(bytes.count), bytes)
        }
        var service: DNSServiceRef?
        let error = DNSServiceRegister(
            &service, 0, 0, nil, ControlLink.serviceType, nil, nil, port.bigEndian,
            TXTRecordGetLength(&text), TXTRecordGetBytesPtr(&text), nil, nil
        )
        return error == kDNSServiceErr_NoError ? service : nil
    }

    private static func sendAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            var sent = 0
            while sent < raw.count {
                let count = Darwin.send(fd, raw.baseAddress!.advanced(by: sent), raw.count - sent, 0)
                if count > 0 { sent += count; continue }
                if count < 0, errno == EINTR { continue }
                return false
            }
            return true
        }
    }

    private static func recvExact(_ fd: Int32, _ count: Int) -> Data? {
        var buffer = [UInt8](repeating: 0, count: count)
        var got = 0
        while got < count {
            let received = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress!.advanced(by: got), count - got, 0) }
            if received > 0 { got += received; continue }
            if received < 0, errno == EINTR { continue }
            return nil // closed, timed out (handshake) or reset
        }
        return Data(buffer)
    }
}
