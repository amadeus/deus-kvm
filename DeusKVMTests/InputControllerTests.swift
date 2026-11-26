import XCTest

/// What a DirectInputController sent, with the stream's readiness under the test's control.
final class InputRecorder {
    var events: [NetworkInputEvent] = []
    var pointerReady = true
    var keysReady = true

    var sink: InputSink {
        InputSink(send: { self.events.append($0) }, pointerReady: { self.pointerReady }, keysReady: { self.keysReady })
    }

    var keyboard: [KeyboardReport] {
        events.compactMap { if case let .keyboard(report) = $0 { report } else { nil } }
    }

    var consumer: [ConsumerKey] {
        events.compactMap { if case let .consumer(key) = $0 { key } else { nil } }
    }
}

final class InputControllerTests: XCTestCase {
    func testPointerGoesOverTheStreamUnclampedAndIsDroppedWhileItCannotCarryIt() {
        let recorder = InputRecorder()
        let controller = DirectInputController()
        controller.start(recorder.sink)
        controller.handle(DirectInputEvent(kind: .motion(500, -3), modifiers: []))
        controller.handle(DirectInputEvent(kind: .button(.left, true), modifiers: []))
        XCTAssertEqual(recorder.events, [.motion(500, -3), .button(.left, true)])
        recorder.events.removeAll()
        recorder.pointerReady = false
        controller.handle(DirectInputEvent(kind: .motion(5, 5), modifiers: []))
        controller.handle(DirectInputEvent(kind: .button(.right, true), modifiers: []))
        XCTAssertEqual(recorder.events, [], "nothing goes while the stream is down or the PC cannot inject")
        controller.handle(DirectInputEvent(kind: .button(.left, false), modifiers: []))
        XCTAssertEqual(recorder.events, [.button(.left, false)], "a release follows its press")
        controller.handle(DirectInputEvent(kind: .button(.right, false), modifiers: []))
        XCTAssertEqual(recorder.events, [.button(.left, false)], "a release without a sent press is dropped")
    }

    func testStoppingReleasesWhatTheStreamHolds() {
        let recorder = InputRecorder()
        let controller = DirectInputController()
        controller.start(recorder.sink)
        controller.handle(DirectInputEvent(kind: .button(.middle, true), modifiers: []))
        controller.handle(DirectInputEvent(kind: .keyDown(.a), modifiers: [.leftShift]))
        controller.handle(DirectInputEvent(kind: .consumer(.volumeUp, down: true), modifiers: []))
        recorder.events.removeAll()
        controller.stop()
        XCTAssertEqual(recorder.events, [.reset], "synthetic cleanup is distinct from user input and cannot acquire ownership")
        recorder.events.removeAll()
        controller.stop()
        XCTAssertEqual(recorder.events, [], "a second stop has nothing to release")
    }

    func testKeysMediaAndScrollWaitForAStreamThatCarriesThem() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "network-keys-\(UUID())"))
        let recorder = InputRecorder()
        let controller = DirectInputController(defaults: defaults)
        controller.start(recorder.sink)
        controller.handle(DirectInputEvent(kind: .keyDown(.a), modifiers: [.leftShift]))
        controller.handle(DirectInputEvent(kind: .consumer(.volumeUp, down: true), modifiers: []))
        controller.handle(DirectInputEvent(kind: .scroll(wheel: 2, pan: 0), modifiers: []))
        XCTAssertEqual(recorder.events, [
            .keyboard(KeyboardReport(modifiers: [.leftShift], keys: [.a])), .consumer(.volumeUp), .scroll(wheel: 2, pan: 0)
        ])

        // The companion stops taking keys while A is held: nothing goes, and the next state after
        // it recovers carries everything held, so nothing is lost or left behind.
        recorder.events.removeAll()
        recorder.keysReady = false
        controller.handle(DirectInputEvent(kind: .keyDown(.b), modifiers: [.leftShift]))
        controller.handle(DirectInputEvent(kind: .scroll(wheel: 1, pan: 0), modifiers: []))
        XCTAssertEqual(recorder.events, [])
        recorder.keysReady = true
        controller.handle(DirectInputEvent(kind: .keyUp(.b), modifiers: [.leftShift]))
        XCTAssertEqual(recorder.events, [.keyboard(KeyboardReport(modifiers: [.leftShift], keys: [.a]))])
    }

    func testEventKindsSortIntoPointerAndRelativeInput() {
        XCTAssertTrue(DirectInputEvent(kind: .motion(1, 1), modifiers: []).isPointer)
        XCTAssertTrue(DirectInputEvent(kind: .button(.left, true), modifiers: []).isPointer)
        XCTAssertFalse(DirectInputEvent(kind: .scroll(wheel: 1, pan: 0), modifiers: []).isPointer)
        XCTAssertFalse(DirectInputEvent(kind: .keyDown(.a), modifiers: []).isPointer)
        XCTAssertTrue(DirectInputEvent(kind: .motion(1, 1), modifiers: []).isRelative)
        XCTAssertTrue(DirectInputEvent(kind: .scroll(wheel: 1, pan: 0), modifiers: []).isRelative)
        XCTAssertFalse(DirectInputEvent(kind: .button(.left, false), modifiers: []).isRelative)
    }
}
