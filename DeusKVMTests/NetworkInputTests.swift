import XCTest

final class NetworkInputTests: XCTestCase {
    func testRecordsHaveTheDocumentedLayout() {
        XCTAssertEqual(
            Array(InputRecord.motion(sequence: 0x0403_0201, captured: 0x0807_0605, dx: -2, dy: 300)),
            [1, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0xFE, 0xFF, 0x2C, 0x01]
        )
        XCTAssertEqual(
            Array(InputRecord.motion(sequence: 1, captured: 1, dx: 40000, dy: -40000).suffix(4)),
            [0xFF, 0x7F, 0x00, 0x80],
            "deltas clamp to 16 bits"
        )
        XCTAssertEqual(Array(InputRecord.button(sequence: 7, captured: 9, button: .right, down: true)), [2, 7, 0, 0, 0, 9, 0, 0, 0, 2, 1])
        XCTAssertEqual(Array(InputRecord.button(sequence: 7, captured: 9, button: .middle, down: false).suffix(2)), [3, 0])
        XCTAssertEqual(InputRecord.buttonCode(.left), 1)
        XCTAssertEqual(Array(InputRecord.ping(sequence: 2, captured: 3)), [3, 2, 0, 0, 0, 3, 0, 0, 0])
        XCTAssertEqual(InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0]))?.sequence, 0x10)
        XCTAssertEqual(InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0]))?.received, 0x20)
        XCTAssertNil(InputRecord.ack(Data([1, 2, 3])))
        XCTAssertEqual(InputRecord.clock(uptime: 0.0015), 15)
        XCTAssertEqual(InputRecord.clock(uptime: 429_496.7296), 0, "wraps at 32 bits of 100 µs ticks")
        XCTAssertEqual(InputRecord.sealedAckLength, 24)
    }

    func testMotionFlowsWhileTheWindowHasRoomThenCoalesces() throws {
        var state = InputStreamState()
        var sent: [Data] = []
        for index in 0 ..< InputStreamState.window {
            try sent.append(XCTUnwrap(state.motion(dx: 1, dy: Int32(index), now: 1 + Double(index) * 0.015)))
        }
        XCTAssertEqual(state.inflight, InputStreamState.window)
        XCTAssertNil(state.motion(dx: 5, dy: 5, now: 1.1), "the window is full")
        XCTAssertNil(state.motion(dx: 2, dy: -1, now: 1.115), "and the second frame joins the first")
        XCTAssertEqual(state.coalesced, 2)
        let flushed = try XCTUnwrap(state.acked(1, now: 1.12), "one acknowledgement makes room for the pending delta")
        XCTAssertEqual(Array(flushed.suffix(4)), [7, 0, 4, 0], "the pending delta is the sum, 7 and 4")
        XCTAssertEqual(state.inflight, InputStreamState.window)
        XCTAssertNil(state.acked(2, now: 1.13), "nothing is pending now")
        XCTAssertEqual(state.frames, InputStreamState.window + 1)
        XCTAssertNotNil(state.motion(dx: 1, dy: 1, now: 1.145), "after the flush motion flows again")
        XCTAssertNil(state.acked(6, now: 1.2), "everything acknowledged, nothing pending")
        XCTAssertNotNil(state.motion(dx: 1, dy: 1, now: 3), "a rest is not a gap")
        XCTAssertTrue(state.summary.contains("coalesced=2"))
        XCTAssertTrue(state.summary.contains("gapMaxMs=55.0"), state.summary)
    }

    func testButtonsAlwaysGoOutAfterPendingMotion() {
        var state = InputStreamState()
        for index in 0 ..< InputStreamState.window {
            _ = state.motion(dx: 1, dy: 1, now: Double(index))
        }
        XCTAssertNil(state.motion(dx: 3, dy: 0, now: 5))
        let records = state.button(.left, down: true, now: 6)
        XCTAssertEqual(records.count, 2)
        XCTAssertEqual(records[0].first, InputRecord.motion)
        XCTAssertEqual(records[1].first, InputRecord.button)
        XCTAssertEqual(state.button(.left, down: false, now: 7).count, 1)
        XCTAssertEqual(state.buttons, 2)
        XCTAssertEqual(state.inflight, InputStreamState.window + 3, "buttons do not wait for the window")
    }

    func testStallAndPingTiming() {
        var state = InputStreamState()
        XCTAssertFalse(state.stalled(now: 10))
        XCTAssertNotNil(state.ping(now: 10), "an idle stream pings")
        XCTAssertNil(state.ping(now: 10.5), "not twice within a second")
        XCTAssertNil(state.ping(now: 12), "nor while a record is unacknowledged")
        XCTAssertFalse(state.stalled(now: 10.9))
        XCTAssertTrue(state.stalled(now: 11.1))
        _ = state.acked(1, now: 11.2)
        XCTAssertFalse(state.stalled(now: 20))
        XCTAssertNotNil(state.ping(now: 20))
        state.noteStall()
        XCTAssertTrue(state.summary.contains("stalls=1"))
    }

    func testAcknowledgementsMeasureRoundTripAndIgnoreStrays() {
        var state = InputStreamState()
        _ = state.motion(dx: 1, dy: 1, now: 1.000)
        _ = state.motion(dx: 1, dy: 1, now: 1.015)
        XCTAssertNil(state.acked(9, now: 1.02), "an unknown sequence is ignored")
        XCTAssertEqual(state.inflight, 2)
        XCTAssertNil(state.acked(2, now: 1.020), "acknowledging the second covers the first")
        XCTAssertEqual(state.inflight, 0)
        XCTAssertTrue(state.summary.hasPrefix("frames=2 buttons=0"))
        XCTAssertTrue(state.summary.contains("rttMaxMs=5.0"), state.summary)
        XCTAssertNil(state.acked(2, now: 1.03), "a repeated acknowledgement changes nothing")
    }

    func testOfferRoundTripsAsJSON() throws {
        let offer = InputNetworkOffer(hosts: ["10.0.0.2"], port: 50000, key: "abc")
        let data = try JSONEncoder().encode(offer)
        XCTAssertEqual(try JSONDecoder().decode(InputNetworkOffer.self, from: data), offer)
        XCTAssertTrue(String(data: data, encoding: .utf8)?.contains("\"port\":50000") == true)
    }
}
