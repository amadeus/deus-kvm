import XCTest

final class TouchpadReportTests: XCTestCase {
    func testZeroReportIsAllZeroAndThirtyFourBytes() {
        XCTAssertEqual(Array(TouchpadReport.zero.data), Array(repeating: 0, count: 34))
    }

    func testContactsScanTimeCountAndButtonLandAtTheirOffsets() {
        let report = TouchpadReport(
            contacts: [
                TouchpadContact(id: 0, x: 0x0123, y: 0x0FFF),
                TouchpadContact(id: 7, x: 0, y: 1, tip: false, confidence: false)
            ],
            scanTime: 0xABCD,
            button: true
        )
        let bytes = Array(report.data)
        XCTAssertEqual(Array(bytes[0 ..< 6]), [0x03, 0, 0x23, 0x01, 0xFF, 0x0F])
        XCTAssertEqual(Array(bytes[6 ..< 12]), [0x00, 7, 0, 0, 1, 0])
        XCTAssertEqual(Array(bytes[12 ..< 30]), Array(repeating: 0, count: 18), "unused slots stay zero")
        XCTAssertEqual(Array(bytes[30 ..< 34]), [0xCD, 0xAB, 2, 1])
    }

    func testMoreThanFiveContactsAreTruncatedToTheDescriptor() {
        let contacts = (0 ..< 7).map { TouchpadContact(id: UInt8($0), x: 1, y: 1) }
        let bytes = Array(TouchpadReport(contacts: contacts).data)
        XCTAssertEqual(bytes.count, 34)
        XCTAssertEqual(bytes[32], 5)
        XCTAssertEqual(bytes[24 ..< 30].map(\.self), [0x03, 4, 1, 0, 1, 0])
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
