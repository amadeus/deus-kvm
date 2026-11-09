import XCTest

final class ScrollAccumulatorTests: XCTestCase {
    func testSpeedOnePassesStepsThrough() {
        var accumulator = ScrollAccumulator()
        XCTAssertEqual(accumulator.scale(wheel: 3, pan: -2, by: 1).wheel, 3)
        XCTAssertEqual(accumulator.scale(wheel: 3, pan: -2, by: 1).pan, -2)
    }

    func testSlowingDownAccumulatesSmallStepsInsteadOfDroppingThem() {
        var accumulator = ScrollAccumulator()
        // At quarter speed a single-step scroll emits nothing until four have arrived.
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 0, by: 0.25).wheel, 0)
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 0, by: 0.25).wheel, 0)
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 0, by: 0.25).wheel, 0)
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 0, by: 0.25).wheel, 1, "the fourth step completes one whole step")
    }

    func testSpeedingUpEnlargesStepsAndClampsToOneReport() {
        var accumulator = ScrollAccumulator()
        XCTAssertEqual(accumulator.scale(wheel: 10, pan: 4, by: 2).wheel, 20)
        XCTAssertEqual(accumulator.scale(wheel: 10, pan: 4, by: 2).pan, 8)
        XCTAssertEqual(accumulator.scale(wheel: 100, pan: 0, by: 3).wheel, 127, "one report cannot exceed 127")
    }

    func testDirectionIsPreservedWhenSlowing() {
        var accumulator = ScrollAccumulator()
        for _ in 0 ..< 3 {
            XCTAssertEqual(accumulator.scale(wheel: -1, pan: 0, by: 0.3).wheel, 0)
        }
        XCTAssertEqual(accumulator.scale(wheel: -1, pan: 0, by: 0.3).wheel, -1, "a down scroll never becomes an up step")
    }

    func testSpeedIsClampedToTheSliderRange() {
        var accumulator = ScrollAccumulator()
        XCTAssertEqual(accumulator.scale(wheel: 10, pan: 0, by: 99).wheel, 30, "3x is the fastest")
        var slow = ScrollAccumulator()
        // Below the minimum behaves as the 0.25 minimum: four steps to one.
        for _ in 0 ..< 3 {
            _ = slow.scale(wheel: 1, pan: 0, by: 0)
        }
        XCTAssertEqual(slow.scale(wheel: 1, pan: 0, by: 0).wheel, 1)
    }

    func testResetClearsTheCarriedFraction() {
        var accumulator = ScrollAccumulator()
        _ = accumulator.scale(wheel: 1, pan: 1, by: 0.5) // leaves 0.5 pending on each axis
        accumulator.reset()
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 1, by: 0.5).wheel, 0, "the pending half was cleared")
    }

    func testZeroStepsStayZeroAndDoNotConsumeRemainder() {
        var accumulator = ScrollAccumulator()
        _ = accumulator.scale(wheel: 1, pan: 0, by: 0.5)
        XCTAssertEqual(accumulator.scale(wheel: 0, pan: 0, by: 0.5).wheel, 0)
        XCTAssertEqual(accumulator.scale(wheel: 1, pan: 0, by: 0.5).wheel, 1, "the carried half still completes")
    }
}
