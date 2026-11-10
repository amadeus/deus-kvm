import Darwin
import Foundation

/// A paste starts a temporary Mac listener. Windows connects out, avoiding a PC firewall rule.
/// The transfer runs on one dedicated thread with BSD sockets and blocking I/O (see
/// FileNetworkServer for why Network.framework is avoided): requests stay `window` blocks
/// ahead of the replies, and each reply is written straight to the partial file.
final class FileNetworkReceiver: @unchecked Sendable {
    private static let window = 16
    private static let receiveBuffer: Int32 = 4 * 1024 * 1024
    private let offer: ClipboardFileOffer
    private let destination: URL?
    private let received: (@Sendable (Data) -> Void)?
    private let canAccess: @Sendable () -> Bool
    private let ready: @Sendable (ClipboardFileOffer) -> Void
    private let progress: @Sendable (Int) -> Void
    private let completion: @Sendable (String?) -> Void
    private let key = FileNetworkCrypto.random()
    private let lock = NSLock()
    private var started = false
    private var canceled = false
    private var sockets: Set<Int32> = []

    /// One line per transfer (counts and timings only) for the diagnostics log; set before `start`.
    var note: (@Sendable (String) -> Void)?

    init(
        offer: ClipboardFileOffer,
        destination: URL?,
        canAccess: @escaping @Sendable () -> Bool,
        ready: @escaping @Sendable (ClipboardFileOffer) -> Void,
        progress: @escaping @Sendable (Int) -> Void,
        completion: @escaping @Sendable (String?) -> Void,
        received: (@Sendable (Data) -> Void)? = nil
    ) {
        self.offer = offer; self.destination = destination; self.canAccess = canAccess
        self.ready = ready; self.progress = progress; self.completion = completion
        self.received = received
    }

    func start() {
        let begin: Bool = lock.withLock {
            guard !started else { return false }
            started = true
            return true
        }
        guard begin else { return }
        // The thread owns the receiver until the transfer ends, like the queue it replaced.
        let thread = Thread { [self] in
            let result = transfer()
            completion(lock.withLock { canceled } && result != nil ? Failure.canceled : result)
        }
        thread.name = "io.github.amadeus.deuskvm.file-receive"
        thread.start()
    }

    func cancel() {
        let unstarted: Bool = lock.withLock {
            canceled = true
            for fd in sockets {
                shutdown(fd, SHUT_RDWR) // wakes the transfer thread, which closes it
            }
            guard !started else { return false }
            started = true
            return true
        }
        if unstarted { completion(Failure.canceled) }
    }

    // MARK: Transfer (dedicated thread)

    private func transfer() -> String? {
        guard canAccess(), offer.valid, destination != nil || offer.isImage && received != nil else { return Failure.unavailable }
        guard let (listener, port) = LocalSocket.openListener(receiveBuffer: Self.receiveBuffer) else { return Failure.network }
        guard track(listener) else { close(listener); return Failure.canceled }
        let hosts = FileNetworkServer.addresses()
        guard !hosts.isEmpty else { release(listener); return Failure.network }
        var request = offer
        request.network = FileNetworkOffer(hosts: hosts, port: port, key: key.base64EncodedString())
        ready(request)
        let accepted = acceptOne(listener)
        release(listener) // one connection per paste
        guard let fd = accepted else { return isCanceled ? Failure.canceled : Failure.timedOut }
        guard track(fd) else { close(fd); return Failure.canceled }
        defer { release(fd) }
        LocalSocket.tune(fd)
        return session(fd)
    }

    private func acceptOne(_ listener: Int32) -> Int32? {
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        var poller = pollfd(fd: listener, events: Int16(POLLIN), revents: 0)
        while !isCanceled, ProcessInfo.processInfo.systemUptime < deadline {
            poller.revents = 0
            let ready = poll(&poller, 1, 250)
            if ready < 0, errno != EINTR { return nil }
            guard ready > 0 else { continue }
            let fd = accept(listener, nil, nil)
            if fd >= 0 { return fd }
        }
        return nil
    }

    private func session(_ fd: Int32) -> String? {
        guard let client = receive(fd, 32) else { return Failure.interrupted }
        let server = FileNetworkCrypto.random()
        var incoming = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client")
        var outgoing = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server")
        guard LocalSocket.sendAll(fd, server) else { return Failure.interrupted }
        var sink = destination.map { PartialFile(destination: $0) }
        defer { sink?.discard() }
        var image = Data()
        var stats = ReceiveStats()
        defer { if stats.blocks > 0 { note?("file-receive: \(stats.summary)") } }
        // An empty file still takes one request, answered by the status byte alone.
        let blocks = max(1, (offer.size + FileNetworkCrypto.maximumBlock - 1) / FileNetworkCrypto.maximumBlock)
        var requested = 0
        var expected: [Int] = []
        var offset = 0
        var lastProgress: TimeInterval = 0
        while stats.blocks < blocks {
            while expected.count < Self.window, requested < blocks {
                let count = min(FileNetworkCrypto.maximumBlock, offer.size - requested * FileNetworkCrypto.maximumBlock)
                if let failure = request(fd, block: requested, count: count, with: &outgoing) { return failure }
                requested += 1; expected.append(count)
            }
            let count = expected.removeFirst()
            let reply = reply(fd, count: count, with: &incoming, stats: &stats)
            guard let block = reply.block else { return reply.failure }
            if sink == nil { image.append(block) } else if let failure = sink?.write(block) { return failure }
            offset += count
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastProgress >= 0.1 || offset == offer.size { lastProgress = now; progress(offset) }
        }
        if sink == nil {
            guard canAccess() else { return Failure.imageCanceled }
            received?(image)
            return nil
        }
        return sink?.commit(canAccess: canAccess)
    }

