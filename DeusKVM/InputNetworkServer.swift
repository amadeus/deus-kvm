import Darwin
import Foundation
import os

/// The Mac's end of the local-network pointer stream (NETWORK_POINTER_PLAN.md): a TCP listener
/// the PC's desktop worker connects to, the nonce handshake and keys of the file transfer path
/// under the "input" purpose, and `InputStreamState` for flow control.
///
/// It uses a BSD socket on a dedicated thread rather than Network.framework: a pointer step is
/// written to the socket the moment it is captured, with no dispatch hop, and acknowledgements
/// are read by a blocking recv on the same thread. Sends use MSG_DONTWAIT so the Bluetooth queue
/// that calls `send` is never blocked; a step that cannot be queued at once is dropped (motion
/// coalesces, the stall timer closes a truly stuck link). All mutable state is under `lock`;
/// `isUp`, `carrier` and `currentPort` are readable from any thread.
final class InputNetworkServer: @unchecked Sendable {
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "NetworkInput")
    private let lock = NSLock()
    private let timerQueue = DispatchQueue(label: "io.github.amadeus.deuskvm.network-input.timer", qos: .utility)

    private var running = false
    private var up = false
    private var blocked = false
    private var port: UInt16?
    private var listenFD: Int32 = -1
    private var connFD: Int32 = -1
    private var secret: Data?
    private var incoming: FileNetworkCrypto?
    private var outgoing: FileNetworkCrypto?
    private var state = InputStreamState()
    private var stalledOut = false
    private var sessionStalls = 0
    private var sessionReconnects = 0
    private var everConnected = false
    private var timer: DispatchSourceTimer?
    private var lastStateText = ""

    /// Short state text for the settings view ("listening", "up", "stalled"), any thread.
    var onStateChange: (@Sendable (String) -> Void)?
    /// An edge-return request the PC carried on an acknowledgement, on the stream's thread. The
    /// PC repeats it on every acknowledgement for a short while; the receiver acts once.
    var onLeave: (@Sendable (InputLeave) -> Void)?

    /// Connected and the PC injects: the controller sends pointer events here.
    var isUp: Bool {
        lock.withLock { up && !blocked }
    }

    /// For the tap's routing: down, up, or connected but unable to inject.
    var carrier: NetworkCarrier {
        lock.withLock { up ? (blocked ? .unavailable : .up) : .down }
    }

    var currentPort: UInt16? {
        lock.withLock { port }
    }

    // MARK: Control

    func configure(_ enabled: Bool) {
        if enabled { start() } else { stop(reason: "off") }
    }

    /// A fresh key for the current listener; the previous connection, if any, is dropped so the
    /// PC reconnects with the new key.
    func offer() -> InputNetworkOffer? {
        lock.withLock {
            guard let port else { return nil }
            dropConnectionLocked()
            let hosts = FileNetworkServer.addresses()
            guard !hosts.isEmpty else { return nil }
            let key = FileNetworkCrypto.random()
            secret = key
            return InputNetworkOffer(hosts: hosts, port: port, key: key.base64EncodedString())
        }
    }

    func revoke() {
        lock.withLock { secret = nil; dropConnectionLocked() }
    }

    // MARK: Hot path

    /// Bluetooth queue: one captured pointer event. Writes inline, no dispatch hop.
    func send(_ event: NetworkInputEvent) {
        lock.withLock {
            guard up else { return }
            let now = ProcessInfo.processInfo.systemUptime
            switch event {
            case let .motion(dx, dy):
                if let record = state.motion(dx: dx, dy: dy, now: now) { writeLocked(record) }
            case let .button(button, down):
                for record in state.button(button, down: down, now: now) {
                    writeLocked(record)
                }
            }
        }
    }

    /// A remote session begins: counters restart, the connection stays.
    func resetSession() {
        lock.withLock {
            if up { state = InputStreamState() }
            sessionStalls = 0; sessionReconnects = 0
        }
    }

    /// One line for the session's diagnostics; empty if the stream carried nothing.
    func summary(_ completion: @escaping @Sendable (String) -> Void) {
        let line: String = lock.withLock {
            guard state.frames + state.buttons > 0 || sessionStalls > 0 else { return "" }
            return "up=\(up) \(state.summary) sessionStalls=\(sessionStalls) reconnects=\(sessionReconnects)"
        }
        completion(line)
    }

    // MARK: Listener

    private func start() {
        var startThread: Thread?
        var alreadyRunning = false
        lock.withLock {
            guard !running else { alreadyRunning = true; return }
            let fd = socket(AF_INET, SOCK_STREAM, 0)
            guard fd >= 0 else { return }
            var yes: Int32 = 1
            setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
            var addr = sockaddr_in()
            addr.sin_family = sa_family_t(AF_INET)
            addr.sin_addr.s_addr = 0 // INADDR_ANY
            addr.sin_port = 0 // ephemeral
            let bound = withUnsafePointer(to: &addr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
            }
            guard bound == 0, listen(fd, 1) == 0 else { close(fd); return }
            var local = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            _ = withUnsafeMutablePointer(to: &local) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) }
            }
            listenFD = fd
            port = UInt16(bigEndian: local.sin_port)
            running = true
            let thread = Thread { [weak self] in self?.acceptLoop(fd) }
            thread.name = "io.github.amadeus.deuskvm.network-input"
            startThread = thread
        }
        guard !alreadyRunning else { return }
        guard let startThread else { note("listener unavailable"); return }
        note("listening")
        startThread.start()
    }

    private func stop(reason: String) {
        let (listen, conn): (Int32, Int32) = lock.withLock {
            running = false
            secret = nil
            let (l, c) = (listenFD, connFD)
            listenFD = -1; connFD = -1
            port = nil
            setUpLocked(false)
            return (l, c)
        }
        if conn >= 0 { shutdown(conn, SHUT_RDWR); close(conn) }
        if listen >= 0 { close(listen) } // unblocks accept()
        stopTimer()
        note(reason)
    }

    /// Drops the current connection but keeps the listener (lock held).
    private func dropConnectionLocked() {
        let conn = connFD
        connFD = -1
        setUpLocked(false)
        incoming = nil; outgoing = nil
        if conn >= 0 { shutdown(conn, SHUT_RDWR) } // unblocks the recv loop, which closes it
    }

    // MARK: Dedicated thread

    private func acceptLoop(_ listen: Int32) {
        while lock.withLock({ running }) {
            let fd = accept(listen, nil, nil)
            if fd < 0 {
                if errno == EINTR { continue }
                break // listener closed
            }
            guard lock.withLock({ running }) else { close(fd); break }
            serve(fd)
        }
    }

    private func serve(_ fd: Int32) {
        var one: Int32 = 1
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
        var timeout = timeval(tv_sec: 1, tv_usec: 0) // wake to re-check `running`
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

        guard let client = recvExact(fd, 32), handshake(fd, client: client) else { close(fd); return }
        note("up")
        startTimer(fd)

        var reason = "closed"
        acks: while true {
            guard let sealed = recvExact(fd, InputRecord.sealedAckLength) else { break }
            let outcome: (ok: Bool, leave: InputLeave?) = lock.withLock {
                guard connFD == fd, let plain = try? incoming?.open(sealed), let ack = InputRecord.ack(plain) else { return (false, nil) }
                setBlockedLocked(ack.blocked)
                if let record = state.acked(ack.sequence, received: ack.received, now: ProcessInfo.processInfo.systemUptime) {
                    writeLocked(record)
                }
                return (true, ack.leave)
            }
            if !outcome.ok { reason = "bad ack"; break acks }
            if let leave = outcome.leave { onLeave?(leave) }
        }

        stopTimer()
        let stalled: Bool = lock.withLock {
            let was = stalledOut
            stalledOut = false
            if connFD == fd { connFD = -1; setUpLocked(false); incoming = nil; outgoing = nil }
            return was
        }
        close(fd)
        note(stalled ? "stalled" : reason)
    }

    private func handshake(_ fd: Int32, client: Data) -> Bool {
        let key: Data? = lock.withLock { secret }
        guard let key, client.count == 32 else { return false }
        let server = FileNetworkCrypto.random()
        let inc = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client", purpose: "input")
        let out = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server", purpose: "input")
        guard sendAll(fd, server) else { return false }
        lock.withLock {
            guard running else { return }
            if everConnected { sessionReconnects += 1 }
            everConnected = true
            connFD = fd
            incoming = inc; outgoing = out
            state = InputStreamState()
            setUpLocked(true)
        }
        return lock.withLock { connFD == fd }
    }

    // MARK: Timer (ping + stall)

    private func startTimer(_ fd: Int32) {
        stopTimer()
        let timer = DispatchSource.makeTimerSource(queue: timerQueue)
        timer.schedule(deadline: .now() + 0.25, repeating: 0.25)
        timer.setEventHandler { [weak self] in self?.tick(fd) }
        lock.withLock { self.timer = timer }
        timer.resume()
    }

    private func stopTimer() {
        let timer: DispatchSourceTimer? = lock.withLock { defer { self.timer = nil }; return self.timer }
        timer?.cancel()
    }

    private func tick(_ fd: Int32) {
        let action: (close: Bool, ping: Data?) = lock.withLock {
            guard up, connFD == fd else { return (false, nil) }
            let now = ProcessInfo.processInfo.systemUptime
            if state.stalled(now: now) {
                state.noteStall(); sessionStalls += 1; stalledOut = true
                return (true, nil)
            }
            if let ping = state.ping(now: now) { writeLocked(ping) }
            return (false, nil)
        }
        if action.close { shutdown(fd, SHUT_RDWR) } // unblocks recvExact; serve() tears down
    }

    // MARK: Socket I/O

    /// Lock held. Seals and writes one record without blocking; a step that will not fit is
    /// dropped (motion tolerates it), a hard error shuts the connection for the recv loop.
    private func writeLocked(_ record: Data) {
        guard connFD >= 0, up, let sealed = try? outgoing?.seal(record), sealed.count <= 255 else { return }
        var frame = Data([UInt8(sealed.count)])
        frame.append(sealed)
        let fd = connFD
        let sent = frame.withUnsafeBytes { Darwin.send(fd, $0.baseAddress, frame.count, Int32(MSG_DONTWAIT)) }
        if sent == frame.count { return }
        if sent < 0, errno == EAGAIN || errno == EWOULDBLOCK { return } // drop
        shutdown(fd, SHUT_RDWR) // partial or hard error: the stream is corrupt, end it
    }

    private func sendAll(_ fd: Int32, _ data: Data) -> Bool {
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

    private func recvExact(_ fd: Int32, _ count: Int) -> Data? {
        var buffer = [UInt8](repeating: 0, count: count)
        var got = 0
        while got < count {
            let n = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress!.advanced(by: got), count - got, 0) }
            if n > 0 { got += n; continue }
            if n == 0 { return nil } // peer closed
            if errno == EINTR { continue }
            if errno == EAGAIN || errno == EWOULDBLOCK { // recv timeout: keep waiting while this is still the connection
                if lock.withLock({ running && connFD == fd }) { continue }
                return nil
            }
            return nil // other error (EBADF after close, ECONNRESET, ...)
        }
        return Data(buffer)
    }

    // MARK: State helpers (lock held)

    private func setUpLocked(_ value: Bool) {
        up = value
        if !value { blocked = false }
    }

    private func setBlockedLocked(_ value: Bool) {
        guard blocked != value else { return }
        blocked = value
        // Noting calls out to user code; do it off the lock via the timer queue to avoid reentrancy.
        let text = value ? "blocked (secure desktop)" : "up"
        timerQueue.async { [weak self] in self?.note(text) }
    }

    private func note(_ text: String) {
        let changed: Bool = lock.withLock {
            guard text != lastStateText else { return false }
            lastStateText = text
            return true
        }
        guard changed else { return }
        log.notice("network input: \(text, privacy: .public)")
        onStateChange?(text)
    }
}
