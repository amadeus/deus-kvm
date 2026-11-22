import Foundation
import os

/// The companion protocol with each PC, over the control link (`ControlServer`). Clients are
/// keyed by the PC's id (its identity key's hash); a PC is `ready` once its HELLO arrives.
@MainActor
final class CompanionService: ObservableObject {
    @Published private(set) var ready: Set<UUID> = []
    @Published private(set) var subscribedHosts: Set<UUID> = []
    @Published private(set) var monitors: [UUID: [PCMonitor]] = [:]
    @Published private(set) var blind: [UUID: UInt8] = [:]
    private(set) var lastSeen: [UUID: TimeInterval] = [:]
    var onLeave: ((UUID, UInt8, UInt8, UInt16) -> Void)?
    var clipboardTarget: (() -> UUID?)?
    var onClipboard: ((UUID, CompanionProtocol.Message, Data) throws -> Void)?
    var onReady: ((UUID) -> Void)?
    var onActivity: ((UUID) -> Void)?
    var onComputerName: ((UUID, String) -> Void)?
    /// A problem report answered by Windows: (companion, token, text).
    var onReport: ((UUID, UInt32, String) -> Void)?
    /// The control link packets are sent on.
    var server: ControlServer?
    private var clients: [UUID: Client] = [:]
    private var selection = CompanionSelection()
    var onSelection: (() -> Void)?
    var onDisableRequested: ((Bool, @escaping () -> Void) -> Void)?
    private var wantsControl = false
    /// Link round trips while controlling Windows; diagnostics only.
    private var pings = PingTracker()
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Companion")

    private struct Client {
        let token: Int
        var supportsResume = false
        var supportsCenter = false
        var supportsClipboard = false
        var supportsFiles = false
        var supportsFileNetwork = false
        var supportsImages = false
        var supportsSelection = false
        var supportsTakeover = false
        var supportsReport = false
        var inputNetwork = 0
    }

    func setAvailability(enabled: Bool, allowed: Set<UUID>) {
        if !enabled { wantsControl = false }
        guard selection.update(enabled: enabled, allowed: allowed) else { return }
        for id in ready where clients[id]?.supportsSelection == true {
            send(.availability, payload: availabilityPacket(for: id), to: id)
        }
        onSelection?()
    }

    func requestControl() {
        wantsControl = true
        for id in ready where clients[id]?.supportsTakeover == true && selection.available(to: id) {
            send(.requestControl, payload: CompanionProtocol.u32(selection.epoch), to: id)
        }
    }

    func prepareControlRequest() {
        wantsControl = true
    }

    private func availabilityPacket(for id: UUID) -> Data {
        var packet = selection.packet(for: id)
        if wantsControl, selection.available(to: id), clients[id]?.supportsTakeover == true { packet[4] = 2 }
        return packet
    }

    func reset() {
        selection.reset()
        subscribedHosts.removeAll()
        clients.removeAll(); ready.removeAll(); monitors.removeAll(); blind.removeAll(); lastSeen.removeAll()
    }

    /// A PC finished the link handshake: the Mac's HELLO goes out first, as before.
    func connected(_ id: UUID, token: Int) {
        disconnect(id)
        clients[id] = Client(token: token)
        subscribedHosts.insert(id)
        sendJSON(CompanionHello(
            v: 1, role: "mac", name: Host.current().localizedName ?? "Mac", chunk: CompanionProtocol.maximumChunkSize,
            clipboard: 1, files: 1, fileReceive: 1, fileNetwork: 1, images: 1,
            selection: 1, available: selection.available(to: id), availabilityEpoch: selection.epoch,
            takeover: true, requestControl: wantsControl
        ), type: .hello, to: id)
    }

    /// The link to a PC closed; a stale token (an older, replaced connection) is ignored.
    func disconnected(_ id: UUID, token: Int) {
        guard clients[id]?.token == token else { return }
        disconnect(id)
    }

    func receive(_ packet: CompanionProtocol.Packet, from id: UUID, token: Int) {
        guard clients[id]?.token == token else { return }
        do {
            try handle(packet, from: id)
        } catch {
            log.error("invalid companion message: \(String(describing: error), privacy: .public)")
            disconnect(id)
            server?.close(id)
        }
    }

    private func disconnect(_ id: UUID) {
        selection.disconnect(id)
        if ready.contains(id) { log.notice("companion disconnected"); pings.cancel() }
        subscribedHosts.remove(id)
        clients.removeValue(forKey: id); ready.remove(id); monitors.removeValue(forKey: id)
        blind.removeValue(forKey: id); lastSeen.removeValue(forKey: id)
    }

