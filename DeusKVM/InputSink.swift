import Foundation

/// Where captured input goes while controlling the PC: the local-network input stream.
struct InputSink {
    let send: (NetworkInputEvent) -> Void
    /// The stream is up and the PC injects: pointer motion and buttons can go now.
    let pointerReady: () -> Bool
    /// As `pointerReady`, and the companion also takes keys, media keys and scroll.
    let keysReady: () -> Bool
}
