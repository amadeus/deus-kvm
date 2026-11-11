import AppKit
import Carbon
import Combine
import CoreBluetooth

struct EdgeDisplay: Identifiable, Equatable {
    let id: String
    let name: String
    let bounds: CGRect
}

@MainActor
final class EdgeSwitchCoordinator: ObservableObject {
    private lazy var clipboard = MacClipboardSync(service: lowEnergy.companion)
    private let lowEnergy: HIDPeripheral
    private let central: HIDCentral
    private let cursor = CursorConcealer()
    private let diagnostics: CaptureDiagnostics
    /// Remote input goes from the tap thread straight to the Bluetooth queue; only crossings,
    /// returns and tap lifecycle events come here.
    private lazy var tap = InputTap(
        output: { [weak self] event in self?._receive(event) },
        forward: { [transport = lowEnergy.transport] in
            transport.input($1, generation: $0, capturedAt: $2)
        },
        network: { [server = networkPointer.server] in server.carrier },
        stats: diagnostics.tapStats
    )
    private lazy var networkPointer = NetworkPointer()
    @Published private(set) var networkPointerState = "off"
    private var timer: Timer?
    private var companionDeadlineTimer: Timer?
    private var started = false
    private var observers: [NSObjectProtocol] = []
    private var subscriptions = Set<AnyCancellable>()
    private var tapReady = false
    private var tapStarting = false
    private var currentTarget: UUID?
    private var captureTarget: UUID?
    private var restoringPreferences = true
    private var switchID: UInt8 = 0
    private var companionCapture = false
    private var lastReturnRejection: TimeInterval = 0
    @Published private(set) var reportingProblem = false
    @Published private(set) var pcMonitors: [PCMonitor] = []
    @Published private(set) var companionStatus = "Waiting for Windows companion"
    /// Settings bindings (the corner slider especially) can repeat unchanged values; only
    /// real changes save, and only the Windows edge/display changes resend CONFIG.
    @Published var pcMonitorID = "" {
        didSet { if pcMonitorID != oldValue { _save(configuresCompanion: true) } }
    }

    @Published private(set) var isEnabled = true
    @Published private(set) var connectionState = StatusBarConnectionState.searching
    @Published private(set) var isRemote = false
    @Published private(set) var targetAvailable = false
    @Published private(set) var keyboardMonitoringReady = false
    @Published private(set) var permissionGranted = false
    @Published private(set) var secureInput = false
    /// The app macOS holds responsible for Secure Input, so the menu can name what to close.
    @Published private(set) var secureInputHolder: String?
    @Published private(set) var displays: [EdgeDisplay] = []
    @Published private(set) var lastError: String?
    @Published private(set) var canSwitch = false
    @Published private(set) var captureBlocker: String?
    @Published var edgeEnabled = AppSettings.defaultEdgeSwitchEnabled {
        didSet { if edgeEnabled != oldValue { _save() } }
    }

    @Published var displayID = "" {
        didSet { if displayID != oldValue { _save() } }
    }

    @Published var edge: DisplayEdge = .right {
        didSet { if edge != oldValue { _save(configuresCompanion: true) } }
    }

    @Published var cornerSize = AppSettings.defaultCornerSize {
        didSet { if cornerSize != oldValue { _save() } }
    }

    var recordingShortcut = false {
        didSet { _configure() }
    }

    @Published var locked = false {
        didSet { _configure() }
    }

    @Published var shortcut = ToggleShortcut() {
        didSet { if shortcut != oldValue { _save() } }
    }

    init(lowEnergy: HIDPeripheral, central: HIDCentral) {
        self.lowEnergy = lowEnergy
        self.central = central
        diagnostics = CaptureDiagnostics(stats: lowEnergy.transport.stats)
        let defaults = UserDefaults.standard
        isEnabled = defaults.object(forKey: AppSettings.enabledKey) as? Bool ?? true
        connectionState = isEnabled ? .unavailable : .disabled
        edgeEnabled = defaults.object(forKey: AppSettings.edgeSwitchEnabledKey) as? Bool ?? AppSettings.defaultEdgeSwitchEnabled
        displayID = defaults.string(forKey: AppSettings.edgeDisplayUUIDKey) ?? ""
        edge = DisplayEdge(rawValue: defaults.string(forKey: AppSettings.edgeSideKey) ?? "") ?? .right
        cornerSize = defaults.object(forKey: AppSettings.cornerSizePxKey) as? Double ?? AppSettings.defaultCornerSize
        shortcut = ToggleShortcut(
            keyCode: UInt16(clamping: defaults.object(forKey: AppSettings.toggleKeyCodeKey) as? Int ?? 53),
            modifiers: UInt64(defaults
                .object(forKey: AppSettings.toggleModifiersKey) as? Int ?? Int(CGEventFlags.maskSecondaryFn.rawValue)),
            enabled: defaults.object(forKey: AppSettings.toggleHotkeyEnabledKey) as? Bool ?? true
        )
        pcMonitorID = defaults.string(forKey: "pcMonitorID") ?? ""
        restoringPreferences = false
    }

