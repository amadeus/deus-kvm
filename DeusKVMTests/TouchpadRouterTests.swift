import XCTest

final class TouchpadRouterTests: XCTestCase {
    private func route(
        _ event: TouchpadRouter.Event, _ presence: TouchpadPresence, enabled: Bool = true, network: Bool = false,
        router: inout TouchpadRouter
    ) -> TouchpadRouter.Action {
        var asked = false
        let action = router.route(event, enabled: enabled, network: network, presence: { asked = true; return presence })
        if !enabled { XCTAssertFalse(asked, "the pad is not consulted while precision mode is off") }
        return action
    }

    // MARK: Precision mode without the network (Phase 2 behaviour)

    func testMotionIsDroppedWhileAFingerIsDownOrJustLifted() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching(1), router: &router), .drop)
        XCTAssertEqual(route(.motion, .touching(3), router: &router), .drop)
        XCTAssertEqual(route(.motion, .recent, router: &router), .drop)
        XCTAssertEqual(route(.motion, .none, router: &router), .forward, "a real mouse moves as before")
    }

    func testContinuousScrollIsDroppedAndDiscreteWheelKept() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.scroll(continuous: true), .none, router: &router), .drop, "momentum after the lift too")
        XCTAssertEqual(route(.scroll(continuous: false), .touching(1), router: &router), .forward)
        XCTAssertEqual(route(.scroll(continuous: true), .none, network: true, router: &router), .drop)
    }

    func testClickWhileTouchingIsThePadButtonAndItsReleaseFollowsWhateverThePadDoes() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .touching(1), router: &router), .padButton(true))
        XCTAssertEqual(router.padButton, .left)
        XCTAssertEqual(route(.motion, .touching(1), router: &router), .drop, "the drag comes from the contacts")
        XCTAssertEqual(route(.button(.left, down: false), .none, router: &router), .padButton(false))
        XCTAssertNil(router.padButton)
        XCTAssertEqual(route(.button(.right, down: true), .touching(2), router: &router), .padButton(true), "Windows decides which")
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
        XCTAssertEqual(route(.button(.left, down: false), .touching(1), router: &router), .forward, "its release follows it")
        XCTAssertEqual(router.forwarded, [])
        XCTAssertEqual(route(.button(.middle, down: true), .none, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: true), .touching(2), router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.middle, down: false), .touching(2), router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .touching(2), router: &router), .padButton(false))
    }

    func testEverythingPassesThroughWhenTheModeIsOffExceptAHeldPadButton() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching(1), enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.scroll(continuous: true), .touching(1), enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: true), .touching(1), enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .touching(1), enabled: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.right, down: false), .none, enabled: false, router: &router), .forward, "unknown releases forward")
        XCTAssertEqual(route(.button(.left, down: true), .touching(2), router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.left, down: false), .none, enabled: false, router: &router), .padButton(false), "never stuck")
        XCTAssertEqual(route(.other, .touching(1), router: &router), .forward)
    }

    // MARK: The network stream

    func testLoneFingerAndRealMouseUseTheNetworkWhileTwoFingersStayWithWindows() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching(1), network: true, router: &router), .network)
        XCTAssertEqual(route(.motion, .recent, network: true, router: &router), .network, "the finger's tail follows it")
        XCTAssertEqual(route(.motion, .none, network: true, router: &router), .network, "a real mouse is smoother there too")
        XCTAssertEqual(route(.motion, .touching(2), network: true, router: &router), .drop)
        XCTAssertEqual(route(.motion, .touching(4), network: true, router: &router), .drop)
    }

    func testOneFingerClicksAndTapsGoOverTheNetworkTwoFingerOnesToWindows() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .touching(1), network: true, router: &router), .network, "physical click")
        XCTAssertEqual(router.networked, [.left])
        XCTAssertEqual(route(.button(.left, down: false), .touching(1), network: true, router: &router), .network)
        XCTAssertEqual(router.networked, [])
        XCTAssertEqual(route(.button(.left, down: true), .recent, network: true, router: &router), .network, "one-finger tap")
        XCTAssertEqual(route(.button(.left, down: false), .none, network: true, router: &router), .network)
        XCTAssertEqual(route(.button(.right, down: true), .recent, network: true, router: &router), .drop, "two-finger tap is Windows'")
        XCTAssertEqual(route(.button(.right, down: false), .none, network: true, router: &router), .drop)
        XCTAssertEqual(route(.button(.right, down: true), .touching(2), network: true, router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.right, down: false), .touching(2), network: true, router: &router), .padButton(false))
        XCTAssertEqual(route(.button(.left, down: true), .none, network: true, router: &router), .network, "real mouse")
        XCTAssertEqual(route(.button(.left, down: false), .none, network: true, router: &router), .network)
    }

    func testReleasesFollowTheirPressAcrossAStreamChange() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.button(.left, down: true), .touching(1), network: true, router: &router), .network)
        XCTAssertEqual(
            route(.button(.left, down: false), .touching(1), network: false, router: &router),
            .network,
            "the controller falls back"
        )
        XCTAssertEqual(route(.button(.left, down: true), .none, network: false, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: false), .none, network: true, router: &router), .forward)
        XCTAssertEqual(route(.button(.left, down: true), .touching(1), network: false, router: &router), .padButton(true))
        XCTAssertEqual(route(.button(.left, down: false), .touching(1), network: true, router: &router), .padButton(false))
    }

    func testModeOffWithTheNetworkSendsPointerEventsThere() {
        var router = TouchpadRouter()
        XCTAssertEqual(route(.motion, .touching(2), enabled: false, network: true, router: &router), .network)
        XCTAssertEqual(route(.button(.right, down: true), .recent, enabled: false, network: true, router: &router), .network)
        XCTAssertEqual(route(.button(.right, down: false), .recent, enabled: false, network: false, router: &router), .network)
        XCTAssertEqual(
            route(.scroll(continuous: true), .none, enabled: false, network: true, router: &router),
            .forward,
            "wheel stays on Bluetooth"
        )
        XCTAssertEqual(route(.other, .none, enabled: false, network: true, router: &router), .forward)
    }

    func testResetForgetsHeldButtons() {
        var router = TouchpadRouter()
        _ = route(.button(.left, down: true), .touching(2), router: &router)
        _ = route(.button(.right, down: true), .none, router: &router)
        _ = route(.button(.middle, down: true), .none, network: true, router: &router)
        router.reset()
        XCTAssertNil(router.padButton)
        XCTAssertEqual(router.forwarded, [])
        XCTAssertEqual(router.networked, [])
        XCTAssertEqual(route(.button(.left, down: false), .none, router: &router), .drop)
    }

    func testEventsMapFromCapturedInputKinds() {
        XCTAssertEqual(TouchpadRouter.Event(.mouseMove(1, 0), continuous: false), .motion)
        XCTAssertEqual(TouchpadRouter.Event(.scroll(wheel: 1, pan: 0), continuous: true), .scroll(continuous: true))
        XCTAssertEqual(TouchpadRouter.Event(.mouseButton(.right, true), continuous: false), .button(.right, down: true))
        XCTAssertEqual(TouchpadRouter.Event(.keyDown(.a), continuous: false), .other)
        XCTAssertEqual(TouchpadRouter.Event(.touchpadButton(true), continuous: false), .other)
        XCTAssertEqual(TouchpadRouter.Event(.networkMove(1, 1), continuous: false), .other)
    }

    func testNetworkedEventsKeepDeltasAndButtons() {
        let move = DirectInputEvent(kind: .mouseMove(-300, 12), modifiers: [.leftShift]).networked
        guard case let .networkMove(dx, dy) = move.kind else { return XCTFail("expected a network move") }
        XCTAssertEqual([dx, dy], [-300, 12])
        XCTAssertEqual(move.modifiers, [.leftShift])
        XCTAssertTrue(move.isRelative, "the gate may drop it like any motion")
        guard case let .networkButton(button, down) = DirectInputEvent(kind: .mouseButton(.right, false), modifiers: []).networked.kind
        else { return XCTFail("expected a network button") }
        XCTAssertEqual(button, .right)
        XCTAssertFalse(down)
        XCTAssertFalse(DirectInputEvent(kind: .networkButton(.left, true), modifiers: []).isRelative)
        guard case .keyDown = DirectInputEvent(kind: .keyDown(.a), modifiers: []).networked.kind
        else { return XCTFail("keys are unchanged") }
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

    func testControllerSendsNetworkEventsWhileUpAndFallsBackToBluetoothOtherwise() {
        var network: [NetworkInputEvent] = []
        var mouse: [MouseReport] = []
        nonisolated(unsafe) var ready = true
        let controller = DirectInputController()
        controller.start(HIDInput(
            sendMouse: { mouse.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil,
            sendNetwork: { network.append($0) }, networkReady: { ready }
        ))
        mouse.removeAll()
        controller.handle(DirectInputEvent(kind: .networkMove(500, -3), modifiers: []))
        controller.handle(DirectInputEvent(kind: .networkButton(.left, true), modifiers: []))
        XCTAssertEqual(network, [.motion(500, -3), .button(.left, true)])
        XCTAssertTrue(mouse.isEmpty)
        ready = false
        controller.handle(DirectInputEvent(kind: .networkMove(500, -3), modifiers: []))
        XCTAssertEqual(mouse, [MouseReport(dX: 127, dY: -3)], "Bluetooth carries it, clamped to the report")
        controller.handle(DirectInputEvent(kind: .networkButton(.left, false), modifiers: []))
        XCTAssertEqual(network.last, .button(.left, false), "a release follows its press to the network")
        controller.handle(DirectInputEvent(kind: .networkButton(.right, true), modifiers: []))
        XCTAssertEqual(mouse.last, MouseReport(buttons: .right), "a press while down is a Bluetooth button")
        controller.handle(DirectInputEvent(kind: .networkButton(.right, false), modifiers: []))
        XCTAssertEqual(mouse.last, MouseReport())
        ready = true
        controller.handle(DirectInputEvent(kind: .networkButton(.middle, true), modifiers: []))
        network.removeAll()
        controller.stop()
        XCTAssertEqual(network, [.button(.middle, false)], "capture ends with network buttons released")
        controller.handle(DirectInputEvent(kind: .mouseMove(1000, 0), modifiers: []))
        XCTAssertTrue(network.count == 1)
    }

    func testMouseMoveClampsOnlyWhenItBecomesABluetoothReport() {
        var mouse: [MouseReport] = []
        let controller = DirectInputController()
        controller.start(HIDInput(
            sendMouse: { mouse.append($0) }, sendKeyboard: { _ in }, sendConsumer: { _ in },
            isActive: true, isConnected: true, activeError: nil
        ))
        mouse.removeAll()
        controller.handle(DirectInputEvent(kind: .mouseMove(-1000, 64), modifiers: []))
        XCTAssertEqual(mouse, [MouseReport(dX: -127, dY: 64)])
        controller.stop()
    }
}
