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
    let directInput = DirectInputController()
    private lazy var clipboard = MacClipboardSync(service: lowEnergy.companion)
    private let lowEnergy: HIDPeripheral
    private let central: HIDCentral
    private let cursor = CursorConcealer()
    private let diagnostics = CaptureDiagnostics()
    private lazy var tap = InputTap { [weak self] event in self?._receive(event) }
    private var timer: Timer?
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
    @Published private(set) var pcMonitors: [PCMonitor] = []
    @Published private(set) var companionStatus = "Waiting for Windows companion"
    @Published var pcMonitorID = "" {
        didSet { _save() }
    }

    @Published private(set) var isEnabled = true
    @Published private(set) var connectionState = StatusBarConnectionState.searching
    @Published private(set) var isRemote = false
    @Published private(set) var targetAvailable = false
    @Published private(set) var keyboardMonitoringReady = false
    @Published private(set) var permissionGranted = false
    @Published private(set) var secureInput = false
    @Published private(set) var displays: [EdgeDisplay] = []
    @Published private(set) var lastError: String?
    @Published private(set) var canSwitch = false
    @Published private(set) var captureBlocker: String?
    @Published var edgeEnabled = false {
        didSet { _save() }
    }

    @Published var displayID = "" {
        didSet { _save() }
    }

    @Published var edge: DisplayEdge = .right {
        didSet { _save() }
    }

    @Published var cornerSize = AppSettings.defaultCornerSize {
        didSet { _save() }
    }

    var recordingShortcut = false {
        didSet { _configure() }
    }

    @Published var locked = false {
        didSet { _configure() }
    }

    @Published var shortcut = ToggleShortcut() {
        didSet { _save() }
    }

    init(lowEnergy: HIDPeripheral, central: HIDCentral) {
        self.lowEnergy = lowEnergy
        self.central = central
        let defaults = UserDefaults.standard
        isEnabled = defaults.object(forKey: AppSettings.enabledKey) as? Bool ?? true
        connectionState = isEnabled ? .unavailable : .disabled
        edgeEnabled = defaults.bool(forKey: AppSettings.edgeSwitchEnabledKey)
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
        guard timer == nil else { return }
        lowEnergy.companion.onReady = { [weak self] _ in self?._configureCompanion() }
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
            self?._returnFromPC(target: target, switchID: switchID, edge: edge, fraction: fraction)
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
                DispatchQueue.main.async { self?._refreshConnectionState() }
            }.store(in: &subscriptions)
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.returnLocal()
                self?._refreshDisplays()
            }
        })
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.returnLocal() }
            })
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?._refresh() }
        }
        timer?.tolerance = 0.1
        _refresh()
    }

    func stop() {
        returnLocal()
        lowEnergy.companion.setAvailability(enabled: false, allowed: lowEnergy.hostPolicy.allowed)
        clipboard.stop()
        timer?.invalidate()
        timer = nil
        tap.stop()
        tapReady = false
        tapStarting = false
    }

    func setEnabled(_ value: Bool) {
        guard value != isEnabled else { return }
        if !value { returnLocal(reason: "DeusKVM disabled") }
        isEnabled = value
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
        if isRemote, let target = captureTarget {
            lowEnergy.companion.send(.exit, payload: Data([switchID]), to: target)
        }
        companionCapture = false
        tap.forceLocal()
        cursor.restore(at: point)
        if wasRemote { tap.observeReturn(since: startedAt) }
        directInput.stop()
        isRemote = false
        captureTarget = nil
        refreshClipboard()
        if wasRemote {
            let duration = Int((ProcessInfo.processInfo.systemUptime - startedAt) * 1000)
            diagnostics.transition("return: \(reason) localRestoreMs=\(duration)")
        }
    }

    var shortcutLabel: String {
        guard shortcut.enabled else { return L10n.Layout.disabledString }
        let flags = CGEventFlags(rawValue: shortcut.modifiers)
        var label = ""
        if flags.contains(.maskControl) { label += "⌃" }
        if flags.contains(.maskAlternate) { label += "⌥" }
        if flags.contains(.maskShift) { label += "⇧" }
        if flags.contains(.maskCommand) { label += "⌘" }
        if flags.contains(.maskSecondaryFn) { label += "fn " }
        return label + (shortcut.keyCode == 53 ? "⎋" : L10n.Layout.keyCodeString(shortcut.keyCode))
    }

    private var geometry: EdgeGeometry? {
        guard let display = displays.first(where: { $0.id == displayID }) else { return nil }
        return EdgeGeometry(
            bounds: display.bounds, edge: edge, otherDisplays: displays.filter { $0.id != displayID }.map(\.bounds),
            cornerSize: max(0, cornerSize)
        )
    }

    private func _refreshDisplays() {
        displays = NSScreen.screens.compactMap { screen in
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            let id = number.uint32Value
            guard let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return nil }
            return EdgeDisplay(id: CFUUIDCreateString(nil, uuid) as String, name: screen.localizedName, bounds: CGDisplayBounds(id))
        }
        if !displays.contains(where: { $0.id == displayID }), let fallback = displays.first {
            displayID = fallback.id
        }
        _configure()
    }

    private func _refresh() {
        let permission = AccessibilityPermission.isTrusted
        let secure = IsSecureEventInputEnabled()
        if permissionGranted != permission { permissionGranted = permission }
        if secureInput != secure { secureInput = secure }
        currentTarget = isEnabled && lowEnergy.state == .poweredOn ? lowEnergy.hostPolicy.target : nil
        let available = currentTarget != nil
        if targetAvailable != available { targetAvailable = available }
        refreshCaptureReadiness()
        if isEnabled { _refreshCompanion() } else { companionStatus = "DeusKVM is disabled" }
        _refreshConnectionState()
        refreshClipboard()
        if isRemote, !canSwitch || captureTarget != currentTarget {
            returnLocal(
                reason: "capture lost: AX=\(permissionGranted) secure=\(secureInput) target=\(captureTarget == currentTarget)"
            )
        }
        if isRemote { diagnostics.sample(parkingPoint: cursor.parkingPoint, companion: lowEnergy.companion.diagnosticState) }
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

    private func _refreshConnectionState() {
        let companion = lowEnergy.companion
        let state = StatusBarConnectionState.resolve(
            enabled: isEnabled, bluetooth: lowEnergy.state,
            link: .init(
                target: lowEnergy.hostPolicy.target,
                subscribers: Set(lowEnergy.subscribedCentrals.keys).union(companion.subscribedHosts),
                companions: companion.ready, lastSeen: companion.lastSeen,
                captureReady: canSwitch, waiting: currentTarget.map { companion.isWaiting($0) } ?? false
            ), now: ProcessInfo.processInfo.systemUptime
        )
        if connectionState != state { connectionState = state }
    }

    private func _configure() {
        tap.configure(TapConfiguration(
            targetAvailable: canSwitch,
            edgeEnabled: edgeEnabled && !locked && !recordingShortcut,
            geometry: geometry,
            shortcut: recordingShortcut ? ToggleShortcut(enabled: false) : shortcut
        ))
    }

    private func _save() {
        guard !restoringPreferences else { return }
        if isRemote { returnLocal() }
        let defaults = UserDefaults.standard
        defaults.set(pcMonitorID, forKey: "pcMonitorID")
        _configureCompanion()
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

    private func _returnFromPC(target: UUID, switchID: UInt8, edge: UInt8, fraction: UInt16) {
        guard isRemote, !locked, !recordingShortcut, target == captureTarget, switchID == self.switchID,
              edge == self.edge.opposite.wireValue, let geometry else { return }
        guard tap.acceptCompanionReturn() else {
            let now = ProcessInfo.processInfo.systemUptime
            if now - lastReturnRejection >= 1 {
                lastReturnRejection = now
                diagnostics.transition("Windows edge deferred: \(tap.returnDiagnostic)")
            }
            return
        }
        _returnLocal(at: geometry.entryPoint(fraction: fraction), reason: "Windows edge")
    }

    private func _receive(_ event: TapOutput) {
        switch event {
        case let .keyboardMonitoring(token, ready):
            guard isEnabled, tap.isCurrentRun(token) else { return }
            keyboardMonitoringReady = ready
        case let .installed(token, success):
            guard isEnabled, tap.isCurrentRun(token) else { return }
            tapStarting = false
            tapReady = success
            if !success { lastError = L10n.DirectInput.captureFailedString }
        case let .begin(generation, origin, fromEdge):
            let startedAt = ProcessInfo.processInfo.systemUptime
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
            directInput.start(HIDInput.make(lowEnergy: lowEnergy, central: central))
            isRemote = true
            refreshClipboard()
            let setupMs = Int((ProcessInfo.processInfo.systemUptime - startedAt) * 1000)
            diagnostics.transition("remote capture began setupMs=\(setupMs)")
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
        case let .input(generation, input, capturedAt):
            guard tap.isCurrent(generation), isRemote else { return }
            diagnostics.input(capturedAt: capturedAt)
            directInput.handle(input)
        case let .end(generation):
            if tap.isCurrent(generation) { returnLocal(reason: "toggle hotkey") }
        case .disabled:
            returnLocal(reason: "event tap disabled")
            tap.reenable()
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