    func start() {
        guard !started else { return }
        started = true
        diagnostics.started(enabled: isEnabled)
        wireDiagnostics()
        networkPointer.note = { [weak self] in self?.diagnostics.note($0) }
        networkPointer.onStateChange = { [weak self] in self?.networkPointerState = $0 }
        // The stream belongs to the capture target, so its requests go through the same checks.
        networkPointer.onLeave = { [weak self] leave in
            guard let self, let target = captureTarget else { return }
            _returnFromPC(target: target, switchID: leave.switchID, edge: leave.edge, fraction: leave.fraction, via: "network")
        }
        lowEnergy.companion.onReady = { [weak self] _ in self?._configureCompanion(); self?._refresh() }
        lowEnergy.companion.onActivity = { [weak self] id in
            guard let self, started, isEnabled, id == currentTarget else { return }
            let recovering = companionDeadlineTimer == nil
            companionDeadlineTimer?.invalidate(); companionDeadlineTimer = nil
            if recovering { _refresh() } else { scheduleChecks() }
        }
        lowEnergy.companion.onSelection = { [weak self] in self?._refresh() }
        lowEnergy.companion.onDisableRequested = { [weak self] current, acknowledge in
            guard let self else { return }
            if current { setEnabled(false) } else {
                returnLocal(reason: "previous Windows ownership revoked")
                lowEnergy.releaseInputForDisable()
            }
            lowEnergy.afterInputRelease(acknowledge)
        }
        lowEnergy.companion.onLeave = { [weak self] target, switchID, edge, fraction in
            self?._returnFromPC(target: target, switchID: switchID, edge: edge, fraction: fraction, via: "Bluetooth")
        }
        _refreshDisplays()
        lowEnergy.onTargetWillChange = { [weak self] in
            self?.returnLocal(reason: "input target changed")
            self?.clipboard.update(target: nil, remote: false, enabled: false, available: false)
        }
        lowEnergy.setEnabled(isEnabled)
        central.setEnabled(isEnabled)
        lowEnergy.$hostPolicy.combineLatest(lowEnergy.$state)
            .sink { [weak self] _ in
                DispatchQueue.main.async { self?._refresh() }
            }.store(in: &subscriptions)
        lowEnergy.$subscribedCentrals.map { _ in () }
            .merge(with: lowEnergy.companion.objectWillChange.map { _ in () })
            .sink { [weak self] in
                DispatchQueue.main.async { self?._refresh() }
            }.store(in: &subscriptions)
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.returnLocal()
                self?._refreshDisplays()
                self?._refresh()
            }
        })
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.returnLocal(); self?.diagnostics.system(name) }
            })
        }
        for name in [
            NSWorkspace.didWakeNotification,
            NSWorkspace.sessionDidBecomeActiveNotification,
            NSWorkspace.didActivateApplicationNotification
        ] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.diagnostics.system(name); self?._refresh() }
            })
        }
        for name in [NSApplication.didBecomeActiveNotification, UserDefaults.didChangeNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?._refresh() }
            })
        }
        _refresh()
    }

    func stop() {
        started = false
        returnLocal()
        lowEnergy.companion.setAvailability(enabled: false, allowed: lowEnergy.hostPolicy.allowed)
        clipboard.stop()
        timer?.invalidate()
        timer = nil
        companionDeadlineTimer?.invalidate(); companionDeadlineTimer = nil
        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
            DistributedNotificationCenter.default().removeObserver(observer)
        }
        observers.removeAll(); subscriptions.removeAll()
        tap.stop()
        tapReady = false
        tapStarting = false
        lowEnergy.transport.flush()
        diagnostics.stopping()
    }

    func setEnabled(_ value: Bool) {
        guard value != isEnabled else { return }
        if !value { returnLocal(reason: "DeusKVM disabled") }
        isEnabled = value
        diagnostics.note(value ? "DeusKVM enabled" : "DeusKVM disabled")
        UserDefaults.standard.set(value, forKey: AppSettings.enabledKey)
        if !value {
            tap.stop(); tapReady = false; tapStarting = false; keyboardMonitoringReady = false
        }
        central.setEnabled(value)
        if value { lowEnergy.companion.prepareControlRequest() }
        lowEnergy.setEnabled(value)
        if value { lowEnergy.companion.requestControl() }
        _refresh()
    }

    func toggle() {
        guard isEnabled else { return }
        guard permissionGranted else { AccessibilityPermission.request(); return }
        _refresh()
        guard isRemote || canSwitch else { lastError = captureBlocker; return }
        tap.requestToggle()
    }

    func returnLocal(reason: String = "local request") {
        _returnLocal(at: nil, reason: reason)
    }

    private func _returnLocal(at point: CGPoint?, reason: String) {
        let wasRemote = isRemote
        let startedAt = ProcessInfo.processInfo.systemUptime
        // End the session first: the tap forwards straight to the Bluetooth queue, and nothing
        // captured for this session may follow EXIT there.
        tap.forceLocal()
        if isRemote, let target = captureTarget {
            lowEnergy.companion.send(.exit, payload: Data([switchID]), to: target)
        }
        companionCapture = false
        cursor.restore(at: point)
        if wasRemote { tap.observeReturn(since: startedAt) }
        lowEnergy.transport.stopInput()
        if wasRemote { isRemote = false; networkPointer.logSession() }
        captureTarget = nil
        refreshClipboard()
        scheduleChecks()
        if wasRemote {
            let duration = Int((ProcessInfo.processInfo.systemUptime - startedAt) * 1000)
            diagnostics.ended("return: \(reason) localRestoreMs=\(duration)")
        }
    }

    private var geometry: EdgeGeometry? {
        guard let display = displays.first(where: { $0.id == displayID }) else { return nil }
        return EdgeGeometry(
            bounds: display.bounds, edge: edge, otherDisplays: displays.filter { $0.id != displayID }.map(\.bounds),
            cornerSize: max(0, cornerSize)
        )
    }

    private func _refreshDisplays() {
        let next: [EdgeDisplay] = NSScreen.screens.compactMap { screen in
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            let id = number.uint32Value
            guard let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return nil }
            return EdgeDisplay(id: CFUUIDCreateString(nil, uuid) as String, name: screen.localizedName, bounds: CGDisplayBounds(id))
        }
        if next != displays { displays = next }
        if !displays.contains(where: { $0.id == displayID }), let fallback = displays.first {
            displayID = fallback.id
        }
        _configure()
    }

    private func _refresh() {
        guard started else { return }
        defer { scheduleChecks() }
        let permission = AccessibilityPermission.isTrusted
        let secure = IsSecureEventInputEnabled()
        if permissionGranted != permission { permissionGranted = permission }
        if secureInput != secure { secureInputChanged(to: secure) }
        let nextTarget = isEnabled && lowEnergy.state == .poweredOn ? lowEnergy.hostPolicy.target : nil
        if nextTarget != currentTarget {
            companionDeadlineTimer?.invalidate(); companionDeadlineTimer = nil
            currentTarget = nextTarget
        }
        let available = currentTarget != nil
        if targetAvailable != available { targetAvailable = available }
        refreshCaptureReadiness()
        if isEnabled { _refreshCompanion() } else if companionStatus != "DeusKVM is disabled" { companionStatus = "DeusKVM is disabled" }
        networkPointer.refresh(enabled: isEnabled, target: currentTarget, companion: lowEnergy.companion)
        _refreshConnectionState()
        refreshClipboard()
        if isRemote, !canSwitch || captureTarget != currentTarget {
            returnLocal(
                reason: "capture lost: AX=\(permissionGranted) secure=\(secureInput) target=\(captureTarget == currentTarget)"
            )
        }
        recordDiagnostics()
        if isEnabled, permissionGranted, !tapReady, !tapStarting {
            tapStarting = tap.start()
        }
        _configure()
    }

    private func refreshClipboard() {
        clipboard.update(
            target: canSwitch ? lowEnergy.hostPolicy.target : nil,
            remote: isRemote,
            enabled: UserDefaults.standard.object(forKey: AppSettings.clipboardEnabledKey) as? Bool ?? true,
            available: !secureInput
        )
    }

    private func _configure() {
        tap.configure(TapConfiguration(
            targetAvailable: canSwitch,
            edgeEnabled: edgeEnabled && !locked && !recordingShortcut,
            geometry: geometry,
            shortcut: recordingShortcut ? ToggleShortcut(enabled: false) : shortcut
        ))
    }

    private func _save(configuresCompanion: Bool = false) {
        guard !restoringPreferences else { return }
        if isRemote { returnLocal() }
        let defaults = UserDefaults.standard
        defaults.set(pcMonitorID, forKey: "pcMonitorID")
        if configuresCompanion { _configureCompanion() }
        defaults.set(edgeEnabled, forKey: AppSettings.edgeSwitchEnabledKey)
        defaults.set(displayID, forKey: AppSettings.edgeDisplayUUIDKey)
        defaults.set(edge.rawValue, forKey: AppSettings.edgeSideKey)
        defaults.set(cornerSize, forKey: AppSettings.cornerSizePxKey)
        defaults.set(Int(shortcut.keyCode), forKey: AppSettings.toggleKeyCodeKey)
        defaults.set(Int(shortcut.modifiers), forKey: AppSettings.toggleModifiersKey)
        defaults.set(shortcut.enabled, forKey: AppSettings.toggleHotkeyEnabledKey)
        _configure()
    }

    private func _refreshCompanion() {
        let companion = lowEnergy.companion
        let fresh = currentTarget.flatMap { companion.lastSeen[$0] }.map {
            ProcessInfo.processInfo.systemUptime - $0 < 10
        } ?? false
        let monitors = currentTarget.flatMap { companion.monitors[$0] } ?? []
        if monitors != pcMonitors { pcMonitors = monitors; _configureCompanion() }
        let ready = currentTarget.map { companion.ready.contains($0) } ?? false
        let blind = currentTarget.flatMap { companion.blind[$0] } ?? 4
        let status = captureBlocker ?? (ready && fresh ? (blind == 0 ? "Windows edge return ready" :
                "Windows edge return unavailable — use the hotkey") : "Waiting for Windows companion")
        if companionStatus != status { companionStatus = status }
        if isRemote, companionCapture, !ready || !fresh {
            returnLocal(reason: ready ? "companion heartbeat expired" : "companion disconnected")
        }
    }

    private func _configureCompanion() {
        guard let target = currentTarget, lowEnergy.companion.allowsControl(target) else { return }
        let companion = lowEnergy.companion
        let monitors = companion.monitors[target] ?? []
        if let monitor = monitors.first(where: { $0.id == pcMonitorID }) ??
            (pcMonitorID.isEmpty ? monitors.first(where: \.primary) ?? monitors.first : nil)
        {
            companion.sendJSON(PCConfiguration(edge: edge.opposite.wireValue, monitor: monitor.id), type: .config, to: target)
        }
        if isRemote, captureTarget == target, companion.supportsResume(target) {
            // HELLO/SCREEN_INFO can arrive after the user has already crossed.
            // Reassert ownership without another entry warp; EXIT remains ordered
            // after this message on the control stream if the user returns locally.
            companionCapture = true
            companion.send(.resume, payload: Data([switchID, edge.opposite.wireValue]), to: target)
        }
    }

    private func _receive(_ event: TapOutput) {
        switch event {
        case let .keyboardMonitoring(token, ready):
            guard isEnabled, tap.isCurrentRun(token) else { return }
            keyboardMonitoringReady = ready
            _refresh()
        case let .installed(token, success):
            guard isEnabled, tap.isCurrentRun(token) else { return }
            tapStarting = false
            tapReady = success
            if success { _refresh() } else {
                lastError = L10n.DirectInput.captureFailedString
                scheduleChecks()
            }
        case let .begin(generation, origin, fromEdge):
            let startedAt = ProcessInfo.processInfo.systemUptime
            _refresh()
            guard tap.isCurrent(generation) else { return }
            guard canSwitch, !IsSecureEventInputEnabled(),
                  let geometry else { returnLocal(); return }
            guard cursor.hide(at: geometry.parkingPoint, returningTo: origin) else {
                lastError = L10n.Layout.cursorFailedString
                returnLocal()
                return
            }
            lastError = nil
            captureTarget = currentTarget
            isRemote = true
            scheduleChecks()
            refreshClipboard()
            let setupMs = Int((ProcessInfo.processInfo.systemUptime - startedAt) * 1000)
            beginDiagnostics(setupMs: setupMs)
            switchID &+= 1
            companionCapture = currentTarget.map { lowEnergy.companion.ready.contains($0) } ?? false
            if let target = currentTarget {
                let fraction = geometry.fraction(at: origin)
                let entry = CompanionEntry.packet(
                    id: switchID, edge: edge.opposite.wireValue, fraction: fraction,
                    fromEdge: fromEdge, supportsCenter: lowEnergy.companion.supportsCenter(target)
                )
                lowEnergy.companion.send(
                    CompanionProtocol.Message(rawValue: entry.type)!, payload: entry.payload, to: target
                )
            }
            // Input captured since the crossing waits on the Bluetooth queue until this point,
            // so it follows ENTER and never forwards for a refused crossing.
            lowEnergy.transport.startInput(generation: generation, network: networkPointer.server)
        case let .end(generation):
            if tap.isCurrent(generation) { returnLocal(reason: "toggle hotkey") }
        case .disabled:
            returnLocal(reason: "event tap disabled")
            tap.reenable()
        case let .note(message):
            diagnostics.note(message)
        }
    }
}

