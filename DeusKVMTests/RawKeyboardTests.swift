import XCTest

final class RawKeyboardTests: XCTestCase {
    func testPCKeysAreDistinctFromF13ThroughF15() {
        let expected: [UInt32: Keycode] = [0x46: .printScreen, 0x47: .scrollLock, 0x48: .pause, 0x68: .f13, 0x69: .f14, 0x6A: .f15]
        for (usage, key) in expected {
            XCTAssertEqual(RawKeyboardState.key(usagePage: 7, usage: usage), key)
        }
        XCTAssertEqual(Set(expected.values).count, 6)
        XCTAssertEqual(RawKeyboardState.quartzAliases, [0x69, 0x6B, 0x71, 0x72])
    }

    func testBackslashAndNonUSHashUseQuartzWithEventModifiers() {
        // A successful IOHIDManager open must not suppress the only usable pipe
        // event or send a raw press with a stale session Shift snapshot.
        XCTAssertFalse(RawKeyboardState.quartzAliases.contains(0x2A))
        for usage: UInt32 in [0x31, 0x32] {
            XCTAssertNil(RawKeyboardState.key(usagePage: 7, usage: usage))
        }
        XCTAssertEqual(Keycode(macVirtualKey: 0x2A), .backslash)
    }

    func testUnrepresentedKeysUseRawPathButOrdinaryKeysAreNotDuplicated() {
        for usage: UInt32 in [0x70, 0x71, 0x72, 0x73, 0x74, 0x85, 0x88, 0x8A, 0xB0, 0xDD] {
            let key = Keycode(rawValue: UInt8(usage))
            if Keycode.macVirtualKeys.values.contains(key) { continue }
            XCTAssertEqual(RawKeyboardState.key(usagePage: 7, usage: usage), key)
        }
        for usage: UInt32 in [0x04, 0x28, 0x2A, 0x39, 0x4C, 0xE0, 0xE6] {
            XCTAssertNil(RawKeyboardState.key(usagePage: 7, usage: usage))
        }
        XCTAssertNil(RawKeyboardState.key(usagePage: 0x0C, usage: 0xE9))
        XCTAssertNil(RawKeyboardState.key(usagePage: 7, usage: 0))
    }

    func testDuplicateReportsAndMultipleKeyboardsDoNotCauseExtraPressesOrEarlyRelease() {
        var state = RawKeyboardState()
        XCTAssertTrue(state.update(device: 1, key: .printScreen, down: true))
        XCTAssertFalse(state.update(device: 1, key: .printScreen, down: true))
        XCTAssertFalse(state.update(device: 2, key: .printScreen, down: true))
        XCTAssertFalse(state.update(device: 1, key: .printScreen, down: false))
        XCTAssertTrue(state.update(device: 2, key: .printScreen, down: false))
        XCTAssertFalse(state.update(device: 2, key: .printScreen, down: false))
    }

    func testUnplugReleasesOnlyKeysNoOtherKeyboardStillHolds() {
        var state = RawKeyboardState()
        _ = state.update(device: 1, key: .pause, down: true)
        _ = state.update(device: 1, key: .f21, down: true)
        _ = state.update(device: 2, key: .pause, down: true)
        XCTAssertEqual(state.remove(device: 1), [.f21])
        XCTAssertEqual(state.remove(device: 2), [.pause])
    }

    func testRawKeyHoldBlocksHandoffEvenWhenQuartzHasNoKeyCode() {
        var state = HandoffState()
        state.heldRawKeys.insert(.f24)
        state.requestToggle()
        XCTAssertFalse(state.takeToggle())
        state.heldRawKeys.remove(.f24)
        XCTAssertTrue(state.takeToggle())
    }
}
