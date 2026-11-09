import XCTest

final class TouchpadTrackerTests: XCTestCase {
    private func raw(_ id: Int32, state: Int32 = 4, x: Float = 0.5, y: Float = 0.5) -> TouchpadRawContact {
        TouchpadRawContact(identifier: id, state: state, x: x, y: y)
    }

    func testTouchingStatesReportTipOnAndOtherStatesDoNotLand() {
        var tracker = TouchpadTracker()
        XCTAssertEqual(tracker.track([raw(1, state: 1), raw(2, state: 2)]), [], "arriving and hovering fingers are not down")
        XCTAssertFalse(tracker.isTouching)
        let landed = tracker.track([raw(1, state: 3), raw(2, state: 4)])
        XCTAssertEqual(landed.map(\.id), [1, 2])
        XCTAssertTrue(landed.allSatisfy { $0.tip && $0.confidence })
        XCTAssertTrue(tracker.isTouching)
    }

    func testVanishedContactLiftsOnceAtItsLastPosition() {
        var tracker = TouchpadTracker()
        _ = tracker.track([raw(7, x: 0.25, y: 0.75)])
        let lifted = tracker.track([])
        XCTAssertEqual(lifted, [TouchpadContact(id: 7, x: TouchpadTracker.logical(0.25), y: TouchpadTracker.logical(0.25), tip: false)])
        XCTAssertEqual(tracker.track([]), [], "no second lift")
        XCTAssertFalse(tracker.isTouching)
    }

    func testBreakStateLiftsAtTheLastKnownPositionNotTheBreakPosition() {
        var tracker = TouchpadTracker()
        _ = tracker.track([raw(3, x: 0.1, y: 0.9)])
        let lifted = tracker.track([raw(3, state: 5, x: 0.9, y: 0.1)])
        XCTAssertEqual(lifted.count, 1)
        XCTAssertFalse(lifted[0].tip)
        XCTAssertEqual(lifted[0].x, TouchpadTracker.logical(0.1))
        XCTAssertEqual(tracker.track([raw(3, state: 7)]), [], "lingering frames after the lift report nothing")
    }

    func testMovingContactKeepsItsIdentifierAndOrder() {
        var tracker = TouchpadTracker()
        _ = tracker.track([raw(2, x: 0.2), raw(9, x: 0.9)])
        let moved = tracker.track([raw(9, x: 0.8), raw(2, x: 0.3)])
        XCTAssertEqual(moved.map(\.id), [2, 9], "reports keep landing order whatever the frame's order")
        XCTAssertEqual(moved.map(\.x), [TouchpadTracker.logical(0.3), TouchpadTracker.logical(0.8)])
    }

    func testIdentifiersAreMaskedToTheDescriptorRange() {
        XCTAssertEqual(TouchpadTracker.identifier(0), 0)
        XCTAssertEqual(TouchpadTracker.identifier(127), 127)
        XCTAssertEqual(TouchpadTracker.identifier(128), 0)
        XCTAssertEqual(TouchpadTracker.identifier(300), 44)
        var tracker = TouchpadTracker()
        XCTAssertEqual(tracker.track([raw(200)]).map(\.id), [72])
    }

    func testYIsFlippedAndPositionsClamp() {
        XCTAssertTrue(TouchpadTracker.flipsY, "the framework's origin is bottom left, Windows' top left")
        let (x, y) = TouchpadTracker.position(raw(1, x: 0.25, y: 0))
        XCTAssertEqual(x, 1024)
        XCTAssertEqual(y, TouchpadReport.logicalMaximum, "y = 0 at the bottom of the pad is the digitizer's maximum")
        XCTAssertEqual(TouchpadTracker.position(raw(1, x: 0, y: 1)).1, 0)
        XCTAssertEqual(TouchpadTracker.logical(-0.5), 0)
        XCTAssertEqual(TouchpadTracker.logical(1.5), TouchpadReport.logicalMaximum)
        XCTAssertEqual(TouchpadTracker.logical(.nan), 0)
        XCTAssertEqual(TouchpadTracker.logical(.infinity), 0)
    }

