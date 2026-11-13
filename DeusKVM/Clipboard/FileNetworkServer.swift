import CoreWLAN
import Darwin
import Foundation

/// Serves clipboard file blocks to the PC. A BSD listener runs on its own thread and each
/// connection is served on another, with blocking I/O and a large send buffer: a block goes
/// to the kernel the moment it is sealed. Network.framework's per-send completion lagged the
/// wire by tens of milliseconds and held Mac->PC pastes to a few MB/s on a gigabit link.
/// Source reads still run on the clipboard queue, which owns the pasteboard state they check.
final class FileNetworkServer: @unchecked Sendable {
    typealias Read = @Sendable (UInt32, UInt32, UInt32, Int) -> Data?
    private static let sendBuffer: Int32 = 4 * 1024 * 1024
    private let queue: DispatchQueue
    private let read: Read
    private let lock = NSLock()
    private var generation = 0
    private var port: UInt16?
    private var secret: Data?
    private var scope: (epoch: UInt32, sequence: UInt32)?
    private var clients: Set<Int32> = []
    /// Write end of the current accept loop's wake pipe; closing it ends that loop.
    private var wake: Int32 = -1

    /// One line per finished transfer (counts and timings only) for the diagnostics log.
    var note: (@Sendable (String) -> Void)?

    init(queue: DispatchQueue, read: @escaping Read) {
        self.queue = queue; self.read = read
    }

    /// The listener binds synchronously, so an offer never waits for it to start.
    var starting: Bool {
        false
    }

    func configure(_ enabled: Bool) {
        // Closing its wake pipe ends the previous accept loop, which closes its own socket.
        let previous: Int32 = lock.withLock {
            generation += 1; port = nil
            defer { wake = -1 }
            return wake
        }
        if previous >= 0 { close(previous) }
        revoke()
        guard enabled, let (listener, bound) = LocalSocket.openListener() else { return }
        var pipeFDs: [Int32] = [-1, -1]
        guard pipe(&pipeFDs) == 0 else { close(listener); return }
        let current: Int = lock.withLock { port = bound; wake = pipeFDs[1]; return generation }
        let woken = pipeFDs[0]
        let thread = Thread { [weak self] in
            guard let self else { close(listener); close(woken); return }
            acceptLoop(listener, wake: woken, generation: current)
        }
        thread.name = "io.github.amadeus.deuskvm.file-network"
        thread.start()
    }

    func revoke() {
        lock.withLock {
            secret = nil; scope = nil
            // Each serving thread closes its own socket; shutting down here only wakes it.
            for fd in clients {
                shutdown(fd, SHUT_RDWR)
            }
            clients.removeAll()
        }
    }

    func offer(epoch: UInt32, sequence: UInt32, file: Bool) -> FileNetworkOffer? {
        revoke()
        guard file, let port = lock.withLock({ port }) else { return nil }
        let hosts = Self.addresses()
        guard !hosts.isEmpty else { return nil }
        let key = FileNetworkCrypto.random()
        lock.withLock { secret = key; scope = (epoch, sequence) }
        return FileNetworkOffer(hosts: hosts, port: port, key: key.base64EncodedString())
    }

    // MARK: Listener

    /// Sleeps until a connection or its wake pipe closes (a reconfigure), with no periodic wakeup;
    /// this thread alone closes the listener, so its descriptor is never reused under a blocked accept.
    private func acceptLoop(_ listener: Int32, wake: Int32, generation mine: Int) {
        defer { close(listener); close(wake) }
        var pollers = [pollfd(fd: listener, events: Int16(POLLIN), revents: 0), pollfd(fd: wake, events: Int16(POLLIN), revents: 0)]
        while lock.withLock({ generation == mine }) {
            pollers[0].revents = 0; pollers[1].revents = 0
            let ready = poll(&pollers, 2, -1)
            if ready < 0, errno != EINTR { return }
            if pollers[1].revents != 0 || pollers[0].revents & Int16(POLLERR | POLLHUP | POLLNVAL) != 0 { return }
            guard ready > 0, pollers[0].revents & Int16(POLLIN) != 0 else { continue }
            let fd = accept(listener, nil, nil)
            guard fd >= 0 else { continue }
            let admitted: Bool = lock.withLock {
                guard generation == mine, secret != nil, clients.count < 2 else { return false }
                clients.insert(fd)
                return true
            }
            guard admitted else { close(fd); continue }
            let thread = Thread { [weak self] in
                guard let self else { close(fd); return }
                serve(fd)
            }
            thread.name = "io.github.amadeus.deuskvm.file-network.client"
            thread.start()
        }
    }