    private func handle(_ packet: CompanionProtocol.Packet, from id: UUID) throws {
        guard let type = CompanionProtocol.Message(rawValue: packet.type) else { throw CompanionProtocol.Failure.malformed }
        if type == .hello {
            try receiveHello(packet.payload, from: id)
            return
        }
        guard ready.contains(id) else { throw CompanionProtocol.Failure.malformed }
        lastSeen[id] = ProcessInfo.processInfo.systemUptime
        defer { onActivity?(id) }
        if [.clipGrab, .clipGet, .clipData, .clipState, .fileOffer, .fileGet, .fileData, .fileAccept].contains(type) {
            try receiveClipboard(packet, type: type, from: id)
            return
        }
        try handleControl(type, payload: packet.payload, from: id)
    }

    private func handleControl(_ type: CompanionProtocol.Message, payload: Data, from id: UUID) throws {
        switch type {
        case .selection:
            try receiveSelection(payload, from: id)
        case .screens:
            try receiveScreens(payload, from: id)
        case .ping:
            guard payload.count == 4 else { throw CompanionProtocol.Failure.malformed }
            send(.pong, payload: payload, to: id)
        case .state:
            guard payload.count == 3 else { throw CompanionProtocol.Failure.malformed }
            if blind[id] != payload[0] { blind[id] = payload[0] }
        case .leave:
            guard payload.count == 4, payload[1] < 4 else { throw CompanionProtocol.Failure.malformed }
            let frac = UInt16(payload[2]) | UInt16(payload[3]) << 8
            onLeave?(id, payload[0], payload[1], frac)
        case .enterAck:
            guard payload.count == 7 else { throw CompanionProtocol.Failure.malformed }
            if blind[id] != payload[6] { blind[id] = payload[6] }
        case .report, .pong:
            try receiveDiagnostic(type, payload: payload, from: id)
        default: throw CompanionProtocol.Failure.malformed
        }
    }

    /// Replies to the Mac's own diagnostic requests: PONG round trips and problem reports.
    private func receiveDiagnostic(_ type: CompanionProtocol.Message, payload: Data, from id: UUID) throws {
        if type == .pong {
            if payload.count == 4 { pings.receive(CompanionProtocol.read32(payload, 0), now: ProcessInfo.processInfo.systemUptime) }
            return
        }
        guard payload.count >= 4 else { throw CompanionProtocol.Failure.malformed }
        let text = String(bytes: payload.dropFirst(4), encoding: .utf8) ?? "(report was not valid UTF-8)"
        onReport?(id, CompanionProtocol.read32(payload, 0), text)
    }

    private func receiveSelection(_ payload: Data, from id: UUID) throws {
        guard clients[id]?.supportsSelection == true, payload.count == 5 else { throw CompanionProtocol.Failure.malformed }
        let epoch = CompanionProtocol.read32(payload, 0)
        let current = epoch == selection.epoch
        try selection.receive(payload, from: id)
        if !selection.granted(to: id) { monitors.removeValue(forKey: id); blind.removeValue(forKey: id) }
        onSelection?()
        guard clients[id]?.supportsTakeover == true else { return }
        if payload[4] == 1, current { wantsControl = false }
        if payload[4] == 0, current || !selection.granted(to: id) {
            let acknowledge = { [weak self] in self?.send(.releaseAck, payload: CompanionProtocol.u32(epoch), to: id) }
            onDisableRequested?(current) { acknowledge() }
        }
    }

    private func receiveClipboard(_ packet: CompanionProtocol.Packet, type: CompanionProtocol.Message, from id: UUID) throws {
        guard clipboardTarget?() == id, allowsControl(id) else { return }
        guard supportsClipboard(id), packet.stream == ([.clipData, .fileOffer, .fileData, .fileAccept].contains(type) ? 1 : 0) else {
            throw CompanionProtocol.Failure.malformed
        }
        try onClipboard?(id, type, packet.payload)
    }

    private func receiveHello(_ data: Data, from id: UUID) throws {
        let hello = try JSONDecoder().decode(CompanionHello.self, from: data)
        guard hello.v == 1, hello.role == "pc", clients[id] != nil else { throw CompanionProtocol.Failure.malformed }
        clients[id]?.supportsResume = hello.resume == true
        clients[id]?.supportsCenter = hello.center == true
        clients[id]?.supportsClipboard = hello.clipboard == 1
        clients[id]?.supportsFiles = hello.files == 1
        clients[id]?.supportsFileNetwork = hello.fileNetwork == 1
        clients[id]?.supportsImages = hello.images == 1
        clients[id]?.supportsSelection = hello.selection == 1
        clients[id]?.supportsTakeover = hello.takeover == true
        clients[id]?.supportsReport = hello.report == 1
        clients[id]?.inputNetwork = hello.inputNetwork ?? 0
        selection.disconnect(id)
        if let name = hello.computerName { onComputerName?(id, name) }
        ready.insert(id)
        lastSeen[id] = ProcessInfo.processInfo.systemUptime
        if hello.selection == 1 { send(.availability, payload: availabilityPacket(for: id), to: id) }
        if wantsControl, hello.takeover == true, selection.available(to: id) {
            send(.requestControl, payload: CompanionProtocol.u32(selection.epoch), to: id)
        }
        onReady?(id)
        log.info("Windows companion handshake complete")
    }

