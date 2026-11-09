import XCTest

final class PointerRouterTests: XCTestCase {
    func testMotionTakesTheNetworkWhenUpAndBluetoothOtherwise() {
        var router = PointerRouter()
        XCTAssertEqual(router.route(.motion, network: true), .network)
        XCTAssertEqual(router.route(.motion, network: false), .forward)
    }

    func testScrollAlwaysGoesOverTheBluetoothMouse() {
        var router = PointerRouter()
        XCTAssertEqual(router.route(.scroll, network: true), .forward)
        XCTAssertEqual(router.route(.scroll, network: false), .forward)
    }

    func testButtonReleasesFollowTheirPressAcrossACarrierChange() {
        var router = PointerRouter()
        XCTAssertEqual(router.route(.button(.left, down: true), network: true), .network)
        XCTAssertEqual(router.networked, [.left])
        XCTAssertEqual(router.route(.button(.left, down: false), network: false), .network, "release follows the press to the network")
        XCTAssertEqual(router.networked, [])
        XCTAssertEqual(router.route(.button(.right, down: true), network: false), .forward)
        XCTAssertEqual(router.route(.button(.right, down: false), network: true), .forward, "and back to Bluetooth")
        XCTAssertEqual(
            router.route(.button(.middle, down: false), network: true),
            .network,
            "an unknown release follows the current carrier"
        )
    }

    func testResetForgetsHeldButtons() {
        var router = PointerRouter()
        _ = router.route(.button(.left, down: true), network: true)
        _ = router.route(.button(.right, down: true), network: false)
        router.reset()
        XCTAssertEqual(router.networked, [])
        XCTAssertEqual(router.forwarded, [])
    }

    func testEventsMapFromCapturedInputKinds() {
        XCTAssertEqual(PointerRouter.Event(.mouseMove(1, 0)), .motion)
        XCTAssertEqual(PointerRouter.Event(.scroll(wheel: 1, pan: 0)), .scroll)
        XCTAssertEqual(PointerRouter.Event(.mouseButton(.right, true)), .button(.right, down: true))
        XCTAssertEqual(PointerRouter.Event(.keyDown(.a)), .other)
        XCTAssertEqual(PointerRouter.Event(.networkMove(1, 1)), .other)
    }

    func testNetworkedEventsKeepDeltasAndButtons() {
        let move = DirectInputEvent(kind: .mouseMove(-300, 12), modifiers: [.leftShift]).networked
        guard case let .networkMove(dx, dy) = move.kind else { return XCTFail("expected a network move") }
        XCTAssertEqual([dx, dy], [-300, 12])
        XCTAssertEqual(move.modifiers, [.leftShift])
        XCTAssertTrue(move.isRelative)
        guard case let .networkButton(button, down) = DirectInputEvent(kind: .mouseButton(.right, false), modifiers: []).networked.kind
        else { return XCTFail("expected a network button") }
        XCTAssertEqual(button, .right)
        XCTAssertFalse(down)
        guard case .keyDown = DirectInputEvent(kind: .keyDown(.a), modifiers: []).networked.kind
        else { return XCTFail("keys are unchanged") }
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
        ready = true
        controller.handle(DirectInputEvent(kind: .networkButton(.middle, true), modifiers: []))
        network.removeAll()
        controller.stop()
        XCTAssertEqual(network, [.button(.middle, false)], "capture ends with network buttons released")
    }
}
