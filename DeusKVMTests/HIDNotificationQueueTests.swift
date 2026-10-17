import XCTest

final class HIDNotificationQueueTests: XCTestCase {
    func testMovementCannotOverwriteControlAltDeleteOrItsRelease() {
        var queue = HIDNotificationQueue<Int>()
        let down = KeyboardReport(modifiers: [.leftCtrl, .leftAlt], keys: [.deleteForward]).data
        queue.append(down, target: 2)
        queue.append(Data([1]), target: 1, coalesceMotion: true)
        queue.append(Data([2]), target: 1, coalesceMotion: true)
        queue.append(KeyboardReport.zero.data, target: 2)
        XCTAssertEqual(queue.first?.data, down)
        queue.removeFirst()
        XCTAssertEqual(queue.first?.data, Data([2]))
        queue.removeFirst()
        XCTAssertEqual(queue.first?.data, KeyboardReport.zero.data)
        queue.removeFirst()
        XCTAssertNil(queue.first)
    }

    func testPendingMotionKeepsTravelWithinReportRange() {
        var queue = HIDNotificationQueue<Int>()
        XCTAssertFalse(queue.append(MouseReport(buttons: .left, dX: 3, dY: -2).data, target: 1, coalesceMotion: true))
        XCTAssertTrue(queue.append(MouseReport(buttons: .left, dX: 4, dY: -5).data, target: 1, coalesceMotion: true))
        XCTAssertEqual(queue.first?.data, MouseReport(buttons: .left, dX: 7, dY: -7).data)
        XCTAssertEqual(queue.count, 1)
        queue.append(MouseReport(buttons: .left, dX: 125, dY: -125).data, target: 1, coalesceMotion: true)
        XCTAssertEqual(queue.first?.data, MouseReport(buttons: .left, dX: 127, dY: -127).data)
        queue.append(MouseReport(buttons: .left, dX: -127, dY: 127).data, target: 1, coalesceMotion: true)
        XCTAssertEqual(queue.first?.data, MouseReport(buttons: .left).data)
        // A button change stays its own ordered report; later motion merges after it.
        queue.append(MouseReport().data, target: 1)
        queue.append(MouseReport(dX: 1).data, target: 1, coalesceMotion: true)
        queue.append(MouseReport(dX: 1).data, target: 1, coalesceMotion: true)
        XCTAssertEqual(queue.count, 3)
        queue.removeFirst()
        XCTAssertEqual(queue.first?.data, MouseReport().data)
        queue.removeFirst()
        XCTAssertEqual(queue.first?.data, MouseReport(dX: 2).data)
    }

    func testCapsLockAndConsumerPulsesArePreservedInOrder() {
        var queue = HIDNotificationQueue<Int>()
        let packets: [(Int, Data)] = [
            (2, KeyboardReport(keys: [.capsLock]).data), (2, KeyboardReport.zero.data),
            (6, ConsumerReport(key: .volumeUp).data), (6, ConsumerReport.zero.data)
        ]
        for (target, data) in packets {
            queue.append(data, target: target)
        }
        for (target, data) in packets {
            XCTAssertEqual(queue.first?.target, target)
            XCTAssertEqual(queue.first?.data, data)
            queue.removeFirst()
        }
        XCTAssertNil(queue.first)
    }

    func testCompactionAndTargetResetCannotReplayOldInput() {
        var queue = HIDNotificationQueue<Int>()
        for n in 0 ..< 200 {
            queue.append(Data([UInt8(n)]), target: 2)
        }
        for n in 0 ..< 150 {
            XCTAssertEqual(queue.first?.data, Data([UInt8(n)]))
            queue.removeFirst()
        }
        queue.removeAll()
        queue.append(Data([255]), target: 6)
        XCTAssertEqual(queue.first?.data, Data([255]))
        queue.removeFirst()
        XCTAssertNil(queue.first)
    }
}
