import Foundation
import XCTest

final class PolishTests: XCTestCase {
    func testExplicitSwitchCentersButEdgesKeepTheirPosition() throws {
        let centered = CompanionEntry.packet(id: 7, edge: 1, fraction: 12345, fromEdge: false, supportsCenter: true)
        XCTAssertEqual(centered.type, CompanionProtocol.Message.enterCenter.rawValue)
        XCTAssertEqual(centered.payload, Data([7, 1]))
        for (fromEdge, supported) in [(true, true), (true, false), (false, false)] {
            let entry = CompanionEntry.packet(id: 7, edge: 1, fraction: 12345, fromEdge: fromEdge, supportsCenter: supported)
            XCTAssertEqual(entry.type, CompanionProtocol.Message.enter.rawValue)
            XCTAssertEqual(entry.payload, Data([7, 1, 57, 48]))
        }
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion","chunk":20}"#.utf8)
        XCTAssertNil(try JSONDecoder().decode(CompanionHello.self, from: legacy).center)
    }

    func testFastDisableEnableWaitsForOldWorkerAndIgnoresLateReadyCallbacks() throws {
        var state = TapRunState()
        let old = try XCTUnwrap(state.begin())
        XCTAssertTrue(state.isRunning)
        state.stop()
        XCTAssertFalse(state.isRunning)
        XCTAssertFalse(state.isCurrent(old))
        XCTAssertNil(state.begin())
        state.finish(old)
        let next = try XCTUnwrap(state.begin())
        XCTAssertNotEqual(old, next)
        state.finish(old)
        XCTAssertTrue(state.isRunning)
        XCTAssertTrue(state.isCurrent(next))
        state.finish(next)
        XCTAssertFalse(state.isRunning)
    }
}
