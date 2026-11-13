import CoreGraphics
import Foundation
import os

/// all mutable tap state is protected by lock; the callback decides routing synchronously
final class InputTap: @unchecked Sendable {
    private let lock = NSLock()
    private var configuration = TapConfiguration()
    private var handoff = HandoffState()
    /// Every change goes through here, so the remote-only and motion taps follow it.
    private var remote = false {
        didSet { if remote != oldValue { _applyTaps() } }
    }

    private var generation = 0
    private var edgeArmed = false
    private var location = CGPoint.zero
    private var dropNextMotion = false
    private var returnProbe: ReturnMotionProbe? {
        didSet { if (returnProbe == nil) != (oldValue == nil) { _applyTaps() } }
    }

    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Capture")
    private var rawKeyboardReady = false
    private var rawShortcutKeys: Set<UInt16> = []
    /// Tap thread only, while the tap runs.
    private var keyboard: RawKeyboardMonitor?
    private var resyncGate = ResyncGate()
    /// Routes pointer events between the local-network stream and the Bluetooth mouse.
    private var router = PointerRouter()
    /// Gesture types already noted in the diagnostics log this launch.
    private var notedGestureTypes: Set<UInt32> = []
    /// Keys, modifiers, buttons and media keys: always on, and rare.
    private var eventTap: CFMachPort?
    /// Trackpad gestures and scrolling, only while remote (on the Mac they pass through anyway).
    private var remoteTap: CFMachPort?
    /// Motion and drags: while remote, and on the Mac near the edge (`PointerProximity`).
    private var motionTap: CFMachPort?
    private var motionGate = MotionGate.tap
    private var pollTimer: CFRunLoopTimer?
    private var lastPoll: CGPoint?
    private var runLoop: CFRunLoop?
    private var deferredTimer: CFRunLoopTimer?
    private var deferredDeadline: TimeInterval?
    private var deferredGeneration = 0
    private var runState = TapRunState()
    private let output: @MainActor @Sendable (TapOutput) -> Void
    /// Remote input skips the main thread; the receiver orders it by generation.
    private let forward: @Sendable (Int, DirectInputEvent, TimeInterval) -> Void
    /// What the local-network pointer stream can do, asked per pointer event.
    private let network: @Sendable () -> NetworkCarrier
    private let stats: TapStats

    init(
        output: @escaping @MainActor @Sendable (TapOutput) -> Void,
        forward: @escaping @Sendable (Int, DirectInputEvent, TimeInterval) -> Void,
        network: @escaping @Sendable () -> NetworkCarrier = { .down },
        stats: TapStats = TapStats()
    ) {
        self.output = output
        self.forward = forward
        self.network = network
        self.stats = stats
    }

    func configure(_ value: TapConfiguration) {
        lock.withLock {
            guard configuration != value else { return }
            configuration = value
            edgeArmed = false
            handoff.cancel()
            _applyTaps()
        }
    }

    @discardableResult
    func start() -> Bool {
        guard let token = lock.withLock({ runState.begin() }) else { return false }
        Thread { [self] in _run(token) }.start()
        return true
    }

    func isCurrentRun(_ token: Int) -> Bool {
        lock.withLock { runState.isCurrent(token) }
    }

    func stop() {
        lock.withLock {
            runState.stop()
            _cancelTick()
            remote = false
            generation += 1
            for tap in [eventTap, remoteTap, motionTap].compactMap(\.self) {
                CFMachPortInvalidate(tap)
            }
            if let pollTimer { CFRunLoopTimerInvalidate(pollTimer) }
            if let runLoop { CFRunLoopStop(runLoop) }
        }
    }

    func reenable() {
        lock.withLock {
            if let eventTap { CGEvent.tapEnable(tap: eventTap, enable: true) }
            // macOS may have disabled either of the others instead; restore what the state wants.
            if let remoteTap { CGEvent.tapEnable(tap: remoteTap, enable: remote) }
            if let motionTap { CGEvent.tapEnable(tap: motionTap, enable: motionGate == .tap) }
            // Events, releases included, were lost while macOS had the tap disabled.
            _requestResync("tap re-enabled", force: true)
        }
    }

