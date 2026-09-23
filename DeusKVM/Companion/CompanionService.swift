import CoreBluetooth
import Foundation
import os

@MainActor
final class CompanionService: ObservableObject {
    nonisolated(unsafe) static let uuid = CBUUID(string: "d5df0001-fd35-4b5c-8fc9-39dd1c43cb1d")
    @Published private(set) var ready: Set<UUID> = []
    @Published private(set) var subscribedHosts: Set<UUID> = []
    @Published private(set) var monitors: [UUID: [PCMonitor]] = [:]
    @Published private(set) var blind: [UUID: UInt8] = [:]
    private(set) var lastSeen: [UUID: TimeInterval] = [:]
    var onLeave: ((UUID, UInt8, UInt8, UInt16) -> Void)?
    var clipboardTarget: (() -> UUID?)?
    var onClipboard: ((UUID, CompanionProtocol.Message, Data) throws -> Void)?
    var onReady: ((UUID) -> Void)?
    var onComputerName: ((UUID, String) -> Void)?
    private var manager: CBPeripheralManager?
    private var clients: [UUID: Client] = [:]
    private var controls: [Queued] = []
    private var bulk: [Queued] = []
    private var blocked = false
    private var selection = CompanionSelection()
    var onSelection: (() -> Void)?
    var onDisableRequested: ((Bool, @escaping () -> Void) -> Void)?
    private var wantsControl = false
    private var chars: [CBMutableCharacteristic] = []
    var diagnosticState: String {
        "queued=\(controls.count + bulk.count) blocked=\(blocked)"
    }

    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Companion")

    private struct Client {
        let central: CBCentral
        var subscriptions: Set<Int> = []
        var controlEncoder = CompanionProtocol.Encoder()
        var bulkEncoder = CompanionProtocol.Encoder()
        var controlDecoder = CompanionProtocol.Decoder()
        var bulkDecoder = CompanionProtocol.Decoder()
        var helloSent = false
        var supportsResume = false
        var supportsCenter = false
        var supportsClipboard = false
        var supportsFiles = false
        var supportsSelection = false
        var supportsTakeover = false
    }

    private struct Queued { let data: Data; let index: Int; let central: CBCentral }

    func build(_ manager: CBPeripheralManager) -> CBMutableService {
        reset()
        self.manager = manager
        let service = CBMutableService(type: Self.uuid, primary: true)
        let properties: [CBCharacteristicProperties] = [
            .notifyEncryptionRequired,
            .writeWithoutResponse,
            .notifyEncryptionRequired,
            .write,
            .read
        ]
        chars = (0 ..< 5).map { index in
            CBMutableCharacteristic(
                type: CBUUID(string: String(format: "d5df%04x-fd35-4b5c-8fc9-39dd1c43cb1d", index + 2)),
                properties: properties[index], value: nil,
                permissions: index == 1 || index == 3 ? .writeEncryptionRequired : .readEncryptionRequired
            )
        }
        service.characteristics = chars
        return service
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
        controls.removeAll(); bulk.removeAll(); chars.removeAll(); blocked = false; manager = nil
    }

    func owns(_ characteristic: CBCharacteristic) -> Bool {
        chars.contains { $0.uuid == characteristic.uuid }
    }

    func subscribed(_ central: CBCentral, _ characteristic: CBCharacteristic) {
        guard let index = chars.firstIndex(where: { $0.uuid == characteristic.uuid }) else { return }
        var client = clients[central.identifier] ?? Client(central: central)
        client.subscriptions.insert(index)
        let sendHello = client.subscriptions.contains(0) && client.subscriptions.contains(2) && !client.helloSent
        client.helloSent = client.helloSent || sendHello
        clients[central.identifier] = client
        if !subscribedHosts.contains(central.identifier) { subscribedHosts.insert(central.identifier) }
        if sendHello {
            sendJSON(CompanionHello(
                v: 1, role: "mac", name: "DeusKVM", chunk: 20, clipboard: 1, files: 1,
                selection: 1, available: selection.available(to: central.identifier), availabilityEpoch: selection.epoch,
                takeover: true, requestControl: wantsControl
            ), type: .hello, to: central.identifier)
        }
    }

    func unsubscribed(_ central: CBCentral) {
        disconnect(central.identifier)
    }

    private func disconnect(_ id: UUID) {
        selection.disconnect(id)
        if ready.contains(id) { log.notice("companion disconnected") }
        subscribedHosts.remove(id)
        clients.removeValue(forKey: id); ready.remove(id); monitors.removeValue(forKey: id)
        blind.removeValue(forKey: id); lastSeen.removeValue(forKey: id)
        controls.removeAll { $0.central.identifier == id }; bulk.removeAll { $0.central.identifier == id }
    }

