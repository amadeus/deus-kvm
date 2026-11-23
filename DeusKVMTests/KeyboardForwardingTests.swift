import AppKit
import XCTest

@MainActor
final class KeyboardForwardingTests: XCTestCase {
    private var recorder = InputRecorder()
    private var reports: [KeyboardReport] {
        recorder.keyboard
    }

    private var consumerReports: [ConsumerKey] {
        recorder.consumer
    }

    func testControlAltDeleteFullPressAndReleaseSequence() throws {
        let controller = makeController()
        try send(controller, .flagsChanged, key: 0x3B, flags: [.maskControl])
        try send(controller, .flagsChanged, key: 0x3A, flags: [.maskControl, .maskAlternate])
        try send(controller, .keyDown, key: 0x75, flags: [.maskControl, .maskAlternate])
        XCTAssertEqual(reports.last, KeyboardReport(modifiers: [.leftCtrl, .leftAlt], keys: [.deleteForward]))
        try send(controller, .keyUp, key: 0x75, flags: [.maskControl, .maskAlternate])
        try send(controller, .flagsChanged, key: 0x3A, flags: [.maskControl])
        try send(controller, .flagsChanged, key: 0x3B, flags: [])
        XCTAssertEqual(reports.map(\.modifiers.rawValue), [1, 5, 5, 5, 1, 0])
        XCTAssertEqual(reports.last, .zero)
    }

    func testNavigationKeypadFunctionAndInternationalKeysHaveMatchingReleases() throws {
        let expected: [(UInt16, UInt8)] = [
            (0x72, 0x49), (0x73, 0x4A), (0x74, 0x4B), (0x75, 0x4C), (0x77, 0x4D), (0x79, 0x4E),
            (0x41, 0x63), (0x43, 0x55), (0x45, 0x57), (0x47, 0x53), (0x4B, 0x54), (0x4C, 0x58),
            (0x4E, 0x56), (0x51, 0x67), (0x52, 0x62), (0x53, 0x59), (0x54, 0x5A), (0x55, 0x5B),
            (0x56, 0x5C), (0x57, 0x5D), (0x58, 0x5E), (0x59, 0x5F), (0x5B, 0x60), (0x5C, 0x61),
            (0x69, 0x68), (0x6B, 0x69), (0x71, 0x6A), (0x6A, 0x6B),
            (0x40, 0x6C), (0x4F, 0x6D), (0x50, 0x6E), (0x5A, 0x6F),
            (0x0A, 0x64), (0x5D, 0x89), (0x5E, 0x87), (0x5F, 0x85), (0x66, 0x91), (0x68, 0x90), (0x6E, 0x65)
        ]
        let controller = makeController()
        for (mac, hid) in expected {
            try send(controller, .keyDown, key: mac)
            XCTAssertEqual(reports.last?.keys.map(\.rawValue), [hid], "Mac key \(mac)")
            try send(controller, .keyUp, key: mac)
            XCTAssertEqual(reports.last, .zero, "Release Mac key \(mac)")
        }
    }

    func testRightModifiersAndBothSidesRemainDistinct() {
        let flags: CGEventFlags = [.maskControl, .maskShift, .maskAlternate, .maskCommand]
        XCTAssertEqual(KeyboardModifiers(eventFlags: CGEventFlags(rawValue: flags.rawValue | 0x2054)).rawValue, 0xF0)
        XCTAssertEqual(KeyboardModifiers(eventFlags: CGEventFlags(rawValue: flags.rawValue | 0x207F)).rawValue, 0xFF)
        XCTAssertEqual(KeyboardModifiers(eventFlags: flags).rawValue, 0x0F)
        XCTAssertEqual(KeyboardModifiers(eventFlags: CGEventFlags(rawValue: 0x2054)).rawValue, 0)
    }

