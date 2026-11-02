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
}