    /// Replaces tracked held input with macOS's current state, on the tap thread.
    func resyncHeldInput(_ reason: String) {
        lock.withLock { _requestResync(reason, force: true) }
    }

    func requestToggle() {
        lock.withLock { handoff.requestToggle(); _scheduleTick() }
    }

    var returnDiagnostic: String {
        lock.withLock {
            "remote=\(remote) keys=\(handoff.heldKeys.count) raw=\(handoff.heldRawKeys.count) " +
                "media=\(handoff.heldMediaKeys.count) buttons=\(handoff.heldButtons.count) " +
                "modifiers=\(!handoff.modifiers.isDisjoint(with: ToggleShortcut.modifierMask))"
        }
    }

    func acceptCompanionReturn() -> Bool {
        lock.withLock {
            guard remote, handoff.isReleased else { return false }
            remote = false
            generation += 1
            handoff.cancel()
            edgeArmed = false
            dropNextMotion = true
            return true
        }
    }

    func forceLocal() {
        lock.withLock {
            remote = false
            generation += 1
            handoff.cancel()
            edgeArmed = false
            dropNextMotion = true
        }
    }

    func observeReturn(since startedAt: TimeInterval) {
        lock.withLock {
            guard !remote, let position = CGEvent(source: nil)?.location else { return }
            returnProbe = ReturnMotionProbe(startedAt: startedAt, origin: position)
            _scheduleTick()
        }
    }

    func isCurrent(_ value: Int) -> Bool {
        lock.withLock { generation == value }
    }

    private func _emit(_ event: TapOutput) {
        let output = output
        DispatchQueue.main.async { output(event) }
    }

    private func _run(_ token: Int) {
        defer {
            lock.withLock { runState.finish(token) }
            _emit(.installed(token, false))
        }
        guard lock.withLock({ runState.isRunning && runState.isCurrent(token) }) else { return }
        let refcon = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        // A split-off tap that cannot be created leaves its events on the main tap, as before.
        let remoteTaps = Self.makeTap(TapMask.remote, refcon, enabled: false)
        let motionTaps = Self.makeTap(TapMask.motion, refcon, enabled: true)
        let mask = TapMask.main | (remoteTaps == nil ? TapMask.remote : 0) | (motionTaps == nil ? TapMask.motion : 0)
        guard let main = Self.makeTap(mask, refcon, enabled: true) else {
            for extra in [remoteTaps, motionTaps].compactMap(\.self) {
                CFMachPortInvalidate(extra.tap)
            }
            return
        }
        let loop = CFRunLoopGetCurrent()!
        for extra in [remoteTaps, motionTaps].compactMap(\.self) {
            CFRunLoopAddSource(loop, extra.source, .commonModes)
        }
        let poll = CFRunLoopTimerCreateWithHandler(nil, CFAbsoluteTimeGetCurrent() + 1e9, 1e9, 0, 0) { [weak self] _ in self?._poll() }!
        CFRunLoopTimerSetTolerance(poll, 0.002)
        CFRunLoopAddTimer(loop, poll, .commonModes)
        lock.withLock {
            eventTap = main.tap
            remoteTap = remoteTaps?.tap
            motionTap = motionTaps?.tap
            pollTimer = poll
            motionGate = .tap
            runLoop = loop
            handoff.heldRawKeys.removeAll()
            rawShortcutKeys.removeAll()
            location = CGEvent(source: nil)?.location ?? .zero
            _ = _resyncFromSystem()
            _applyTaps()
        }
        let keyboard = RawKeyboardMonitor { [weak self] key, down in self?._rawKey(key, down: down) }
        self.keyboard = keyboard
        let keyboardReady = keyboard.start(on: loop)
        lock.withLock { rawKeyboardReady = keyboardReady }
        _emit(.keyboardMonitoring(token, keyboardReady))
        CFRunLoopAddSource(loop, main.source, .commonModes)
        CGEvent.tapEnable(tap: main.tap, enable: true)
        _emit(.installed(token, true))
        if lock.withLock({ runState.isRunning }) { CFRunLoopRun() }
        keyboard.stop(on: loop)
        self.keyboard = nil
        for tap in [main.tap, remoteTaps?.tap, motionTaps?.tap].compactMap(\.self) {
            CFMachPortInvalidate(tap)
        }
        CFRunLoopTimerInvalidate(poll)
        lock.withLock {
            _cancelTick()
            eventTap = nil
            remoteTap = nil
            motionTap = nil
            pollTimer = nil
            runLoop = nil
        }
    }

