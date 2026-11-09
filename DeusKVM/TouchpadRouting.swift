import Foundation

/// Decides, per captured pointer event while a session is remote, what the tap forwards and
/// where. Three carriers exist: the Bluetooth mouse collection (`forward`), the Bluetooth
/// touchpad collection's button (`padButton`) and the local-network stream (`network`).
///
/// With precision mode on and the network up: a lone finger and a real mouse go over the
/// network, two or more fingers belong to Windows' contact handling (motion dropped, a click
/// becomes the pad's button), continuous scroll is dropped, a one-finger tap is a network
/// click and a two-finger tap is left to Windows. Without the network, the lone finger's
/// motion is dropped too (the contacts carry it) and a one-finger click is the pad's button.
/// With precision mode off, everything goes to the network when it is up and to Bluetooth
/// otherwise. Every release follows its press to the same carrier, so no side keeps a button.
struct TouchpadRouter: Sendable, Equatable {
    enum Event: Sendable, Equatable {
        case motion
        case scroll(continuous: Bool)
        case button(MouseButtons, down: Bool)
        case other
    }

    enum Action: Sendable, Equatable {
        case forward
        case network
        case drop
        case padButton(Bool)
    }

    /// The button whose press became the pad's button, until that button is released.
    private(set) var padButton: MouseButtons?
    /// Buttons forwarded to the Bluetooth mouse collection; their releases follow them there.
    private(set) var forwarded: MouseButtons = []
    /// Buttons pressed over the network stream; their releases follow them there.
    private(set) var networked: MouseButtons = []

    mutating func route(_ event: Event, enabled: Bool, network: Bool, presence: () -> TouchpadPresence) -> Action {
        switch event {
        case .motion: motion(enabled: enabled, network: network, presence: presence)
        case let .scroll(continuous): enabled && continuous ? .drop : .forward
        case let .button(button, down: true): press(button, enabled: enabled, network: network, presence: presence)
        case let .button(button, down: false): release(button, enabled: enabled)
        case .other: .forward
        }
    }

    private func motion(enabled: Bool, network: Bool, presence: () -> TouchpadPresence) -> Action {
        guard enabled else { return network ? .network : .forward }
        switch presence() {
        case .none: return network ? .network : .forward
        case .recent, .touching(1): return network ? .network : .drop
        case .touching: return .drop
        }
    }

    private mutating func press(_ button: MouseButtons, enabled: Bool, network: Bool, presence: () -> TouchpadPresence) -> Action {
        guard enabled else { return carry(button, network: network) }
        switch presence() {
        case let .touching(count) where count >= 2:
            // A physical click with two or more fingers: Windows decides left or right.
            padButton = button
            return .padButton(true)
        case .touching:
            guard network else { padButton = button; return .padButton(true) }
            networked.insert(button)
            return .network
        case .recent:
            // macOS tap-to-click. One finger is ours when the network carries the pointer;
            // two fingers are always Windows' own two-finger tap.
            guard network, button == .left else { return .drop }
            networked.insert(button)
            return .network
        case .none:
            return carry(button, network: network)
        }
    }

    private mutating func release(_ button: MouseButtons, enabled: Bool) -> Action {
        if padButton == button { padButton = nil; return .padButton(false) }
        if networked.contains(button) { networked.remove(button); return .network }
        if forwarded.contains(button) { forwarded.remove(button); return .forward }
        return enabled ? .drop : .forward
    }

    private mutating func carry(_ button: MouseButtons, network: Bool) -> Action {
        if network { networked.insert(button); return .network }
        forwarded.insert(button)
        return .forward
    }

    /// A new session starts with nothing held.
    mutating func reset() {
        padButton = nil
        forwarded = []
        networked = []
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

extension DirectInputEvent {
    /// The same event on the local-network carrier; non-pointer events are unchanged.
    var networked: DirectInputEvent {
        switch kind {
        case let .mouseMove(dx, dy): DirectInputEvent(kind: .networkMove(dx, dy), modifiers: modifiers)
        case let .mouseButton(button, down): DirectInputEvent(kind: .networkButton(button, down), modifiers: modifiers)
        default: self
        }
    }
}
