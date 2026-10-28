import XCTest

final class CaptureReadinessTests: XCTestCase {
    private var ready: CaptureReadiness {
        CaptureReadiness(
            enabled: true,
            target: true,
            accessibility: true,
            keyboardMonitoring: .ready,
            tap: true,
            display: true,
            secureInput: false,
            granted: true
        )
    }

    func testConnectedMacCannotOfferANonfunctionalSwitchButton() {
        XCTAssertTrue(ready.canSwitch)
        for key in [\CaptureReadiness.enabled, \.target, \.accessibility, \.tap, \.display, \.granted] {
            var state = ready
            state[keyPath: key] = false
            XCTAssertFalse(state.canSwitch)
            XCTAssertNotNil(state.blocker)
        }
        var state = ready
        state.secureInput = true
        XCTAssertFalse(state.canSwitch)
    }

    func testKeyboardFailureRemainsBlockedWithoutMisreportingPermission() {
        var state = ready
        state.keyboardMonitoring = .unavailable(-536_870_203)
        XCTAssertFalse(state.canSwitch)
        XCTAssertEqual(state.blocker, KeyboardMonitoringStatus.unavailable(-536_870_203).message)
        XCTAssertNotEqual(state.blocker, KeyboardMonitoringStatus.permissionRequired.message)
        state.keyboardMonitoring = .permissionRequired
        XCTAssertFalse(state.canSwitch)
        XCTAssertEqual(state.blocker, KeyboardMonitoringStatus.permissionRequired.message)
        state.keyboardMonitoring = .ready
        XCTAssertTrue(state.canSwitch)
        XCTAssertNil(state.blocker)
    }

    func testWaitingAndCaptureFailureExplainWhyEntryIsBlocked() {
        var state = ready
        state.waiting = true
        XCTAssertFalse(state.canSwitch)
        XCTAssertTrue(state.blocker?.contains("transfer control") == true)
        state.waiting = false
        state.tap = false
        XCTAssertTrue(state.blocker?.contains("capture") == true)
        state.tap = true
        state.display = false
        XCTAssertTrue(state.blocker?.contains("display") == true)
    }
}
