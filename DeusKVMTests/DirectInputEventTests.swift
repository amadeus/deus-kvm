import CoreGraphics
import XCTest

final class DirectInputEventTests: XCTestCase {
    func testControlAltForwardDeleteProducesWindowsDeleteReport() throws {
        // macOS emits 0x75 for forward Delete, including Fn+Delete on Apple keyboards.
        let event = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 0x75, keyDown: true))
        event.flags = [.maskControl, .maskAlternate, .maskSecondaryFn]
        let input = try XCTUnwrap(DirectInputEvent(type: .keyDown, event: event))
        guard case let .keyDown(key) = input.kind else { return XCTFail("Expected key down") }
        XCTAssertEqual(key, .deleteForward)
        XCTAssertEqual(input.modifiers, [.leftCtrl, .leftAlt], "Fn is not a modifier Windows sees")
    }

    func testForwardDeleteReleaseStillMapsAfterModifiersAreReleased() throws {
        let event = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 0x75, keyDown: false))
        event.flags = []
        let input = try XCTUnwrap(DirectInputEvent(type: .keyUp, event: event))
        guard case let .keyUp(key) = input.kind else { return XCTFail("Expected key up") }
        XCTAssertEqual(key, .deleteForward)
        XCTAssertEqual(input.modifiers, [])
    }

    func testBackspaceRemainsDistinctFromForwardDelete() throws {
        let event = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 0x33, keyDown: true))
        event.flags = [.maskControl, .maskAlternate]
        let input = try XCTUnwrap(DirectInputEvent(type: .keyDown, event: event))
        guard case let .keyDown(key) = input.kind else { return XCTFail("Expected key down") }
        XCTAssertEqual(key, .backspace)
        XCTAssertEqual(input.modifiers, [.leftCtrl, .leftAlt])
    }

    func testHorizontalOnlyScrollSurvivesAndUsesHIDDirection() throws {
        try assertScroll(vertical: 0, horizontal: 3, wheel: 0, pan: -3)
        try assertScroll(vertical: 0, horizontal: -3, wheel: 0, pan: 3)
    }

    func testDiagonalScrollPreservesBothAxes() throws {
        try assertScroll(vertical: -2, horizontal: 5, wheel: -2, pan: -5)
    }

    func testVerticalScrollKeepsExistingDirection() throws {
        try assertScroll(vertical: 4, horizontal: 0, wheel: 4, pan: 0)
        try assertScroll(vertical: -4, horizontal: 0, wheel: -4, pan: 0)
    }

    func testBothAxesStayInsideDescriptorRangeWithoutOverflow() throws {
        try assertScroll(vertical: 1000, horizontal: -1000, wheel: 127, pan: 127)
        try assertScroll(vertical: -1000, horizontal: 1000, wheel: -127, pan: -127)
        try assertScroll(vertical: -128, horizontal: -128, wheel: -127, pan: 127)
    }

    func testEmptyScrollIsIgnored() throws {
        XCTAssertNil(try scrollEvent(vertical: 0, horizontal: 0))
    }

    private func assertScroll(
        vertical: Int32,
        horizontal: Int32,
        wheel: Int8,
        pan: Int8,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws {
        let event = try XCTUnwrap(scrollEvent(vertical: vertical, horizontal: horizontal), file: file, line: line)
        guard case let .scroll(actualWheel, actualPan) = event.kind else {
            return XCTFail("Expected a scroll event", file: file, line: line)
        }
        XCTAssertEqual(actualWheel, wheel, file: file, line: line)
        XCTAssertEqual(actualPan, pan, file: file, line: line)
    }

    private func scrollEvent(vertical: Int32, horizontal: Int32) throws -> DirectInputEvent? {
        // In-memory event only: never posted to either computer.
        let event = try XCTUnwrap(CGEvent(
            scrollWheelEvent2Source: nil, units: .line, wheelCount: 2, wheel1: 0, wheel2: 0, wheel3: 0
        ))
        event.setIntegerValueField(.scrollWheelEventDeltaAxis1, value: Int64(vertical))
        event.setIntegerValueField(.scrollWheelEventDeltaAxis2, value: Int64(horizontal))
        return DirectInputEvent(type: .scrollWheel, event: event)
    }
}