    // MARK: Connection

    private func serve(_ fd: Int32) {
        defer {
            lock.withLock { _ = clients.remove(fd) }
            close(fd)
        }
        LocalSocket.tune(fd, sendBuffer: Self.sendBuffer)
        guard let client = receive(fd, 32, seconds: 3), let key = lock.withLock({ secret }) else { return }
        let server = FileNetworkCrypto.random()
        var incoming = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client")
        var outgoing = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server")
        guard LocalSocket.sendAll(fd, server) else { return }
        var stats = TransferStats()
        defer { if stats.blocks > 0 { note?("file-network: \(stats.summary)") } }
        // A request is a u32 length (always 32) and four sealed u32 fields with their tag.
        while let request = receive(fd, 36, seconds: 15) {
            guard CompanionProtocol.read32(request, 0) == 32,
                  let plain = try? incoming.open(Data(request.dropFirst(4))), plain.count == 16,
                  let scope = lock.withLock({ self.scope }),
                  CompanionProtocol.read32(plain, 0) == scope.epoch,
                  CompanionProtocol.read32(plain, 4) == scope.sequence else { return }
            let offset = CompanionProtocol.read32(plain, 8), count = Int(CompanionProtocol.read32(plain, 12))
            guard count > 0, count <= FileNetworkCrypto.maximumBlock else { return }
            let began = DispatchTime.now().uptimeNanoseconds
            let data = queue.sync { read(scope.epoch, scope.sequence, offset, count) }
            let sealedAt = DispatchTime.now().uptimeNanoseconds
            guard let response = try? outgoing.seal(Data([data == nil ? 1 : 0]) + (data ?? Data())) else { return }
            var frame = CompanionProtocol.u32(UInt32(response.count))
            frame.append(response)
            // A source failure is answered with its status byte, then the connection ends.
            guard LocalSocket.sendAll(fd, frame), let data else { return }
            stats.add(bytes: data.count, read: sealedAt - began, send: DispatchTime.now().uptimeNanoseconds - sealedAt)
        }
    }

    private func receive(_ fd: Int32, _ count: Int, seconds: Double) -> Data? {
        let deadline = ProcessInfo.processInfo.systemUptime + seconds
        return LocalSocket.receive(fd, count) {
            ProcessInfo.processInfo.systemUptime < deadline && lock.withLock { clients.contains(fd) }
        }
    }

    /// Private IPv4 addresses of this Mac, wired interfaces first so a peer connects over
    /// Ethernet rather than Wi-Fi when both reach the same network (Wi-Fi adds milliseconds).
    static func addresses() -> [String] {
        var first: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&first) == 0 else { return [] }
        defer { freeifaddrs(first) }
        let wifi = CWWiFiClient.shared().interface()?.interfaceName
        var wired: [String] = []
        var wireless: [String] = []
        var item = first
        while let entry = item {
            defer { item = entry.pointee.ifa_next }
            guard let address = entry.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET),
                  entry.pointee.ifa_flags & UInt32(IFF_UP) != 0,
                  entry.pointee.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(address, socklen_t(address.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0,
                  let value = String(
                      bytes: host.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) },
                      encoding: .utf8
                  ) else { continue }
            let octets = value.split(separator: ".").compactMap { Int($0) }
            guard octets.count == 4,
                  octets[0] == 10 || (octets[0] == 172 && (16 ... 31).contains(octets[1])) ||
                  (octets[0] == 192 && octets[1] == 168) || (octets[0] == 169 && octets[1] == 254) else { continue }
            let name = String(cString: entry.pointee.ifa_name)
            if name == wifi {
                if !wireless.contains(value) { wireless.append(value) }
            } else if !wired.contains(value) {
                wired.append(value)
            }
        }
        return Array((wired + wireless).prefix(4))
    }
}

