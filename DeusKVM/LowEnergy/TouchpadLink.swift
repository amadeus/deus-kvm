import Foundation

/// The touchpad collection's side of the Bluetooth queue: scripted playback (Phase 1 of the
/// precision touchpad experiment) and the live contact stream (Phase 2). Everything here
/// runs on the Bluetooth queue; the transport supplies the send path and tells it when a
/// remote session starts and stops. Frames from the source are dispatched onto the queue.
final class TouchpadLink: @unchecked Sendable {
    struct Hooks: Sendable {
        /// The contact characteristic exists: the experiment setting was on at launch.
        let isAdvertised: @Sendable () -> Bool
        /// CoreBluetooth accepts notifications right now (no backpressure).
        let isReady: @Sendable () -> Bool
        /// Sends one contact report; true if CoreBluetooth took it at once.
        let send: @Sendable (Data) -> Bool
        /// Main thread: the stream's state and a diagnostics line.
        let note: @Sendable (_ streaming: Bool, _ message: String) -> Void
    }

    /// Advertised, subscribed and Windows wrote Input Mode 3 (mirrored from the main thread).
    /// Until then live frames are dropped and the mouse path carries the pad.
    var precision = false

    private let queue: DispatchQueue
    private let source: TouchpadSource
    private let hooks: Hooks
    private var epoch = 0
    private var stream = TouchpadStream()

    init(queue: DispatchQueue, source: TouchpadSource, hooks: Hooks) {
        self.queue = queue
        self.source = source
        self.hooks = hooks
    }

    /// The manager was replaced: playback stops, the stream ends silently.
    func reset() {
        epoch += 1
        precision = false
        stream = TouchpadStream()
        _ = source.stop()
    }

    var snapshot: String {
        "precision=\(precision) contactsStreaming=\(stream.streaming)"
    }

    // MARK: Live contacts

    /// Session start: the source runs whenever the collection is advertised, so a mode switch
    /// mid-session needs nothing else. A missing framework or device leaves the mouse path.
    func startSession() {
        guard hooks.isAdvertised() else { return }
        stream = TouchpadStream()
        let started = ProcessInfo.processInfo.systemUptime
        let outcome = source.start(
            deliver: { [weak self] contacts, timestamp in self?.queue.async { self?.frame(contacts, at: timestamp) } },
            fault: { [weak self] reason in self?.queue.async { self?.fault(reason) } }
        )
        let setupMs = Int((ProcessInfo.processInfo.systemUptime - started) * 1000)
        switch outcome {
        case let .started(detail):
            stream.streaming = true
            hooks.note(true, "touchpad: contacts streaming setupMs=\(setupMs) \(detail)")
        case let .unavailable(reason):
            hooks.note(false, "touchpad: contacts unavailable, mouse path kept: \(reason)")
        }
    }

    /// Session end: every finger still down lifts and the pad's button releases, ahead of the
    /// controller's zero reports; then the session's counts go to the log.
    func stopSession() {
        guard stream.streaming else { return }
        let lifted = source.stop()
        if !lifted.isEmpty || stream.button {
            sendFrame(TouchpadFrame(contacts: lifted, button: false), at: ProcessInfo.processInfo.systemUptime)
        }
        stream.streaming = false
        hooks.note(false, "touchpad: session \(stream.summary) \(source.clockDetail)")
    }

    /// The pad's physical click, routed by the tap, goes out with the contacts last reported.
    func button(_ down: Bool) {
        guard stream.streaming, precision, stream.button != down else { return }
        stream.buttons += 1
        sendFrame(TouchpadFrame(contacts: stream.contacts, button: down), at: ProcessInfo.processInfo.systemUptime)
    }

    private func frame(_ contacts: [TouchpadContact], at timestamp: TimeInterval) {
        guard stream.streaming else { return }
        stream.frames += 1
        guard precision else { stream.idle += 1; return }
        sendFrame(TouchpadFrame(contacts: contacts, button: stream.button), at: timestamp)
    }

    /// The source saw a record it does not understand: its frames stop and the tap goes back
    /// to forwarding the pad as a mouse (its presence reads `.none` from here on).
    private func fault(_ reason: String) {
        guard stream.streaming else { return }
        stream.streaming = false
        _ = source.stop()
        hooks.note(false, "touchpad: contacts stopped, mouse path kept: \(reason)")
    }

    /// Hybrid reports as the scripts send them, stamped with the frame's capture time. A frame
    /// with neither a lift nor a button change is dropped while the link is backed up.
    private func sendFrame(_ frame: TouchpadFrame, at timestamp: TimeInterval) {
        if !hooks.isReady(), TouchpadStream.droppable(frame, button: stream.button) { stream.drops += 1; return }
        stream.lifts += frame.contacts.filter { !$0.tip }.count
        stream.button = frame.button
        stream.contacts = frame.contacts.filter(\.tip)
        let scanTime = TouchpadReport.scanTime(uptime: timestamp)
        for report in TouchpadReport.reports(contacts: frame.contacts, scanTime: scanTime, button: frame.button) {
            stream.reports += 1
            if !hooks.send(report.data) { stream.deferred += 1 }
        }
    }

    // MARK: Scripted playback

    private struct Playback: Sendable {
        let frames: [TouchpadFrame]
        let interval: TimeInterval
        let epoch: Int
        let completion: @Sendable (Int, Int) -> Void
        var index = 0
        var accepted = 0
        var deferred = 0
    }

    /// Sends scripted contact frames `interval` apart, each stamped with the scan time of its
    /// send. The completion gets the reports CoreBluetooth took at once and the reports that
    /// waited in the backpressure queue.
    func play(_ frames: [TouchpadFrame], interval: TimeInterval, completion: @escaping @Sendable (Int, Int) -> Void) {
        playFrame(Playback(frames: frames, interval: interval, epoch: epoch, completion: completion))
    }

    private func playFrame(_ playback: Playback) {
        guard epoch == playback.epoch, playback.index < playback.frames.count, hooks.isAdvertised()
        else { playback.completion(playback.accepted, playback.deferred); return }
        let frame = playback.frames[playback.index]
        let scanTime = TouchpadReport.scanTime(uptime: ProcessInfo.processInfo.systemUptime)
        var next = playback
        next.index += 1
        for report in TouchpadReport.reports(contacts: frame.contacts, scanTime: scanTime, button: frame.button) {
            if hooks.send(report.data) { next.accepted += 1 } else { next.deferred += 1 }
        }
        let scheduled = next
        queue.asyncAfter(deadline: .now() + playback.interval) { [self] in playFrame(scheduled) }
    }
}
