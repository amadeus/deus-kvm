import CoreGraphics
import XCTest

final class TapWorkScheduleTests: XCTestCase {
    func testIdleAndHeldInputDoNotSchedulePolling() {
        XCTAssertNil(TapWorkSchedule.deadline(now: 10, handoffReady: false, probeStarted: nil))
        var handoff = HandoffState(heldKeys: [0])
        handoff.requestToggle()
        XCTAssertNil(TapWorkSchedule.deadline(now: 10, handoffReady: handoff.pendingToggle && handoff.isReleased, probeStarted: nil))
        _ = handoff.key(code: 0, down: false, repeatEvent: false, flags: [], shortcut: .init())
        XCTAssertEqual(
            TapWorkSchedule.deadline(now: 10, handoffReady: handoff.pendingToggle && handoff.isReleased, probeStarted: nil),
            10.02
        )
    }

    func testProbeDeadlineIsNotExtendedByMotionAndEarlierWorkWins() {
        XCTAssertEqual(TapWorkSchedule.deadline(now: 10.5, handoffReady: false, probeStarted: 10), 11)
        XCTAssertEqual(TapWorkSchedule.deadline(now: 10.8, handoffReady: false, probeStarted: 10), 11)
        XCTAssertEqual(TapWorkSchedule.deadline(now: 10.5, handoffReady: true, probeStarted: 10), 10.52)
        XCTAssertEqual(TapWorkSchedule.deadline(now: 10.99, handoffReady: true, probeStarted: 10), 11)
    }

    func testWarpOnlyCorrectsDriftIncludingNegativeDisplayCoordinates() {
        let parked = CGPoint(x: -900, y: 400)
        XCTAssertFalse(TapWorkSchedule.needsCursorCorrection(location: parked, parkingPoint: parked))
        XCTAssertTrue(TapWorkSchedule.needsCursorCorrection(location: CGPoint(x: -899.5, y: 400), parkingPoint: parked))
        XCTAssertTrue(TapWorkSchedule.needsCursorCorrection(location: CGPoint(x: -900, y: 401), parkingPoint: parked))
    }
}
