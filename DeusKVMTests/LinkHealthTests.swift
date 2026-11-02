import XCTest

final class LinkHealthTests: XCTestCase {
    func testEventLagAcceptsNanosecondOrTickTimestampsAndRejectsImplausibleValues() {
        // Apple silicon: 125/3 ns per tick (24 MHz).
        let nowNs: UInt64 = 50_000_000_000_000
        let nowTicks = nowNs * 3 / 125
        XCTAssertEqual(
            EventLag.nanoseconds(timestamp: nowNs - 4_000_000, nowTicks: nowTicks, nowNanoseconds: nowNs, numer: 125, denom: 3),
            4_000_000
        )
        let tickStamp = nowTicks - 96000 // 4 ms of ticks
        XCTAssertEqual(
            EventLag.nanoseconds(timestamp: tickStamp, nowTicks: nowTicks, nowNanoseconds: nowNs, numer: 125, denom: 3),
            4_000_000
        )
        XCTAssertNil(EventLag.nanoseconds(timestamp: 0, nowTicks: nowTicks, nowNanoseconds: nowNs, numer: 125, denom: 3))
        XCTAssertNil(EventLag.nanoseconds(timestamp: nowNs + 1, nowTicks: nowTicks, nowNanoseconds: nowNs, numer: 125, denom: 3))
        XCTAssertNil(EventLag.nanoseconds(
            timestamp: nowNs - 60_000_000_000, nowTicks: nowTicks, nowNanoseconds: nowNs, numer: 125, denom: 3
        ))
    }

    func testTapStatsAveragesOnlyMeasuredEventsAndResets() {
        let stats = TapStats()
        stats.event(lagNanoseconds: 2_000_000)
        stats.event(lagNanoseconds: 6_000_000)
        stats.event(lagNanoseconds: nil)
        stats.warp()
        let interval = stats.take()
        XCTAssertEqual(interval.events, 3)
        XCTAssertEqual(interval.measured, 2)
        XCTAssertEqual(interval.lagSumMs, 8, accuracy: 0.001)
        XCTAssertEqual(interval.lagMaxMs, 6, accuracy: 0.001)
        XCTAssertEqual(interval.warps, 1)
        XCTAssertEqual(stats.take(), TapStats.Interval())
    }

    func testPingTrackerPacesMeasuresAndCountsTimeouts() throws {
        var pings = PingTracker()
        let first = try XCTUnwrap(pings.next(now: 10))
        XCTAssertNil(pings.next(now: 11), "one ping outstanding at a time")
        pings.receive(first &+ 1, now: 10.01)
        XCTAssertEqual(try XCTUnwrap(pings.outstandingAge(now: 10.5)), 0.5, accuracy: 0.001)
        pings.receive(first, now: 10.04)
        XCTAssertNil(pings.outstandingAge(now: 10.5))
        XCTAssertNil(pings.next(now: 11), "next ping waits for the interval")
        let second = try XCTUnwrap(pings.next(now: 12))
        XCTAssertNotEqual(first, second)
        XCTAssertNil(pings.next(now: 16.9))
        XCTAssertNotNil(pings.next(now: 17), "an unanswered ping times out after five seconds")
        let interval = pings.take()
        XCTAssertEqual(interval.replies, 1)
        XCTAssertEqual(interval.rttMaxMs, 40, accuracy: 0.01)
        XCTAssertEqual(interval.lost, 1)
        pings.cancel()
        XCTAssertNotNil(pings.next(now: 30))
        XCTAssertEqual(pings.take().lost, 0, "cancel is not a loss")
    }

