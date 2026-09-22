import CoreGraphics
import XCTest

@MainActor
final class PointerSpeedTests: XCTestCase {
    func testTinyMovementsAccumulateSymmetricallyInsteadOfDisappearing() {
        var scaler = PointerMotionScaler()
        var x = 0
        var y = 0
        for _ in 0 ..< 100 {
            let movement = scaler.scale(dx: 1, dy: -1, speed: 0.25)
            x += Int(movement.x)
            y += Int(movement.y)
        }
        XCTAssertEqual(x, 25)
        XCTAssertEqual(y, -25)
    }

    func testOppositeFineMovementsCancelWithoutDrift() {
        var scaler = PointerMotionScaler()
        for _ in 0 ..< 100 {
            let outward = scaler.scale(dx: 1, dy: -1, speed: 0.5)
            let inward = scaler.scale(dx: -1, dy: 1, speed: 0.5)
            XCTAssertEqual(outward.x, 0)
            XCTAssertEqual(outward.y, 0)
            XCTAssertEqual(inward.x, 0)
            XCTAssertEqual(inward.y, 0)
        }
    }

    func testNormalSpeedPreservesExistingReportsIncludingTheirRangeLimit() {
        var scaler = PointerMotionScaler()
        for delta in [-1000, -128, -127, -1, 0, 1, 127, 128, 1000] {
            let movement = scaler.scale(dx: Int64(delta), dy: -Int64(delta), speed: 1)
            XCTAssertEqual(Int(movement.x), max(-127, min(127, delta)))
            XCTAssertEqual(Int(movement.y), max(-127, min(127, -delta)))
        }
    }

    func testExtremeMotionIsBoundedWithoutOverflowOrDeferredTravel() {
        var scaler = PointerMotionScaler()
        let movement = scaler.scale(dx: .max, dy: .min, speed: 2)
        XCTAssertEqual(movement.x, 127)
        XCTAssertEqual(movement.y, -127)
        let stopped = scaler.scale(dx: 0, dy: 0, speed: 2)
        XCTAssertEqual(stopped.x, 0)
        XCTAssertEqual(stopped.y, 0)
        let next = scaler.scale(dx: 1, dy: -1, speed: 2)
        XCTAssertEqual(next.x, 2)
        XCTAssertEqual(next.y, -2)
    }

    func testInvalidPreferencesAreSafeAndSpeedChangesDiscardOldFractions() {
        XCTAssertEqual(PointerMotionScaler.validatedSpeed(.nan), 1)
        XCTAssertEqual(PointerMotionScaler.validatedSpeed(.infinity), 1)
        XCTAssertEqual(PointerMotionScaler.validatedSpeed(-1), 0.25)
        XCTAssertEqual(PointerMotionScaler.validatedSpeed(100), 2)
        var scaler = PointerMotionScaler()
        _ = scaler.scale(dx: 3, dy: -3, speed: 0.25)
        let reset = scaler.scale(dx: 1, dy: -1, speed: 1)
        XCTAssertEqual(reset.x, 1)
        XCTAssertEqual(reset.y, -1)
        let fine = scaler.scale(dx: 1, dy: -1, speed: 0.25)
        XCTAssertEqual(fine.x, 0)
        XCTAssertEqual(fine.y, 0)
    }

    func testCapturedMotionScalesBeforeClippingAndPreservesDragAndScroll() throws {
        let suite = "DeusKVMTests.Pointer.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(0.25, forKey: AppSettings.windowsPointerSpeedKey)
        var reports: [MouseReport] = []
        let controller = DirectInputController(defaults: defaults)
        controller.start(HIDInput(
            sendMouse: { reports.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil
        ))
        defer { controller.stop() }
        controller.handle(DirectInputEvent(kind: .mouseButton(.left, true), modifiers: []))
        let event = try XCTUnwrap(CGEvent(
            mouseEventSource: nil, mouseType: .leftMouseDragged, mouseCursorPosition: .zero, mouseButton: .left
        ))
        event.setIntegerValueField(.mouseEventDeltaX, value: 400)
        event.setIntegerValueField(.mouseEventDeltaY, value: -200)
        try controller.handle(XCTUnwrap(DirectInputEvent(type: .leftMouseDragged, event: event)))
        controller.handle(DirectInputEvent(kind: .scroll(wheel: 2, pan: -3), modifiers: []))
        controller.handle(DirectInputEvent(kind: .mouseButton(.left, false), modifiers: []))
        XCTAssertEqual(reports, [
            MouseReport(buttons: .left), MouseReport(buttons: .left, dX: 100, dY: -50),
            MouseReport(buttons: .left, wheel: 2, pan: -3), MouseReport(buttons: .left), .zero
        ])
    }

    func testLivePreferencesResetAndNewCaptureDoNotLeakResidualMotion() throws {
        let suite = "DeusKVMTests.Pointer.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        var reports: [MouseReport] = []
        let hid = HIDInput(
            sendMouse: { reports.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil
        )
        let controller = DirectInputController(defaults: defaults)
        controller.start(hid)
        controller.handle(DirectInputEvent(kind: .mouseMove(4, -4), modifiers: []))
        XCTAssertEqual(reports.last, MouseReport(dX: 4, dY: -4))
        defaults.set(2.0, forKey: AppSettings.windowsPointerSpeedKey)
        controller.handle(DirectInputEvent(kind: .mouseMove(4, -4), modifiers: []))
        XCTAssertEqual(reports.last, MouseReport(dX: 8, dY: -8))

        defaults.set(0.25, forKey: AppSettings.windowsPointerSpeedKey)
        controller.handle(DirectInputEvent(kind: .mouseMove(3, -3), modifiers: []))
        controller.stop()
        reports.removeAll()
        controller.handle(DirectInputEvent(kind: .mouseMove(100, 100), modifiers: []))
        XCTAssertTrue(reports.isEmpty)
        controller.start(hid)
        controller.handle(DirectInputEvent(kind: .mouseMove(1, -1), modifiers: []))
        XCTAssertTrue(reports.isEmpty)
        controller.handle(DirectInputEvent(kind: .mouseMove(3, -3), modifiers: []))
        XCTAssertEqual(reports, [MouseReport(dX: 1, dY: -1)])

        defaults.removeObject(forKey: AppSettings.windowsPointerSpeedKey)
        controller.handle(DirectInputEvent(kind: .mouseMove(4, -4), modifiers: []))
        XCTAssertEqual(reports.last, MouseReport(dX: 4, dY: -4))
        controller.stop()
    }

    func testSavedSpeedAppliesToARecreatedController() throws {
        let suite = "DeusKVMTests.Pointer.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(1.5, forKey: AppSettings.windowsPointerSpeedKey)
        let reopenedDefaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let controller = DirectInputController(defaults: reopenedDefaults)
        var reports: [MouseReport] = []
        controller.start(HIDInput(
            sendMouse: { reports.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil
        ))
        defer { controller.stop() }
        controller.handle(DirectInputEvent(kind: .mouseMove(10, -10), modifiers: []))
        XCTAssertEqual(reports, [MouseReport(dX: 15, dY: -15)])
    }
}