    /// Sends the request for one block; a failure message, or nil once it is on the wire.
    private func request(_ fd: Int32, block: Int, count: Int, with outgoing: inout FileNetworkCrypto) -> String? {
        guard canAccess() else { return Failure.unavailableNow }
        let range = CompanionProtocol.u32(UInt32(max(1, count)))
        let plain = ClipboardTransfer.header(offer.epoch, offer.sequence, UInt32(block * FileNetworkCrypto.maximumBlock)) + range
        guard let record = try? outgoing.seal(plain) else { return Failure.authentication }
        return LocalSocket.sendAll(fd, CompanionProtocol.u32(UInt32(record.count)) + record) ? nil : Failure.sendFailed
    }

    /// Receives and opens the next reply; its block bytes, or the failure message.
    private func reply(
        _ fd: Int32, count: Int, with incoming: inout FileNetworkCrypto, stats: inout ReceiveStats
    ) -> (block: Data?, failure: String?) {
        let waited = DispatchTime.now().uptimeNanoseconds
        guard let header = receive(fd, 4) else { return (nil, Failure.lost) }
        let length = Int(CompanionProtocol.read32(header, 0))
        guard length >= 17, length <= count + 17 else { return (nil, Failure.invalid) }
        guard let sealed = receive(fd, length) else { return (nil, Failure.lost) }
        stats.add(bytes: count, wait: DispatchTime.now().uptimeNanoseconds - waited)
        guard let plain = try? incoming.open(sealed) else { return (nil, Failure.written) }
        guard plain.first == 0, plain.count == count + 1, canAccess() else { return (nil, Failure.changed) }
        return (plain.dropFirst(), nil)
    }

    private func receive(_ fd: Int32, _ count: Int) -> Data? {
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        return LocalSocket.receive(fd, count) { !isCanceled && ProcessInfo.processInfo.systemUptime < deadline }
    }

    // MARK: Socket bookkeeping (cancel wakes tracked sockets; only this thread closes them)

    private var isCanceled: Bool {
        lock.withLock { canceled }
    }

    private func track(_ fd: Int32) -> Bool {
        lock.withLock {
            guard !canceled else { return false }
            sockets.insert(fd)
            return true
        }
    }

    private func release(_ fd: Int32) {
        lock.withLock { _ = sockets.remove(fd) }
        close(fd)
    }
}

private enum Failure {
    static let unavailable = "The copied file is no longer available. Copy it again."
    static let unavailableNow = "The copied file is no longer available."
    static let network = "File transfer requires a local network connection."
    static let timedOut = "The file transfer timed out. Check the local network and copy the file again."
    static let interrupted = "The network connection was interrupted. Copy the file again to retry."
    static let sendFailed = "The network connection was interrupted."
    static let lost = "The file or connection became unavailable. Copy the file again."
    static let authentication = "The file transfer could not be authenticated."
    static let invalid = "Invalid file response."
    static let changed = "The source file changed or is no longer available. Copy it again."
    static let written = "The file could not be authenticated or written. Copy it again to retry."
    static let folder = "Cannot create a file in this Finder folder."
    static let collision = "Cannot finish the paste. A file with that name may already exist, or the folder may be read-only."
    static let imageCanceled = "Image transfer canceled."
    static let canceled = "File transfer canceled."
}

/// The paste target: a hidden partial file beside the destination, renamed into place only
/// when complete, and removed on any failure.
private struct PartialFile {
    let destination: URL
    private var temporary: URL?
    private var handle: FileHandle?

    init(destination: URL) {
        self.destination = destination
    }

    mutating func write(_ bytes: Data) -> String? {
        if handle == nil {
            let url = destination.deletingLastPathComponent().appendingPathComponent(".deuskvm-\(UUID().uuidString).partial")
            let descriptor = open(url.path, O_CREAT | O_EXCL | O_WRONLY, 0o600)
            guard descriptor >= 0 else { return Failure.folder }
            temporary = url; handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        }
        do { try handle?.write(contentsOf: bytes) } catch { return Failure.written }
        return nil
    }

    mutating func commit(canAccess: () -> Bool) -> String? {
        do { try handle?.synchronize(); try handle?.close() } catch { return Failure.written }
        handle = nil
        guard canAccess(), let temporary else { return Failure.canceled }
        // Exclusive rename is atomic and never overwrites an existing destination.
        guard renamex_np(temporary.path, destination.path, UInt32(RENAME_EXCL)) == 0 else { return Failure.collision }
        self.temporary = nil
        return nil
    }

    mutating func discard() {
        try? handle?.close(); handle = nil
        if let temporary { try? FileManager.default.removeItem(at: temporary) }
        temporary = nil
    }
}

/// Per-transfer throughput and time spent waiting on the socket.
private struct ReceiveStats {
    private(set) var blocks = 0
    private var bytes = 0
    private var waitNanos: UInt64 = 0
    private let start = DispatchTime.now().uptimeNanoseconds

    mutating func add(bytes count: Int, wait: UInt64) {
        blocks += 1; bytes += count; waitNanos += wait
    }

    var summary: String {
        let seconds = max(Double(DispatchTime.now().uptimeNanoseconds - start) / 1e9, 0.000_001)
        let megabytes = Double(bytes) / 1_000_000
        return String(
            format: "received MB=%.1f s=%.2f MBps=%.1f blocks=%d waitMs=%.0f",
            megabytes, seconds, megabytes / seconds, blocks, Double(waitNanos) / 1e6
        )
    }
}
