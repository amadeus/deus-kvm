import Foundation
import Network

/// All state and source reads are confined to the clipboard queue, never input/main.
final class FileNetworkServer: @unchecked Sendable {
    typealias Read = @Sendable (UInt32, UInt32, UInt32, Int) -> Data?
    private let queue: DispatchQueue
    private let read: Read
    private var listener: NWListener?
    private var port: UInt16?
    private var startingAt: TimeInterval = 0
    private var secret: Data?
    private var scope: (epoch: UInt32, sequence: UInt32)?
    private var clients: [UUID: NWConnection] = [:]
    private var deadlines: [UUID: UInt64] = [:]

    init(queue: DispatchQueue, read: @escaping Read) {
        self.queue = queue; self.read = read
    }

    var starting: Bool {
        listener != nil && port == nil && ProcessInfo.processInfo.systemUptime - startingAt < 1
    }

    func configure(_ enabled: Bool) {
        listener?.cancel(); listener = nil; port = nil; revoke()
        guard enabled else { return }
        do {
            let parameters = NWParameters.tcp
            parameters.includePeerToPeer = false
            let next = try NWListener(using: parameters, on: .any)
            listener = next; startingAt = ProcessInfo.processInfo.systemUptime
            next.stateUpdateHandler = { [weak self, weak next] state in
                guard let self, let next, listener === next else { return }
                if case .ready = state { port = next.port?.rawValue }
                if case .failed = state { port = nil; listener = nil; next.cancel(); revoke() }
            }
            next.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            next.start(queue: queue)
        } catch { listener = nil }
    }

    func revoke() {
        secret = nil; scope = nil
        for connection in clients.values {
            connection.cancel()
        }
        clients.removeAll(); deadlines.removeAll()
    }

    func offer(epoch: UInt32, sequence: UInt32, file: Bool) -> FileNetworkOffer? {
        revoke()
        guard file, let port else { return nil }
        let hosts = Self.addresses()
        guard !hosts.isEmpty else { return nil }
        let key = FileNetworkCrypto.random()
        secret = key; scope = (epoch, sequence)
        return FileNetworkOffer(hosts: hosts, port: port, key: key.base64EncodedString())
    }

    private func accept(_ connection: NWConnection) {
        guard secret != nil, clients.count < 2 else { connection.cancel(); return }
        let id = UUID()
        clients[id] = connection
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready: hello(id)
            case .failed, .cancelled: close(id)
            default: break
            }
        }
        connection.start(queue: queue)
        timeout(id, seconds: 3)
    }

    private func close(_ id: UUID) {
        let connection = clients.removeValue(forKey: id)
        deadlines.removeValue(forKey: id); connection?.cancel()
    }

    private func timeout(_ id: UUID, seconds: Double) {
        let token = (deadlines[id] ?? 0) + 1
        deadlines[id] = token
        queue.asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard let self, deadlines[id] == token else { return }
            close(id)
        }
    }

    private func receive(_ id: UUID, count: Int, done: @escaping @Sendable (Data) -> Void) {
        guard let connection = clients[id] else { return }
        connection.receive(minimumIncompleteLength: count, maximumLength: count) { [weak self] data, _, _, error in
            guard let self, clients[id] != nil else { return }
            guard error == nil, let data, data.count == count else { close(id); return }
            done(data)
        }
    }

    private func hello(_ id: UUID) {
        receive(id, count: 32) { [weak self] client in
            guard let self, let key = secret, let connection = clients[id] else { return }
            let server = FileNetworkCrypto.random()
            let incoming = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client")
            let outgoing = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server")
            connection.send(content: server, completion: .contentProcessed { [weak self] error in
                guard error == nil else { self?.close(id); return }
                self?.request(id, incoming: incoming, outgoing: outgoing)
            })
        }
    }

    private func request(_ id: UUID, incoming: FileNetworkCrypto, outgoing: FileNetworkCrypto) {
        timeout(id, seconds: 15)
        receive(id, count: 4) { [weak self] header in
            guard let self else { return }
            // A request is exactly four u32 fields plus the authentication tag.
            guard CompanionProtocol.read32(header, 0) == 32 else { close(id); return }
            receive(id, count: 32) { [weak self] ciphertext in
                guard let self, let scope, let connection = clients[id] else { return }
                var incoming = incoming, outgoing = outgoing
                do {
                    let plain = try incoming.open(ciphertext)
                    guard plain.count == 16, CompanionProtocol.read32(plain, 0) == scope.epoch,
                          CompanionProtocol.read32(plain, 4) == scope.sequence else { close(id); return }
                    let offset = CompanionProtocol.read32(plain, 8), count = Int(CompanionProtocol.read32(plain, 12))
                    guard count > 0, count <= FileNetworkCrypto.maximumBlock else { close(id); return }
                    let data = read(scope.epoch, scope.sequence, offset, count)
                    let response = try outgoing.seal(Data([data == nil ? 1 : 0]) + (data ?? Data()))
                    let nextIncoming = incoming, nextOutgoing = outgoing
                    connection.send(
                        content: CompanionProtocol.u32(UInt32(response.count)) + response,
                        completion: .contentProcessed { [weak self] error in
                            guard error == nil, data != nil else { self?.close(id); return }
                            self?.request(id, incoming: nextIncoming, outgoing: nextOutgoing)
                        }
                    )
                } catch { close(id) }
            }
        }
    }

    static func addresses() -> [String] {
        var first: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&first) == 0 else { return [] }
        defer { freeifaddrs(first) }
        var result: [String] = []
        var item = first
        while let entry = item {
            defer { item = entry.pointee.ifa_next }
            guard let address = entry.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET),
                  entry.pointee.ifa_flags & UInt32(IFF_UP) != 0,
                  entry.pointee.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(address, socklen_t(address.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0
            else { continue }
            guard let value = String(bytes: host.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, encoding: .utf8) else { continue }
            let octets = value.split(separator: ".").compactMap { Int($0) }
            guard octets.count == 4,
                  octets[0] == 10 || (octets[0] == 172 && (16 ... 31).contains(octets[1])) ||
                  (octets[0] == 192 && octets[1] == 168) || (octets[0] == 169 && octets[1] == 254) else { continue }
            if !result.contains(value) { result.append(value) }
        }
        return Array(result.prefix(4))
    }
}
