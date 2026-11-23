import Foundation
import XCTest

final class CompanionProtocolTests: XCTestCase {
    func testProblemReportMessagesAndHelloCapability() throws {
        XCTAssertEqual(CompanionProtocol.Message(rawValue: 0x30), .reportRequest)
        XCTAssertEqual(CompanionProtocol.Message(rawValue: 0x31), .report)
        let current = #"{"v":1,"role":"pc","name":"DeusKVM Companion","report":1}"#
        let older = #"{"v":1,"role":"pc","name":"DeusKVM Companion"}"#
        XCTAssertEqual(try JSONDecoder().decode(CompanionHello.self, from: Data(current.utf8)).report, 1)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: Data(older.utf8)).report)
    }

    func testClipboardCapabilityIsOptionalForExistingCompanions() throws {
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion"}"#.utf8)
        let current = Data(#"{"v":1,"role":"pc","name":"Companion","clipboard":1}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: legacy).clipboard)
        XCTAssertEqual(try JSONDecoder().decode(CompanionHello.self, from: current).clipboard, 1)
    }

    func testResumeCapabilityIsOptionalForExistingCompanions() throws {
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion"}"#.utf8)
        let current = Data(#"{"v":1,"role":"pc","name":"Companion","resume":true}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: legacy).resume)
        XCTAssertEqual(try JSONDecoder().decode(CompanionHello.self, from: current).resume, true)
        XCTAssertEqual(CompanionProtocol.Message.resume.rawValue, 0x16)
    }
}
