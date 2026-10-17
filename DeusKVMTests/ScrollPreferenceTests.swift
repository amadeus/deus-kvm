import XCTest

@MainActor
final class ScrollPreferenceTests: XCTestCase {
    func testAxesInvertIndependentlyAndChangesApplyDuringCapture() throws {
        let suite = "DeusKVMTests.Scroll.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        var reports: [MouseReport] = []
        let controller = DirectInputController(defaults: defaults)
        controller.start(HIDInput(
            sendMouse: { reports.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil
        ))
        defer { controller.stop() }
        controller.handle(DirectInputEvent(kind: .mouseButton(.left, true), modifiers: []))

        func scroll(expectedWheel: Int8, expectedPan: Int8) {
            reports.removeAll()
            controller.handle(DirectInputEvent(kind: .scroll(wheel: 127, pan: -127), modifiers: []))
            XCTAssertEqual(reports, [MouseReport(buttons: .left, wheel: expectedWheel, pan: expectedPan)])
        }

        scroll(expectedWheel: 127, expectedPan: -127)
        defaults.set(true, forKey: AppSettings.invertVerticalScrollKey)
        scroll(expectedWheel: -127, expectedPan: -127)
        defaults.set(true, forKey: AppSettings.invertHorizontalScrollKey)
        scroll(expectedWheel: -127, expectedPan: 127)
        defaults.set(false, forKey: AppSettings.invertVerticalScrollKey)
        scroll(expectedWheel: 127, expectedPan: 127)
        defaults.set(false, forKey: AppSettings.invertHorizontalScrollKey)
        scroll(expectedWheel: 127, expectedPan: -127)
    }
}
