import CoreGraphics
import Foundation
import os

struct TapConfiguration: Equatable, Sendable {
    var targetAvailable = false
    var edgeEnabled = false
    var geometry: EdgeGeometry?
    var shortcut = ToggleShortcut()
}

enum TapOutput: Sendable {
    case installed(Int, Bool)
    case keyboardMonitoring(Int, Bool)
    case begin(Int, CGPoint, fromEdge: Bool)
    case input(Int, DirectInputEvent, TimeInterval)
    case end(Int)
    case disabled
}

/// all mutable tap state is protected by lock; the callback decides routing synchronously
final class InputTap: @unchecked Sendable {
    private let lock = NSLock()
    private var configuration = TapConfiguration()
    private var handoff = HandoffState()
    private var remote = false
    private var generation = 0
    private var edgeArmed = false
    private var location = CGPoint.zero
    private var dropNextMotion = false
    private var returnProbe: ReturnMotionProbe?
    private let log = Logger(subsystem: "io.github.amadeus.deuskvm", category: "Capture")
    private var rawKeyboardReady = false
    private var rawShortcutKeys: Set<UInt16> = []
    private var eventTap: CFMachPort?
    private var runLoop: CFRunLoop?
    private var deferredTimer: CFRunLoopTimer?
    private var deferredDeadline: TimeInterval?
    private var deferredGeneration = 0
    private var runState = TapRunState()
    private let output: @MainActor @Sendable (TapOutput) -> Void

    init(output: @escaping @MainActor @Sendable (TapOutput) -> Void) {
        self.output = output
    }

    func configure(_ value: TapConfiguration) {
        lock.withLock {
            guard configuration != value else { return }
            configuration = value
            edgeArmed = false
            handoff.cancel()
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
            if let eventTap { CFMachPortInvalidate(eventTap) }
            if let runLoop { CFRunLoopStop(runLoop) }
        }
    }

    func reenable() {
        lock.withLock {
            if let eventTap { CGEvent.tapEnable(tap: eventTap, enable: true) }
        }
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
        let mask = [
            CGEventType.keyDown, .keyUp, .flagsChanged, .mouseMoved, .leftMouseDown, .leftMouseUp,
            .leftMouseDragged, .rightMouseDown, .rightMouseUp, .rightMouseDragged,
            .otherMouseDown, .otherMouseUp, .otherMouseDragged, .scrollWheel, MediaKeyEvent.eventType
        ].reduce(CGEventMask(0)) { $0 | CGEventMask(1) << CGEventMask($1.rawValue) }
        let refcon = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
            eventsOfInterest: mask, callback: Self.callback, userInfo: refcon
        ), let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            return
        }
        let loop = CFRunLoopGetCurrent()!
        lock.withLock {
            eventTap = tap
            runLoop = loop
            handoff.heldRawKeys.removeAll()
            handoff.heldMediaKeys.removeAll()
            rawShortcutKeys.removeAll()
            handoff.heldKeys = Set((0 ... 127).compactMap { code in
                CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(code)) ? UInt16(code) : nil
            })
            location = CGEvent(source: nil)?.location ?? .zero
            handoff.updateModifiers(CGEventSource.flagsState(.combinedSessionState))
            handoff.heldButtons = Set((0 ... 31).compactMap { button in
                guard let cgButton = CGMouseButton(rawValue: UInt32(button)) else { return nil }
                return CGEventSource.buttonState(.combinedSessionState, button: cgButton) ? Int64(button) : nil
            })
        }
        let keyboard = RawKeyboardMonitor { [weak self] key, down in self?._rawKey(key, down: down) }
        let keyboardReady = keyboard.start(on: loop)
        lock.withLock { rawKeyboardReady = keyboardReady }
        _emit(.keyboardMonitoring(token, keyboardReady))
        CFRunLoopAddSource(loop, source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        _emit(.installed(token, true))
        if lock.withLock({ runState.isRunning }) { CFRunLoopRun() }
        keyboard.stop(on: loop)
        CFMachPortInvalidate(tap)
        lock.withLock {
            _cancelTick()
            eventTap = nil
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
        remote = true
        returnProbe = nil
        generation += 1
        edgeArmed = false
        dropNextMotion = true
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
                remote = false
                generation += 1
                edgeArmed = false
                handoff.cancel()
                _emit(.disabled)
                return false
            }
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
        let motion = [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged].contains(type)
        if !remote {
            if motion, returnProbe != nil, let point = CGEvent(source: nil)?.location {
                let delta = event.getIntegerValueField(.mouseEventDeltaX) != 0 || event.getIntegerValueField(.mouseEventDeltaY) != 0
                if let result = returnProbe?.observe(now: ProcessInfo.processInfo.systemUptime, position: point, hasDelta: delta) {
                    log.notice("\(result, privacy: .public)")
                    returnProbe = nil
                }
            }
            location = event.location
            if motion, dropNextMotion {
                dropNextMotion = false
                edgeArmed = false
            } else if motion { _checkEdge(event) }
        } else if motion {
            if let point = configuration.geometry?.parkingPoint,
               TapWorkSchedule.needsCursorCorrection(location: event.location, parkingPoint: point)
            { CGWarpMouseCursorPosition(point) }
            if dropNextMotion {
                dropNextMotion = false
                return true
            }
        }
        if wasRemote, let input = DirectInputEvent(type: type, event: event) {
            _emit(.input(generation, input, ProcessInfo.processInfo.systemUptime))
        }
        return wasRemote
    }

    private func _suppressRawAlias(code: UInt16, down: Bool, flags: CGEventFlags) -> Bool {
        if !down { return rawShortcutKeys.remove(code) != nil || remote }
        let shortcut = (remote || configuration.targetAvailable) && configuration.shortcut.matches(key: code, flags: flags)
        return remote || rawShortcutKeys.contains(code) || shortcut
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
            _emit(.input(generation, input, ProcessInfo.processInfo.systemUptime))
        }
    }

    private func _media(_ event: CGEvent) -> Bool {
        guard let media = MediaKeyEvent(type: MediaKeyEvent.eventType, event: event) else { return false }
        if media.isDown {
            handoff.heldMediaKeys.insert(media.key.rawValue)
        } else {
            handoff.heldMediaKeys.remove(media.key.rawValue)
        }
        if remote, let input = DirectInputEvent(type: MediaKeyEvent.eventType, event: event) {
            _emit(.input(generation, input, ProcessInfo.processInfo.systemUptime))
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
        if edgeArmed, handoff.isReleased { _begin(fromEdge: true) }
    }

    private static let callback: CGEventTapCallBack = { _, type, event, userInfo in
        guard let userInfo else { return Unmanaged.passUnretained(event) }
        let owner = Unmanaged<InputTap>.fromOpaque(userInfo).takeUnretainedValue()
        return owner._handle(type: type, event: event) ? nil : Unmanaged.passUnretained(event)
    }
}
