import Foundation
import XCTest

final class PolishTests: XCTestCase {
    func testExplicitSwitchCentersButEdgesKeepTheirPosition() throws {
        let centered = CompanionEntry.packet(id: 7, edge: 1, fraction: 12345, fromEdge: false, supportsCenter: true, capture: 42)
        XCTAssertEqual(centered.type, CompanionProtocol.Message.enterCenter.rawValue)
        XCTAssertEqual(centered.payload, Data([7, 1]) + CompanionProtocol.u64(42))
        for (fromEdge, supported) in [(true, true), (true, false), (false, false)] {
            let entry = CompanionEntry.packet(id: 7, edge: 1, fraction: 12345, fromEdge: fromEdge, supportsCenter: supported, capture: 42)
            XCTAssertEqual(entry.type, CompanionProtocol.Message.enter.rawValue)
            XCTAssertEqual(entry.payload, Data([7, 1, 57, 48]) + CompanionProtocol.u64(42))
        }
        let legacy = Data(#"{"v":1,"role":"pc","name":"Companion"}"#.utf8)
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
