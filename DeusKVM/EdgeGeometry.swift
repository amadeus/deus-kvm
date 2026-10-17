import CoreGraphics
import Foundation

enum DisplayEdge: String, CaseIterable, Identifiable, Sendable {
    case left, right, top, bottom

    var wireValue: UInt8 {
        UInt8(Self.allCases.firstIndex(of: self)!)
    }

    var opposite: Self {
        Self.allCases[Int(wireValue ^ 1)]
    }

    var id: String {
        rawValue
    }
}

struct EdgeGeometry: Equatable, Sendable {
    let bounds: CGRect
    let edge: DisplayEdge
    let otherDisplays: [CGRect]
    var cornerSize: Double = 0

    /// Whole points: an odd-sized display (for example 1728 × 1117) has a half-point
    /// center, and the tap's exact parked-position check must not warp on every event.
    var parkingPoint: CGPoint {
        CGPoint(x: bounds.midX.rounded(.down), y: bounds.midY.rounded(.down))
    }

    func isAtEdge(_ point: CGPoint) -> Bool {
        guard !bounds.isEmpty else { return false }
        let along: Double
        let length: Double
        let outside: CGPoint
        switch edge {
        case .left:
            guard abs(point.x - bounds.minX) <= 1 else { return false }
            along = point.y - bounds.minY
            length = bounds.height
            outside = CGPoint(x: bounds.minX - 1, y: point.y)
        case .right:
            guard abs(point.x - (bounds.maxX - 1)) <= 1 else { return false }
            along = point.y - bounds.minY
            length = bounds.height
            outside = CGPoint(x: bounds.maxX, y: point.y)
        case .top:
            guard abs(point.y - bounds.minY) <= 1 else { return false }
            along = point.x - bounds.minX
            length = bounds.width
            outside = CGPoint(x: point.x, y: bounds.minY - 1)
        case .bottom:
            guard abs(point.y - (bounds.maxY - 1)) <= 1 else { return false }
            along = point.x - bounds.minX
            length = bounds.width
            outside = CGPoint(x: point.x, y: bounds.maxY)
        }
        return along >= cornerSize && along < length - cornerSize && !otherDisplays.contains { $0.contains(outside) }
    }

    func isOutward(dx: Int64, dy: Int64) -> Bool {
        switch edge {
        case .left: dx < 0
        case .right: dx > 0
        case .top: dy < 0
        case .bottom: dy > 0
        }
    }

    func fraction(at point: CGPoint) -> UInt16 {
        let vertical = edge == .left || edge == .right
        let value = vertical ? (point.y - bounds.minY) / max(1, bounds.height - 1) :
            (point.x - bounds.minX) / max(1, bounds.width - 1)
        return UInt16((min(1, max(0, value)) * 65535).rounded())
    }

    func entryPoint(fraction: UInt16) -> CGPoint {
        let value = Double(fraction) / 65535
        switch edge {
        case .left: return inset(CGPoint(x: bounds.minX, y: bounds.minY + value * (bounds.height - 1)))
        case .right: return inset(CGPoint(x: bounds.maxX - 1, y: bounds.minY + value * (bounds.height - 1)))
        case .top: return inset(CGPoint(x: bounds.minX + value * (bounds.width - 1), y: bounds.minY))
        case .bottom: return inset(CGPoint(x: bounds.minX + value * (bounds.width - 1), y: bounds.maxY - 1))
        }
    }

    func inset(_ point: CGPoint) -> CGPoint {
        CGPoint(
            x: min(max(point.x, bounds.minX + 2), bounds.maxX - 3),
            y: min(max(point.y, bounds.minY + 2), bounds.maxY - 3)
        )
    }
}
