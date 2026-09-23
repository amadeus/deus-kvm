import XCTest

final class DirectPointerTests: XCTestCase {
    func testCoalescingRetainsTravelAndWireMatchesWindows() throws {
        var buffer = DirectPointerBuffer()
        for _ in 0 ..< 1000 {
            XCTAssertTrue(buffer.append(dx: 0.5, dy: -0.25, report: .zero))
        }
        XCTAssertEqual(buffer.pending.count, 1)
        let packet = try XCTUnwrap(buffer.take()).payload(id: 7, serial: 1)
        XCTAssertEqual(Array(packet), [7, 0, 244, 1, 0, 0, 6, 255, 255, 0, 0, 0, 1])
        let frames = CompanionProtocol.Encoder().encodeForTest(packet)
        XCTAssertEqual(frames.count, 1)
        XCTAssertEqual(frames[0].count, 20)
    }

    func testClickDragAndScrollAreBarriers() {
        var buffer = DirectPointerBuffer()
        XCTAssertTrue(buffer.append(dx: 10, dy: 0, report: .zero))
        XCTAssertTrue(buffer.append(dx: 0, dy: 0, report: MouseReport(buttons: .left)))
        for _ in 0 ..< 10 {
            XCTAssertTrue(buffer.append(dx: 2, dy: 0, report: MouseReport(buttons: .left)))
        }
        XCTAssertTrue(buffer.append(dx: 0, dy: 0, report: .zero))
        XCTAssertTrue(buffer.append(dx: 0, dy: 0, report: MouseReport(wheel: -1, pan: 2)))
        XCTAssertEqual(buffer.pending.map(\.buttons), [0, 1, 1, 0, 0])
        XCTAssertEqual(buffer.pending.map(\.x), [2560, 2560, 7680, 7680, 7680])
        XCTAssertEqual(buffer.pending.last?.wheel, -1)
        XCTAssertEqual(buffer.pending.last?.pan, 2)
    }

    @MainActor
    func testDirectMouseNeverAlsoMovesHIDAndKeyboardRemainsHID() throws {
        let suite = "DeusKVMTests.DirectPointer.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(0.5, forKey: AppSettings.windowsPointerSpeedKey)
        defaults.set(true, forKey: AppSettings.invertVerticalScrollKey)
        var hidMouse: [MouseReport] = []
        var keys: [KeyboardReport] = []
        var motion: [(Double, Double)] = []
        var reports: [MouseReport] = []
        let hid = HIDInput(
            sendMouse: { hidMouse.append($0) },
            sendKeyboard: { keys.append($0) },
            sendConsumer: { _ in },
            isActive: true,
            isConnected: true,
            activeError: nil
        )
        let controller = DirectInputController(defaults: defaults)
        controller.start(hid, directMouse: { x, y, report in motion.append((x, y)); reports.append(report) })
        controller.handle(.init(kind: .mouseMove(1000, -500), modifiers: []))
        controller.handle(.init(kind: .mouseButton(.left, true), modifiers: []))
        controller.handle(.init(kind: .scroll(wheel: 2, pan: -3), modifiers: []))
        controller.handle(.init(kind: .keyDown(.a), modifiers: []))
        XCTAssertTrue(hidMouse.isEmpty)
        XCTAssertEqual(motion.first?.0, 500)
        XCTAssertEqual(motion.first?.1, -250)
        XCTAssertEqual(reports[1].buttons, .left)
        XCTAssertEqual(reports[2], MouseReport(buttons: .left, wheel: -2, pan: -3))
        XCTAssertEqual(keys.last?.keys, [.a])
        controller.stop()
        let count = reports.count
        controller.start(hid)
        controller.handle(.init(kind: .mouseMove(10, -10), modifiers: []))
        XCTAssertEqual(reports.count, count)
        XCTAssertEqual(hidMouse.last, MouseReport(dX: 5, dY: -5))
        controller.stop()
    }

