import Foundation

/// Scale only forwarded motion, retaining fractions without building a motion backlog.
struct PointerMotionScaler {
    static let speedRange = 0.25 ... 2.0
    static let defaultSpeed = 1.0
    private var speed = defaultSpeed
    private var remainderX = 0.0
    private var remainderY = 0.0

    static func validatedSpeed(_ value: Double) -> Double {
        guard value.isFinite else { return defaultSpeed }
        return min(speedRange.upperBound, max(speedRange.lowerBound, value))
    }

    mutating func scale(dx: Int64, dy: Int64, speed requested: Double) -> (x: Int8, y: Int8) {
        let next = Self.validatedSpeed(requested)
        if next != speed {
            remainderX = 0
            remainderY = 0
            speed = next
        }
        return (
            Self.axis(dx, speed: speed, remainder: &remainderX),
            Self.axis(dy, speed: speed, remainder: &remainderY)
        )
    }

    private static func axis(_ delta: Int64, speed: Double, remainder: inout Double) -> Int8 {
        let scaled = Double(delta) * speed + remainder
        // Preserve the existing HID range and one-report-per-event ceiling.
        // Never replay clipped whole counts later as delayed cursor movement.
        let bounded = min(127, max(-127, scaled))
        let result = Int8(bounded.rounded(.towardZero))
        remainder = scaled == bounded ? bounded - Double(result) : 0
        return result
    }
}
