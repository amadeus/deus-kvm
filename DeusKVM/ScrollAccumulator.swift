import Foundation

/// Scales wheel and pan steps by a speed multiplier while controlling Windows. The HID wheel is
/// whole steps per report, so a fraction of a step is carried to the next scroll event: slowing
/// scrolling down (multiplier below 1) accumulates small steps instead of dropping them, and
/// speeding it up (above 1) sends larger steps, clamped to one report's range. The remainder is
/// reset between capture sessions so a stale fraction never nudges the first scroll.
struct ScrollAccumulator {
    /// The slider's range; 1 is the trackpad's own speed.
    static let minimum = 0.25
    static let maximum = 3.0
    static let defaultSpeed = 1.0

    private var verticalRemainder = 0.0
    private var horizontalRemainder = 0.0

    mutating func reset() {
        verticalRemainder = 0
        horizontalRemainder = 0
    }

    /// The scaled whole steps to send; fractions carry to the next call.
    mutating func scale(wheel: Int8, pan: Int8, by speed: Double) -> (wheel: Int8, pan: Int8) {
        let clamped = min(max(speed, Self.minimum), Self.maximum)
        return (step(Int(wheel), &verticalRemainder, clamped), step(Int(pan), &horizontalRemainder, clamped))
    }

    private func step(_ delta: Int, _ remainder: inout Double, _ speed: Double) -> Int8 {
        guard delta != 0 else { return 0 }
        let scaled = Double(delta) * speed + remainder
        let whole = scaled.rounded(.towardZero)
        remainder = scaled - whole
        return Int8(min(127, max(-127, Int(whole))))
    }
}
