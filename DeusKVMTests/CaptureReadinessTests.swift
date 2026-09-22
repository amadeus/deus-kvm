import XCTest

final class CaptureReadinessTests: XCTestCase {
    private var ready: CaptureReadiness {
        CaptureReadiness(
            enabled: true,
            target: true,
            accessibility: true,
            keyboardMonitoring: true,
            tap: true,
            display: true,
            secureInput: false,
            granted: true
        )
    }

    func testConnectedMacCannotOfferANonfunctionalSwitchButton() {
        XCTAssertTrue(ready.canSwitch)
        for key in [\CaptureReadiness.enabled, \.target, \.accessibility, \.keyboardMonitoring, \.tap, \.display, \.granted] {
            var state = ready
            state[keyPath: key] = false
            XCTAssertFalse(state.canSwitch)
            XCTAssertNotNil(state.blocker)
        }
        var state = ready
        state.secureInput = true
        XCTAssertFalse(state.canSwitch)
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
