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
        XCTAssertEqual(
            InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0, 0, 0, 0, 0])),
            InputAck(sequence: 0x10, received: 0x20, blocked: false)
        )
        XCTAssertEqual(InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0, 1, 0, 0, 0]))?.blocked, true, "bit 0: the PC cannot inject")
        XCTAssertNil(InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0])), "the old eight-byte form is rejected")
        XCTAssertEqual(
            InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0, 0x0F, 0xAB, 0xEF, 0xBE])),
            InputAck(sequence: 0x10, received: 0x20, blocked: true, leave: InputLeave(switchID: 0xAB, edge: 3, fraction: 0xBEEF)),
            "bit 1: an edge-return request; edge in bits 2-3, switch id 8-15, fraction 16-31"
        )
        XCTAssertNil(InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0, 1, 0xAB, 0xEF, 0xBE]))?.leave, "no bit 1, no request")
        XCTAssertNil(InputRecord.ack(Data([1, 2, 3])))
        let deferred = InputRecord.ack(Data([0x10, 0, 0, 0, 0x20, 0, 0, 0, 0x10, 0, 0, 0]))
        XCTAssertEqual(deferred?.deferred, true, "bit 4: sent later than its record's read")
        XCTAssertEqual(deferred?.blocked, false)
        XCTAssertNil(deferred?.leave)
        XCTAssertEqual(InputRecord.clock(uptime: 0.0015), 15)
        XCTAssertEqual(InputRecord.clock(uptime: 429_496.7296), 0, "wraps at 32 bits of 100 µs ticks")
        XCTAssertEqual(InputRecord.sealedAckLength, 28)
    }

    func testMotionFlowsWhileThePCKeepsUpAndCoalescesOnceItIsBehind() throws {
        var state = InputStreamState()
        // A 1000 Hz mouse: forty records in 40 ms with no acknowledgement yet all go out.
        for index in 0 ..< 40 {
            XCTAssertNotNil(state.motion(dx: 1, dy: 0, now: 1 + Double(index) * 0.001), "record \(index)")
        }
        XCTAssertEqual(state.inflight, 40)
        // The allowed lag is 40 ms beyond the PC's 16 ms acknowledgement pacing.
        XCTAssertEqual(InputStreamState.maxLag, 0.056, accuracy: 1e-9)
        XCTAssertFalse(state.behind(now: 1.0559))
        XCTAssertTrue(state.behind(now: 1.057), "the first record is now older than the allowed lag")
        XCTAssertNil(state.motion(dx: 5, dy: 5, now: 1.057), "so motion waits")
        XCTAssertNil(state.motion(dx: 2, dy: -1, now: 1.058), "and joins the pending delta")
        XCTAssertEqual(state.coalesced, 2)
        XCTAssertNil(state.acked(10, now: 1.071), "record 11, sent at 1.010, is still too old")
        let flushed = try XCTUnwrap(state.acked(40, now: 1.072), "everything acknowledged: the pending delta leaves")
        XCTAssertEqual(Array(flushed.suffix(4)), [7, 0, 4, 0], "the pending delta is the sum, 7 and 4")
        XCTAssertEqual(state.frames, 41)
        XCTAssertEqual(state.inflight, 1)
        XCTAssertNotNil(state.motion(dx: 1, dy: 1, now: 1.066), "motion flows again")
        XCTAssertNil(state.acked(42, now: 1.074))
        XCTAssertNotNil(state.motion(dx: 1, dy: 1, now: 3), "a rest is not a gap")
        XCTAssertTrue(state.summary.contains("coalesced=2"))
        XCTAssertTrue(state.summary.contains("gapMaxMs=18.0"), state.summary)
    }

    func testTheNextCheckIsTheStallDeadlineInFlightAndThePingAtRest() {
        var state = InputStreamState()
        XCTAssertEqual(state.nextCheck(now: 5), 5 + InputStreamState.pingAfter, "a fresh stream pings after a second")
        XCTAssertNotNil(state.motion(dx: 1, dy: 0, now: 10))
        XCTAssertNotNil(state.motion(dx: 1, dy: 0, now: 10.03))
        XCTAssertEqual(state.nextCheck(now: 10.04), 10 + InputStreamState.stallAfter, "the oldest record's stall deadline")
        XCTAssertNil(state.acked(1, now: 10.04))
        XCTAssertEqual(state.nextCheck(now: 10.04), 10.03 + InputStreamState.stallAfter)
        XCTAssertNil(state.acked(2, now: 10.05))
        XCTAssertEqual(state.nextCheck(now: 10.05), 10.03 + InputStreamState.pingAfter, "all acknowledged: a ping after the last send")
        XCTAssertNil(state.ping(now: 11.02), "not before it is due")
        XCTAssertNotNil(state.ping(now: 11.03))
    }

    func testAnInflightCapBoundsAnUnresponsivePC() {
        var state = InputStreamState()
        var sent = 0
        for index in 0 ..< InputStreamState.maxInflight + 5 where state.motion(dx: 1, dy: 0, now: 1 + Double(index) * 0.0001) != nil {
            sent += 1
        }
        XCTAssertEqual(sent, InputStreamState.maxInflight)
        XCTAssertEqual(state.coalesced, 5)
    }

    func testButtonsAlwaysGoOutAfterPendingMotion() {
        var state = InputStreamState()
        for index in 0 ..< 4 {
            _ = state.motion(dx: 1, dy: 1, now: Double(index) * 0.001)
        }
        XCTAssertNil(state.motion(dx: 3, dy: 0, now: 5), "the oldest record is seconds old")
        let records = state.button(.left, down: true, now: 6)
        XCTAssertEqual(records.count, 2)
        XCTAssertEqual(records[0].first, InputRecord.motion)
        XCTAssertEqual(records[1].first, InputRecord.button)
        XCTAssertEqual(state.button(.left, down: false, now: 7).count, 1)
        XCTAssertEqual(state.buttons, 2)
        XCTAssertEqual(state.inflight, 7, "buttons do not wait for the PC")
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

    func testAcknowledgementsAreCumulativeMeasureRoundTripAndJitterAndIgnoreStrays() {
        var state = InputStreamState()
        _ = state.motion(dx: 1, dy: 1, now: 1.000)
        _ = state.motion(dx: 1, dy: 1, now: 1.015)
        _ = state.motion(dx: 1, dy: 1, now: 1.030)
        XCTAssertNil(state.acked(9, now: 1.02), "an unknown sequence is ignored")
        XCTAssertEqual(state.inflight, 3)
        // The PC's clock reads 100 000 ticks ahead of ours; delivery took 1.0 ms then 2.5 ms.
        XCTAssertNil(
            state.acked(2, received: InputRecord.clock(uptime: 1.015) &+ 100_000 &+ 10, now: 1.020),
            "acknowledging the second covers the first"
        )
        XCTAssertEqual(state.inflight, 1)
        XCTAssertNil(state.acked(3, received: InputRecord.clock(uptime: 1.030) &+ 100_000 &+ 25, now: 1.035))
        XCTAssertEqual(state.inflight, 0)
        XCTAssertTrue(state.summary.hasPrefix("frames=3 buttons=0"))
        XCTAssertTrue(state.summary.contains("rttMaxMs=5.0"), state.summary)
        XCTAssertTrue(state.summary.contains("jitterMs=1.5"), state.summary)
        XCTAssertNil(state.acked(3, now: 1.04), "a repeated acknowledgement changes nothing")
        XCTAssertNil(state.acked(2, now: 1.04), "an older one neither")

        // A paced (deferred) acknowledgement left after the read: delivery counts, the round trip not.
        var paced = InputStreamState()
        _ = paced.motion(dx: 1, dy: 1, now: 2.000)
        XCTAssertNil(paced.acked(1, received: InputRecord.clock(uptime: 2.000) &+ 7, deferred: true, now: 2.016))
        XCTAssertEqual(paced.inflight, 0)
        XCTAssertTrue(paced.summary.contains("rttAvgMs=0.0 rttMaxMs=0.0"), paced.summary)
    }

    func testOfferRoundTripsAsJSON() throws {
        let offer = InputNetworkOffer(hosts: ["10.0.0.2"], port: 50000, key: "abc")
        let data = try JSONEncoder().encode(offer)
        XCTAssertEqual(try JSONDecoder().decode(InputNetworkOffer.self, from: data), offer)
        XCTAssertTrue(String(data: data, encoding: .utf8)?.contains("\"port\":50000") == true)
    }
}