    func testCapsLockTogglesOnceForEachLatchChangeAndNeverStaysHeld() throws {
        let controller = makeController()
        let initial = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        let changed: CGEventFlags = initial ? [] : [.maskAlphaShift]
        try send(controller, .flagsChanged, key: 0x39, flags: changed)
        try send(controller, .flagsChanged, key: 0x39, flags: changed)
        try send(controller, .flagsChanged, key: 0x39, flags: initial ? [.maskAlphaShift] : [])
        XCTAssertEqual(reports.filter { $0.keys.contains(.capsLock) }.count, 2)
        XCTAssertEqual(reports.last, .zero)
    }

    func testAutorepeatLeavesKeyHeldUntilRealRelease() throws {
        let controller = makeController()
        try send(controller, .keyDown, key: 0x00)
        let repeated = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true))
        repeated.setIntegerValueField(.keyboardEventAutorepeat, value: 1)
        XCTAssertNil(DirectInputEvent(type: .keyDown, event: repeated))
        XCTAssertEqual(reports.last?.keys, [.a])
        try send(controller, .keyUp, key: 0x00)
        XCTAssertEqual(reports.last, .zero)
    }

    func testMediaCaptureRepeatReleaseAndStopCleanup() throws {
        let controller = makeController()
        try media(controller, code: 0, down: true)
        try media(controller, code: 0, down: true, repeatEvent: true)
        XCTAssertEqual(consumerReports, [.volumeUp])
        try media(controller, code: 0, down: false)
        XCTAssertEqual(consumerReports.last, ConsumerKey.none)
        try media(controller, code: 16, down: true)
        try send(controller, .keyDown, key: 0x75, flags: [.maskControl])
        controller.stop()
        XCTAssertEqual(consumerReports.last, ConsumerKey.none)
        XCTAssertEqual(reports.last, .zero)
    }

    func testReleasingOneConsumerKeyDoesNotReleaseAnother() throws {
        let controller = makeController()
        try media(controller, code: 0, down: true)
        try media(controller, code: 1, down: true)
        try media(controller, code: 0, down: false)
        XCTAssertEqual(consumerReports.last, .volumeDown)
        try media(controller, code: 1, down: false)
        XCTAssertEqual(consumerReports.last, ConsumerKey.none)
    }

    func testMediaHoldDefersHandoffUntilRelease() {
        var state = HandoffState()
        state.heldMediaKeys.insert(0xE9)
        state.requestToggle()
        XCTAssertFalse(state.takeToggle())
        state.heldMediaKeys.remove(0xE9)
        XCTAssertTrue(state.takeToggle())
    }

    func testEveryHeldKeyIsSentWithoutASixKeyLimit() throws {
        let controller = makeController()
        for key: UInt16 in [0, 1, 2, 3, 4, 5, 6] {
            try send(controller, .keyDown, key: key)
        }
        XCTAssertEqual(reports.last?.keys.count, 7)
        try send(controller, .keyUp, key: 6)
        XCTAssertEqual(reports.last?.keys.count, 6)
        controller.stop()
        XCTAssertEqual(reports.last, .zero)
    }

    private func makeController() -> DirectInputController {
        recorder = InputRecorder()
        let controller = DirectInputController()
        controller.start(recorder.sink)
        return controller
    }

    private func send(_ controller: DirectInputController, _ type: CGEventType, key: UInt16, flags: CGEventFlags = []) throws {
        // Construct only; no test posts input to either computer.
        let event = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: type == .keyDown))
        event.flags = flags
        try controller.handle(XCTUnwrap(DirectInputEvent(type: type, event: event)))
    }

    private func media(_ controller: DirectInputController, code: Int, down: Bool, repeatEvent: Bool = false) throws {
        let data = (code << 16) | (down ? 0xA00 : 0xB00) | (repeatEvent ? 1 : 0)
        let event = try XCTUnwrap(NSEvent.otherEvent(
            with: .systemDefined, location: .zero, modifierFlags: [], timestamp: 0,
            windowNumber: 0, context: nil, subtype: 8, data1: data, data2: -1
        )?.cgEvent)
        try controller.handle(XCTUnwrap(DirectInputEvent(type: MediaKeyEvent.eventType, event: event)))
    }
}
