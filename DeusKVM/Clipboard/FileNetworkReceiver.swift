import Foundation
import Network

/// A paste starts a temporary Mac listener. Windows connects out, avoiding a PC firewall rule.
final class FileNetworkReceiver: @unchecked Sendable {
    private let queue = DispatchQueue(label: "DeusKVM.file-receive")
    private let offer: ClipboardFileOffer
    private let destination: URL?
    private let received: (@Sendable (Data) -> Void)?
    private var image = Data()
    private let canAccess: @Sendable () -> Bool
    private let ready: @Sendable (ClipboardFileOffer) -> Void
    private let progress: @Sendable (Int) -> Void
    private let completion: @Sendable (String?) -> Void
    private let key = FileNetworkCrypto.random()
    private var listener: NWListener?
    private var connection: NWConnection?
    private var incoming: FileNetworkCrypto?
    private var outgoing: FileNetworkCrypto?
    private var handle: FileHandle?
    private var temporary: URL?
    private var offset = 0
    /// Waiting for each 256 KiB reply before asking again idles the link for a round trip
    /// plus both ends' processing per block. Replies stay in request order on one connection.
    private static let window = 4
    private var requested = 0
    private var expected: [Int] = []
    private var deadline: UInt64 = 0
    private var ended = false
    private var lastProgress: TimeInterval = 0

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
        queue.async { [self] in startListener() }
    }

    func cancel() {
        queue.async { [self] in finish("File transfer canceled.") }
    }

    private func startListener() {
        guard canAccess(), offer.valid, destination != nil || offer.isImage && received != nil else {
            finish("The copied file is no longer available. Copy it again."); return
        }
        do {
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true // pipelined requests are complete records; don't hold them for ACKs
            let server = try NWListener(using: NWParameters(tls: nil, tcp: tcp), on: .any)
            listener = server
            server.stateUpdateHandler = { [weak self, weak server] state in
                guard let self, let server, !ended else { return }
                if case .ready = state {
                    let hosts = FileNetworkServer.addresses()
                    guard !hosts.isEmpty,
                          let port = server.port else { finish("File transfer requires a local network connection."); return }
                    var request = offer
                    request.network = FileNetworkOffer(hosts: hosts, port: port.rawValue, key: key.base64EncodedString())
                    ready(request)
                }
                if case .failed = state { finish("File transfer requires a local network connection.") }
            }
            server.newConnectionHandler = { [weak self] next in
                guard let self, !ended, connection == nil else { next.cancel(); return }
                connection = next
                next.stateUpdateHandler = { [weak self] state in
                    guard let self, !ended else { return }
                    if case .ready = state { hello() }
                    if case .failed = state { finish("The network connection was interrupted. Copy the file again to retry.") }
                }
                next.start(queue: queue)
            }
            server.start(queue: queue); timeout()
        } catch { finish("File transfer requires a local network connection.") }
    }

    private func timeout() {
        deadline &+= 1
        let token = deadline
        queue.asyncAfter(deadline: .now() + 15) { [weak self] in
            guard let self, !ended, deadline == token else { return }
            finish("The file transfer timed out. Check the local network and copy the file again.")
        }
    }

    private func receive(_ count: Int, then: @escaping @Sendable (Data) -> Void) {
        connection?.receive(minimumIncompleteLength: count, maximumLength: count) { [weak self] data, _, _, error in
            guard let self, !ended else { return }
            guard canAccess(), error == nil, let data, data.count == count else {
                finish("The file or connection became unavailable. Copy the file again."); return
            }
            then(data)
        }
    }

    private func send(_ data: Data, then: @escaping @Sendable () -> Void) {
        connection?.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self, !ended else { return }
            guard error == nil else { finish("The network connection was interrupted."); return }
            then()
        })
    }

    private func hello() {
        receive(32) { [weak self] client in
            guard let self else { return }
            let server = FileNetworkCrypto.random()
            incoming = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client")
            outgoing = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server")
            send(server) { [weak self] in self?.request(); self?.response() }
        }
    }

    /// An empty file still takes one request, answered by the status byte alone.
    private var blocks: Int {
        max(1, (offer.size + FileNetworkCrypto.maximumBlock - 1) / FileNetworkCrypto.maximumBlock)
    }

    private func request() {
        guard !ended, canAccess() else { finish("The copied file is no longer available."); return }
        while expected.count < Self.window, requested < blocks {
            let start = requested * FileNetworkCrypto.maximumBlock
            let count = min(FileNetworkCrypto.maximumBlock, offer.size - start)
            do {
                let range = CompanionProtocol.u32(UInt32(max(1, count)))
                let record = try outgoing!.seal(ClipboardTransfer.header(offer.epoch, offer.sequence, UInt32(start)) + range)
                requested += 1; expected.append(count)
                send(CompanionProtocol.u32(UInt32(record.count)) + record) {}
            } catch { finish("The file transfer could not be authenticated."); return }
        }
    }

    private func response() {
        guard !ended, let count = expected.first else { return }
        timeout()
        receive(4) { [weak self] header in
            guard let self else { return }
            let length = Int(CompanionProtocol.read32(header, 0))
            guard length >= 17, length <= count + 17 else { finish("Invalid file response."); return }
            receive(length) { [weak self] data in self?.block(data, count: count) }
        }
    }

    private func block(_ data: Data, count: Int) {
        do {
            let plain = try incoming!.open(data)
            guard plain.first == 0, plain.count == count + 1, canAccess() else {
                finish("The source file changed or is no longer available. Copy it again."); return
            }
            if destination == nil {
                image.append(plain.dropFirst())
            } else if handle == nil, let destination {
                let url = destination.deletingLastPathComponent().appendingPathComponent(".deuskvm-\(UUID().uuidString).partial")
                let descriptor = open(url.path, O_CREAT | O_EXCL | O_WRONLY, 0o600)
                guard descriptor >= 0 else { finish("Cannot create a file in this Finder folder."); return }
                temporary = url; handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
            }
            try handle?.write(contentsOf: plain.dropFirst())
            expected.removeFirst()
            offset += count
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastProgress >= 0.1 || offset == offer.size { lastProgress = now; progress(offset) }
            if offset < offer.size { request(); response(); return }
            if destination == nil {
                guard canAccess() else { finish("Image transfer canceled."); return }
                received?(image); image = Data(); finish(nil); return
            }
            try handle?.synchronize(); try handle?.close(); handle = nil
            guard canAccess(), let temporary, let destination else { finish("File transfer canceled."); return }
            // Exclusive rename is atomic and never overwrites an existing destination.
            guard renamex_np(temporary.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
                finish("Cannot finish the paste. A file with that name may already exist, or the folder may be read-only."); return
            }
            finish(nil)
        } catch { finish("The file could not be authenticated or written. Copy it again to retry.") }
    }

    private func finish(_ error: String?) {
        guard !ended else { return }
        ended = true; listener?.cancel(); connection?.cancel(); listener = nil; connection = nil
        image = Data()
        try? handle?.close(); handle = nil
        if let temporary { try? FileManager.default.removeItem(at: temporary) }
        completion(error)
    }
}
