import AppKit

/// NX_SYSDEFINED / NX_SUBTYPE_AUX_CONTROL_BUTTONS from the macOS HID event headers.
struct MediaKeyEvent {
    static let eventType = CGEventType(rawValue: 14)!
    let key: ConsumerKey
    let isDown: Bool

    init?(type: CGEventType, event: CGEvent) {
        guard type == Self.eventType, let native = NSEvent(cgEvent: event),
              native.subtype.rawValue == 8 else { return nil }
        self.init(data: native.data1)
    }

    init?(data: Int) {
        let state = (data >> 8) & 0xFF
        guard state == 0x0A || state == 0x0B,
              let key = Self.keys[(data >> 16) & 0xFFFF] else { return nil }
        self.key = key
        isDown = state == 0x0A
    }

    /// NX_KEYTYPE_* -> USB HID Consumer usages. A held key stays held on the PC until its
    /// release; macOS's own repeats are not forwarded.
    private static let keys: [Int: ConsumerKey] = [
        0: .volumeUp, 1: .volumeDown, 2: .brightnessUp, 3: .brightnessDown,
        6: .power, 7: .mute, 14: .eject, 16: .playPause,
        17: .scanNext, 18: .scanPrev, 19: .fastForward, 20: .rewind,
        21: .illuminationUp, 22: .illuminationDown, 23: .illuminationToggle, 25: .menu
    ]
}
