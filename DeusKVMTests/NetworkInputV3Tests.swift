import XCTest

final class NetworkInputV3Tests: XCTestCase {
    private struct Record: Decodable {
        let name: String
        let type: UInt8
        let hex: String
    }

    private struct Fixture: Decodable {
        let capture: String
        let ownership: String
        let sequence: UInt32
        let captured: UInt32
        let records: [Record]
        let acknowledgement: String
        let control: [String: String]
        let clipboardRequest: ClipboardSnapshotRequest
        let clipboardSnapshot: ClipboardSnapshot
    }

    private func fixture() throws -> Fixture {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("protocol/multi-mac-fixtures.json")
        return try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    }

    private func bytes(_ hex: String) -> Data {
        let chars = Array(hex)
        return Data(stride(from: 0, to: chars.count, by: 2).map { UInt8(String(chars[$0 ..< $0 + 2]), radix: 16)! })
    }

    func testEveryInputRecordMatchesWindowsCaptureFixtures() throws {
        let fixture = try fixture()
        let capture = try XCTUnwrap(UInt64(fixture.capture, radix: 16))
        for item in fixture.records {
            let record: Data
            switch item.type {
            case InputRecord.motion:
                record = InputRecord.motion(sequence: fixture.sequence, captured: fixture.captured, dx: -2, dy: 300)
            case InputRecord.button:
                record = InputRecord.button(sequence: fixture.sequence, captured: fixture.captured, button: .right, down: true)
            case InputRecord.ping:
                record = InputRecord.ping(sequence: fixture.sequence, captured: fixture.captured)
            case InputRecord.keyboard:
                record = InputRecord.keyboard(
                    sequence: fixture.sequence, captured: fixture.captured,
                    report: KeyboardReport(modifiers: [.leftShift, .rightGUI], keys: [.a, Keycode(rawValue: 0x4C)])
                )
            case InputRecord.consumer:
                record = InputRecord.consumer(sequence: fixture.sequence, captured: fixture.captured, key: .volumeUp)
            case InputRecord.scroll:
                record = InputRecord.scroll(sequence: fixture.sequence, captured: fixture.captured, wheel: -3, pan: 1)
            default:
                XCTAssertEqual(item.type, InputRecord.reset)
                record = Data([InputRecord.reset]) + CompanionProtocol.u32(fixture.sequence) + CompanionProtocol.u32(fixture.captured)
            }
            XCTAssertEqual(InputRecord.scoped(record, capture: capture), bytes(item.hex), item.name)
        }
    }

    func testWindowsAcknowledgementPreservesCaptureOwnershipAndLeave() throws {
        let fixture = try fixture()
        let capture = try XCTUnwrap(UInt64(fixture.capture, radix: 16))
        let ownership = try XCTUnwrap(UInt64(fixture.ownership, radix: 16))
        let data = bytes(fixture.acknowledgement)
        let ack = try XCTUnwrap(InputRecord.ack(data))
        XCTAssertEqual(ack.sequence, 16)
        XCTAssertEqual(ack.received, 32)
        XCTAssertTrue(ack.blocked)
        XCTAssertTrue(ack.deferred)
        XCTAssertTrue(ack.owns)
        XCTAssertEqual(ack.capture, capture)
        XCTAssertEqual(ack.ownership, ownership)
        XCTAssertEqual(ack.leave, InputLeave(switchID: 0xAB, edge: 3, fraction: 0xBEEF, capture: capture, ownership: ownership))
        for count in 0 ..< data.count {
            XCTAssertNil(InputRecord.ack(Data(data.prefix(count))))
        }
        XCTAssertNil(InputRecord.ack(data + Data([0])))
        var revoked = data
        revoked[8] &= ~32
        XCTAssertNil(InputRecord.ack(revoked)?.leave, "a non-owner cannot return this Mac through an old edge request")
    }

    func testEntryPacketsMatchWindowsCaptureFixtures() throws {
        let fixture = try fixture()
        let capture = try XCTUnwrap(UInt64(fixture.capture, radix: 16))
        for (fromEdge, key) in [(true, "enter"), (false, "center")] {
            let packet = CompanionEntry.packet(
                id: 0xAB, edge: 3, fraction: 0xBEEF, fromEdge: fromEdge, supportsCenter: true, capture: capture
            )
            XCTAssertEqual(packet.payload, try bytes(XCTUnwrap(fixture.control[key])))
        }
        XCTAssertEqual(Data([0xAB]) + CompanionProtocol.u64(capture), try bytes(XCTUnwrap(fixture.control["exit"])))
        XCTAssertEqual(Data([0xAB, 3]) + CompanionProtocol.u64(capture), try bytes(XCTUnwrap(fixture.control["resume"])))
    }

    func testClipboardManifestPreservesOwnershipBeyondJavaScriptIntegerPrecision() throws {
        let fixture = try fixture()
        let ownership = try XCTUnwrap(UInt64(fixture.ownership, radix: 16))
        let request = try ClipboardSnapshotRequest.decode(JSONEncoder().encode(fixture.clipboardRequest))
        let snapshot = try JSONDecoder().decode(ClipboardSnapshot.self, from: fixture.clipboardSnapshot.encoded())
        XCTAssertEqual(request.generation, ownership)
        XCTAssertEqual(snapshot.generation, ownership)
        XCTAssertEqual(snapshot.kind, .text)
        XCTAssertEqual(snapshot.text.flatMap { String(data: $0, encoding: .utf8) }, "hello\n🚀")
    }

    func testFirstPasteWaitsForMatchingEntryAndCancelledAckCannotStartInput() {
        var entry = CaptureEntryGate()
        var input = InputGate<String>()
        entry.arm(generation: 1, capture: 101)
        XCTAssertEqual(input.receive("paste", generation: 1), [])
        XCTAssertNil(entry.consume(capture: 100))
        XCTAssertNil(input.active)
        if let generation = entry.consume(capture: 101) {
            XCTAssertEqual(input.start(generation), ["paste"])
        } else { XCTFail("the matching acknowledgement must release the pending capture") }
        XCTAssertNil(entry.consume(capture: 101), "duplicate acknowledgement cannot start the capture twice")

        entry.arm(generation: 2, capture: 102)
        input.stop()
        XCTAssertEqual(input.receive("cancelled paste", generation: 2), [])
        entry.cancel()
        input.stop()
        XCTAssertNil(entry.consume(capture: 102))
        XCTAssertNil(input.active)
    }

    func testNewEntrySupersedesEarlierAcknowledgementEvenWhenSwitchByteWraps() {
        var entry = CaptureEntryGate()
        entry.arm(generation: 1, capture: 100)
        entry.arm(generation: 257, capture: 356)
        XCTAssertNil(entry.consume(capture: 100))
        XCTAssertEqual(entry.pendingCapture, 356)
        XCTAssertEqual(entry.consume(capture: 356), 257)
        entry.arm(generation: 258, capture: 0)
        XCTAssertNil(entry.pendingCapture)
        XCTAssertNil(entry.consume(capture: 0))
    }
}
