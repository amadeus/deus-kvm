import XCTest

final class TouchpadRouterTests: XCTestCase {
    private func route(
        _ event: TouchpadRouter.Event, _ presence: TouchpadPresence, enabled: Bool = true, router: inout TouchpadRouter
    ) -> TouchpadRouter.Action {
        var asked = false
        let action = router.route(event, enabled: enabled, presence: { asked = true; return presence })
        if !enabled { XCTAssertFalse(asked, "the pad is not consulted while the mode is off") }
        return action
    }

    func testMotionIsDroppedOnlyWhileAFingerIsDownOrJustLifted() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching, router: &router), .drop)
        XCTAssertEqual(route(.motion, .recent, router: &router), .drop)
        XCTAssertEqual(route(.motion, .none, router: &router), .forward, "a real mouse moves as before")
    }

    func testContinuousScrollIsDroppedAndDiscreteWheelKept() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.scroll(continuous: true), .none, router: &router), .drop, "momentum after the lift too")
        XCTAssertEqual(route(.scroll(continuous: false), .touching, router: &router), .forward)
    }

    func testClickWhileTouchingIsThePadButtonAndItsReleaseFollowsWhateverThePadDoes() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .touching, router: &router), .padButton(true))
        XCTAssertEqual(router.padButton, .left)
        XCTAssertEqual(route(.motion, .touching, router: &router), .drop, "the drag comes from the contacts")
        XCTAssertEqual(route(.button(.left, down: false), .none, router: &router), .padButton(false))
        XCTAssertNil(router.padButton)
        XCTAssertEqual(route(.button(.right, down: true), .touching, router: &router), .padButton(true), "Windows decides which")
        XCTAssertEqual(route(.button(.right, down: false), .recent, router: &router), .padButton(false))
    }

    func testTapToClickWhileRecentIsDroppedWithItsRelease() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .recent, router: &router), .drop)
        XCTAssertEqual(route(.button(.left, down: false), .none, router: &router), .drop, "nothing was pressed on Windows")
        XCTAssertNil(router.padButton)
    }

    func testRealMouseButtonsKeepTheMouseCollectionEvenIfAFingerLandsMeanwhile() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .none, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .touching, router: &router), .forward, "its release follows it")
        XCTAssertEqual(router.forwarded, [])
        XCTAssertEqual(route(.button(.middle, down: true), .none, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: true), .touching, router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.middle, down: false), .touching, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .touching, router: &router), .padButton(false))
    }

    func testEverythingPassesThroughWhenTheModeIsOffExceptAHeldPadButton() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching, enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.scroll(continuous: true), .touching, enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: true), .touching, enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .touching, enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.right, down: false), .none, enabled: false, router: &router), .forward, "unknown releases forward")
        XCTAssertEqual(route(.button(.left, down: true), .touching, router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.left, down: false), .none, enabled: false, router: &router), .padButton(false), "never stuck")
        XCTAssertEqual(route(.other, .touching, router: &router), .forward)
    }

    func testResetForgetsHeldButtons() {
        var router = TouchpadRouter()
        _ = route(.button(.left, down: true), .touching, router: &router)
        _ = route(.button(.right, down: true), .none, router: &router)
        router.reset()
        XCTAssertNil(router.padButton)
        XCTAssertEqual(router.forwarded, [])
        XCTAssertEqual(route(.button(.left, down: false), .none, router: &router), .drop)
    }

    func testEventsMapFromCapturedInputKinds() {
        XCTAssertEqual(TouchpadRouter.Event(.mouseMove(1, 0), continuous: false), .motion)
        XCTAssertEqual(TouchpadRouter.Event(.scroll(wheel: 1, pan: 0), continuous: true), .scroll(continuous: true))
        XCTAssertEqual(TouchpadRouter.Event(.mouseButton(.right, true), continuous: false), .button(.right, down: true))
        XCTAssertEqual(TouchpadRouter.Event(.keyDown(.a), continuous: false), .other)
        XCTAssertEqual(TouchpadRouter.Event(.touchpadButton(true), continuous: false), .other)
    }

    func testControllerHandsThePadButtonToItsHook() {
        var presses: [Bool] = []
        var mouse: [MouseReport] = []
        let controller = DirectInputController()
        controller.start(HIDInput(
            sendMouse: { mouse.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil, sendTouchpadButton: { presses.append($0) }
        ))
        controller.handle(DirectInputEvent(kind: .touchpadButton(true), modifiers: []))
        controller.handle(DirectInputEvent(kind: .touchpadButton(false), modifiers: []))
        XCTAssertEqual(presses, [true, false])
        XCTAssertTrue(mouse.isEmpty, "the pad's button never touches the mouse collection")
        XCTAssertFalse(DirectInputEvent(kind: .touchpadButton(true), modifiers: []).isRelative, "never dropped by the gate")
        controller.stop()
        controller.handle(DirectInputEvent(kind: .touchpadButton(true), modifiers: []))
        XCTAssertEqual(presses, [true, false])
    }
}
