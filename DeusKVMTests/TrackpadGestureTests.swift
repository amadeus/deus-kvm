import XCTest

final class TrackpadGestureTests: XCTestCase {
    func testMaskCoversEveryGestureTypeAndNoExistingInputType() {
        for type in TrackpadGesture.eventTypes {
            XCTAssertNotEqual(TrackpadGesture.eventMask & (CGEventMask(1) << CGEventMask(type)), 0)
        }
        for type: CGEventType in [.keyDown, .keyUp, .flagsChanged, .mouseMoved, .scrollWheel, .leftMouseDown] {
            XCTAssertFalse(TrackpadGesture.eventTypes.contains(type.rawValue))
        }
    }
}
