import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var lowEnergy: HIDPeripheral
    @EnvironmentObject private var coordinator: EdgeSwitchCoordinator
    @StateObject private var login = LaunchAtLoginController()
    @EnvironmentObject private var names: DeviceNameStore
    @AppStorage(AppSettings.invertVerticalScrollKey) private var invertVerticalScroll = false
    @AppStorage(AppSettings.invertHorizontalScrollKey) private var invertHorizontalScroll = false
    @AppStorage(AppSettings.clipboardEnabledKey) private var clipboardEnabled = true
    @AppStorage(AppSettings.windowsPointerSpeedKey) private var storedPointerSpeed = PointerMotionScaler.defaultSpeed
    @AppStorage(AppSettings.directPointerKey) private var directPointer = false
    @State private var showReset = false
    @AppStorage(AppSettings.developerModeKey) private var developerMode = false
    @AppStorage(AppSettings.useServiceChangedKey) private var forceServiceChanged = true
    @AppStorage(AppSettings.hasSeenWelcomeKey) private var hasSeenWelcome = false

    var body: some View {
        NavigationStack {
            form
                .settingsFormStyle()
                .navigationTitle(L10n.Tab.settings)
        }
    }

    private var form: some View {
        Form {
            Section {
                Button(coordinator.isEnabled ? "Disable DeusKVM" : "Enable DeusKVM") {
                    coordinator.setEnabled(!coordinator.isEnabled)
                }
                Toggle("Launch DeusKVM at login", isOn: Binding(
                    get: { login.enabled }, set: { value in Task { await login.setEnabled(value) } }
                ))
                .disabled(login.busy)
                if login.needsApproval {
                    Button("Allow in Login Items…") { login.openSettings() }
                }
            } footer: {
                Text(coordinator
                    .isEnabled ? "DeusKVM is enabled." : "Disabled. Advertising, input forwarding and clipboard sharing are paused.")
            }
            Section {
                Toggle("Share text clipboard with Windows", isOn: $clipboardEnabled)
                    .toggleStyle(.switch)
            } footer: {
                Text(
                    "Shares plain text up to 64 KiB. Skips marked private items and pauses while locked or signed out."
                )
            }
            Section {
                Toggle("Direct Windows pointer (experimental)", isOn: $directPointer)
                    .onChange(of: directPointer) { _ in coordinator.returnLocal(reason: "pointer mode changed") }
            } footer: {
                Text(
                    "Bypasses Windows acceleration. Requires updated companion. Turn off for elevated apps and sign-in."
                )
            }
            pointerSpeedSection
            Section("Windows scrolling") {
                Toggle("Invert vertical scrolling", isOn: $invertVerticalScroll)
                    .toggleStyle(.switch)
                Toggle("Invert horizontal scrolling", isOn: $invertHorizontalScroll)
                    .toggleStyle(.switch)
            }
            Section(footer: Text(L10n.Settings.forceServiceChangedHint)) {
                Toggle(L10n.Settings.forceServiceChanged, isOn: $forceServiceChanged)
                    .onChange(of: forceServiceChanged) {
                        if $0 {
                            lowEnergy.scheduleServiceChanged()
                        }
                    }
            }
            Section(header: Text(L10n.Settings.advanced)) {
                Toggle(L10n.Settings.developerMode, isOn: $developerMode)
                Button(role: .destructive) { showReset = true } label: {
                    Label(L10n.Settings.reset, systemImage: "trash")
                }
            }
        }
        .onAppear { login.refresh() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in login.refresh() }
        .alert("Could not change login startup", isPresented: Binding(
            get: { login.error != nil }, set: { if !$0 { login.error = nil } }
        )) {
            Button("OK") { login.error = nil }
        } message: { Text(login.error ?? "") }
        .confirmationDialog(L10n.Settings.resetConfirm, isPresented: $showReset, titleVisibility: .visible) {
            Button(L10n.Settings.reset, role: .destructive) {
                Task { if await login.setEnabled(false) { _resetAll() } }
            }
        }
    }

    private var pointerSpeed: Binding<Double> {
        Binding(
            get: { PointerMotionScaler.validatedSpeed(storedPointerSpeed) },
            set: { storedPointerSpeed = PointerMotionScaler.validatedSpeed($0) }
        )
    }

    private var pointerSpeedSection: some View {
        Section {
            HStack {
                Text("Windows pointer speed")
                Spacer()
                Text("\(pointerSpeed.wrappedValue, format: .number.precision(.fractionLength(2)))×")
                    .monospacedDigit()
                Button("Reset") { storedPointerSpeed = PointerMotionScaler.defaultSpeed }
                    .disabled(storedPointerSpeed == PointerMotionScaler.defaultSpeed)
                    .accessibilityLabel("Reset Windows pointer speed")
            }
            Slider(value: pointerSpeed, in: PointerMotionScaler.speedRange, step: 0.05)
                .accessibilityLabel("Windows pointer speed")
                .accessibilityValue(Text("\(pointerSpeed.wrappedValue, format: .number.precision(.fractionLength(2))) times"))
        } footer: {
            Text("Adjusts movement sent from this Mac to Windows. Your Mac pointer and other Windows mice keep their settings.")
        }
    }

    private func _resetAll() {
        lowEnergy.clearAllowedHosts()
        names.clear()
        if let bundleID = Bundle.main.bundleIdentifier {
            UserDefaults.standard.removePersistentDomain(forName: bundleID)
        }
        hasSeenWelcome = false
        coordinator.setEnabled(true)
    }
}

#if DEBUG
    #Preview {
        let peripheral = HIDPeripheral()
        let central = HIDCentral()
        SettingsView()
            .environmentObject(peripheral)
            .environmentObject(central)
            .environmentObject(EdgeSwitchCoordinator(lowEnergy: peripheral, central: central))
            .environmentObject(DeviceNameStore())
    }
#endif
