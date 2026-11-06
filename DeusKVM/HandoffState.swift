import CoreGraphics
import Foundation

struct ToggleShortcut: Equatable, Sendable {
    var keyCode: UInt16 = 53
    var modifiers: UInt64 = CGEventFlags.maskSecondaryFn.rawValue
    var enabled = true

    static let modifierMask: CGEventFlags = [.maskShift, .maskControl, .maskAlternate, .maskCommand, .maskSecondaryFn]

    func matches(key: UInt16, flags: CGEventFlags) -> Bool {
        enabled && key == keyCode && flags.intersection(Self.modifierMask).rawValue == modifiers
    }
}

/// tracks physical releases without changing the existing HID translation
struct HandoffState: Sendable {
    var heldKeys: Set<UInt16> = []
    var heldRawKeys: Set<Keycode> = []
    var heldMediaKeys: Set<UInt16> = []
    var heldButtons: Set<Int64> = []
    var modifiers: CGEventFlags = []
    private(set) var pendingToggle = false
    private var consumedKey: UInt16?
    /// modifier transitions are represented by event flags, not the global key-state snapshot
    static let modifierKeyCodes: Set<UInt16> = [54, 55, 56, 57, 58, 59, 60, 61, 62, 63]

    init(heldKeys: Set<UInt16> = [], heldButtons: Set<Int64> = []) {
        self.heldKeys = heldKeys
        self.heldButtons = heldButtons
    }

    var isReleased: Bool {
        heldKeys.isEmpty && heldRawKeys.isEmpty && heldMediaKeys.isEmpty && heldButtons.isEmpty && modifiers
            .isDisjoint(with: ToggleShortcut.modifierMask)
    }

    mutating func key(code: UInt16, down: Bool, repeatEvent: Bool, flags: CGEventFlags, shortcut: ToggleShortcut) -> Bool {
        modifiers = flags
        if down { heldKeys.insert(code) } else { heldKeys.remove(code) }
        if consumedKey == code {
            if !down { consumedKey = nil }
            return true
        }
        if down, !repeatEvent, shortcut.matches(key: code, flags: flags) {
            pendingToggle = true
            consumedKey = code
            return true
        }
        return false
    }

    mutating func updateModifiers(_ flags: CGEventFlags) {
        modifiers = flags
        heldKeys.subtract(Self.modifierKeyCodes)
    }

    mutating func requestToggle() {
        pendingToggle = true
    }

    mutating func takeToggle() -> Bool {
        guard pendingToggle, isReleased else { return false }
        pendingToggle = false
        return true
    }

    mutating func cancel() {
        pendingToggle = false
    }

    /// Replaces tracked input with what macOS reports as held now. Releases the tap never saw
    /// (Secure Input hides key-ups, such as Q after ⌃⌘Q locks the screen) otherwise block every
    /// crossing until that key is pressed again. Media keys cannot be queried, so they clear.
    mutating func resync(keys: Set<UInt16>, buttons: Set<Int64>, flags: CGEventFlags) -> HeldInputCorrection {
        let correction = HeldInputCorrection(
            keys: heldKeys.subtracting(keys).subtracting(Self.modifierKeyCodes).count,
            buttons: heldButtons.subtracting(buttons).count,
            media: heldMediaKeys.count
        )
        heldKeys = keys
        heldButtons = buttons
        heldMediaKeys.removeAll()
        if let consumedKey, !keys.contains(consumedKey) { self.consumedKey = nil }
        updateModifiers(flags)
        return correction
    }
}

/// Stale entries a resync dropped; counts only.
struct HeldInputCorrection: Equatable {
    var keys = 0
    var buttons = 0
    var media = 0
    var raw = 0

    var total: Int {
        keys + buttons + media + raw
    }

    func message(_ reason: String) -> String {
        "held input corrected (\(reason)): keys=\(keys) raw=\(raw) media=\(media) buttons=\(buttons)"
    }
}

/// Bounds how often a blocked crossing asks macOS for the real held state.
struct ResyncGate {
    var interval: TimeInterval = 0.5
    private var last = -TimeInterval.infinity

    mutating func allow(now: TimeInterval) -> Bool {
        guard now - last >= interval else { return false }
        last = now
        return true
    }
}
