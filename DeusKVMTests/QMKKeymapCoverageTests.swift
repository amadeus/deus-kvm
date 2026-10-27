import AppKit
import XCTest

@MainActor
final class QMKKeymapCoverageTests: XCTestCase {
    private struct Entry {
        let name: String
        let route: String
        let mac: UInt16
        let input: UInt16
        let output: UInt16
        let modifiers: UInt8
    }

    private var reports: [KeyboardReport] = []
    private var consumers: [ConsumerReport] = []

    func testEveryHostKeyInPinnedQMKLayoutsPressesAndReleases() throws {
        let entries = try fixture()
        XCTAssertEqual(entries.filter { $0.route != "firmware" }.count, 111)
        for entry in entries where entry.route != "firmware" {
            let controller = makeController()
            let flags = flags(for: entry.modifiers)
            switch entry.route {
            case "quartz":
                // Exercise the routing predicate used by InputTap with raw monitoring ON.
                // In particular, KC_PIPE must not be suppressed in favour of a raw callback.
                XCTAssertFalse(RawKeyboardState.quartzAliases.contains(entry.mac), entry.name)
                XCTAssertNil(RawKeyboardState.key(usagePage: 7, usage: UInt32(entry.input)), entry.name)
                try send(controller, .keyDown, key: entry.mac, flags: flags)
                XCTAssertEqual(reports.last?.data, Data([entry.modifiers, 0, UInt8(entry.output), 0, 0, 0, 0, 0]), entry.name)
                try send(controller, .keyUp, key: entry.mac)
            case "raw":
                let key = try XCTUnwrap(RawKeyboardState.key(usagePage: 7, usage: UInt32(entry.input)), entry.name)
                controller.handle(DirectInputEvent(kind: .keyDown(key), modifiers: []))
                XCTAssertEqual(reports.last?.keys.map(\.rawValue), [UInt8(entry.output)], entry.name)
                controller.handle(DirectInputEvent(kind: .keyUp(key), modifiers: []))
            case "modifier":
                try send(controller, .flagsChanged, key: entry.mac, flags: flags)
                XCTAssertEqual(reports.last?.modifiers.rawValue, entry.modifiers, entry.name)
                try send(controller, .flagsChanged, key: entry.mac)
            case "caps":
                let initial = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
                try send(controller, .flagsChanged, key: entry.mac, flags: initial ? [] : [.maskAlphaShift])
                XCTAssertEqual(reports.filter { $0.keys.contains(.capsLock) }.count, 1)
            case "consumer":
                try media(controller, code: entry.mac, down: true)
                XCTAssertEqual(consumers.last?.key.rawValue, entry.output, entry.name)
                try media(controller, code: entry.mac, down: false)
                XCTAssertEqual(consumers.last, .zero, entry.name)
            default:
                XCTFail("Unvalidated route for \(entry.name): \(entry.route)")
            }
            if entry.route != "consumer" { XCTAssertEqual(reports.last, .zero, entry.name) }
            controller.stop()
        }
    }

    func testPipeRepeatThenBackslashDoesNotLoseShiftOrLeaveItHeld() throws {
        let controller = makeController()
        // QMK can send the modifier and key in one report. No preceding flagsChanged
        // or current global Shift state is required for this keyDown to be correct.
        try send(controller, .keyDown, key: 0x2A, flags: [.maskShift])
        XCTAssertEqual(reports.last?.data, Data([2, 0, 0x31, 0, 0, 0, 0, 0]))
        let repeated = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 0x2A, keyDown: true))
        repeated.flags = .maskShift
        repeated.setIntegerValueField(.keyboardEventAutorepeat, value: 1)
        XCTAssertNil(DirectInputEvent(type: .keyDown, event: repeated))
        try send(controller, .keyUp, key: 0x2A)
        XCTAssertEqual(reports.last, .zero)
        try send(controller, .keyDown, key: 0x2A)
        XCTAssertEqual(reports.last?.data, Data([0, 0, 0x31, 0, 0, 0, 0, 0]))
        controller.stop()
        XCTAssertEqual(reports.last, .zero)
    }

    func testMenuAndApplicationOverlapWithoutEarlyRelease() {
        let controller = makeController()
        for key: Keycode in [.menu, .application] {
            controller.handle(DirectInputEvent(kind: .keyDown(key), modifiers: []))
            XCTAssertEqual(reports.last?.keys, [.application])
        }
        controller.handle(DirectInputEvent(kind: .keyUp(.menu), modifiers: []))
        XCTAssertEqual(reports.last?.keys, [.application])
        controller.handle(DirectInputEvent(kind: .keyUp(.application), modifiers: []))
        XCTAssertEqual(reports.last, .zero)
    }

    private func fixture() throws -> [Entry] {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("protocol/qmk-keymap-coverage.txt")
        return try String(contentsOf: url, encoding: .utf8).split(separator: "\n").filter { !$0.hasPrefix("#") }.map { line in
            let fields = line.split(separator: " ").map(String.init)
            XCTAssertEqual(fields.count, 6)
            return try Entry(
                name: fields[0], route: fields[1], mac: XCTUnwrap(UInt16(fields[2], radix: 16)),
                input: XCTUnwrap(UInt16(fields[3], radix: 16)), output: XCTUnwrap(UInt16(fields[4], radix: 16)),
                modifiers: XCTUnwrap(UInt8(fields[5], radix: 16))
            )
        }
    }

    private func makeController() -> DirectInputController {
        reports = []; consumers = []
        let controller = DirectInputController()
        controller.start(HIDInput(
            sendMouse: { _ in }, sendKeyboard: { self.reports.append($0) }, sendConsumer: { self.consumers.append($0) },
            isActive: true, isConnected: true, activeError: nil
        ))
        return controller
    }

    private func flags(for modifiers: UInt8) -> CGEventFlags {
        let flags: [CGEventFlags] = [.maskControl, .maskShift, .maskAlternate, .maskCommand]
        let sides: [UInt64] = [0x0001, 0x0002, 0x0020, 0x0008, 0x2000, 0x0004, 0x0040, 0x0010]
        return CGEventFlags(rawValue: (0 ..< 8).reduce(UInt64(0)) { result, bit in
            modifiers & (1 << bit) == 0 ? result : result | sides[bit] | flags[bit % 4].rawValue
        })
    }

    private func send(_ controller: DirectInputController, _ type: CGEventType, key: UInt16, flags: CGEventFlags = []) throws {
        let event = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: type == .keyDown))
        event.flags = flags
        try controller.handle(XCTUnwrap(DirectInputEvent(type: type, event: event)))
    }

    private func media(_ controller: DirectInputController, code: UInt16, down: Bool) throws {
        let event = try XCTUnwrap(NSEvent.otherEvent(
            with: .systemDefined, location: .zero, modifierFlags: [], timestamp: 0,
            windowNumber: 0, context: nil, subtype: 8, data1: (Int(code) << 16) | (down ? 0xA00 : 0xB00), data2: -1
        )?.cgEvent)
        try controller.handle(XCTUnwrap(DirectInputEvent(type: MediaKeyEvent.eventType, event: event)))
    }
}
