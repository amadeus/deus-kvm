import Foundation

/// A status in two lengths: a label of at most three words for the menu bar, and the full
/// sentence, with what to do about it, for the controls window and diagnostics.
struct StatusText: Equatable, Sendable {
    let label: String
    let detail: String

    static let disabled = StatusText(label: "Disabled", detail: "DeusKVM is disabled")
    static let edgeReady = StatusText(label: "Edge return ready", detail: "Windows edge return ready")
    static let hotkeyOnly = StatusText(label: "Hotkey return only", detail: "Windows edge return unavailable — use the hotkey")
    static let companionOffline = StatusText(label: "Companion offline", detail: "Waiting for Windows companion")
}

/// Connection readiness alone does not mean a Mac can capture and route input.
struct CaptureReadiness {
    var enabled = false
    var target = false
    var accessibility = false
    var keyboardMonitoring = false
    var tap = false
    var display = false
    var secureInput = false
    var granted = false
    var waiting = false

    var canSwitch: Bool {
        blocker == nil
    }

    var blocker: String? {
        block?.detail
    }

    var block: StatusText? {
        if !enabled { return .disabled }
        if !accessibility { return StatusText(label: "Needs Accessibility", detail: "Allow Accessibility in Setup to control Windows") }
        if !keyboardMonitoring {
            return StatusText(label: "Needs Input Monitoring", detail: "Allow Input Monitoring in Setup, then quit and reopen DeusKVM")
        }
        if !tap { return StatusText(label: "Capture not ready", detail: "Input capture is not ready; check permissions in Setup") }
        if !display { return StatusText(label: "No display selected", detail: "Choose an available Mac display in Configuration") }
        if secureInput { return StatusText(label: "Secure Input on", detail: "Secure Input is active on this Mac") }
        if !target { return StatusText(label: "PC not connected", detail: "Waiting for the allowed PC's keyboard and mouse connection") }
        if waiting { return StatusText(label: "Waiting for Windows", detail: "Waiting for Windows to transfer control") }
        if !granted { return StatusText(label: "Awaiting companion approval", detail: "Waiting for Windows companion to grant control") }
        return nil
    }
}

/// Whether the controls window should open when DeusKVM launches: only while a permission is
/// missing or no Windows PC has been paired yet.
enum LaunchWindowPolicy {
    static func shouldOpen(bluetooth: Bool, accessibility: Bool, inputMonitoring: Bool, pairedPC: Bool) -> Bool {
        !(bluetooth && accessibility && inputMonitoring && pairedPC)
    }
}