extension EdgeSwitchCoordinator {
    private func _refreshConnectionState() {
        let companion = lowEnergy.companion
        let state = StatusBarConnectionState.resolve(
            enabled: isEnabled, bluetooth: lowEnergy.state, secureInput: secureInput,
            link: .init(
                target: lowEnergy.hostPolicy.target,
                subscribers: Set(lowEnergy.subscribedCentrals.keys).union(companion.subscribedHosts),
                companions: companion.ready, lastSeen: companion.lastSeen,
                captureReady: canSwitch, waiting: currentTarget.map { companion.isWaiting($0) } ?? false
            ), now: ProcessInfo.processInfo.systemUptime
        )
        if connectionState != state { connectionState = state }
        let holder = secureInput ? SecureInputHolder.name() : nil
        if secureInputHolder != holder { secureInputHolder = holder }
    }

    private func scheduleChecks() {
        guard started else { return }
        let interval = CoordinatorSchedule.pollInterval(
            enabled: isEnabled, remote: isRemote, permission: permissionGranted, secure: secureInput, tapReady: tapReady
        )
        if let interval {
            if timer?.timeInterval != interval {
                timer?.invalidate()
                timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated { self?._refresh() }
                }
                timer?.tolerance = interval / 5
            }
        } else { timer?.invalidate(); timer = nil }
        guard companionDeadlineTimer == nil,
              let seen = currentTarget.flatMap({ lowEnergy.companion.lastSeen[$0] }),
              let deadline = CoordinatorSchedule.linkDeadline(lastSeen: seen, now: ProcessInfo.processInfo.systemUptime)
        else { return }
        companionDeadlineTimer = Timer.scheduledTimer(
            withTimeInterval: max(0.001, deadline - ProcessInfo.processInfo.systemUptime), repeats: false
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.companionDeadlineTimer = nil
                self?._refresh()
            }
        }
    }

    /// Each refresh: link changes; while remote, a diagnostic ping and the per-second sample.
    private func recordDiagnostics() {
        if isRemote {
            if let captureTarget { lowEnergy.companion.ping(captureTarget) }
            let companion = lowEnergy.companion
            diagnostics.sample(parkingPoint: cursor.parkingPoint, blind: companion.blind.values.sorted(), pings: companion.takePings())
        }
        let companion = currentTarget.map { id in
            lowEnergy.companion.ready.contains(id) && lowEnergy.companion.lastSeen[id].map {
                ProcessInfo.processInfo.systemUptime - $0 < 10
            } == true
        } ?? false
        diagnostics.link(bluetooth: lowEnergy.state, target: targetAvailable, companion: companion)
    }

    /// An edge-return request from the PC, over the network stream (normally) or Bluetooth (its
    /// fallback). Whichever arrives first returns; the rest no longer match and are ignored.
    private func _returnFromPC(target: UUID, switchID: UInt8, edge: UInt8, fraction: UInt16, via: String) {
        guard isRemote, !locked, !recordingShortcut, target == captureTarget, switchID == self.switchID,
              edge == self.edge.opposite.wireValue, let geometry else { return }
        guard tap.acceptCompanionReturn() else {
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastReturnRejection >= 1 {
                lastReturnRejection = now
                diagnostics.note("Windows edge deferred: \(tap.returnDiagnostic)")
            }
            return
        }
        _returnLocal(at: geometry.entryPoint(fraction: fraction), reason: "Windows edge via \(via)")
    }

    /// Secure Input hides key releases from the tap; once it ends, held state is re-read.
    private func secureInputChanged(to secure: Bool) {
        if !secure { tap.resyncHeldInput("Secure Input ended") }
        secureInput = secure
    }

    private func wireDiagnostics() {
        lowEnergy.companion.onReport = { [weak self] _, token, text in self?.diagnostics.receiveWindowsReport(token: token, text: text) }
        observeUnlock()
    }

    /// Unlocking is the usual end of a ⌃⌘Q lock, whose Q release the tap never saw.
    private func observeUnlock() {
        observers.append(DistributedNotificationCenter.default().addObserver(
            forName: .init("com.apple.screenIsUnlocked"), object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.tap.resyncHeldInput("screen unlocked") }
        })
    }

    private func beginDiagnostics(setupMs: Int) {
        lowEnergy.companion.cancelPing()
        _ = lowEnergy.companion.takePings()
        diagnostics.began("remote capture began setupMs=\(setupMs)")
    }

    /// Writes what the app knows right now, the recent per-second samples, the Windows
    /// companion's report and bluetoothd's recent link-parameter lines, then calls `completion`
    /// with the file it wrote. Used for lag, early returns or anything else that misbehaves.
    func reportProblem(completion: @escaping @MainActor @Sendable (URL) -> Void) {
        guard !reportingProblem else { return }
        reportingProblem = true
        let now = ProcessInfo.processInfo.systemUptime
        let companion = lowEnergy.companion
        let seen = currentTarget.flatMap { companion.lastSeen[$0] }.map { String(Int((now - $0) * 1000)) } ?? "none"
        let ping = companion.pingOutstandingAge(now: now).map { String(Int($0 * 1000)) } ?? "none"
        let state: [String] = [
            "enabled=\(isEnabled)", "remote=\(isRemote)", "connection=\(connectionState)", "canSwitch=\(canSwitch)",
            "secureInput=\(secureInput)", "target=\(currentTarget != nil)", "captureTarget=\(captureTarget != nil)",
            "companionCapture=\(companionCapture)"
        ]
        let link: [String] = [
            "status=\(companionStatus)", "ready=\(currentTarget.map { companion.ready.contains($0) } ?? false)",
            "lastSeenAgeMs=\(seen)", "pingOutstandingMs=\(ping)", "blind=\(companion.blind.values.sorted())"
        ]
        let context = [
            "=== problem report === \(CaptureDiagnostics.version) uptimeS=\(Int(now))",
            "state: " + state.joined(separator: " "),
            "companion: " + link.joined(separator: " "),
            "bluetooth: state=\(lowEnergy.state.rawValue) advertising=\(lowEnergy.isAdvertising) " +
                "subscribed=\(lowEnergy.subscribedCentrals.count) connected=\(lowEnergy.connectedCentrals.count)"
        ]
        diagnostics.writeProblemReport(
            context: context, transport: lowEnergy.transport, companion: companion, target: currentTarget
        ) { [weak self] url in
            self?.reportingProblem = false
            completion(url)
        }
    }

    private func refreshCaptureReadiness() {
        let companion = lowEnergy.companion
        let state = CaptureReadiness(
            enabled: isEnabled, target: targetAvailable, accessibility: permissionGranted,
            keyboardMonitoring: keyboardMonitoringReady, tap: tapReady, display: geometry != nil,
            secureInput: secureInput, granted: currentTarget.map { companion.allowsControl($0) } ?? false,
            waiting: currentTarget.map { companion.isWaiting($0) } ?? false
        )
        if canSwitch != state.canSwitch { canSwitch = state.canSwitch }
        if captureBlocker != state.blocker { captureBlocker = state.blocker }
    }
}
