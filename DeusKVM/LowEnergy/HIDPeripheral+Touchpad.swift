import CoreBluetooth
import Foundation
import os

/// Precision touchpad experiment (PRECISION_TOUCHPAD_PLAN.md): everything Windows does with
/// the touchpad collection is written to the diagnostics log, the developer view can send
/// scripted contact frames to the PC, and precision mode (Phase 2) is decided here and
/// mirrored to the transport and the input tap.
extension HIDPeripheral {
    /// A durable line: unified log and the diagnostics file, whatever the developer setting.
    func note(_ message: String) {
        touchpadLog.notice("\(message, privacy: .public)")
        DiagnosticsLog.shared.write(message)
    }

    /// Precision mode is advertised ∧ subscribed ∧ last Input Mode write = 3. Each flip is
    /// logged and pushed to the Bluetooth queue in order with the route.
    func refreshPrecisionTouchpad() {
        let next = isTouchpadAdvertised && touchpadSubscribed && touchpadInputMode == TouchpadReport.inputModePrecisionTouchpad
        guard next != isPrecisionTouchpad else { return }
        isPrecisionTouchpad = next
        transport.setPrecisionTouchpad(next)
        let mode = touchpadInputMode.map(String.init) ?? "none"
        note("touchpad: precision mode \(next ? "on" : "off") (subscribed=\(touchpadSubscribed) mode=\(mode))")
    }

    /// The transport started or stopped a session's contact stream.
    func touchpadStreamChanged(_ streaming: Bool, _ message: String) {
        if touchpadStreaming != streaming { touchpadStreaming = streaming }
        note(message)
    }

    /// Windows writes Input Mode (3 selects precision touchpad reports) and Selective Reporting
    /// after reading the descriptor; both are worth a line even before the PC is the target.
    func noteTouchpadWrite(_ write: HIDWrite) {
        guard let id = reportID(forCharacteristic: write.characteristic), let name = TouchpadReport.featureName(id) else { return }
        let hex = (write.value ?? Data()).map { String(format: "%02x", $0) }.joined()
        note("touchpad: \(write.central.identifier) wrote \(name) = \(hex.isEmpty ? "empty" : hex)")
        if id == ReportID.touchpadInputMode.rawValue, let mode = write.value?.first, mode != touchpadInputMode {
            touchpadInputMode = mode
        }
    }

    /// Sends one scripted gesture to the current PC and logs how the link took it.
    func playTouchpadScript(_ script: TouchpadScript) {
        guard isTouchpadAdvertised else { return }
        guard hostPolicy.target != nil else { note("touchpad: \(script.rawValue) not sent, no PC is the input target"); return }
        let frames = script.frames
        note("touchpad: sending \(script.rawValue), \(frames.count) frames \(Int(TouchpadScript.interval * 1000)) ms apart")
        transport.playTouchpad(frames, interval: TouchpadScript.interval) { [weak self] accepted, deferred in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self?.note("touchpad: \(script.rawValue) done, \(accepted) reports sent at once, \(deferred) waited for the link")
                }
            }
        }
    }
}

private let touchpadLog = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Touchpad")
