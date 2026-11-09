import Foundation

/// the active HID backend for input capture
struct HIDInput {
    let sendMouse: (MouseReport) -> Void
    let sendKeyboard: (KeyboardReport) -> Void
    let sendConsumer: (ConsumerReport) -> Void
    let isActive: Bool
    let isConnected: Bool
    let activeError: String?
    /// The local-network pointer stream and whether it is up right now; nil without one.
    var sendNetwork: ((NetworkInputEvent) -> Void)?
    var networkReady: (() -> Bool)?
}
