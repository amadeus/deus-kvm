import XCTest

@MainActor
final class ScrollPreferenceTests: XCTestCase {
    func testAxesInvertIndependentlyAndChangesApplyDuringCapture() throws {
        let suite = "DeusKVMTests.Scroll.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let recorder = InputRecorder()
        let controller = DirectInputController(defaults: defaults)
        controller.start(recorder.sink)
        defer { controller.stop() }

        func scroll(expectedWheel: Int8, expectedPan: Int8) {
            recorder.events.removeAll()
            controller.handle(DirectInputEvent(kind: .scroll(wheel: 127, pan: -127), modifiers: []))
            XCTAssertEqual(recorder.events, [.scroll(wheel: expectedWheel, pan: expectedPan)])
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
