import Foundation

/// Decides, per captured pointer event while a session is remote, whether it goes over the
/// local-network pointer stream or the Bluetooth mouse collection. Motion and clicks take the
/// network while it is up (smooth, the pad's own rate) and Bluetooth otherwise; scrolling
/// always goes over the Bluetooth mouse as a wheel. Every release follows its press to the same
/// carrier, so neither side is left with a held button.
struct PointerRouter: Sendable, Equatable {
    enum Event: Sendable, Equatable {
        case motion
        case scroll
        case button(MouseButtons, down: Bool)
        case other
    }

    enum Action: Sendable, Equatable {
        case forward
        case network
    }

    /// Buttons pressed over the network stream; their releases follow them there.
    private(set) var networked: MouseButtons = []
    /// Buttons forwarded to the Bluetooth mouse; their releases follow them there.
    private(set) var forwarded: MouseButtons = []

    mutating func route(_ event: Event, network: Bool) -> Action {
        switch event {
        case .motion:
            return network ? .network : .forward
        case .scroll:
            return .forward
        case let .button(button, down: true):
            if network { networked.insert(button); return .network }
            forwarded.insert(button)
            return .forward
        case let .button(button, down: false):
            if networked.contains(button) { networked.remove(button); return .network }
            if forwarded.contains(button) { forwarded.remove(button); return .forward }
            return network ? .network : .forward
        case .other:
            return .forward
        }
    }

    /// A new session starts with nothing held.
    mutating func reset() {
        networked = []
        forwarded = []
    }
}

extension PointerRouter.Event {
    init(_ kind: DirectInputEvent.Kind) {
        switch kind {
        case .mouseMove: self = .motion
        case .scroll: self = .scroll
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
