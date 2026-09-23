import Foundation
import XCTest

final class ClipboardTransferTests: XCTestCase {
    func testUnicodeNewlinesAndEmptyTextWithoutEcho() throws {
        for text in ["", "hello\r\nworld\rline\n😀 café 日本語"] {
            let pair = Pair()
            let bytes = try XCTUnwrap(ClipboardTransfer.text(text))
            pair.mac.observe(bytes); try pair.pump()
            XCTAssertEqual(pair.appliedPC, [bytes])
            pair.pc.yield(); try pair.pump()
            XCTAssertTrue(pair.appliedMac.isEmpty)
            XCTAssertEqual(pair.appliedPC.count, 1)
        }
    }

    func testLargeCopiesUseBoundedBlocksIncludingThe64KiBLimit() throws {
        for count in [1023, 1024, 1025, 20480, 65536] {
            let pair = Pair()
            let data = Data(repeating: 120, count: count)
            pair.mac.observe(data); try pair.pump()
            XCTAssertEqual(pair.appliedPC, [data])
        }
    }

    func testRepeatedRoundTripsUseTheLatestCopyWithoutEcho() throws {
        let pair = Pair()
        for index in 0 ..< 4 {
            pair.mac.setActive(false, now: 0); pair.pc.setActive(true, now: 0)
            pair.mac.observe(ClipboardTransfer.text("Mac \(index)")); try pair.pump()
            XCTAssertEqual(pair.appliedPC.last.flatMap(ClipboardTransfer.decode), "Mac \(index)")
            pair.pc.observe(ClipboardTransfer.text("superseded"))
            pair.pc.observe(ClipboardTransfer.text("PC \(index)")); try pair.pump()
            pair.pc.setActive(false, now: 0); pair.mac.setActive(true, now: 0); try pair.pump()
            XCTAssertEqual(pair.appliedMac.last.flatMap(ClipboardTransfer.decode), "PC \(index)")
            pair.mac.yield(); pair.pc.yield(); try pair.pump()
            XCTAssertEqual(pair.appliedMac.count, index + 1)
            XCTAssertEqual(pair.appliedPC.count, index + 1)
        }
    }

    func testSourceCopyChangesMidTransfer() throws {
        let pair = Pair()
        pair.mac.observe(Data(repeating: 120, count: 3000)); try pair.step(); try pair.step()
        pair.mac.observe(ClipboardTransfer.text("replacement")); try pair.pump()
        XCTAssertEqual(pair.appliedPC.compactMap(ClipboardTransfer.decode), ["replacement"])
    }

    func testInitialSnapshotWaitsForYieldAndInactiveDestinationWaitsForSwitch() throws {
        let pair = Pair()
        pair.pc.setActive(false, now: 0)
        pair.mac.observe(ClipboardTransfer.text("initial"), announce: false)
        XCTAssertTrue(pair.queue.isEmpty)
        pair.mac.yield(); try pair.pump()
        XCTAssertTrue(pair.appliedPC.isEmpty)
        pair.pc.setActive(true, now: 0); try pair.pump()
        XCTAssertEqual(pair.appliedPC.first.flatMap(ClipboardTransfer.decode), "initial")
    }

    func testMacCopiesOnlyAnnounceAtHandoffAndWindowsReturnStillWorks() throws {
        let pair = Pair()
        pair.mac.setActive(true, now: 0); pair.pc.setActive(false, now: 0)
        pair.mac.observe(ClipboardTransfer.text("first local copy"), announce: false)
        pair.mac.observe(ClipboardTransfer.text("latest menu copy"), announce: false)
        XCTAssertTrue(pair.queue.isEmpty)
        XCTAssertTrue(pair.appliedPC.isEmpty)
        pair.mac.setActive(false, now: 1); pair.pc.setActive(true, now: 1)
        pair.mac.yield(); try pair.pump()
        XCTAssertEqual(pair.appliedPC.compactMap(ClipboardTransfer.decode), ["latest menu copy"])
        pair.pc.observe(ClipboardTransfer.text("Windows copy")); try pair.pump()
        XCTAssertTrue(pair.appliedMac.isEmpty)
        pair.pc.setActive(false, now: 2); pair.mac.setActive(true, now: 2)
        pair.pc.yield(); try pair.pump()
        XCTAssertEqual(pair.appliedMac.compactMap(ClipboardTransfer.decode), ["Windows copy"])
    }

    func testLocalCopyOrPrivateContentCancelsIncomingText() throws {
        let pair = Pair()
        pair.mac.observe(Data(repeating: 120, count: 3000))
        try pair.step(); try pair.step(); try pair.step()
        pair.pc.observe(ClipboardTransfer.text("local copy")); try pair.pump()
        XCTAssertTrue(pair.appliedPC.isEmpty)
        pair.pc.setActive(false, now: 0)
        pair.mac.observe(ClipboardTransfer.text("old")); try pair.pump()
        pair.mac.observe(nil); try pair.pump()
        pair.pc.setActive(true, now: 0); try pair.pump()
        XCTAssertTrue(pair.appliedPC.isEmpty)
        XCTAssertNil(ClipboardTransfer.text(String(repeating: "x", count: 65537)))
        XCTAssertNil(ClipboardTransfer.text(String(repeating: "é", count: 32769)))
        XCTAssertNil(ClipboardTransfer.text("a\0b"))
    }