    /// Called with the tap lock held. Add only one deferred callback after releases.
    private func _scheduleTick() {
        guard runState.isRunning, let loop = runLoop else { return }
        let edgeReady = !remote && configuration.targetAvailable && configuration.edgeEnabled && edgeArmed &&
            configuration.geometry?.isAtEdge(location) == true
        let handoffReady = handoff.isReleased && (handoff.pendingToggle || edgeReady)
        guard handoffReady || returnProbe != nil else { _cancelTick(); return }
        let now = ProcessInfo.processInfo.systemUptime
        guard let deadline = TapWorkSchedule.deadline(now: now, handoffReady: handoffReady, probeStarted: returnProbe?.startedAt)
        else { return }
        // Motion while a released toggle is pending must not postpone that toggle.
        if let deferredDeadline, deferredDeadline <= deadline { return }
        _cancelTick()
        deferredDeadline = deadline
        let token = deferredGeneration
        let timer = CFRunLoopTimerCreateWithHandler(nil, CFAbsoluteTimeGetCurrent() + max(0, deadline - now), 0, 0, 0) { [weak self] _ in
            self?._tick(token)
        }!
        deferredTimer = timer
        CFRunLoopAddTimer(loop, timer, .commonModes)
        CFRunLoopWakeUp(loop)
    }

    /// Called with the lock held. Runs a resync on the tap thread after this callback returns,
    /// at most every `ResyncGate.interval` unless forced.
    private func _requestResync(_ reason: String, force: Bool = false) {
        guard runState.isRunning, let runLoop, force || resyncGate.allow(now: ProcessInfo.processInfo.systemUptime) else { return }
        CFRunLoopPerformBlock(runLoop, CFRunLoopMode.commonModes.rawValue) { [weak self] in self?._resync(reason) }
        CFRunLoopWakeUp(runLoop)
    }

    /// Tap thread. Raw keyboard callbacks take the lock, so the device refresh runs first.
    private func _resync(_ reason: String) {
        let rawBefore = lock.withLock { handoff.heldRawKeys.count }
        keyboard?.refresh()
        lock.withLock {
            guard runState.isRunning else { return }
            defer { _scheduleTick() }
            var correction = _resyncFromSystem()
            correction.raw = max(0, rawBefore - handoff.heldRawKeys.count)
            guard correction.total > 0 else { return }
            let message = correction.message(reason)
            log.notice("\(message, privacy: .public)")
            _emit(.note(message))
        }
    }

