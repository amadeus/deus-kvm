import AppKit
import CoreGraphics
import Foundation

@MainActor
final class DirectInputController: ObservableObject {
    @Published private(set) var isCapturing = false

    private let defaults: UserDefaults
    private var pressedKeys: Set<Keycode> = []
    private var pressedMouseButtons: MouseButtons = []
    private var modifiers: KeyboardModifiers = []

    private var sendKeyboard: ((KeyboardReport) -> Void)?
    private var capsLock = false
    private var pressedConsumerKeys: [ConsumerKey] = []
    private var sendConsumer: ((ConsumerReport) -> Void)?
    private var sendMouse: ((MouseReport) -> Void)?
    private var directMouse: ((Double, Double, MouseReport) -> Void)?
    private var pointerMotion = PointerMotionScaler()

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// retains the upstream report translation; tap and cursor lifetime belong to the coordinator
    func start(_ hid: HIDInput, directMouse: ((Double, Double, MouseReport) -> Void)? = nil) {
        stop()
        self.directMouse = directMouse
        sendKeyboard = hid.sendKeyboard
        sendMouse = hid.sendMouse
        sendConsumer = hid.sendConsumer
        capsLock = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        isCapturing = true
    }

    func stop() {
        directMouse = nil
        pointerMotion = PointerMotionScaler()
        pressedKeys.removeAll()
        pressedConsumerKeys.removeAll()
        pressedMouseButtons = []
        modifiers = []
        sendKeyboard?(.zero)
        sendMouse?(.zero)
        sendConsumer?(.zero)
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
            let speed = (defaults.object(forKey: AppSettings.windowsPointerSpeedKey) as? NSNumber)?.doubleValue
                ?? PointerMotionScaler.defaultSpeed
            if let directMouse {
                let gain = PointerMotionScaler.validatedSpeed(speed)
                directMouse(Double(dx) * gain, Double(dy) * gain, MouseReport(buttons: pressedMouseButtons))
                return
            }
            let motion = pointerMotion.scale(dx: dx, dy: dy, speed: speed)
            if motion.x != 0 || motion.y != 0 {
                sendMouse?(MouseReport(buttons: pressedMouseButtons, dX: motion.x, dY: motion.y))
            }
        case let .mouseButton(button, isDown):
            if isDown {
                pressedMouseButtons.insert(button)
            } else {
                pressedMouseButtons.remove(button)
            }
            mouseReport(MouseReport(buttons: pressedMouseButtons))
        case let .scroll(wheel, pan):
            // Source deltas are already clamped to -127...127, so negation is safe.
            // Read on each scroll so settings changes apply during capture too.
            let vertical = defaults.bool(forKey: AppSettings.invertVerticalScrollKey) ? -wheel : wheel
            let horizontal = defaults.bool(forKey: AppSettings.invertHorizontalScrollKey) ? -pan : pan
            mouseReport(MouseReport(buttons: pressedMouseButtons, wheel: vertical, pan: horizontal))
            mouseReport(MouseReport(buttons: pressedMouseButtons))
        }
    }

    private func mouseReport(_ report: MouseReport) {
        if let directMouse { directMouse(0, 0, report) } else { sendMouse?(report) }
    }

    private func sendKeyboardReport() {
        sendKeyboard?(KeyboardReport(modifiers: modifiers, keys: pressedKeys.sorted { $0.rawValue < $1.rawValue }))
    }
}
