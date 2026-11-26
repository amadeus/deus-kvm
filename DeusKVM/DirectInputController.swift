import AppKit
import CoreGraphics
import Foundation

/// Turns captured input into input-stream events; during capture it runs on the input queue.
/// Input the stream cannot carry right now is dropped: each keyboard and media-key event is the
/// whole state, so the next one after the stream recovers brings the PC up to date.
final class DirectInputController {
    private(set) var isCapturing = false

    private let defaults: UserDefaults
    private var sink: InputSink?
    private var pressedKeys: Set<Keycode> = []
    private var modifiers: KeyboardModifiers = []
    private var capsLock = false
    private var pressedConsumerKeys: [ConsumerKey] = []
    private var scroll = ScrollAccumulator()
    /// Buttons pressed over the stream, released there when capture stops.
    private var sentButtons: MouseButtons = []
    /// Whether a keyboard or media-key state went out this session, so stopping clears it.
    private var keyboardSent = false
    private var consumerSent = false

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// Retains the stream for this session; tap and cursor lifetime belong to the coordinator.
    func start(_ sink: InputSink) {
        stop()
        self.sink = sink
        capsLock = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        scroll.reset()
        isCapturing = true
    }

    func stop() {
        if isCapturing { sink?.send(.reset) }
        pressedKeys.removeAll()
        pressedConsumerKeys.removeAll()
        modifiers = []
        sentButtons = []
        keyboardSent = false
        consumerSent = false
        sink = nil
        isCapturing = false
    }

    func handle(_ event: DirectInputEvent) {
        guard isCapturing else { return }

        modifiers = event.modifiers

        switch event.kind {
        case let .keyDown(key):
            pressedKeys.insert(key)
            sendKeyboard()
        case let .keyUp(key):
            pressedKeys.remove(key)
            sendKeyboard()
        case let .flagsChanged(currentCapsLock):
            if capsLock != currentCapsLock {
                capsLock = currentCapsLock
                // Caps Lock arrives as a latched flag, not an ordinary down/up pair.
                // Forward each latch transition as one physical press and release.
                pressedKeys.insert(.capsLock)
                sendKeyboard()
                pressedKeys.remove(.capsLock)
            }
            sendKeyboard()
        case let .consumer(key, down):
            let previous = pressedConsumerKeys.last
            pressedConsumerKeys.removeAll { $0 == key }
            if down { pressedConsumerKeys.append(key) }
            let current = pressedConsumerKeys.last
            if previous != current { sendConsumer(current ?? .none) }
        case let .motion(dx, dy):
            if sink?.pointerReady() == true { sink?.send(.motion(dx, dy)) }
        case let .button(button, isDown):
            self.button(button, isDown)
        case let .scroll(wheel, pan):
            // Read on each scroll so settings changes apply during capture too.
            let speed = defaults.object(forKey: AppSettings.scrollSpeedKey) as? Double ?? ScrollAccumulator.defaultSpeed
            let scaled = scroll.scale(wheel: wheel, pan: pan, by: speed)
            // Source deltas are clamped to -127...127, so negation is safe.
            let vertical = defaults.bool(forKey: AppSettings.invertVerticalScrollKey) ? -scaled.wheel : scaled.wheel
            let horizontal = defaults.bool(forKey: AppSettings.invertHorizontalScrollKey) ? -scaled.pan : scaled.pan
            guard vertical != 0 || horizontal != 0, sink?.keysReady() == true else { return }
            sink?.send(.scroll(wheel: vertical, pan: horizontal))
        }
    }

    /// A release follows its press even if the stream has since stalled (the stream drops it
    /// while down, and the PC releases everything when the stream ends).
    private func button(_ button: MouseButtons, _ isDown: Bool) {
        if isDown, sink?.pointerReady() == true {
            sentButtons.insert(button)
            sink?.send(.button(button, true))
        } else if !isDown, sentButtons.contains(button) {
            sentButtons.remove(button)
            sink?.send(.button(button, false))
        }
    }

    private func sendKeyboard() {
        guard sink?.keysReady() == true else { return }
        // QMK KC_MENU is Keyboard Menu (0x76), not KC_APP (0x65). Windows
        // supports the latter as its context-menu key. Keep physical identities
        // in pressedKeys so releasing either source cannot release the other.
        let keys = Set(pressedKeys.map { $0 == .menu ? .application : $0 })
        sink?.send(.keyboard(KeyboardReport(modifiers: modifiers, keys: keys.sorted { $0.rawValue < $1.rawValue })))
        keyboardSent = true
    }

    private func sendConsumer(_ key: ConsumerKey) {
        guard sink?.keysReady() == true else { return }
        sink?.send(.consumer(key))
        consumerSent = true
    }
}
