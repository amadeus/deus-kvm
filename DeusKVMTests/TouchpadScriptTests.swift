import XCTest

final class TouchpadScriptTests: XCTestCase {
    func testEveryScriptLiftsEveryFingerOnlyInItsLastFrame() throws {
        for script in TouchpadScript.allCases {
            let frames = script.frames
            XCTAssertGreaterThan(frames.count, 1, script.rawValue)
            let last = try XCTUnwrap(frames.last, script.rawValue)
            XCTAssertFalse(last.contacts.isEmpty, script.rawValue)
            XCTAssertTrue(last.contacts.allSatisfy { !$0.tip }, "\(script.rawValue) ends with tips off")
            for frame in frames.dropLast() {
                XCTAssertTrue(frame.contacts.allSatisfy(\.tip), "\(script.rawValue) keeps tips on until the lift")
                XCTAssertEqual(frame.contacts.map(\.id), last.contacts.map(\.id), "\(script.rawValue) keeps identifiers")
            }
        }
    }

    func testContactsStayInsideTheLogicalRangeWithUniqueIdentifiers() {
        for script in TouchpadScript.allCases {
            for frame in script.frames {
                XCTAssertEqual(Set(frame.contacts.map(\.id)).count, frame.contacts.count, script.rawValue)
                XCTAssertLessThanOrEqual(frame.contacts.count, TouchpadReport.maximumContacts, script.rawValue)
                for contact in frame.contacts {
                    XCTAssertLessThanOrEqual(contact.x, TouchpadReport.logicalMaximum, script.rawValue)
                    XCTAssertLessThanOrEqual(contact.y, TouchpadReport.logicalMaximum, script.rawValue)
                    XCTAssertTrue(contact.confidence, script.rawValue)
                }
            }
        }
    }

    func testMoveTravelsRightWithOneFingerAtConstantHeight() {
        let frames = TouchpadScript.move.frames
        let xs = frames.map { $0.contacts[0].x }
        XCTAssertTrue(frames.allSatisfy { $0.contacts.count == 1 })
        XCTAssertEqual(xs, xs.sorted())
        XCTAssertEqual(xs.first, TouchpadScript.logical(0.30))
        XCTAssertEqual(xs.last, TouchpadScript.logical(0.70))
        XCTAssertEqual(Set(frames.map { $0.contacts[0].y }).count, 1)
        XCTAssertFalse(frames.contains { $0.button })
    }

    func testClickPressesTheButtonWhileTheFingerRests() {
        let frames = TouchpadScript.click.frames
        let buttons = frames.map(\.button)
        XCTAssertEqual(buttons, [false, false, false, true, true, true, true, true, true, false, false, false, false])
        XCTAssertEqual(Set(frames.flatMap { $0.contacts.map { "\($0.x),\($0.y)" } }).count, 1, "the finger does not move")
    }

    func testMultiFingerGesturesUseTheExpectedFingerCountsAndDirections() throws {
        let scroll = TouchpadScript.scrollDown.frames
        XCTAssertTrue(scroll.allSatisfy { $0.contacts.count == 2 })
        XCTAssertLessThan(try XCTUnwrap(scroll.first?.contacts[0].y), try XCTUnwrap(scroll.last?.contacts[0].y))
        let swipeUp = TouchpadScript.threeFingerSwipeUp.frames
        XCTAssertTrue(swipeUp.allSatisfy { $0.contacts.count == 3 })
        XCTAssertGreaterThan(try XCTUnwrap(swipeUp.first?.contacts[1].y), try XCTUnwrap(swipeUp.last?.contacts[1].y))
        let swipeLeft = TouchpadScript.fourFingerSwipeLeft.frames
        XCTAssertTrue(swipeLeft.allSatisfy { $0.contacts.count == 4 })
        XCTAssertGreaterThan(try XCTUnwrap(swipeLeft.first?.contacts[3].x), try XCTUnwrap(swipeLeft.last?.contacts[3].x))
        XCTAssertEqual(TouchpadScript.tap.frames.count, 6, "five resting frames and the lift")
    }

    func testLogicalClampsNormalizedPositions() {
        XCTAssertEqual(TouchpadScript.logical(-1), 0)
        XCTAssertEqual(TouchpadScript.logical(0), 0)
        XCTAssertEqual(TouchpadScript.logical(0.5), 2048)
        XCTAssertEqual(TouchpadScript.logical(2), 4095)
    }
}
