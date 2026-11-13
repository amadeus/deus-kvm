import CoreGraphics

/// Trackpad gestures while controlling Windows. The HID link has no way to express them, and
/// macOS must not run Mission Control, switch Spaces or zoom behind the PC, so they are swallowed.
enum TrackpadGesture {
    /// Quartz event types for trackpad gestures (NSEventType values; Quartz has no public names):
    /// rotate, begin/end gesture, gesture, magnify (also the Dock's swipe control), swipe,
    /// smart magnify, quick look and pressure.
    static let eventTypes: [UInt32] = [18, 19, 20, 29, 30, 31, 32, 33, 34]

    static var eventMask: CGEventMask {
        eventTypes.reduce(CGEventMask(0)) { $0 | CGEventMask(1) << CGEventMask($1) }
    }

    /// Type 29: the raw touch frames behind every gesture, sent for any touch on the pad, moving
    /// the pointer included (as many as pointer moves, and more while fingers rest). macOS acts on
    /// the dedicated types above, so swallowing those still blocks Mission Control, Spaces,
    /// pinch and swipes; the frames themselves go to DeusKVM's panel under the parked cursor.
    static let touchFrames: UInt32 = 29

    /// What is swallowed while controlling Windows.
    static var remoteMask: CGEventMask {
        eventMask & ~(CGEventMask(1) << CGEventMask(touchFrames))
    }
}