    /// Called with the lock held.
    private func _resyncFromSystem() -> HeldInputCorrection {
        let keys = Set((0 ... 127).compactMap { code in
            CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(code)) ? UInt16(code) : nil
        })
        let buttons = Set((0 ... 31).compactMap { button -> Int64? in
            guard let cgButton = CGMouseButton(rawValue: UInt32(button)) else { return nil }
            return CGEventSource.buttonState(.combinedSessionState, button: cgButton) ? Int64(button) : nil
        })
        return handoff.resync(keys: keys, buttons: buttons, flags: CGEventSource.flagsState(.combinedSessionState))
    }

    private func _cancelTick() {
        deferredGeneration += 1
        if let deferredTimer { CFRunLoopTimerInvalidate(deferredTimer) }
        deferredTimer = nil; deferredDeadline = nil
    }

    private func _tick(_ token: Int) {
        lock.withLock {
            guard token == deferredGeneration else { return }
            deferredTimer = nil; deferredDeadline = nil
            defer { _scheduleTick() }
            guard runState.isRunning else { CFRunLoopStop(CFRunLoopGetCurrent()); return }
            if let result = returnProbe?.expired(now: ProcessInfo.processInfo.systemUptime) {
                log.notice("\(result, privacy: .public)")
                returnProbe = nil
            }
            // the last local key-up has returned from its callback before this timer can commit
            if handoff.takeToggle() {
                if remote { _end() } else if configuration.targetAvailable { _begin() }
                return
            }
            guard !remote, configuration.targetAvailable, configuration.edgeEnabled,
                  let geometry = configuration.geometry, edgeArmed,
                  geometry.isAtEdge(location), handoff.isReleased else { return }
            _begin(fromEdge: true)
        }
    }

    private func _begin(fromEdge: Bool = false) {
        guard let geometry = configuration.geometry else { return }
        if !fromEdge, let point = CGEvent(source: nil)?.location { location = point }
        remote = true
        returnProbe = nil
        generation += 1
        edgeArmed = false
        dropNextMotion = true
        router.reset()
        // placement and panel ownership stay on the main actor; input is already suppressed here
        _emit(.begin(generation, fromEdge ? geometry.inset(location) : location, fromEdge: fromEdge))
    }

    private func _end() {
        remote = false
        edgeArmed = false
        dropNextMotion = true
        _emit(.end(generation))
    }

    private func _handle(type: CGEventType, event: CGEvent) -> Bool {
        lock.withLock {
            defer { _scheduleTick() }
            guard runState.isRunning else { return false }
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                guard _lostTap else { return false } // also sent when we turn one of our taps off
                remote = false
                generation += 1
                edgeArmed = false
                handoff.cancel()
                _emit(.disabled)
                return false
            }
            if TrackpadGesture.eventTypes.contains(type.rawValue) { return _gesture(type: type, event: event) }
            return _handleInput(type: type, event: event)
        }
    }

    /// Called with the tap lock held.
    private func _handleInput(type: CGEventType, event: CGEvent) -> Bool {
        if type == MediaKeyEvent.eventType { return _media(event) }
        handoff.modifiers = event.flags
        let wasRemote = remote
        if type == .keyDown || type == .keyUp {
            let code = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
            if rawKeyboardReady, RawKeyboardState.quartzAliases.contains(code) {
                return _suppressRawAlias(code: code, down: type == .keyDown, flags: event.flags)
            }
            let consumed = handoff.key(
                code: UInt16(event.getIntegerValueField(.keyboardEventKeycode)), down: type == .keyDown,
                repeatEvent: event.getIntegerValueField(.keyboardEventAutorepeat) != 0,
                flags: event.flags,
                shortcut: remote || configuration.targetAvailable ? configuration.shortcut : ToggleShortcut(enabled: false)
            )
            if consumed { return true }
        } else if type == .flagsChanged {
            handoff.updateModifiers(event.flags)
        }
        _trackButtons(type: type, event: event)
        if handoff.pendingToggle, !handoff.isReleased,
           [.keyUp, .flagsChanged, .leftMouseUp, .rightMouseUp, .otherMouseUp].contains(type)
        { _requestResync("toggle blocked by held input") }
        let motion = [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged].contains(type)
        if !remote {
            _handleLocal(motion: motion, event: event)
        } else if motion {
            stats.motion(now: ProcessInfo.processInfo.systemUptime)
            if let point = configuration.geometry?.parkingPoint,
               TapWorkSchedule.needsCursorCorrection(location: event.location, parkingPoint: point)
            {
                CGWarpMouseCursorPosition(point)
                stats.warp()
            }
            if dropNextMotion {
                dropNextMotion = false
                return true
            }
        }
        if wasRemote { _forwardRemote(type: type, event: event) }
        return wasRemote
    }

    private func _rawKey(_ key: Keycode, down: Bool) {
        lock.withLock {
            defer { _scheduleTick() }
            guard runState.isRunning else { return }
            if down { handoff.heldRawKeys.insert(key) } else { handoff.heldRawKeys.remove(key) }
            let flags = CGEventSource.flagsState(.combinedSessionState)
            // Raw aliases are also eligible for the configured local shortcut.
            let alias = RawKeyboardState.aliases[key]
            if let alias, handoff.key(
                code: alias, down: down, repeatEvent: false, flags: flags,
                shortcut: remote || configuration.targetAvailable ? configuration.shortcut : ToggleShortcut(enabled: false)
            ) {
                if down { rawShortcutKeys.insert(alias) }
                return
            }
            guard remote else { return }
            let input = DirectInputEvent(kind: down ? .keyDown(key) : .keyUp(key), modifiers: KeyboardModifiers(eventFlags: flags))
            forward(generation, input, ProcessInfo.processInfo.systemUptime)
        }
    }

    private static let callback: CGEventTapCallBack = { _, type, event, userInfo in
        guard let userInfo else { return Unmanaged.passUnretained(event) }
        let owner = Unmanaged<InputTap>.fromOpaque(userInfo).takeUnretainedValue()
        return owner._handle(type: type, event: event) ? nil : Unmanaged.passUnretained(event)
    }
}