    func testSessionChangeRejectsInFlightData() throws {
        let pair = Pair()
        pair.mac.observe(ClipboardTransfer.text("old session")); try pair.step(); try pair.step()
        pair.pc.reset(43); try pair.pump()
        XCTAssertTrue(pair.appliedPC.isEmpty)
        pair.mac.reset(43); pair.mac.observe(ClipboardTransfer.text("new session")); try pair.pump()
        XCTAssertEqual(pair.appliedPC.first.flatMap(ClipboardTransfer.decode), "new session")
    }

    func testRepeatedAnnouncementsCannotApplyTwice() throws {
        let pair = Pair()
        pair.mac.observe(ClipboardTransfer.text("hello"))
        let announcement = try XCTUnwrap(pair.queue.first)
        try pair.pump(); pair.queue.append(announcement); try pair.pump()
        XCTAssertEqual(pair.appliedPC.count, 1)
    }

    func testLostRequestRetriesThenTimesOut() throws {
        let pair = Pair()
        pair.mac.observe(ClipboardTransfer.text("retry")); try pair.step(); pair.queue.removeAll()
        pair.pc.tick(5); try pair.pump()
        XCTAssertEqual(pair.appliedPC.count, 1)
        pair.mac.observe(ClipboardTransfer.text("expire")); try pair.step(); pair.queue.removeAll()
        for tick in 1 ... 3 {
            pair.pc.tick(Double(tick * 5)); XCTAssertEqual(pair.queue.count, 1); pair.queue.removeAll()
        }
        pair.pc.tick(20); XCTAssertTrue(pair.queue.isEmpty)
    }

    func testMalformedAndOversizedPayloadsNeverReachClipboard() throws {
        let pair = Pair()
        XCTAssertThrowsError(try pair.pc.receive(.clipGrab, payload: Data(repeating: 0, count: 11), now: 0))
        XCTAssertThrowsError(try pair.pc.receive(.clipGrab, payload: ClipboardTransfer.header(42, 7, 65537), now: 0))
        try pair.pc.receive(.clipGrab, payload: ClipboardTransfer.header(42, 7, 1), now: 0)
        XCTAssertThrowsError(try pair.pc.receive(.clipData, payload: ClipboardTransfer.header(42, 7, 0), now: 0))
        XCTAssertThrowsError(try pair.pc.receive(.clipData, payload: ClipboardTransfer.header(42, 7, 0) + Data([255]), now: 0))
        XCTAssertTrue(pair.appliedPC.isEmpty)
    }

    func testRetryDeadlineOnlyExistsWhileWaitingAndMovesWithProgress() throws {
        let transfer = ClipboardTransfer()
        var deadlines: [TimeInterval?] = []
        transfer.onDeadlineChanged = { deadlines.append(transfer.retryDeadline) }
        transfer.reset(42)
        transfer.setActive(true, now: 10)
        XCTAssertNil(transfer.retryDeadline)
        try transfer.receive(.clipGrab, payload: ClipboardTransfer.header(42, 1, 1025), now: 10)
        XCTAssertEqual(transfer.retryDeadline, 15)
        try transfer.receive(.clipData, payload: ClipboardTransfer.header(42, 1, 0) + Data(repeating: 65, count: 1024), now: 11)
        XCTAssertEqual(transfer.retryDeadline, 16)
        transfer.tick(15)
        XCTAssertEqual(transfer.retryDeadline, 16)
        try transfer.receive(.clipData, payload: ClipboardTransfer.header(42, 1, 1024) + Data([66]), now: 12)
        XCTAssertNil(transfer.retryDeadline)
        try transfer.receive(.clipGrab, payload: ClipboardTransfer.header(42, 2, 1), now: 13)
        XCTAssertEqual(transfer.retryDeadline, 18)
        transfer.observe(Data([67]))
        XCTAssertNil(transfer.retryDeadline)
        try transfer.receive(.clipGrab, payload: ClipboardTransfer.header(42, 3, 1), now: 14)
        transfer.reset(43)
        XCTAssertNil(transfer.retryDeadline)
        XCTAssertEqual(deadlines, [nil, nil, 15, 16, 16, nil, 18, nil, 19, nil])
    }

    private struct Message {
        let toPC: Bool
        let type: CompanionProtocol.Message
        let bytes: Data
    }

    private final class Pair {
        let mac = ClipboardTransfer()
        let pc = ClipboardTransfer()

        var queue: [Message] = []
        var appliedMac: [Data] = []
        var appliedPC: [Data] = []
        init() {
            mac.send = { [weak self] in self?.queue.append(Message(toPC: true, type: $0, bytes: $1)) }
            pc.send = { [weak self] in self?.queue.append(Message(toPC: false, type: $0, bytes: $1)) }
            mac.apply = { [weak self] in self?.appliedMac.append($0) }
            pc.apply = { [weak self] in self?.appliedPC.append($0) }
            mac.reset(42); pc.reset(42); mac.setActive(false, now: 0); pc.setActive(true, now: 0)
        }

        func step() throws {
            let message = queue.removeFirst()
            let type = message.type
            let bytes = message.bytes
            XCTAssertLessThanOrEqual(bytes.count, type == .clipData ? 1036 : 13)
            var encoder = CompanionProtocol.Encoder()
            XCTAssertLessThan(encoder.encode(.init(stream: type == .clipData ? 1 : 0, type: type.rawValue, payload: bytes)).count, 64)
            try (message.toPC ? pc : mac).receive(type, payload: bytes, now: 0)
        }

        func pump() throws {
            var remaining = 1000
            while !queue.isEmpty {
                guard remaining > 0 else { return XCTFail("Clipboard feedback loop") }
                remaining -= 1; try step()
            }
        }
    }
}
