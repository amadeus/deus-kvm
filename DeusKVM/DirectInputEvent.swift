import CoreGraphics
import Foundation

struct DirectInputEvent: Sendable {
    enum Kind: Sendable {
        case keyDown(Keycode)
        case keyUp(Keycode)
        case flagsChanged(capsLock: Bool)
        case consumer(ConsumerKey, down: Bool)
        case mouseMove(Int8, Int8)
        case mouseButton(MouseButtons, Bool)
        case scroll(wheel: Int8, pan: Int8)
    }

    let kind: Kind
    let modifiers: KeyboardModifiers

    init(kind: Kind, modifiers: KeyboardModifiers) {
        self.kind = kind
        self.modifiers = modifiers
    }

    init?(type: CGEventType, event: CGEvent) {
        let flags = event.flags
        modifiers = KeyboardModifiers(eventFlags: flags)

        switch type {
        case .keyDown:
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 {
                return nil
            }
            guard let key = Keycode(
                macVirtualKey: UInt16(event.getIntegerValueField(.keyboardEventKeycode)),
                keyboardType: UInt32(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeyboardType))
            )
            else { return nil }
            kind = .keyDown(key)
        case .keyUp:
            guard let key = Keycode(
                macVirtualKey: UInt16(event.getIntegerValueField(.keyboardEventKeycode)),
                keyboardType: UInt32(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeyboardType))
            )
            else { return nil }
            kind = .keyUp(key)
        case .flagsChanged:
            kind = .flagsChanged(capsLock: flags.contains(.maskAlphaShift))
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            let dx = Self.clampInt8(event.getIntegerValueField(.mouseEventDeltaX))
            let dy = Self.clampInt8(event.getIntegerValueField(.mouseEventDeltaY))
            guard dx != 0 || dy != 0 else { return nil }
            kind = .mouseMove(dx, dy)
        case .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp:
            guard let button = Self.mouseButton(for: type) else { return nil }
            kind = .mouseButton(button.0, button.1)
        case .scrollWheel:
            let wheel = Self.clampInt8(event.getIntegerValueField(.scrollWheelEventDeltaAxis1))
            // Quartz's horizontal direction is opposite HID AC Pan / Windows.
            // Clamp before negating so extreme source values cannot overflow.
            let pan = -Self.clampInt8(event.getIntegerValueField(.scrollWheelEventDeltaAxis2))
            guard wheel != 0 || pan != 0 else { return nil }
            kind = .scroll(wheel: wheel, pan: pan)
        default:
            guard let media = MediaKeyEvent(type: type, event: event) else { return nil }
            kind = .consumer(media.key, down: media.isDown)
        }
    }

    private static func clampInt8(_ value: Int64) -> Int8 {
        Int8(max(-127, min(127, Int(value))))
    }

    private static func mouseButton(for type: CGEventType) -> (MouseButtons, Bool)? {
        switch type {
        case .leftMouseDown: (.left, true)
        case .leftMouseUp: (.left, false)
        case .rightMouseDown: (.right, true)
        case .rightMouseUp: (.right, false)
        case .otherMouseDown: (.middle, true)
        case .otherMouseUp: (.middle, false)
        default: nil
        }
    }
}
