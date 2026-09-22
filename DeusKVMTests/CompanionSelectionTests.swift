import Foundation
import XCTest

final class CompanionSelectionTests: XCTestCase {
    func testDisableReleasesControlWithoutDisconnectAndOldGrantsCannotReviveIt() throws {
        let pc = UUID()
        var state = CompanionSelection(epoch: 100)
        XCTAssertTrue(state.update(enabled: true, allowed: [pc]))
        let oldGrant = state.packet(for: pc)
        try state.receive(oldGrant, from: pc)
        XCTAssertTrue(state.granted(to: pc))
        XCTAssertTrue(state.update(enabled: false, allowed: [pc]))
        XCTAssertFalse(state.granted(to: pc))
        XCTAssertEqual(state.packet(for: pc).last, 0)
        try state.receive(oldGrant, from: pc)
        XCTAssertFalse(state.granted(to: pc))
        XCTAssertTrue(state.update(enabled: true, allowed: [pc]))
        try state.receive(oldGrant, from: pc)
        XCTAssertFalse(state.granted(to: pc), "Re-enable must wait for a fresh Windows decision")
        try state.receive(state.packet(for: pc), from: pc)
        XCTAssertTrue(state.granted(to: pc))
        state.disconnect(pc)
        XCTAssertFalse(state.granted(to: pc))
    }

    func testPermissionAndExplicitRevocationGateCapture() throws {
        let pc = UUID(), other = UUID()
        var state = CompanionSelection(epoch: 10)
        _ = state.update(enabled: true, allowed: [pc])
        let grant = CompanionProtocol.u32(state.epoch) + Data([1])
        try state.receive(grant, from: other)
        XCTAssertFalse(state.granted(to: other))
        try state.receive(grant, from: pc)
        try state.receive(CompanionProtocol.u32(state.epoch) + Data([0]), from: pc)
        XCTAssertFalse(state.granted(to: pc))
        XCTAssertThrowsError(try state.receive(Data([1]), from: pc))
        XCTAssertThrowsError(try state.receive(CompanionProtocol.u32(state.epoch) + Data([2]), from: pc))
    }

    func testRepeatedStateDoesNotLoseTheGrant() throws {
        let pc = UUID()
        var state = CompanionSelection(epoch: UInt32.max)
        _ = state.update(enabled: true, allowed: [pc])
        XCTAssertEqual(state.epoch, 0)
        try state.receive(state.packet(for: pc), from: pc)
        XCTAssertFalse(state.update(enabled: true, allowed: [pc]))
        XCTAssertTrue(state.granted(to: pc))
    }
}
