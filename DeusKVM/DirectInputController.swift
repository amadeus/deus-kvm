import AppKit
import CoreGraphics
import Foundation

/// Translates captured input into HID reports; during capture it runs on the Bluetooth queue.
final class DirectInputController {
    private(set) var isCapturing = false

    private let defaults: UserDefaults
    private var pressedKeys: Set<Keycode> = []
    private var pressedMouseButtons: MouseButtons = []
    private var modifiers: KeyboardModifiers = []

    private var sendKeyboard: ((KeyboardReport) -> Void)?
    private var capsLock = false
    private var pressedConsumerKeys: [ConsumerKey] = []
    private var sendConsumer: ((ConsumerReport) -> Void)?
    private var sendMouse: ((MouseReport) -> Void)?
    private var sendNetwork: ((NetworkInputEvent) -> Void)?
    private var networkReady: (() -> Bool)?
    /// Buttons pressed over the network stream, released there when capture stops.
    private var networkButtons: MouseButtons = []

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// retains the upstream report translation; tap and cursor lifetime belong to the coordinator
    func start(_ hid: HIDInput) {
        stop()
        sendKeyboard = hid.sendKeyboard
        sendMouse = hid.sendMouse
        sendConsumer = hid.sendConsumer
        sendNetwork = hid.sendNetwork
        networkReady = hid.networkReady
        capsLock = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        isCapturing = true
    }

    func stop() {
        pressedKeys.removeAll()
        pressedConsumerKeys.removeAll()
        pressedMouseButtons = []
        modifiers = []
        sendKeyboard?(.zero)
        sendMouse?(.zero)
        sendConsumer?(.zero)
        for button: MouseButtons in [.left, .right, .middle] where networkButtons.contains(button) {
            sendNetwork?(.button(button, false))
        }
        networkButtons = []
        sendNetwork = nil
        networkReady = nil
        sendKeyboard = nil
        sendMouse = nil
        sendConsumer = nil
        isCapturing = false
    }

    func handle(_ event: DirectInputEvent) {
        guard isCapturing else { return }

        modifiers = event.modifiers

        switch event.kind {
        case let .keyDown(key):
            pressedKeys.insert(key)
            sendKeyboardReport()
        case let .keyUp(key):
            pressedKeys.remove(key)
            sendKeyboardReport()
        case let .flagsChanged(currentCapsLock):
            if capsLock != currentCapsLock {
                capsLock = currentCapsLock
                // Caps Lock arrives as a latched flag, not an ordinary down/up pair.
                // Forward each latch transition as one physical press and release.
                pressedKeys.insert(.capsLock)
                sendKeyboardReport()
                pressedKeys.remove(.capsLock)
            }
            sendKeyboardReport()
        case let .consumer(key, down):
            let previous = pressedConsumerKeys.last
            pressedConsumerKeys.removeAll { $0 == key }
            if down { pressedConsumerKeys.append(key) }
            let current = pressedConsumerKeys.last
            if previous != current { sendConsumer?(ConsumerReport(key: current ?? .none)) }
        case let .mouseMove(dx, dy):
            mouseMove(dx, dy)
        case let .mouseButton(button, isDown):
            mouseButton(button, isDown)
        case let .networkMove(dx, dy):
            networkMove(dx, dy)
        case let .networkButton(button, isDown):
            networkButton(button, isDown)
        case let .scroll(wheel, pan):
            // Source deltas are already clamped to -127...127, so negation is safe.
            // Read on each scroll so settings changes apply during capture too.
            let vertical = defaults.bool(forKey: AppSettings.invertVerticalScrollKey) ? -wheel : wheel
            let horizontal = defaults.bool(forKey: AppSettings.invertHorizontalScrollKey) ? -pan : pan
            // Wheel and pan are relative: one report per step. A trailing zero report doubled
            // scroll traffic and filled CoreBluetooth's queue; reads never replay relative fields.
            sendMouse?(MouseReport(buttons: pressedMouseButtons, wheel: vertical, pan: horizontal))
        }
    }

    /// The report carries 8-bit deltas; larger steps lose the excess, as they always did.
    private func mouseMove(_ dx: Int32, _ dy: Int32) {
        sendMouse?(MouseReport(buttons: pressedMouseButtons, dX: Self.clamp(dx), dY: Self.clamp(dy)))
    }

    /// The stream may have dropped since the tap decided: Bluetooth carries the event then.
    private func networkMove(_ dx: Int32, _ dy: Int32) {
        if networkReady?() == true { sendNetwork?(.motion(dx, dy)) } else { mouseMove(dx, dy) }
    }

    private func networkButton(_ button: MouseButtons, _ isDown: Bool) {
        if isDown, networkReady?() == true {
            networkButtons.insert(button)
            sendNetwork?(.button(button, true))
        } else if !isDown, networkButtons.contains(button) {
            networkButtons.remove(button)
            sendNetwork?(.button(button, false))
        } else {
            mouseButton(button, isDown)
        }
    }

    private static func clamp(_ value: Int32) -> Int8 {
        Int8(max(-127, min(127, value)))
    }

    private func mouseButton(_ button: MouseButtons, _ isDown: Bool) {
        if isDown {
            pressedMouseButtons.insert(button)
        } else {
            pressedMouseButtons.remove(button)
        }
        sendMouse?(MouseReport(buttons: pressedMouseButtons))
    }

    private func sendKeyboardReport() {
        // QMK KC_MENU is Keyboard Menu (0x76), not KC_APP (0x65). Windows
        // supports the latter as its context-menu key. Keep physical identities
        // in pressedKeys so releasing either source cannot release the other.
        let keys = Set(pressedKeys.map { $0 == .menu ? .application : $0 })
        sendKeyboard?(KeyboardReport(modifiers: modifiers, keys: keys.sorted { $0.rawValue < $1.rawValue }))
    }
}
