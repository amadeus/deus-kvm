import XCTest

final class CoordinatorScheduleTests: XCTestCase {
    func testReadyLocalAndDisabledStatesHaveNoStatusPoll() {
        XCTAssertNil(CoordinatorSchedule.pollInterval(enabled: true, remote: false, permission: true, secure: false, tapReady: true))
        XCTAssertNil(CoordinatorSchedule.pollInterval(enabled: false, remote: false, permission: false, secure: true, tapReady: false))
    }

    func testRemoteSafetyAndBlockedRecoveryRetainChecks() {
        XCTAssertEqual(CoordinatorSchedule.pollInterval(enabled: true, remote: true, permission: true, secure: false, tapReady: true), 0.5)
        XCTAssertEqual(CoordinatorSchedule.pollInterval(enabled: true, remote: false, permission: false, secure: false, tapReady: true), 1)
        XCTAssertEqual(CoordinatorSchedule.pollInterval(enabled: true, remote: false, permission: true, secure: true, tapReady: true), 1)
        XCTAssertEqual(CoordinatorSchedule.pollInterval(enabled: true, remote: false, permission: true, secure: false, tapReady: false), 1)
    }

    func testHeartbeatExpiresAtDeadlineAndFreshTrafficExtendsIt() {
        XCTAssertEqual(CoordinatorSchedule.linkDeadline(lastSeen: 100, now: 109), 110)
        XCTAssertNil(CoordinatorSchedule.linkDeadline(lastSeen: 100, now: 110))
        XCTAssertEqual(CoordinatorSchedule.linkDeadline(lastSeen: 109, now: 110), 119)
    }
}
