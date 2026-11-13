import CoreGraphics
import XCTest

final class EdgeGeometryTests: XCTestCase {
    func testReturnMapsFractionOnAllEdgesWithNegativeCoordinates() {
        let bounds = CGRect(x: -1920, y: -200, width: 1920, height: 1080)
        let points = [
            CGPoint(x: -1918, y: 339.5082322423),
            CGPoint(x: -3, y: 339.5082322423),
            CGPoint(x: -960.4853589685, y: -198),
            CGPoint(x: -960.4853589685, y: 877)
        ]
        for (index, edge) in DisplayEdge.allCases.enumerated() {
            let geometry = EdgeGeometry(bounds: bounds, edge: edge, otherDisplays: [])
            let point = geometry.entryPoint(fraction: 32768)
            XCTAssertEqual(point.x, points[index].x, accuracy: 0.01)
            XCTAssertEqual(point.y, points[index].y, accuracy: 0.01)
            XCTAssertEqual(geometry.fraction(at: point), 32768)
            XCTAssertEqual(edge.opposite.opposite, edge)
            XCTAssertFalse(geometry.isAtEdge(point))
        }
    }

    func testDistanceToTheEdgeSegmentOnEveryEdgeAndFromOtherDisplays() {
        let bounds = CGRect(x: -1920, y: -200, width: 1920, height: 1080)
        let left = EdgeGeometry(bounds: bounds, edge: .left, otherDisplays: [])
        XCTAssertEqual(left.distance(to: CGPoint(x: -1920, y: 300)), 0)
        XCTAssertEqual(left.distance(to: CGPoint(x: -1520, y: 300)), 400)
        // Beyond the segment's end, and from a display to its left: straight-line distance.
        XCTAssertEqual(left.distance(to: CGPoint(x: -1917, y: -204)), 5)
        XCTAssertEqual(left.distance(to: CGPoint(x: -2020, y: 300)), 100)
        let right = EdgeGeometry(bounds: bounds, edge: .right, otherDisplays: [])
        XCTAssertEqual(right.distance(to: CGPoint(x: -1, y: 879)), 0)
        XCTAssertEqual(right.distance(to: CGPoint(x: -101, y: 0)), 100)
        let top = EdgeGeometry(bounds: bounds, edge: .top, otherDisplays: [])
        XCTAssertEqual(top.distance(to: CGPoint(x: -960, y: -200)), 0)
        XCTAssertEqual(top.distance(to: CGPoint(x: -960, y: 50)), 250)
        let bottom = EdgeGeometry(bounds: bounds, edge: .bottom, otherDisplays: [])
        XCTAssertEqual(bottom.distance(to: CGPoint(x: -960, y: 879)), 0)
        XCTAssertEqual(bottom.distance(to: CGPoint(x: 2, y: 883)), 5)
        // Every point the edge test accepts is at distance 0 or 1.
        for edge in DisplayEdge.allCases {
            let geometry = EdgeGeometry(bounds: bounds, edge: edge, otherDisplays: [])
            let point = geometry.entryPoint(fraction: 20000)
            let onEdge = CGPoint(
                x: edge == .left ? bounds.minX : edge == .right ? bounds.maxX - 1 : point.x,
                y: edge == .top ? bounds.minY : edge == .bottom ? bounds.maxY - 1 : point.y
            )
            XCTAssertTrue(geometry.isAtEdge(onEdge))
            XCTAssertLessThanOrEqual(geometry.distance(to: onEdge), 1)
        }
    }

    func testParkingPointUsesWholePointsOnOddSizedDisplays() {
        let geometry = EdgeGeometry(bounds: CGRect(x: -1728, y: 0, width: 1728, height: 1117), edge: .left, otherDisplays: [])
        XCTAssertEqual(geometry.parkingPoint, CGPoint(x: -864, y: 558))
        XCTAssertFalse(TapWorkSchedule.needsCursorCorrection(location: geometry.parkingPoint, parkingPoint: geometry.parkingPoint))
    }

    func testSharedEdgeOnlyArmsOnExposedSegment() {
        let geometry = EdgeGeometry(
            bounds: CGRect(x: 0, y: 0, width: 1920, height: 1080), edge: .right,
            otherDisplays: [CGRect(x: 1920, y: 0, width: 1920, height: 600)]
        )
        XCTAssertFalse(geometry.isAtEdge(CGPoint(x: 1919, y: 300)))
        XCTAssertTrue(geometry.isAtEdge(CGPoint(x: 1919, y: 800)))
        XCTAssertFalse(geometry.isAtEdge(CGPoint(x: 1000, y: 800)))
        XCTAssertTrue(geometry.isOutward(dx: 1, dy: 0))
        XCTAssertFalse(geometry.isOutward(dx: -1, dy: 0))
    }

    func testNegativeDisplayCoordinatesAndCorners() {
        let geometry = EdgeGeometry(
            bounds: CGRect(x: -1920, y: -1080, width: 1920, height: 1080), edge: .left,
            otherDisplays: [], cornerSize: 20
        )
        XCTAssertTrue(geometry.isAtEdge(CGPoint(x: -1920, y: -500)))
        XCTAssertFalse(geometry.isAtEdge(CGPoint(x: -1920, y: -1070)))
        XCTAssertEqual(geometry.inset(CGPoint(x: -1920, y: -500)), CGPoint(x: -1918, y: -500))
    }
}
