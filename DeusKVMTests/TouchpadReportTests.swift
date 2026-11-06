import XCTest

final class TouchpadReportTests: XCTestCase {
    func testZeroReportIsAllZeroAndSixteenBytes() {
        XCTAssertEqual(Array(TouchpadReport.zero.data), Array(repeating: 0, count: 16))
    }

    func testContactsScanTimeCountAndButtonLandAtTheirOffsets() {
        let report = TouchpadReport(
            contacts: [
                TouchpadContact(id: 0, x: 0x0123, y: 0x0FFF),
                TouchpadContact(id: 7, x: 0, y: 1, tip: false, confidence: false)
            ],
            scanTime: 0xABCD,
            contactCount: 2,
            button: true
        )
        let bytes = Array(report.data)
        XCTAssertEqual(Array(bytes[0 ..< 6]), [0x03, 0, 0x23, 0x01, 0xFF, 0x0F])
        XCTAssertEqual(Array(bytes[6 ..< 12]), [0x00, 7, 0, 0, 1, 0])
        XCTAssertEqual(Array(bytes[12 ..< 16]), [0xCD, 0xAB, 2, 1])
        XCTAssertEqual(
            Array(TouchpadReport(contacts: [TouchpadContact(id: 3, x: 1, y: 1)], contactCount: 1).data[6 ..< 12]),
            Array(repeating: 0, count: 6),
            "an unused slot stays zero"
        )
    }

    func testHybridSplittingSendsOneFrameAsSeveralReports() {
        let contacts = (0 ..< 5).map { TouchpadContact(id: UInt8($0), x: UInt16($0), y: 9) }
        let reports = TouchpadReport.reports(contacts: contacts, scanTime: 77, button: true)
        XCTAssertEqual(reports.map { $0.contacts.map(\.id) }, [[0, 1], [2, 3], [4]])
        XCTAssertEqual(reports.map(\.contactCount), [5, 0, 0], "the first report carries the frame's count")
        XCTAssertTrue(reports.allSatisfy { $0.scanTime == 77 && $0.button })
        XCTAssertEqual(TouchpadReport.reports(contacts: Array(contacts.prefix(2)), scanTime: 1, button: false).count, 1)
        XCTAssertEqual(TouchpadReport.reports(contacts: Array(contacts.prefix(3)), scanTime: 1, button: false).map(\.contactCount), [3, 0])
        let empty = TouchpadReport.reports(contacts: [], scanTime: 5, button: false)
        XCTAssertEqual(empty, [TouchpadReport(contacts: [], scanTime: 5, contactCount: 0, button: false)])
        XCTAssertEqual(
            TouchpadReport.reports(contacts: contacts + contacts, scanTime: 1, button: false).first?.contactCount,
            5,
            "more than the maximum is truncated"
        )
    }

    func testScanTimeCountsHundredMicrosecondTicksAndWraps() {
        XCTAssertEqual(TouchpadReport.scanTime(uptime: 0), 0)
        XCTAssertEqual(TouchpadReport.scanTime(uptime: 0.0150), 150)
        XCTAssertEqual(TouchpadReport.scanTime(uptime: 6.5535), 65535)
        XCTAssertEqual(TouchpadReport.scanTime(uptime: 6.5536), 0)
        XCTAssertEqual(TouchpadReport.scanTime(uptime: 1000.0001), TouchpadReport.scanTime(uptime: 1000.0001 - 6.5536))
    }

    func testFeatureDefaultsMatchTheDescriptor() {
        XCTAssertEqual(Array(TouchpadReport.capabilities), [5], "five contacts, click-pad")
        XCTAssertEqual(TouchpadReport.certification.count, 256)
        XCTAssertEqual(Array(TouchpadReport.certification.prefix(4)), [0xFC, 0x28, 0xFE, 0x84])
        XCTAssertEqual(Array(TouchpadReport.certification.suffix(4)), [0xA5, 0xE5, 0x24, 0xC2])
        XCTAssertEqual(TouchpadReport.featureDefaults[ReportID.touchpadInputMode.rawValue], Data([0]), "mouse until Windows writes 3")
        XCTAssertEqual(TouchpadReport.featureDefaults[ReportID.touchpadSelectiveReporting.rawValue], Data([0x03]))
        XCTAssertEqual(Set(TouchpadReport.featureDefaults.keys), [8, 9, 10, 11])
        XCTAssertNil(TouchpadReport.featureName(ReportID.touchpad.rawValue))
        XCTAssertEqual(TouchpadReport.featureName(10), "Input Mode")
    }
}