/// Blocking BSD socket helpers shared by the clipboard file server and receiver.
enum LocalSocket {
    /// An IPv4 listener on an ephemeral port. A receive buffer set here, before listen, is
    /// inherited by accepted connections and sizes the window they negotiate.
    static func openListener(receiveBuffer: Int32? = nil) -> (Int32, UInt16)? {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        setOption(fd, SOL_SOCKET, SO_REUSEADDR, 1)
        if let receiveBuffer { setOption(fd, SOL_SOCKET, SO_RCVBUF, receiveBuffer) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = 0 // INADDR_ANY
        address.sin_port = 0 // ephemeral
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0, listen(fd, 4) == 0 else { close(fd); return nil }
        var local = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        guard named == 0 else { close(fd); return nil }
        return (fd, UInt16(bigEndian: local.sin_port))
    }

    /// No Nagle, no SIGPIPE, and a 1 s receive timeout so blocked reads wake to re-check
    /// revocation, cancellation and deadlines.
    static func tune(_ fd: Int32, sendBuffer: Int32? = nil) {
        setOption(fd, Int32(IPPROTO_TCP), TCP_NODELAY, 1)
        setOption(fd, SOL_SOCKET, SO_NOSIGPIPE, 1)
        if let sendBuffer { setOption(fd, SOL_SOCKET, SO_SNDBUF, sendBuffer) }
        var timeout = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
    }

    static func setOption(_ fd: Int32, _ level: Int32, _ name: Int32, _ value: Int32) {
        var value = value
        setsockopt(fd, level, name, &value, socklen_t(MemoryLayout<Int32>.size))
    }

    /// Exactly `count` bytes, or nil on close, error, or once `keepWaiting` says to stop.
    static func receive(_ fd: Int32, _ count: Int, keepWaiting: () -> Bool) -> Data? {
        var buffer = Data(count: count)
        var got = 0
        while got < count {
            let n = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress!.advanced(by: got), count - got, 0) }
            if n > 0 { got += n; continue }
            if n == 0 { return nil } // peer closed
            guard errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK, keepWaiting() else { return nil }
        }
        return buffer
    }

    static func sendAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            var sent = 0
            while sent < raw.count {
                let n = Darwin.send(fd, raw.baseAddress!.advanced(by: sent), raw.count - sent, 0)
                if n > 0 { sent += n; continue }
                if n < 0, errno == EINTR { continue }
                return false
            }
            return true
        }
    }
}

/// Per-connection throughput: where a served block's time went (source read vs socket send).
private struct TransferStats {
    private(set) var blocks = 0
    private var bytes = 0
    private var readNanos: UInt64 = 0
    private var sendNanos: UInt64 = 0
    private let start = DispatchTime.now().uptimeNanoseconds

    mutating func add(bytes count: Int, read: UInt64, send: UInt64) {
        blocks += 1; bytes += count; readNanos += read; sendNanos += send
    }

    var summary: String {
        let seconds = max(Double(DispatchTime.now().uptimeNanoseconds - start) / 1e9, 0.000_001)
        let megabytes = Double(bytes) / 1_000_000
        return String(
            format: "served MB=%.1f s=%.2f MBps=%.1f blocks=%d readMs=%.0f sendMs=%.0f",
            megabytes, seconds, megabytes / seconds, blocks, Double(readNanos) / 1e6, Double(sendNanos) / 1e6
        )
    }
}
