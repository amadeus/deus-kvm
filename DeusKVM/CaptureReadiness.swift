import Foundation

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
        if !enabled { return "DeusKVM is disabled" }
        if !accessibility { return "Allow Accessibility in Setup to control Windows" }
        if !keyboardMonitoring { return "Allow Input Monitoring in Setup, then quit and reopen DeusKVM" }
        if !tap { return "Input capture is not ready; check permissions in Setup" }
        if !display { return "Choose an available Mac display in Layout" }
        if secureInput { return "Secure Input is active on this Mac" }
        if !target { return "Waiting for the allowed PC's keyboard and mouse connection" }
        if waiting { return "Waiting for Windows to transfer control" }
        if !granted { return "Waiting for Windows companion to grant control" }
        return nil
    }
}
