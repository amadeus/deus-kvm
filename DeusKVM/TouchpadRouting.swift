import Foundation

/// Decides, per captured pointer event while a session is remote, what the tap forwards. With
/// Windows driving the pad from its contacts, the trackpad's motion, continuous scroll and
/// taps stay out of the mouse collection and a physical click becomes the pad's button; a
/// real mouse goes through the mouse collection as before. With the mode off everything is
/// forwarded unchanged, except that a pad button still held is always released.
struct TouchpadRouter: Sendable, Equatable {
    enum Event: Sendable, Equatable {
        case motion
        case scroll(continuous: Bool)
        case button(MouseButtons, down: Bool)
        case other
    }

    enum Action: Sendable, Equatable {
        case forward
        case drop
        case padButton(Bool)
    }

    /// The button whose press became the pad's button, until that button is released.
    private(set) var padButton: MouseButtons?
    /// Buttons forwarded to the mouse collection, so their releases follow them there even if
    /// a finger has landed on the pad meanwhile.
    private(set) var forwarded: MouseButtons = []

    mutating func route(_ event: Event, enabled: Bool, presence: () -> TouchpadPresence) -> Action {
        switch event {
        case .motion:
            return enabled && presence() != .none ? .drop : .forward
        case let .scroll(continuous):
            return enabled && continuous ? .drop : .forward
        case let .button(button, down: true):
            guard enabled else { forwarded.insert(button); return .forward }
            switch presence() {
            case .touching:
                // A physical click: Windows decides left or right from the contacts.
                padButton = button
                return .padButton(true)
            case .recent:
                // macOS tap-to-click; Windows taps from the contacts and its own setting.
                return .drop
            case .none:
                forwarded.insert(button)
                return .forward
            }
        case let .button(button, down: false):
            if padButton == button { padButton = nil; return .padButton(false) }
            if forwarded.contains(button) { forwarded.remove(button); return .forward }
            return enabled ? .drop : .forward
        case .other:
            return .forward
        }
    }

    /// A new session starts with nothing held.
    mutating func reset() {
        padButton = nil
        forwarded = []
    }
}

extension TouchpadRouter.Event {
    init(_ kind: DirectInputEvent.Kind, continuous: Bool) {
        switch kind {
        case .mouseMove: self = .motion
        case .scroll: self = .scroll(continuous: continuous)
        case let .mouseButton(button, down): self = .button(button, down: down)
        default: self = .other
        }
    }
}
