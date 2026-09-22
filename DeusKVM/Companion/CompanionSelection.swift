import Foundation

struct CompanionSelection {
    private(set) var epoch: UInt32
    private(set) var enabled = true
    private(set) var allowed: Set<UUID> = []
    private var grants: Set<UUID> = []

    init(epoch: UInt32 = UInt32.random(in: 1 ... UInt32.max)) {
        self.epoch = epoch
    }

    mutating func update(enabled: Bool, allowed: Set<UUID>) -> Bool {
        guard self.enabled != enabled || self.allowed != allowed else { return false }
        self.enabled = enabled
        self.allowed = allowed
        epoch &+= 1
        grants.removeAll()
        return true
    }

    func available(to id: UUID) -> Bool {
        enabled && allowed.contains(id)
    }

    func packet(for id: UUID) -> Data {
        CompanionProtocol.u32(epoch) + Data([available(to: id) ? 1 : 0])
    }

    mutating func receive(_ data: Data, from id: UUID) throws {
        guard data.count == 5, data[4] <= 1 else { throw CompanionProtocol.Failure.malformed }
        guard CompanionProtocol.read32(data, 0) == epoch else { return }
        if data[4] == 1, available(to: id) { grants.insert(id) } else { grants.remove(id) }
    }

    func granted(to id: UUID) -> Bool {
        available(to: id) && grants.contains(id)
    }

    mutating func disconnect(_ id: UUID) {
        grants.remove(id)
    }

    mutating func reset() {
        grants.removeAll()
    }
}
