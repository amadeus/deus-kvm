import XCTest

final class TrackpadGestureTests: XCTestCase {
    private func swipe(_ motion: Int64, _ progress: Double, velocity: (Double, Double) = (0, 0)) -> DockSwipeRecognizer.Result? {
        var recognizer = DockSwipeRecognizer()
        let began = DockSwipeSample(motion: motion, phase: DockSwipeSample.Phase.began, progress: 0, velocityX: 0, velocityY: 0)
        XCTAssertNil(recognizer.observe(began))
        XCTAssertNil(recognizer.observe(.init(
            motion: motion, phase: 2, progress: progress / 2, velocityX: velocity.0, velocityY: velocity.1
        )))
        return recognizer.observe(.init(motion: 0, phase: DockSwipeSample.Phase.ended, progress: progress, velocityX: 0, velocityY: 0))
    }

    func testSpacesAndMissionControlSwipesMapToWindowsShortcuts() {
        XCTAssertEqual(swipe(1, -0.8)?.shortcut, .nextDesktop)
        XCTAssertEqual(swipe(1, 0.8)?.shortcut, .previousDesktop)
        XCTAssertEqual(swipe(2, -0.8)?.shortcut, .taskView)
        XCTAssertEqual(swipe(2, 0.8)?.shortcut, .showDesktop)
        XCTAssertEqual(
            swipe(1, -0.8)?.summary,
            "gesture: dock swipe ended motion=1 progress=-0.800 velocity=0,0 -> nextDesktop"
        )
    }

    func testDirectionFallsBackToVelocityAndCancelledSwipesDoNothing() {
        XCTAssertEqual(swipe(0, 0, velocity: (-900, 40))?.shortcut, .nextDesktop)
        XCTAssertEqual(swipe(0, 0, velocity: (30, -700))?.shortcut, .taskView)
        XCTAssertNil(swipe(1, 0)?.shortcut)

        var recognizer = DockSwipeRecognizer()
        _ = recognizer.observe(.init(motion: 1, phase: DockSwipeSample.Phase.began, progress: -0.2, velocityX: 0, velocityY: 0))
        let cancelled = recognizer.observe(.init(
            motion: 1,
            phase: DockSwipeSample.Phase.cancelled,
            progress: 0,
            velocityX: 0,
            velocityY: 0
        ))
        XCTAssertNil(cancelled?.shortcut)
        XCTAssertTrue(cancelled?.summary.contains("cancelled") == true)
    }

    func testShortcutReleasesModifiersAfterTheKeySoStartDoesNotOpen() {
        let events = WindowsGestureShortcut.nextDesktop.events(capsLock: true)
        XCTAssertEqual(events.count, 3)
        guard case .keyDown(.rightArrow) = events[0].kind, case .keyUp(.rightArrow) = events[1].kind,
              case .flagsChanged(capsLock: true) = events[2].kind else { return XCTFail("unexpected sequence") }
        XCTAssertEqual(events[0].modifiers, [.leftCtrl, .leftGUI])
        XCTAssertEqual(events[1].modifiers, [.leftCtrl, .leftGUI])
        XCTAssertEqual(events[2].modifiers, [])
        guard case .keyDown(.tab) = WindowsGestureShortcut.taskView.events(capsLock: false)[0].kind,
              case .keyDown(.d) = WindowsGestureShortcut.showDesktop.events(capsLock: false)[0].kind
        else {
            return XCTFail("wrong keys")
        }
    }

    func testMaskCoversEveryGestureTypeAndNoExistingInputType() {
        for type in TrackpadGesture.eventTypes {
            XCTAssertNotEqual(TrackpadGesture.eventMask & (CGEventMask(1) << CGEventMask(type)), 0)
        }
        for type: CGEventType in [.keyDown, .keyUp, .flagsChanged, .mouseMoved, .scrollWheel, .leftMouseDown] {
            XCTAssertFalse(TrackpadGesture.eventTypes.contains(type.rawValue))
        }
    }
}