    func testLiftReturnsTipOffForEverythingDownAndEmptiesTheTracker() {
        var tracker = TouchpadTracker()
        _ = tracker.track([raw(1), raw(2, x: 0.75)])
        let lifted = tracker.lift()
        XCTAssertEqual(lifted.map(\.id), [1, 2])
        XCTAssertTrue(lifted.allSatisfy { !$0.tip })
        XCTAssertEqual(lifted[1].x, TouchpadTracker.logical(0.75))
        XCTAssertFalse(tracker.isTouching)
        XCTAssertEqual(tracker.lift(), [])
        XCTAssertEqual(tracker.track([]), [])
    }

    func testAtMostTheDescriptorsMaximumContactsAreTracked() {
        var tracker = TouchpadTracker()
        let six = (1 ... 6).map { raw(Int32($0), x: Float($0) / 10) }
        let first = tracker.track(six)
        XCTAssertEqual(first.map(\.id), [1, 2, 3, 4, 5], "the sixth finger waits")
        let next = tracker.track(Array(six.dropFirst()))
        XCTAssertEqual(next.map { "\($0.id):\($0.tip)" }, ["1:false", "2:true", "3:true", "4:true", "5:true", "6:true"])
        XCTAssertLessThanOrEqual(next.count, 2 * TouchpadReport.maximumContacts)
    }

    func testPresenceFollowsTouchesWithAGraceAfterTheLift() {
        var activity = TouchpadActivity()
        XCTAssertEqual(activity.presence(now: 10), .none)
        activity.update(touching: 1, now: 10)
        XCTAssertEqual(activity.presence(now: 10.5), .touching(1))
        activity.update(touching: 3, now: 10.6)
        XCTAssertEqual(activity.presence(now: 10.6), .touching(3))
        activity.update(touching: 0, now: 11)
        XCTAssertEqual(activity.presence(now: 11.1), .recent)
        XCTAssertEqual(activity.presence(now: 11 + TouchpadActivity.grace), .none)
        activity.update(touching: 0, now: 12)
        XCTAssertEqual(activity.presence(now: 12.01), .none, "staying lifted does not renew the grace")
        activity.update(touching: 2, now: 13)
        XCTAssertEqual(activity.presence(now: 13), .touching(2))
    }

    func testHidingTheLoneFingerReportsOnlyTwoOrMoreAndLiftsWhenOneRemains() {
        var tracker = TouchpadTracker()
        XCTAssertEqual(tracker.track([raw(1)], hidingLoneFinger: true), [], "a lone finger is the network's")
        XCTAssertFalse(tracker.isTouching)
        let landed = tracker.track([raw(1, x: 0.2), raw(2, x: 0.4)], hidingLoneFinger: true)
        XCTAssertEqual(landed.map(\.id), [1, 2], "the second finger brings the first with it")
        XCTAssertEqual(landed[0].x, TouchpadTracker.logical(0.2))
        let lifted = tracker.track([raw(1, x: 0.25)], hidingLoneFinger: true)
        XCTAssertEqual(lifted.map { "\($0.id):\($0.tip)" }, ["1:false", "2:false"], "dropping to one lifts both")
        XCTAssertEqual(lifted[0].x, TouchpadTracker.logical(0.2), "at the last reported position")
        XCTAssertEqual(tracker.track([raw(1, x: 0.3)], hidingLoneFinger: true), [])
        XCTAssertEqual(tracker.track([raw(1, x: 0.3)], hidingLoneFinger: false).map(\.id), [1], "policy off: it lands")
        XCTAssertEqual(
            tracker.track([raw(1, x: 0.3)], hidingLoneFinger: true).map { "\($0.id):\($0.tip)" },
            ["1:false"],
            "policy on: it lifts"
        )
    }

    func testOnlyFramesWithoutLiftsOrButtonChangesAreDroppable() {
        let down = TouchpadContact(id: 1, x: 1, y: 1)
        var up = down
        up.tip = false
        XCTAssertTrue(TouchpadStream.droppable(TouchpadFrame(contacts: [down]), button: false))
        XCTAssertFalse(TouchpadStream.droppable(TouchpadFrame(contacts: [down, up]), button: false))
        XCTAssertFalse(TouchpadStream.droppable(TouchpadFrame(contacts: [down], button: true), button: false))
        XCTAssertFalse(TouchpadStream.droppable(TouchpadFrame(contacts: [], button: false), button: true))
        XCTAssertTrue(TouchpadStream.droppable(TouchpadFrame(contacts: [down], button: true), button: true))
    }
}