    func testTransportStatsSplitsAnOngoingStallAcrossIntervals() {
        let stats = TransportStats()
        stats.sent(); stats.sent()
        stats.transmitBlocked(at: 100)
        stats.transmitBlocked(at: 100.2) // already stalled
        stats.notification(rejected: true, merged: false, pending: 3)
        stats.notification(merged: true, pending: 2)
        stats.companionSent(bytes: 20)
        stats.companionReceived(frames: 2, bytes: 40)
        var interval = stats.takeInterval(now: 100.5)
        XCTAssertEqual(interval.hidSent, 2)
        XCTAssertEqual(interval.hidRejected, 1)
        XCTAssertEqual(interval.hidMerged, 1)
        XCTAssertEqual(interval.hidPending, 2)
        XCTAssertEqual(interval.hidPendingMax, 3)
        XCTAssertEqual(interval.stallMs, 500, accuracy: 0.01)
        XCTAssertEqual(interval.stallMaxMs, 500, accuracy: 0.01)
        XCTAssertEqual(interval.framesOut, 1)
        XCTAssertEqual(interval.bytesOut, 20)
        XCTAssertEqual(interval.framesIn, 2)
        XCTAssertEqual(interval.bytesIn, 40)
        XCTAssertEqual(stats.stallAge(now: 101) ?? 0, 1, accuracy: 0.001)
        stats.transmitReady(at: 101.25)
        interval = stats.takeInterval(now: 102)
        XCTAssertEqual(interval.stallMs, 750, accuracy: 0.01, "only the time after the previous sample")
        XCTAssertEqual(interval.stallMaxMs, 1250, accuracy: 0.01, "the whole stall")
        XCTAssertEqual(interval.hidSent, 0)
        XCTAssertNil(stats.stallAge(now: 102))
        XCTAssertEqual(stats.takeInterval(now: 103).stallMs, 0)
    }

    func testSummaryAggregatesSamplesAndHistoryKeepsTheNewestInOrder() {
        func sample(_ second: Int, events: Int, lag: Double, rtt: Double) -> HealthSample {
            HealthSample(
                date: Date(timeIntervalSinceReferenceDate: TimeInterval(second)), visible: 0, parked: second != 2, blind: [0],
                transport: TransportInterval(events: events, hidSent: events, hidRejected: 1, stallMs: 10, stallMaxMs: 10),
                tap: TapStats.Interval(events: events, measured: events, lagSumMs: lag * Double(events), lagMaxMs: lag, warps: 0),
                ping: PingTracker.Interval(replies: 1, rttSumMs: rtt, rttMaxMs: rtt, lost: 0)
            )
        }
        var summary = HealthSummary()
        summary.add(sample(1, events: 10, lag: 2, rtt: 20))
        summary.add(sample(2, events: 30, lag: 6, rtt: 60))
        let line = summary.line
        for field in [
            "samples=2", "notParked=1", "events=40", "hidSent=40", "hidRejected=2", "stallMs=20", "stallMaxMs=10",
            "tapLagMaxMs=6.0", "tapLagAvgMs=5.0", "pings=2", "rttAvgMs=40.0", "rttMaxMs=60.0"
        ] {
            XCTAssertTrue(line.contains(field), "\(field) missing from \(line)")
        }
        XCTAssertTrue(sample(1, events: 1, lag: 1, rtt: 1).line.hasPrefix("visible=0 parked=true events=1 maxDispatchMs=0"))

        var history = HealthHistory(capacity: 3)
        for second in 1 ... 5 {
            history.append(sample(second, events: second, lag: 1, rtt: 1))
        }
        XCTAssertEqual(history.samples.map(\.transport.events), [3, 4, 5])
    }

    func testWindowsReportMatchesOnlyTheAwaitedTokenOnce() {
        var request = WindowsReportRequest()
        let first = request.begin()
        XCTAssertFalse(request.finish(first &+ 1), "a stale or foreign token is ignored")
        XCTAssertTrue(request.finish(first))
        XCTAssertFalse(request.finish(first), "the answer and the timeout cannot both complete it")
        let second = request.begin()
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(request.waiting, second)
        XCTAssertEqual(
            WindowsReportRequest.lines("link: ready=True\nhistory (0, oldest first):"),
            ["windows companion report:", "windows: link: ready=True", "windows: history (0, oldest first):"]
        )
    }

    func testBluetoothLinkLinesDropHeadersAndKeepTheNewest() {
        let output = "Timestamp               Ty Process[PID:TID]\nline 1\nline 2\nline 3\n"
        XCTAssertEqual(
            BluetoothLinkLog.lines(output, limit: 2),
            ["bluetoothd link lines: 2 of 3", "bluetoothd: line 2", "bluetoothd: line 3"]
        )
    }
}
