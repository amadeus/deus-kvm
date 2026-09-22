import CoreBluetooth
import Foundation

/// Connection readiness, independent of whether input is currently on Mac or PC.
enum StatusBarConnectionState: Equatable {
    case disabled, unavailable, searching, connecting, waiting, ready

    struct Link {
        var target: UUID?
        var subscribers: Set<UUID> = []
        var companions: Set<UUID> = []
        var lastSeen: [UUID: TimeInterval] = [:]
        var captureReady = true
        var waiting = false
    }

    static func resolve(
        enabled: Bool, bluetooth: CBManagerState, link: Link, now: TimeInterval
    ) -> Self {
        guard enabled else { return .disabled }
        guard bluetooth == .poweredOn else { return .unavailable }
        if link.waiting { return .waiting }
        if let target = link.target, link.companions.contains(target), let seen = link.lastSeen[target], now - seen < 10 {
            return link.captureReady ? .ready : .connecting
        }
        return link.target != nil || !link.subscribers.isEmpty || !link.companions.isEmpty ? .connecting : .searching
    }

    func symbol(isRemote: Bool) -> String {
        switch self {
        case .disabled: "pause.circle"
        case .unavailable: "antenna.radiowaves.left.and.right.slash"
        case .searching: "antenna.radiowaves.left.and.right"
        case .connecting: "arrow.triangle.2.circlepath"
        case .waiting: "clock"
        case .ready: isRemote ? "keyboard.fill" : "keyboard"
        }
    }

    var label: String {
        switch self {
        case .disabled: "DeusKVM disabled"
        case .unavailable: "DeusKVM — Bluetooth unavailable"
        case .searching: "DeusKVM — searching for a PC"
        case .connecting: "DeusKVM — connecting to PC"
        case .waiting: "DeusKVM — waiting for Windows control"
        case .ready: "DeusKVM ready"
        }
    }
}
