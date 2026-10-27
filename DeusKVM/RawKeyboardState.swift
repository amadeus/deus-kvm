import Foundation

/// Only keys Quartz cannot represent faithfully take the raw path. Ordinary keys
/// retain macOS transformations such as Fn+Delete, without being sent twice.
struct RawKeyboardState {
    private var devices: [UInt64: Set<Keycode>] = [:]

    static let aliases: [Keycode: UInt16] = [
        .printScreen: 0x69, .f13: 0x69, .scrollLock: 0x6B, .f14: 0x6B, .pause: 0x71, .f15: 0x71,
        .insert: 0x72, .help: 0x72
    ]
    static let quartzAliases = Set(aliases.values)
    static let ambiguousKeys = Set(aliases.keys)
    /// Both backslash usages become Quartz 0x2A and Windows scan code 0x2B.
    /// Keep them on Quartz so QMK's KC_PIPE carries Shift from the same event.
    /// Raw callbacks can precede the session modifier update (or be unavailable
    /// for a particular device even when the manager opened successfully).
    private static let quartzKeys = Set(Keycode.macVirtualKeys.values).union([.nonUSHash])

    static func key(usagePage: UInt32, usage: UInt32) -> Keycode? {
        // Same range as the existing keyboard report descriptor; modifiers stay
        // on the Quartz path where the local return shortcut is recognized.
        guard usagePage == 0x07, (0x04 ... 0xDD).contains(usage) else { return nil }
        let key = Keycode(rawValue: UInt8(usage))
        return ambiguousKeys.contains(key) || !quartzKeys.contains(key) ? key : nil
    }

    mutating func update(device: UInt64, key: Keycode, down: Bool) -> Bool {
        let wasHeld = devices.values.contains { $0.contains(key) }
        if down {
            devices[device, default: []].insert(key)
        } else {
            devices[device]?.remove(key)
        }
        let isHeld = devices.values.contains { $0.contains(key) }
        return wasHeld != isHeld
    }

    mutating func remove(device: UInt64) -> Set<Keycode> {
        let removed = devices.removeValue(forKey: device) ?? []
        return removed.filter { key in !devices.values.contains { $0.contains(key) } }
    }
}
