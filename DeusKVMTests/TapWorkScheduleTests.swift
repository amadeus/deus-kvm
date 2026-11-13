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

    func testPollsAwayFromTheEdgeSoTheCursorCannotReachItUnseen() throws {
        XCTAssertEqual(PointerProximity.band, 360, accuracy: 0.001)
        XCTAssertNil(PointerProximity.pollInterval(distance: 0))
        XCTAssertNil(PointerProximity.pollInterval(distance: 360))
        // Never later than the cursor could arrive at top speed, and never slower than 10 Hz.
        for distance in [361.0, 900, 1500, 2999, 3000, 6000, 20000] {
            let interval = try XCTUnwrap(PointerProximity.pollInterval(distance: distance))
            XCTAssertLessThanOrEqual(interval * PointerProximity.topSpeed, distance + 0.001)
            XCTAssertGreaterThanOrEqual(interval, PointerProximity.shortestPoll)
            XCTAssertLessThanOrEqual(interval, PointerProximity.longestPoll)
        }
    }

    func testTheThreeTapsSplitTheOriginalMaskWithoutOverlap() {
        let original = [
            CGEventType.keyDown, .keyUp, .flagsChanged, .mouseMoved, .leftMouseDown, .leftMouseUp,
            .leftMouseDragged, .rightMouseDown, .rightMouseUp, .rightMouseDragged,
            .otherMouseDown, .otherMouseUp, .otherMouseDragged, .scrollWheel, MediaKeyEvent.eventType
        ].reduce(TrackpadGesture.eventMask) { $0 | CGEventMask(1) << CGEventMask($1.rawValue) }
        // Everything but the raw touch frames, which are no longer swallowed while remote.
        let touchFrames = CGEventMask(1) << CGEventMask(TrackpadGesture.touchFrames)
        XCTAssertEqual(TapMask.main | TapMask.motion | TapMask.remote, original & ~touchFrames)
        XCTAssertEqual(TapMask.remote & touchFrames, 0)
        for type in TrackpadGesture.eventTypes where type != TrackpadGesture.touchFrames {
            XCTAssertNotEqual(TapMask.remote & CGEventMask(1) << CGEventMask(type), 0, "gesture type \(type) is still swallowed")
        }
        XCTAssertEqual(TapMask.main & TapMask.motion, 0)
        XCTAssertEqual(TapMask.main & TapMask.remote, 0)
        XCTAssertEqual(TapMask.motion & TapMask.remote, 0)
        // Buttons and keys stay on the always-on tap: they decide whether input is held.
        XCTAssertNotEqual(TapMask.main & CGEventMask(1) << CGEventMask(CGEventType.leftMouseUp.rawValue), 0)
        XCTAssertNotEqual(TapMask.main & CGEventMask(1) << CGEventMask(CGEventType.flagsChanged.rawValue), 0)
    }

    func testWarpOnlyCorrectsDriftIncludingNegativeDisplayCoordinates() {
        let parked = CGPoint(x: -900, y: 400)
        XCTAssertFalse(TapWorkSchedule.needsCursorCorrection(location: parked, parkingPoint: parked))
        XCTAssertTrue(TapWorkSchedule.needsCursorCorrection(location: CGPoint(x: -899.5, y: 400), parkingPoint: parked))
        XCTAssertTrue(TapWorkSchedule.needsCursorCorrection(location: CGPoint(x: -900, y: 401), parkingPoint: parked))
    }
}
