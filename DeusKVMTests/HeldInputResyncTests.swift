import CoreGraphics
import XCTest

final class HeldInputResyncTests: XCTestCase {
    private let qKey: UInt16 = 12
    private let lockFlags: CGEventFlags = [.maskCommand, .maskControl]

    /// ⌃⌘Q: the tap sees Q go down, then the lock screen's Secure Input hides its release.
    private func lockedWithControlCommandQ() -> HandoffState {
        var handoff = HandoffState()
        _ = handoff.key(code: qKey, down: true, repeatEvent: false, flags: lockFlags, shortcut: ToggleShortcut())
        handoff.updateModifiers([])
        return handoff
    }

    func testControlCommandQLockLeavesQHeldUntilResync() {
        var handoff = lockedWithControlCommandQ()
        XCTAssertFalse(handoff.isReleased, "the missed Q release blocks every crossing")
        let correction = handoff.resync(keys: [], buttons: [], flags: [])
        XCTAssertTrue(handoff.isReleased)
        XCTAssertEqual(correction, HeldInputCorrection(keys: 1))
        XCTAssertEqual(correction.message("screen unlocked"), "held input corrected (screen unlocked): keys=1 raw=0 media=0 buttons=0")
    }

    func testPendingToggleFiresOnceTheStaleKeyIsDropped() {
        var handoff = lockedWithControlCommandQ()
        handoff.requestToggle()
        XCTAssertFalse(handoff.takeToggle())
        _ = handoff.resync(keys: [], buttons: [], flags: [])
        XCTAssertTrue(handoff.takeToggle())
    }

    func testInputStillHeldOnTheMacKeepsBlocking() {
        var handoff = lockedWithControlCommandQ()
        handoff.heldButtons = [0]
        var correction = handoff.resync(keys: [0], buttons: [0], flags: [])
        XCTAssertEqual(correction.total, 1, "only Q was stale")
        XCTAssertFalse(handoff.isReleased, "A and the left button are really held")
        XCTAssertEqual(handoff.heldKeys, [0])

        correction = handoff.resync(keys: [55], buttons: [], flags: [.maskCommand])
        XCTAssertEqual(correction, HeldInputCorrection(keys: 1, buttons: 1))
        XCTAssertTrue(handoff.heldKeys.isEmpty, "modifier keys are tracked through flags, not key codes")
        XCTAssertFalse(handoff.isReleased, "a held Command still blocks")
    }

    func testMediaKeysClearBecauseTheyCannotBeQueried() {
        var handoff = HandoffState()
        handoff.heldMediaKeys = [16]
        XCTAssertEqual(handoff.resync(keys: [], buttons: [], flags: []), HeldInputCorrection(media: 1))
        XCTAssertTrue(handoff.isReleased)
    }

    func testAShortcutKeyReleasedDuringSecureInputIsNotSwallowedLater() {
        let shortcut = ToggleShortcut()
        var handoff = HandoffState()
        XCTAssertTrue(handoff.key(code: 53, down: true, repeatEvent: false, flags: .maskSecondaryFn, shortcut: shortcut))
        _ = handoff.resync(keys: [], buttons: [], flags: [])
        XCTAssertFalse(
            handoff.key(code: 53, down: true, repeatEvent: false, flags: [], shortcut: shortcut),
            "a plain Escape after the missed release must reach the Mac"
        )
    }

    func testGateBoundsBlockedCrossingResyncs() {
        var gate = ResyncGate()
        XCTAssertTrue(gate.allow(now: 10))
        XCTAssertFalse(gate.allow(now: 10.3))
        XCTAssertTrue(gate.allow(now: 10.5))
    }
}
