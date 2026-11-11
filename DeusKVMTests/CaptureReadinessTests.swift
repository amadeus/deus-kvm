import XCTest

final class CaptureReadinessTests: XCTestCase {
    func testControlsOpenAtLaunchOnlyWhileSomethingNeedsSettingUp() {
        XCTAssertFalse(LaunchWindowPolicy.shouldOpen(bluetooth: true, accessibility: true, inputMonitoring: true, pairedPC: true))
        XCTAssertTrue(LaunchWindowPolicy.shouldOpen(bluetooth: false, accessibility: true, inputMonitoring: true, pairedPC: true))
        XCTAssertTrue(LaunchWindowPolicy.shouldOpen(bluetooth: true, accessibility: false, inputMonitoring: true, pairedPC: true))
        XCTAssertTrue(LaunchWindowPolicy.shouldOpen(bluetooth: true, accessibility: true, inputMonitoring: false, pairedPC: true))
        XCTAssertTrue(
            LaunchWindowPolicy.shouldOpen(bluetooth: true, accessibility: true, inputMonitoring: true, pairedPC: false),
            "no PC paired yet"
        )
    }

    func testMenuLabelsAreAtMostThreeWordsAndKeepTheFullSentence() {
        var states: [CaptureReadiness] = []
        for flag in 0 ..< 9 {
            var state = ready
            switch flag {
            case 0: state.enabled = false
            case 1: state.accessibility = false
            case 2: state.keyboardMonitoring = false
            case 3: state.tap = false
            case 4: state.display = false
            case 5: state.secureInput = true
            case 6: state.target = false
            case 7: state.waiting = true
            default: state.granted = false
            }
            states.append(state)
        }
        let texts = states.compactMap(\.block) + [.edgeReady, .hotkeyOnly, .companionOffline]
        XCTAssertEqual(texts.count, 12, "every blocker has a status")
        XCTAssertEqual(Set(texts.map(\.label)).count, 12, "and its own label")
        for text in texts {
            XCTAssertLessThanOrEqual(text.label.split(separator: " ").count, 3, text.label)
            XCTAssertGreaterThan(text.detail.count, text.label.count, text.detail)
        }
        XCTAssertEqual(states[2].blocker, "Allow Input Monitoring in Setup, then quit and reopen DeusKVM", "the full sentence is kept")
    }

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
