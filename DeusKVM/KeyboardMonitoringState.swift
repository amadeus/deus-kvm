import IOKit

enum KeyboardMonitoringStatus: Equatable, Sendable {
    case waitingForDevice
    case permissionRequired
    case ready
    case unavailable(IOReturn)

    var isReady: Bool {
        self == .ready
    }
}

/// A seized physical keyboard can coexist with a usable remapper's virtual keyboard.
struct KeyboardMonitoringState {
    private(set) var devices: [UInt64: IOReturn] = [:]

    mutating func opened(_ device: UInt64, result: IOReturn) {
        devices[device] = result
    }

    mutating func remove(_ device: UInt64) {
        devices.removeValue(forKey: device)
    }

    func status(permissionGranted: Bool) -> KeyboardMonitoringStatus {
        guard permissionGranted else { return .permissionRequired }
        if devices.values.contains(kIOReturnSuccess) { return .ready }
        if let error = devices.min(by: { $0.key < $1.key })?.value { return .unavailable(error) }
        return .waitingForDevice
    }

    var needsRetry: Bool {
        devices.values.contains { $0 != kIOReturnSuccess }
    }
}
