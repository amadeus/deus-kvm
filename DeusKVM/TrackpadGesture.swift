import CoreGraphics
import Foundation

/// Trackpad gestures while controlling Windows. The HID link has no way to express them, so
/// they are swallowed (macOS must not run Mission Control or switch Spaces behind the PC), and
/// the system Spaces/Mission Control swipe is translated into the matching Windows shortcut.
enum TrackpadGesture {
    /// Quartz event types for trackpad gestures (NSEventType values; Quartz has no public names):
    /// rotate, begin/end gesture, gesture, magnify (also the Dock's swipe control), swipe,
    /// smart magnify, quick look and pressure.
    static let eventTypes: [UInt32] = [18, 19, 20, 29, 30, 31, 32, 33, 34]

    static var eventMask: CGEventMask {
        eventTypes.reduce(CGEventMask(0)) { $0 | CGEventMask(1) << CGEventMask($1) }
    }
}

/// One Dock swipe event: the system Mission Control/Spaces gesture, whatever finger count the
/// user configured. Undocumented WindowServer fields, as used by open-source space switchers.
struct DockSwipeSample: Equatable {
    enum Phase {
        static let began: Int64 = 1
        static let ended: Int64 = 4
        static let cancelled: Int64 = 8
    }

    static let dockSwipeHIDType: Int64 = 23
    var motion: Int64 // 1 horizontal, 2 vertical
    var phase: Int64
    var progress: Double
    var velocityX: Double
    var velocityY: Double
}

extension DockSwipeSample {
    private static func field(_ raw: UInt32) -> CGEventField? {
        CGEventField(rawValue: raw)
    }

    init?(event: CGEvent) {
        guard let hidType = Self.field(110), event.getIntegerValueField(hidType) == Self.dockSwipeHIDType,
              let motion = Self.field(123), let progress = Self.field(124), let velocityX = Self.field(129),
              let velocityY = Self.field(130), let phase = Self.field(132) else { return nil }
        self.init(
            motion: event.getIntegerValueField(motion), phase: event.getIntegerValueField(phase),
            progress: event.getDoubleValueField(progress),
            velocityX: event.getDoubleValueField(velocityX), velocityY: event.getDoubleValueField(velocityY)
        )
    }
}

enum WindowsGestureShortcut: String, Equatable {
    case taskView, nextDesktop, previousDesktop, showDesktop

    /// Down/up with the modifiers held, then a modifier release. Another key between the
    /// Windows key's press and release keeps Windows from opening Start.
    func events(capsLock: Bool) -> [DirectInputEvent] {
        let (key, modifiers): (Keycode, KeyboardModifiers) = switch self {
        case .taskView: (.tab, [.leftGUI])
        case .nextDesktop: (.rightArrow, [.leftCtrl, .leftGUI])
        case .previousDesktop: (.leftArrow, [.leftCtrl, .leftGUI])
        case .showDesktop: (.d, [.leftGUI])
        }
        return [
            DirectInputEvent(kind: .keyDown(key), modifiers: modifiers),
            DirectInputEvent(kind: .keyUp(key), modifiers: modifiers),
            DirectInputEvent(kind: .flagsChanged(capsLock: capsLock), modifiers: [])
        ]
    }
}

/// Follows one Dock swipe from its first event to its end. The direction comes from the final
/// progress (falling back to velocity); a cancelled swipe does nothing, as on the Mac.
struct DockSwipeRecognizer {
    struct Result: Equatable {
        let shortcut: WindowsGestureShortcut?
        let summary: String
    }

    private var active = false
    private var motion: Int64 = 0
    private var progress = 0.0
    private var velocity = (x: 0.0, y: 0.0)

    mutating func observe(_ sample: DockSwipeSample) -> Result? {
        if sample.phase == DockSwipeSample.Phase.began || !active {
            active = true; motion = 0; progress = 0; velocity = (0, 0)
        }
        if sample.motion != 0 { motion = sample.motion }
        if sample.progress != 0 { progress = sample.progress }
        if sample.velocityX != 0 || sample.velocityY != 0 { velocity = (sample.velocityX, sample.velocityY) }
        let ended = sample.phase == DockSwipeSample.Phase.ended
        guard ended || sample.phase == DockSwipeSample.Phase.cancelled else { return nil }
        active = false
        let shortcut = ended ? Self.shortcut(motion: motion, progress: progress, velocity: velocity) : nil
        let summary = String(
            format: "gesture: dock swipe %@ motion=%lld progress=%.3f velocity=%.0f,%.0f -> %@",
            ended ? "ended" : "cancelled", motion, progress, velocity.x, velocity.y, shortcut?.rawValue ?? "none"
        )
        return Result(shortcut: shortcut, summary: summary)
    }

    /// Assumes progress and velocity follow the fingers in screen coordinates (y grows downward):
    /// fingers left reveal the next desktop as on the Mac, fingers up open Task View.
    static func shortcut(motion: Int64, progress: Double, velocity: (x: Double, y: Double)) -> WindowsGestureShortcut? {
        let vertical = motion == 2 || (motion != 1 && abs(velocity.y) > abs(velocity.x))
        let value = progress != 0 ? progress : vertical ? velocity.y : velocity.x
        guard value != 0 else { return nil }
        if vertical { return value < 0 ? .taskView : .showDesktop }
        return value < 0 ? .nextDesktop : .previousDesktop
    }
}
