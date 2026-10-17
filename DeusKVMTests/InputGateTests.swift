import XCTest

final class InputGateTests: XCTestCase {
    func testInputBeforeApprovalIsReleasedInOrderThenPassesThrough() {
        var gate = InputGate<Int>()
        XCTAssertEqual(gate.receive(1, generation: 4), [])
        XCTAssertEqual(gate.receive(2, generation: 4), [])
        XCTAssertEqual(gate.start(4), [1, 2])
        XCTAssertEqual(gate.receive(3, generation: 4), [3])
        XCTAssertEqual(gate.active, 4)
    }

    func testStopAndRefusedCrossingsDiscardInput() {
        var gate = InputGate<Int>()
        _ = gate.start(4)
        gate.stop()
        XCTAssertEqual(gate.receive(1, generation: 4), [])
        // Generation 6 is refused and never starts; its input must not reach generation 8.
        XCTAssertEqual(gate.receive(2, generation: 6), [])
        XCTAssertEqual(gate.receive(3, generation: 8), [])
        XCTAssertEqual(gate.start(8), [3])
        XCTAssertEqual(gate.receive(4, generation: 8), [4])
    }

    func testOlderSessionsCannotReplaceNewerInput() {
        var gate = InputGate<Int>()
        XCTAssertEqual(gate.receive(1, generation: 8), [])
        XCTAssertEqual(gate.receive(2, generation: 7), [])
        XCTAssertEqual(gate.start(8), [1])
        XCTAssertEqual(gate.receive(3, generation: 7), [])
    }

    func testApprovingAnOlderSessionKeepsNewerWaitingInput() {
        var gate = InputGate<Int>()
        XCTAssertEqual(gate.receive(1, generation: 9), [])
        XCTAssertEqual(gate.start(8), [])
        XCTAssertEqual(gate.receive(2, generation: 8), [2])
        XCTAssertEqual(gate.start(9), [1])
    }

    func testOnlyDroppableWaitingInputIsBounded() {
        var gate = InputGate<Int>()
        for value in 0 ..< InputGate<Int>.capacity + 10 {
            _ = gate.receive(value, generation: 1, droppable: true)
        }
        // A key or button change past the cap still waits, so the host never misses a release.
        _ = gate.receive(-1, generation: 1)
        XCTAssertEqual(gate.start(1), Array(0 ..< InputGate<Int>.capacity) + [-1])
    }
}