    @MainActor
    func testNoPacketsBeforeEntryAndOnePacketUntilMatchingAck() {
        let target = UUID()
        var packets: [Data] = []
        var failures = 0
        let session = DirectPointerSession(target: target, id: 9, send: { packets.append($0) }, failed: { failures += 1 })
        defer { session.stop() }
        session.send(dx: 500, dy: -200, report: .zero)
        session.tick()
        XCTAssertTrue(packets.isEmpty)
        session.receive(target: UUID(), payload: Data([9, 0, 1]))
        session.tick()
        XCTAssertTrue(packets.isEmpty)
        session.receive(target: target, payload: Data([9, 0, 1]))
        session.tick()
        XCTAssertEqual(packets.count, 1)
        for _ in 0 ..< 1000 {
            session.send(dx: 1, dy: 0, report: .zero); session.tick()
        }
        XCTAssertEqual(packets.count, 1)
        session.receive(target: target, payload: Data([8, 1, 1]))
        session.receive(target: target, payload: Data([9, 2, 1]))
        session.tick()
        XCTAssertEqual(packets.count, 1)
        session.receive(target: target, payload: Data([9, 1, 1]))
        session.tick()
        XCTAssertEqual(packets.count, 2)
        XCTAssertEqual(CompanionProtocol.read32(packets[1], 1), 1500 * 256)
        XCTAssertEqual(failures, 0)
    }

    @MainActor
    func testIdleHeartbeatKeepsDragWithoutRepeatingScroll() {
        var time = 0.0
        var packets: [Data] = []
        let target = UUID()
        let session = DirectPointerSession(target: target, id: 1, now: { time }, send: { packets.append($0) }, failed: {})
        defer { session.stop() }
        session.receive(target: target, payload: Data([1, 0, 1]))
        session.send(dx: 1, dy: -1, report: MouseReport(buttons: .left, wheel: 2, pan: -3))
        session.tick()
        session.receive(target: target, payload: Data([1, 1, 1]))
        time = 0.49; session.tick()
        XCTAssertEqual(packets.count, 1)
        time = 0.5; session.tick()
        XCTAssertEqual(packets.count, 2)
        XCTAssertEqual(Array(packets[1].prefix(10)), Array(packets[0].prefix(10)))
        XCTAssertEqual(packets[1][10], 0)
        XCTAssertEqual(packets[1][11], 0)
        time = 3; session.tick()
        session.receive(target: target, payload: Data([1, 2, 1]))
        session.tick()
        XCTAssertEqual(packets.count, 2)
    }

    @MainActor
    func testTimeoutRejectionOverflowAndStopCannotReplay() {
        for scenario in 0 ..< 4 {
            var time = 0.0
            var failures = 0
            var sends = 0
            let target = UUID()
            let session = DirectPointerSession(
                target: target,
                id: 1,
                now: { time },
                send: { _ in sends += 1 },
                failed: { failures += 1 }
            )
            if scenario == 0 { time = 3; session.tick() }
            if scenario == 1 { session.receive(target: target, payload: Data([1, 0, 0])) }
            if scenario == 2 {
                for index in 0 ..< 70 {
                    session.send(dx: 0, dy: 0, report: MouseReport(buttons: index % 2 == 0 ? .left : []))
                }
            }
            if scenario == 3 { session.stop() }
            session.receive(target: target, payload: Data([1, 0, 1]))
            session.send(dx: 1, dy: 1, report: .zero)
            session.tick()
            XCTAssertEqual(sends, 0)
            XCTAssertEqual(failures, scenario == 3 ? 0 : 1)
        }
    }
}

private extension CompanionProtocol.Encoder {
    func encodeForTest(_ payload: Data) -> [Data] {
        var encoder = self
        return encoder.encode(.init(stream: 0, type: CompanionProtocol.Message.pointer.rawValue, payload: payload))
    }
}
