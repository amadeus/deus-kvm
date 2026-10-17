import XCTest

final class HIDReportTests: XCTestCase {
    func testMouseWireFormatIncludesBothScrollAxes() {
        let report = MouseReport(buttons: [.left, .right], dX: -127, dY: 127, wheel: -1, pan: 2)
        XCTAssertEqual(Array(report.data), [3, 129, 127, 255, 2])
        XCTAssertEqual(Array(MouseReport(pan: -2).data), [0, 0, 0, 0, 254])
    }

    func testBootMouseFormatExcludesBothScrollAxes() {
        let report = MouseReport(buttons: [.left, .right], dX: -127, dY: 127, wheel: -1, pan: 2)
        XCTAssertEqual(Array(report.bootData), [3, 129, 127])
        XCTAssertEqual(Array(MouseReport.zero.bootData), [0, 0, 0])
    }

    func testReplayableMouseStateKeepsButtonsOnly() {
        let report = MouseReport(buttons: [.left, .middle], dX: 5, dY: -3, wheel: 2, pan: -1)
        XCTAssertEqual(report.held, MouseReport(buttons: [.left, .middle]))
        XCTAssertEqual(Array(report.held.bootData), [5, 0, 0])
    }

    func testReleaseReportsClearEveryField() {
        XCTAssertEqual(Array(KeyboardReport.zero.data), Array(repeating: 0, count: 8))
        XCTAssertEqual(Array(MouseReport.zero.data), Array(repeating: 0, count: 5))
    }
}
