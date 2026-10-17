import Foundation

/// the active HID backend for input capture
struct HIDInput {
    let sendMouse: (MouseReport) -> Void
    let sendKeyboard: (KeyboardReport) -> Void
    let sendConsumer: (ConsumerReport) -> Void
    let isActive: Bool
    let isConnected: Bool
    let activeError: String?
}
