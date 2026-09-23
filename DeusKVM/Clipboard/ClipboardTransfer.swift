import Foundation

/// Clipboard blocks are requested individually, leaving room for input/control traffic.
final class ClipboardTransfer {
    static let maximumBytes = 65536
    static let blockBytes = 1024
    var send: (CompanionProtocol.Message, Data) -> Void = { _, _ in }
    var apply: (Data) -> Void = { _ in }
    var onDeadlineChanged: () -> Void = {}
    var retryDeadline: TimeInterval? {
        incoming == nil ? nil : requestedAt + 5
    }

    private var epoch: UInt32 = 0
    private(set) var sequence: UInt32 = 0
    private var local: Data?
    private var seenOffer: UInt32?
    private var offer: (sequence: UInt32, size: Int)?
    private var incoming: Data?
    private var active = false
    private var requestedAt: TimeInterval = 0
    private var retries = 0

    static func text(_ value: String?) -> Data? {
        guard let value, value.utf8.count <= maximumBytes * 2, !value.contains("\0") else { return nil }
        let data = Data(value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").utf8)
        return data.count <= maximumBytes ? data : nil
    }

    static func decode(_ data: Data) -> String? {
        guard data.count <= maximumBytes, let text = String(data: data, encoding: .utf8),
              !text.contains("\0"), !text.contains("\r") else { return nil }
        return text
    }

    func reset(_ nextEpoch: UInt32) {
        defer { onDeadlineChanged() }
        epoch = nextEpoch; seenOffer = nil; local = nil; offer = nil; incoming = nil; sequence &+= 1; retries = 0
    }

    func observe(_ text: Data?, announce: Bool = true) {
        defer { onDeadlineChanged() }
        local = text.flatMap { Self.decode($0) == nil ? nil : $0 }
        sequence &+= 1; offer = nil; incoming = nil
        if announce { yield() }
    }

    func yield() {
        send(.clipGrab, Self.header(epoch, sequence, local.map { UInt32($0.count) } ?? UInt32.max))
    }

    func setActive(_ value: Bool, now: TimeInterval) {
        defer { onDeadlineChanged() }
        active = value
        if active, offer != nil, incoming == nil { request(now) }
    }

    func tick(_ now: TimeInterval) {
        defer { onDeadlineChanged() }
        guard incoming != nil, now - requestedAt >= 5 else { return }
        retries += 1
        guard retries <= 3 else { offer = nil; incoming = nil; return }
        request(now)
    }

    func receive(_ type: CompanionProtocol.Message, payload: Data, now: TimeInterval) throws {
        defer { onDeadlineChanged() }
        guard payload.count >= 12, type == .clipData || payload.count == 12 else { throw CompanionProtocol.Failure.malformed }
        let scope = CompanionProtocol.read32(payload, 0)
        let id = CompanionProtocol.read32(payload, 4)
        let value = CompanionProtocol.read32(payload, 8)
        guard scope == epoch else { return }
        switch type {
        case .clipGrab:
            guard value == UInt32.max || value <= Self.maximumBytes else { throw CompanionProtocol.Failure.malformed }
            if seenOffer == id { return }
            seenOffer = id
            incoming = nil; retries = 0
            offer = value == UInt32.max ? nil : (id, Int(value))
            if active, offer != nil { request(now) }
        case .clipGet:
            guard id == sequence, let local else { yield(); return }
            guard value <= local.count, value % UInt32(Self.blockBytes) == 0 else { throw CompanionProtocol.Failure.malformed }
            send(.clipData, Self.header(epoch, id, value) + local.dropFirst(Int(value)).prefix(Self.blockBytes))
        case .clipData:
            try receiveBlock(id: id, offset: value, payload: payload, now: now)
        default: throw CompanionProtocol.Failure.malformed
        }
    }

    private func receiveBlock(id: UInt32, offset: UInt32, payload: Data, now: TimeInterval) throws {
        guard payload.count <= Self.blockBytes + 12 else { throw CompanionProtocol.Failure.malformed }
        guard let offer, offer.sequence == id, var bytes = incoming, offset == bytes.count else { return }
        guard payload.count - 12 == min(Self.blockBytes, offer.size - bytes.count) else { throw CompanionProtocol.Failure.malformed }
        bytes.append(payload.dropFirst(12)); incoming = bytes; retries = 0
        if bytes.count < offer.size { request(now); return }
        incoming = nil; self.offer = nil
        guard Self.decode(bytes) != nil else { throw CompanionProtocol.Failure.malformed }
        local = nil; sequence &+= 1
        apply(bytes)
    }

    private func request(_ now: TimeInterval) {
        guard let offer else { return }
        if incoming == nil { incoming = Data() }
        requestedAt = now
        send(.clipGet, Self.header(epoch, offer.sequence, UInt32(incoming?.count ?? 0)))
    }

    static func header(_ epoch: UInt32, _ sequence: UInt32, _ value: UInt32) -> Data {
        CompanionProtocol.u32(epoch) + CompanionProtocol.u32(sequence) + CompanionProtocol.u32(value)
    }
}