    private func receiveScreens(_ data: Data, from id: UUID) throws {
        let screens = try JSONDecoder().decode(PCScreenInfo.self, from: data).monitors
        guard screens.count <= 32, screens.allSatisfy({ $0.w > 4 && $0.h > 4 && $0.w <= 32768 && $0.h <= 32768 }),
              Set(screens.map(\.id)).count == screens.count else { throw CompanionProtocol.Failure.malformed }
        monitors[id] = screens
        onReady?(id)
    }

    /// Sends a diagnostic PING when one is due. Every companion version answers PONG.
    func ping(_ id: UUID, now: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        guard ready.contains(id), let token = pings.next(now: now) else { return }
        send(.ping, payload: CompanionProtocol.u32(token), to: id)
    }

    func supportsReport(_ id: UUID) -> Bool {
        ready.contains(id) && clients[id]?.supportsReport == true
    }

    /// The companion's desktop worker can connect to the Mac's pointer stream.
    func supportsInputNetwork(_ id: UUID) -> Bool {
        inputNetworkLevel(id) >= 1
    }

    /// 0 none, 1 pointer only, 2 also keys, media keys and scroll.
    func inputNetworkLevel(_ id: UUID) -> Int {
        ready.contains(id) ? clients[id]?.inputNetwork ?? 0 : 0
    }

    func requestReport(_ id: UUID, token: UInt32) {
        send(.reportRequest, payload: CompanionProtocol.u32(token), to: id)
    }

    func takePings() -> PingTracker.Interval {
        pings.take()
    }

    func cancelPing() {
        pings.cancel()
    }

    func pingOutstandingAge(now: TimeInterval = ProcessInfo.processInfo.systemUptime) -> TimeInterval? {
        pings.outstandingAge(now: now)
    }

    func supportsResume(_ id: UUID) -> Bool {
        ready.contains(id) && clients[id]?.supportsResume == true
    }

    func supportsCenter(_ id: UUID) -> Bool {
        ready.contains(id) && clients[id]?.supportsCenter == true
    }

    func supportsClipboard(_ id: UUID) -> Bool {
        ready.contains(id) && clients[id]?.supportsClipboard == true
    }

    func supportsFiles(_ id: UUID) -> Bool {
        supportsClipboard(id) && clients[id]?.supportsFiles == true
    }

    func supportsFileNetwork(_ id: UUID) -> Bool {
        supportsFiles(id) && clients[id]?.supportsFileNetwork == true
    }

    func supportsImages(_ id: UUID) -> Bool {
        supportsFileNetwork(id) && clients[id]?.supportsImages == true
    }

    func allowsControl(_ id: UUID, now: TimeInterval = ProcessInfo.processInfo.systemUptime) -> Bool {
        guard selection.available(to: id), ready.contains(id), let seen = lastSeen[id], now - seen < 10 else { return false }
        return clients[id]?.supportsSelection != true || selection.granted(to: id)
    }

    func isWaiting(_ id: UUID) -> Bool {
        selection.available(to: id) && ready.contains(id) && clients[id]?.supportsSelection == true && !selection.granted(to: id)
    }

    func sendClipboard(_ type: CompanionProtocol.Message, payload: Data, to id: UUID) {
        guard clipboardTarget?() == id, allowsControl(id), supportsClipboard(id),
              payload.count <= ([.fileOffer, .fileAccept].contains(type) ? 4096 : ClipboardTransfer.blockBytes + 12) else { return }
        if [.clipData, .fileOffer, .fileData, .fileAccept].contains(type) {
            enqueue(type, stream: 1, payload: payload, to: id)
        } else {
            send(type, payload: payload, to: id)
        }
    }

    func sendJSON(_ value: some Encodable, type: CompanionProtocol.Message, to id: UUID) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        enqueue(type, stream: 1, payload: data, to: id)
    }

    func send(_ type: CompanionProtocol.Message, payload: Data, to id: UUID) {
        guard ready.contains(id), payload.count <= 13 else { return }
        enqueue(type, stream: 0, payload: payload, to: id)
    }

    private func enqueue(_ type: CompanionProtocol.Message, stream: UInt8, payload: Data, to id: UUID) {
        guard clients[id] != nil, payload.count <= CompanionProtocol.maximumPayload else { return }
        server?.send(CompanionProtocol.Packet(stream: stream, type: type.rawValue, payload: payload), to: id)
    }
}
