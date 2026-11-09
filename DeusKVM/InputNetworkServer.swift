import Foundation
import Network
import os

/// The Mac's end of the local-network pointer stream (NETWORK_POINTER_PLAN.md): a TCP listener
/// the PC's desktop worker connects to, the nonce handshake and keys of the file transfer path
/// under the "input" purpose, and `InputStreamState` for flow control. Everything runs on its
/// own queue. `isUp` is readable from any thread (the tap asks per pointer event) and `send`
/// may be called from the Bluetooth queue.
final class InputNetworkServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "io.github.amadeus.deuskvm.network-input", qos: .userInteractive)
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "NetworkInput")
    private let lock = NSLock()
    private var up = false
    private var port: UInt16?
    private var listener: NWListener?
    private var secret: Data?
    private var connection: NWConnection?
    private var connectionID = 0
    private var incoming: FileNetworkCrypto?
    private var outgoing: FileNetworkCrypto?
    private var state = InputStreamState()
    private var sessionStalls = 0
    private var sessionReconnects = 0
    private var timer: DispatchSourceTimer?
    private var lastStateText = ""

    /// Short state text for the settings view ("listening", "up", "stalled"), any thread.
    var onStateChange: (@Sendable (String) -> Void)?
    /// The stream went up or down; the tracker's lone-finger policy follows it. Any thread.
    var onUpChanged: (@Sendable (Bool) -> Void)?

    var isUp: Bool {
        lock.withLock { up }
    }

    var currentPort: UInt16? {
        lock.withLock { port }
    }

    // MARK: Main thread

    /// Keeps one listener while enabled; a failed listener is replaced on the next call.
    func configure(_ enabled: Bool) {
        queue.sync {
            if !enabled {
                guard listener != nil || connection != nil else { return }
                listener?.cancel(); listener = nil
                setPort(nil)
                revokeLocked()
                note("off")
                return
            }
            guard listener == nil else { return }
            do {
                let tcp = NWProtocolTCP.Options()
                tcp.noDelay = true // every record is one pointer step; never wait for a full segment
                let parameters = NWParameters(tls: nil, tcp: tcp)
                parameters.includePeerToPeer = false
                parameters.serviceClass = .interactiveVoice // pointer steps: lowest queueing the network offers
                let next = try NWListener(using: parameters, on: .any)
                listener = next
                next.stateUpdateHandler = { [weak self, weak next] state in
                    guard let self, let next, listener === next else { return }
                    switch state {
                    case .ready: setPort(next.port?.rawValue); note("listening")
                    case .failed: listener = nil; setPort(nil); revokeLocked(); note("listener failed")
                    default: break
                    }
                }
                next.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
                next.start(queue: queue)
                note("starting")
            } catch {
                listener = nil
                note("listener unavailable")
            }
        }
    }

    /// A fresh key for the current listener; the previous connection, if any, is dropped.
    func offer() -> InputNetworkOffer? {
        queue.sync {
            revokeLocked()
            guard let port else { return nil }
            let hosts = FileNetworkServer.addresses()
            guard !hosts.isEmpty else { return nil }
            let key = FileNetworkCrypto.random()
            secret = key
            return InputNetworkOffer(hosts: hosts, port: port, key: key.base64EncodedString())
        }
    }

    func revoke() {
        queue.sync { revokeLocked() }
    }

    // MARK: Bluetooth queue

    func send(_ event: NetworkInputEvent) {
        queue.async { [self] in
            guard up else { return }
            let now = ProcessInfo.processInfo.systemUptime
            switch event {
            case let .motion(dx, dy):
                if let record = state.motion(dx: dx, dy: dy, now: now) { transmit(record) }
            case let .button(button, down):
                for record in state.button(button, down: down, now: now) {
                    transmit(record)
                }
            }
        }
    }

    /// A remote session begins: counters restart, the connection stays.
    func resetSession() {
        queue.async { [self] in
            if up { state = InputStreamState() }
            sessionStalls = 0; sessionReconnects = 0
        }
    }

    /// One line for the session's diagnostics; empty if the stream carried nothing.
    func summary(_ completion: @escaping @Sendable (String) -> Void) {
        queue.async { [self] in
            guard state.frames + state.buttons > 0 || sessionStalls > 0 else { completion(""); return }
            completion("up=\(up) \(state.summary) sessionStalls=\(sessionStalls) reconnects=\(sessionReconnects)")
        }
    }

    // MARK: Queue only

    private func setPort(_ value: UInt16?) {
        lock.withLock { port = value }
    }

    private func setUp(_ value: Bool) {
        let changed: Bool = lock.withLock {
            defer { up = value }
            return up != value
        }
        if changed { onUpChanged?(value) }
    }

    private func note(_ text: String) {
        guard text != lastStateText else { return }
        lastStateText = text
        log.notice("network input: \(text, privacy: .public)")
        onStateChange?(text)
    }

    private func revokeLocked() {
        secret = nil
        close(reason: nil)
    }

    private func close(reason: String?) {
        connectionID += 1
        connection?.cancel(); connection = nil
        incoming = nil; outgoing = nil
        timer?.cancel(); timer = nil
        setUp(false)
        if let reason { note(reason) } else if listener != nil { note("listening") }
    }

    private func accept(_ next: NWConnection) {
        guard secret != nil else { next.cancel(); return }
        if connection != nil { sessionReconnects += 1 }
        close(reason: nil)
        connectionID += 1
        let id = connectionID
        connection = next
        next.stateUpdateHandler = { [weak self] state in
            guard let self, connectionID == id else { return }
            switch state {
            case .ready: note("handshake"); handshake(id)
            case .failed, .cancelled: if connection === next { close(reason: "closed") }
            default: break
            }
        }
        next.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self, connectionID == id, !up else { return }
            close(reason: "handshake timed out")
        }
    }

    private func receive(_ id: Int, count: Int, done: @escaping @Sendable (Data) -> Void) {
        guard connectionID == id, let connection else { return }
        connection.receive(minimumIncompleteLength: count, maximumLength: count) { [weak self] data, _, _, error in
            guard let self, connectionID == id else { return }
            guard error == nil, let data, data.count == count else { close(reason: "closed"); return }
            done(data)
        }
    }

    private func handshake(_ id: Int) {
        receive(id, count: 32) { [weak self] client in
            guard let self, let key = secret, let connection, connectionID == id else { return }
            let server = FileNetworkCrypto.random()
            incoming = FileNetworkCrypto(secret: key, client: client, server: server, direction: "client", purpose: "input")
            outgoing = FileNetworkCrypto(secret: key, client: client, server: server, direction: "server", purpose: "input")
            connection.send(content: server, completion: .contentProcessed { [weak self] error in
                guard let self, connectionID == id else { return }
                guard error == nil else { close(reason: "closed"); return }
                state = InputStreamState()
                setUp(true)
                note("up")
                readAcks(id)
                startTimer(id)
            })
        }
    }

    private func readAcks(_ id: Int) {
        receive(id, count: InputRecord.sealedAckLength) { [weak self] sealed in
            guard let self, connectionID == id else { return }
            do {
                guard let plain = try incoming?.open(sealed), let ack = InputRecord.ack(plain) else { close(reason: "bad ack"); return }
                if let record = state
                    .acked(ack.sequence, received: ack.received, now: ProcessInfo.processInfo.systemUptime) { transmit(record) }
                readAcks(id)
            } catch {
                close(reason: "bad ack")
            }
        }
    }

    private func startTimer(_ id: Int) {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.25, repeating: 0.25)
        timer.setEventHandler { [weak self] in
            guard let self, connectionID == id, up else { return }
            let now = ProcessInfo.processInfo.systemUptime
            if state.stalled(now: now) {
                state.noteStall(); sessionStalls += 1
                close(reason: "stalled")
                return
            }
            if let ping = state.ping(now: now) { transmit(ping) }
        }
        timer.resume()
        self.timer = timer
    }

    private func transmit(_ record: Data) {
        guard let connection, up else { return }
        do {
            guard let sealed = try outgoing?.seal(record), sealed.count <= 255 else { close(reason: "bad record"); return }
            let id = connectionID
            connection.send(content: Data([UInt8(sealed.count)]) + sealed, completion: .contentProcessed { [weak self] error in
                guard let self, connectionID == id, error != nil else { return }
                close(reason: "closed")
            })
        } catch {
            close(reason: "bad record")
        }
    }
}