extension InputTap {
    /// Called with the tap lock held: input while the pointer is on the Mac.
    private func _handleLocal(motion: Bool, event: CGEvent) {
        if motion, returnProbe != nil, let point = CGEvent(source: nil)?.location {
            let delta = event.getIntegerValueField(.mouseEventDeltaX) != 0 || event.getIntegerValueField(.mouseEventDeltaY) != 0
            if let result = returnProbe?.observe(now: ProcessInfo.processInfo.systemUptime, position: point, hasDelta: delta) {
                log.notice("\(result, privacy: .public)")
                returnProbe = nil
            }
        }
        location = event.location
        guard motion else { return }
        if dropNextMotion {
            dropNextMotion = false
            edgeArmed = false
        } else { _checkEdge(event) }
        if !remote { _gateLocalMotion() }
    }

    private func _suppressRawAlias(code: UInt16, down: Bool, flags: CGEventFlags) -> Bool {
        if !down { return rawShortcutKeys.remove(code) != nil || remote }
        let shortcut = (remote || configuration.targetAvailable) && configuration.shortcut.matches(key: code, flags: flags)
        return remote || rawShortcutKeys.contains(code) || shortcut
    }

    private func _media(_ event: CGEvent) -> Bool {
        guard let media = MediaKeyEvent(type: MediaKeyEvent.eventType, event: event) else { return false }
        if media.isDown {
            handoff.heldMediaKeys.insert(media.key.rawValue)
        } else {
            handoff.heldMediaKeys.remove(media.key.rawValue)
        }
        if remote, let input = DirectInputEvent(type: MediaKeyEvent.eventType, event: event) {
            stats.event(lagNanoseconds: EventLag.nanoseconds(timestamp: event.timestamp))
            forward(generation, input, ProcessInfo.processInfo.systemUptime)
        }
        return remote
    }

    private func _trackButtons(type: CGEventType, event: CGEvent) {
        if [.leftMouseDown, .rightMouseDown, .otherMouseDown].contains(type) {
            handoff.heldButtons.insert(event.getIntegerValueField(.mouseEventButtonNumber))
        } else if [.leftMouseUp, .rightMouseUp, .otherMouseUp].contains(type) {
            handoff.heldButtons.remove(event.getIntegerValueField(.mouseEventButtonNumber))
        }
    }

    private func _checkEdge(_ event: CGEvent) {
        guard configuration.edgeEnabled, configuration.targetAvailable,
              let geometry = configuration.geometry, geometry.isAtEdge(location)
        else {
            edgeArmed = false
            return
        }
        let dx = event.getIntegerValueField(.mouseEventDeltaX)
        let dy = event.getIntegerValueField(.mouseEventDeltaY)
        // switch during this motion callback; only held-input release needs the timer
        edgeArmed = geometry.isOutward(dx: dx, dy: dy) || (dx == 0 && dy == 0)
        if edgeArmed, handoff.isReleased { _begin(fromEdge: true) } else if edgeArmed {
            _requestResync("crossing blocked by held input")
        }
    }

    /// Called with the lock held. Pointer motion and clicks take the local-network stream while
    /// it is up and the Bluetooth mouse otherwise; everything else is forwarded as it is.
    private func _forwardRemote(type: CGEventType, event: CGEvent) {
        guard let input = DirectInputEvent(type: type, event: event) else { return }
        stats.event(lagNanoseconds: EventLag.nanoseconds(timestamp: event.timestamp))
        let routerEvent = PointerRouter.Event(input.kind)
        // Only a stream that is up (not down, not blocked by a secure desktop) takes pointer input.
        let useNetwork = routerEvent != .other && network() == .up
        let routed = router.route(routerEvent, network: useNetwork) == .network ? input.networked : input
        forward(generation, routed, ProcessInfo.processInfo.systemUptime)
    }