    func receive(_ request: CBATTRequest) -> CBATTError.Code {
        guard request.offset == 0, let value = request.value,
              let index = chars.firstIndex(where: { $0.uuid == request.characteristic.uuid }),
              index == 1 || index == 3, var client = clients[request.central.identifier] else { return .writeNotPermitted }
        do {
            let packet = try index == 1 ? client.controlDecoder.receive(value) : client.bulkDecoder.receive(value)
            clients[request.central.identifier] = client
            if let packet {
                guard packet.stream == (index == 1 ? 0 : 1) else { throw CompanionProtocol.Failure.malformed }
                try handle(packet, from: request.central.identifier)
            }
            return .success
        } catch {
            log.error("invalid companion message: \(String(describing: error), privacy: .public)")
            disconnect(request.central.identifier)
            return .unlikelyError
        }
    }

    func respond(to request: CBATTRequest, using manager: CBPeripheralManager) {
        let value = Data("{\"v\":1,\"ready\":\(ready.contains(request.central.identifier))}".utf8)
        guard request.offset <= value.count else { manager.respond(to: request, withResult: .invalidOffset); return }
        request.value = Data(value.dropFirst(request.offset))
        manager.respond(to: request, withResult: .success)
    }

    private func handle(_ packet: CompanionProtocol.Packet, from id: UUID) throws {
        guard let type = CompanionProtocol.Message(rawValue: packet.type) else { throw CompanionProtocol.Failure.malformed }
        if type == .hello {
            try receiveHello(packet.payload, from: id)
            return
        }
        guard ready.contains(id) else { throw CompanionProtocol.Failure.malformed }
        lastSeen[id] = ProcessInfo.processInfo.systemUptime
        if [.clipGrab, .clipGet, .clipData, .clipState, .fileOffer, .fileGet, .fileData].contains(type) {
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
            blind[id] = payload[0]
        case .leave:
            guard payload.count == 4, payload[1] < 4 else { throw CompanionProtocol.Failure.malformed }
            let frac = UInt16(payload[2]) | UInt16(payload[3]) << 8
            onLeave?(id, payload[0], payload[1], frac)
        case .enterAck:
            guard payload.count == 7 else { throw CompanionProtocol.Failure.malformed }
            blind[id] = payload[6]
        case .pong: break
        default: throw CompanionProtocol.Failure.malformed
        }
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
        guard supportsClipboard(id), packet.stream == ([.clipData, .fileOffer, .fileData].contains(type) ? 1 : 0) else {
            throw CompanionProtocol.Failure.malformed
        }
        try onClipboard?(id, type, packet.payload)
    }

    private func receiveHello(_ data: Data, from id: UUID) throws {
        let hello = try JSONDecoder().decode(CompanionHello.self, from: data)
        guard hello.v == 1, hello.role == "pc", hello.chunk >= 20,
              clients[id]?.helloSent == true else { throw CompanionProtocol.Failure.malformed }
        clients[id]?.supportsResume = hello.resume == true
        clients[id]?.supportsCenter = hello.center == true
        clients[id]?.supportsClipboard = hello.clipboard == 1
        clients[id]?.supportsFiles = hello.files == 1
        clients[id]?.supportsSelection = hello.selection == 1
        clients[id]?.supportsTakeover = hello.takeover == true
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

    func allowsControl(_ id: UUID, now: TimeInterval = ProcessInfo.processInfo.systemUptime) -> Bool {
        guard selection.available(to: id), ready.contains(id), let seen = lastSeen[id], now - seen < 10 else { return false }
        return clients[id]?.supportsSelection != true || selection.granted(to: id)
    }

    func isWaiting(_ id: UUID) -> Bool {
        selection.available(to: id) && ready.contains(id) && clients[id]?.supportsSelection == true && !selection.granted(to: id)
    }

    func sendClipboard(_ type: CompanionProtocol.Message, payload: Data, to id: UUID) {
        guard clipboardTarget?() == id, allowsControl(id), supportsClipboard(id),
              payload.count <= ClipboardTransfer.blockBytes + 12 else { return }
        if [.clipData, .fileOffer, .fileData].contains(type) {
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
        guard var client = clients[id], payload.count <= CompanionProtocol.maximumPayload else { return }
        let packet = CompanionProtocol.Packet(stream: stream, type: type.rawValue, payload: payload)
        let frames = stream == 0 ? client.controlEncoder.encode(packet) : client.bulkEncoder.encode(packet)
        clients[id] = client
        guard controls.count + bulk.count + frames.count <= 512 else { disconnect(id); return }
        let items = frames.map { Queued(data: $0, index: stream == 0 ? 0 : 2, central: client.central) }
        if stream == 0 { controls.append(contentsOf: items) } else { bulk.append(contentsOf: items) }
        drain()
    }

    func readyToSend() {
        blocked = false; drain()
    }

    private func drain() {
        guard let manager, !blocked else { return }
        while let item = controls.first ?? bulk.first {
            guard manager.updateValue(item.data, for: chars[item.index], onSubscribedCentrals: [item.central]) else {
                blocked = true
                return
            }
            if !controls.isEmpty { controls.removeFirst() } else { bulk.removeFirst() }
        }
    }
}
