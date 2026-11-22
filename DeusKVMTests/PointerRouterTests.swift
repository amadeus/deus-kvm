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
        XCTAssertTrue(move.isNetworkPointer)
        let click = DirectInputEvent(kind: .mouseButton(.right, false), modifiers: [])
        XCTAssertFalse(click.isNetworkPointer)
        XCTAssertTrue(click.networked.isNetworkPointer)
        XCTAssertFalse(DirectInputEvent(kind: .mouseMove(1, 1), modifiers: []).isNetworkPointer)
        XCTAssertFalse(DirectInputEvent(kind: .scroll(wheel: 1, pan: 0), modifiers: []).isNetworkPointer)
        guard case let .networkButton(button, down) = click.networked.kind
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

    func testKeysMediaAndScrollUseTheNetworkWhenItCarriesThemAndHandOverCleanly() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "network-keys-\(UUID())"))
        let controller = DirectInputController(defaults: defaults)
        var keyboard: [KeyboardReport] = []
        var consumer: [ConsumerReport] = []
        var mouse: [MouseReport] = []
        var network: [NetworkInputEvent] = []
        var keysReady = true
        controller.start(HIDInput(
            sendMouse: { mouse.append($0) }, sendKeyboard: { keyboard.append($0) }, sendConsumer: { consumer.append($0) },
            isActive: true, isConnected: true, activeError: nil,
            sendNetwork: { network.append($0) }, networkReady: { true }, networkKeysReady: { keysReady }
        ))
        keyboard.removeAll(); consumer.removeAll(); mouse.removeAll()
        controller.handle(DirectInputEvent(kind: .keyDown(.a), modifiers: [.leftShift]))
        controller.handle(DirectInputEvent(kind: .consumer(.volumeUp, down: true), modifiers: []))
        controller.handle(DirectInputEvent(kind: .scroll(wheel: 2, pan: 0), modifiers: []))
        XCTAssertEqual(network, [
            .keyboard(KeyboardReport(modifiers: [.leftShift], keys: [.a])), .consumer(.volumeUp), .scroll(wheel: 2, pan: 0)
        ])
        XCTAssertTrue(keyboard.isEmpty && consumer.isEmpty && mouse.isEmpty, "nothing on Bluetooth")

        // The stream stops carrying keys while A is held: the network side is emptied and
        // Bluetooth gets the whole state.
        network.removeAll()
        keysReady = false
        controller.handle(DirectInputEvent(kind: .keyDown(.b), modifiers: [.leftShift]))
        XCTAssertEqual(network, [.keyboard(.zero)])
        XCTAssertEqual(keyboard, [KeyboardReport(modifiers: [.leftShift], keys: [.a, .b])])
        controller.handle(DirectInputEvent(kind: .scroll(wheel: 1, pan: 0), modifiers: []))
        XCTAssertEqual(mouse.count, 1, "scroll falls back to the Bluetooth mouse")

        // Back on the network: Bluetooth is emptied first.
        network.removeAll(); keyboard.removeAll()
        keysReady = true
        controller.handle(DirectInputEvent(kind: .keyUp(.b), modifiers: [.leftShift]))
        XCTAssertEqual(keyboard, [.zero])
        XCTAssertEqual(network, [.keyboard(KeyboardReport(modifiers: [.leftShift], keys: [.a]))])

        // Stopping capture releases what the network holds.
        network.removeAll()
        controller.stop()
        XCTAssertEqual(network, [.keyboard(.zero), .consumer(.none)])
    }
}
