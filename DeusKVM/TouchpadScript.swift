import Foundation

/// One scripted contact frame for the precision touchpad experiment.
struct TouchpadFrame: Sendable, Equatable {
    var contacts: [TouchpadContact]
    var button = false
}

/// Scripted gestures for the Phase 1 test: does Windows treat our collection as a precision
/// touchpad? Each script is a list of frames `interval` apart that ends with every contact
/// lifted, so Windows sees a complete gesture. Positions are written as 0...1 across the pad.
enum TouchpadScript: String, CaseIterable, Identifiable, Sendable {
    case move, tap, click, scrollDown, scrollUp, threeFingerSwipeUp, fourFingerSwipeLeft

    var id: String {
        rawValue
    }

    /// Frame spacing: the Magic Trackpad's own frame rate and the PC link's connection interval.
    static let interval: TimeInterval = 0.015

    var title: String {
        switch self {
        case .move: "Move right"
        case .tap: "Tap"
        case .click: "Click"
        case .scrollDown: "Two-finger scroll down"
        case .scrollUp: "Two-finger scroll up"
        case .threeFingerSwipeUp: "Three-finger swipe up"
        case .fourFingerSwipeLeft: "Four-finger swipe left"
        }
    }

    var frames: [TouchpadFrame] {
        switch self {
        case .move:
            return Self.glide(from: [(0.30, 0.50)], dx: 0.40, dy: 0, frames: 40)
        case .tap:
            return Self.glide(from: [(0.50, 0.50)], dx: 0, dy: 0, frames: 5)
        case .click:
            let rest = Self.hold([(0.50, 0.50)], frames: 3)
            let pressed = Self.hold([(0.50, 0.50)], frames: 6).map { TouchpadFrame(contacts: $0.contacts, button: true) }
            return rest + pressed + rest + [Self.lift([(0.50, 0.50)])]
        case .scrollDown:
            return Self.glide(from: [(0.45, 0.35), (0.55, 0.35)], dx: 0, dy: 0.30, frames: 30)
        case .scrollUp:
            return Self.glide(from: [(0.45, 0.65), (0.55, 0.65)], dx: 0, dy: -0.30, frames: 30)
        case .threeFingerSwipeUp:
            return Self.glide(from: [(0.35, 0.70), (0.50, 0.70), (0.65, 0.70)], dx: 0, dy: -0.40, frames: 12)
        case .fourFingerSwipeLeft:
            return Self.glide(from: [(0.70, 0.40), (0.70, 0.50), (0.70, 0.60), (0.70, 0.70)], dx: -0.40, dy: 0, frames: 12)
        }
    }

    typealias Position = (x: Double, y: Double)

    /// Fingers that land at `starts`, travel `dx`/`dy` over `frames` frames and lift.
    static func glide(from starts: [Position], dx: Double, dy: Double, frames: Int) -> [TouchpadFrame] {
        let steps = max(frames, 2)
        var result: [TouchpadFrame] = []
        for step in 0 ..< steps {
            let progress = Double(step) / Double(steps - 1)
            let positions = starts.map { (x: $0.x + dx * progress, y: $0.y + dy * progress) }
            result.append(TouchpadFrame(contacts: contacts(positions)))
        }
        let ends = starts.map { (x: $0.x + dx, y: $0.y + dy) }
        return result + [lift(ends)]
    }

    /// Fingers resting at `positions` for `frames` frames, without lifting.
    static func hold(_ positions: [Position], frames: Int) -> [TouchpadFrame] {
        Array(repeating: TouchpadFrame(contacts: contacts(positions)), count: frames)
    }

    /// The frame that lifts every finger: Windows needs each identifier reported once with
    /// its tip switch off.
    static func lift(_ positions: [Position]) -> TouchpadFrame {
        TouchpadFrame(contacts: contacts(positions).map { contact in
            var lifted = contact
            lifted.tip = false
            return lifted
        })
    }

    private static func contacts(_ positions: [Position]) -> [TouchpadContact] {
        positions.enumerated().map { index, position in
            TouchpadContact(id: UInt8(index), x: logical(position.x), y: logical(position.y))
        }
    }

    static func logical(_ value: Double) -> UInt16 {
        UInt16((min(max(value, 0), 1) * Double(TouchpadReport.logicalMaximum)).rounded())
    }
}