    private static func makeTap(
        _ mask: CGEventMask, _ refcon: UnsafeMutableRawPointer, enabled: Bool
    ) -> (tap: CFMachPort, source: CFRunLoopSource)? {
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
            eventsOfInterest: mask, callback: callback, userInfo: refcon
        ) else { return nil }
        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            CFMachPortInvalidate(tap)
            return nil
        }
        CGEvent.tapEnable(tap: tap, enable: enabled)
        return (tap, source)
    }

    /// Called with the lock held, whenever `remote`, the return probe or the layout changes.
    private func _applyTaps() {
        if let remoteTap { CGEvent.tapEnable(tap: remoteTap, enable: remote) }
        if remote || returnProbe != nil {
            _setMotion(.tap)
        } else if !_edgeActive {
            _setMotion(.off)
        } else if motionGate == .off {
            _setMotion(.tap) // the next motion event decides between the tap and polling
        }
    }

    /// Lock held: macOS disabled a tap that should be on (answering our own disables looped).
    private var _lostTap: Bool {
        [(eventTap, true), (remoteTap, remote), (motionTap, motionGate == .tap)].contains { tap, on in
            tap.map { on && !CGEvent.tapIsEnabled(tap: $0) } ?? false
        }
    }

    private var _edgeActive: Bool {
        configuration.edgeEnabled && configuration.targetAvailable && configuration.geometry != nil
    }

    /// Called with the lock held. `.tap` while resting costs nothing: no events arrive.
    private func _setMotion(_ next: MotionGate, poll interval: TimeInterval = 0) {
        guard let motionTap else { return }
        if (next == .tap) != (motionGate == .tap) { CGEvent.tapEnable(tap: motionTap, enable: next == .tap) }
        motionGate = next
        guard let pollTimer, let runLoop else { return }
        CFRunLoopTimerSetNextFireDate(pollTimer, CFAbsoluteTimeGetCurrent() + (next == .poll ? interval : 1e9))
        if CFRunLoopGetCurrent() !== runLoop { CFRunLoopWakeUp(runLoop) }
    }

    /// Called with the lock held after a local motion event: away from the edge, poll instead.
    private func _gateLocalMotion() {
        guard motionTap != nil, returnProbe == nil, _edgeActive, let geometry = configuration.geometry,
              let interval = PointerProximity.pollInterval(distance: geometry.distance(to: location)) else { return }
        lastPoll = location
        _setMotion(.poll, poll: interval)
    }

    /// Tap thread, while the cursor moves on the Mac away from the edge.
    private func _poll() {
        lock.withLock {
            defer { _scheduleTick() }
            guard runState.isRunning, motionGate == .poll, !remote else { return }
            guard _edgeActive, let geometry = configuration.geometry, let point = CGEvent(source: nil)?.location else {
                _applyTaps()
                return
            }
            location = point
            edgeArmed = false
            dropNextMotion = false
            if geometry.isAtEdge(point) {
                // It outran the poll; cross as for the motion event that would have arrived there.
                _setMotion(.tap)
                edgeArmed = true
                if handoff.isReleased { _begin(fromEdge: true) } else { _requestResync("crossing blocked by held input") }
                return
            }
            guard point != lastPoll, let interval = PointerProximity.pollInterval(distance: geometry.distance(to: point)) else {
                _setMotion(.tap)
                return
            }
            lastPoll = point
            _setMotion(.poll, poll: interval)
        }
    }

    /// Called with the tap lock held. Gestures pass through on the Mac and are swallowed while
    /// controlling Windows.
    private func _gesture(type: CGEventType, event _: CGEvent) -> Bool {
        guard remote else { return false }
        if notedGestureTypes.insert(type.rawValue).inserted {
            _note("gesture events suppressed while controlling Windows: type \(type.rawValue)")
        }
        return true
    }

    private func _note(_ message: String) {
        log.notice("\(message, privacy: .public)")
        _emit(.note(message))
    }
}
