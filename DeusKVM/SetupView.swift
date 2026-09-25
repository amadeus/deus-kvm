import AppKit
import CoreBluetooth
import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var lowEnergy: HIDPeripheral
    @EnvironmentObject private var central: HIDCentral
    @EnvironmentObject private var names: DeviceNameStore
    @AppStorage(AppSettings.developerModeKey) private var developerMode = false
    @StateObject private var login = LaunchAtLoginController()
    @State private var showReset = false
    @AppStorage(AppSettings.hasSeenWelcomeKey) private var hasSeenWelcome = false
    @State private var selectedInfo: DeviceEntry?
    @EnvironmentObject private var coordinator: EdgeSwitchCoordinator

    @Environment(\.hid) private var hid

    var body: some View {
        NavigationStack {
            form
                .settingsFormStyle()
                .navigationTitle(L10n.Tab.setup)
        }
    }

    private var form: some View {
        Form {
            PermissionsSection()
            devicesSection
            startupSection
            advancedSection
            if developerMode { diagnosticsSection }
            if hid.activeError != nil || (hid.isActive && coordinator.lastError != nil) {
                Section(header: Text(L10n.Section.lastError)) {
                    if let lastError = hid.activeError {
                        Text(verbatim: lastError).foregroundColor(.red).font(.caption)
                    }
                    if hid.isActive, let lastError = coordinator.lastError {
                        Text(verbatim: lastError).foregroundColor(.red).font(.caption)
                    }
                }
            }
        }
        .sheet(item: $selectedInfo) { DeviceInfoView(entry: $0) }
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

    private var devicesSection: some View {
        Section {
            if lowEnergy.state != .poweredOn {
                row(L10n.Status.bluetooth, Text(lowEnergy.state.localizedLabel))
                Button("Open Bluetooth Settings", action: _openBluetoothSettings)
            }
            if _connectedDevices.isEmpty {
                Text(L10n.SettingsOrganization.noDevices).foregroundStyle(.secondary)
            }
            ForEach(_connectedDevices) { connectedDeviceRow($0) }
        } header: {
            Text(L10n.SettingsOrganization.devices)
        } footer: {
            if _connectedDevices.isEmpty {
                Text("Pair through the Windows companion, then turn on Enable control. To replace a PC, turn off its control first.")
            } else {
                Text("Saved per device. When several enabled devices are ready, choose Use device to select the PC to control.")
            }
        }
        .onAppear(perform: _seedAliasesFromScan)
        .onChange(of: lowEnergy.connectedCentrals) { _ in _seedAliasesFromScan() }
        .onChange(of: central.discovered) { _ in _seedAliasesFromScan() }
    }

    private var startupSection: some View {
        Section(L10n.SettingsOrganization.startup) {
            Toggle("Launch DeusKVM at login", isOn: Binding(
                get: { login.enabled }, set: { value in Task { await login.setEnabled(value) } }
            ))
            .disabled(login.busy)
            if login.needsApproval {
                Button("Allow in Login Items…") { login.openSettings() }
            }
        }
    }

    private var advancedSection: some View {
        Section(header: Text(L10n.Settings.advanced)) {
            Toggle(L10n.Settings.developerMode, isOn: $developerMode)
            Button(role: .destructive) { showReset = true } label: {
                Label(L10n.Settings.reset, systemImage: "trash")
            }
        }
    }

    private var diagnosticsSection: some View {
        Section(L10n.SettingsOrganization.diagnostics) {
            row(L10n.Status.bluetooth, Text(lowEnergy.state.localizedLabel))
            row(L10n.Status.advertising, Text(lowEnergy.isAdvertising ? L10n.Value.yes : L10n.Value.no))
            row(L10n.Status.hidService, Text(lowEnergy.isHIDServiceAdded ? L10n.Status.hidServiceAdded : L10n.Value.none))
            row(L10n.Status.subscribedCentrals, Text(lowEnergy.subscribedCentrals.count, format: .number))
            row(L10n.Status.connectedPeripherals, Text(central.connected.count, format: .number))
            row(L10n.Status.hostLEDs, Text(verbatim: lowEnergy.keyboardLEDs.localizedLabel))
        }
    }

    /// hosts that connected to us (peripheral role); subscribed ones can receive input
    private var _connectedDevices: [DeviceEntry] {
        lowEnergy.connectedCentrals.union(lowEnergy.hostPolicy.allowed)
            .map { uuid in
                let alias = names.name(for: uuid)
                let subscribed = lowEnergy.subscribedCentrals.keys.contains(uuid)
                return DeviceEntry(
                    id: uuid,
                    name: alias ?? "",
                    isNamed: alias != nil,
                    rssi: 0,
                    advertisedServices: [],
                    companyID: nil,
                    txPower: nil,
                    isConnectable: nil,
                    isHostConnected: lowEnergy.connectedCentrals.contains(uuid),
                    isCentralConnected: false,
                    isConnecting: false,
                    isSubscribed: subscribed,
                    isActive: lowEnergy.hostPolicy.target == uuid
                )
            }
            .sorted { $0.id.uuidString < $1.id.uuidString }
    }

    private func _seedAliasesFromScan() {
        for uuid in lowEnergy.connectedCentrals where names.name(for: uuid) == nil {
            guard let scanned = central.discovered.first(where: { $0.id == uuid && $0.isNamed })?.name else { continue }
            names.rememberName(scanned, for: uuid)
        }
    }

    private func _openBluetoothSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.BluetoothSettings") else { return }
        NSWorkspace.shared.open(url)
    }

    private func connectedDeviceRow(_ entry: DeviceEntry) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    Text(verbatim: entry.displayName).lineLimit(1)
                    Spacer()
                    Button { selectedInfo = entry } label: { Image(systemName: "info.circle") }
                        .buttonStyle(.borderless)
                        .accessibilityLabel(L10n.DeviceInfo.info)
                }
                Text(_deviceStatus(entry)).font(.caption).foregroundColor(.secondary)
                if entry.isActive {
                    Text(verbatim: coordinator.companionStatus).font(.caption).foregroundStyle(.secondary)
                }
                if developerMode {
                    Text(verbatim: entry.id.uuidString)
                        .font(.caption2).foregroundColor(.secondary).lineLimit(1).truncationMode(.middle)
                }
            }
            Toggle("Enable control", isOn: Binding(
                get: { lowEnergy.hostPolicy.allowed.contains(entry.id) },
                set: { lowEnergy.setAllowed(entry.id, $0) }
            ))
            .toggleStyle(.switch)
            .accessibilityLabel(Text("Enable control: \(entry.displayName)"))
            if lowEnergy.hostPolicy.allowed.contains(entry.id), lowEnergy.hostPolicy.ready.contains(entry.id), !entry.isActive {
                Button("Use device") { lowEnergy.selectHost(entry.id) }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func _deviceStatus(_ entry: DeviceEntry) -> String {
        if entry.isActive { return "Current input device" }
        if !entry.isHostConnected { return "Disconnected" }
        if lowEnergy.hostPolicy.ready.contains(entry.id) { return "Ready" }
        return "Waiting for keyboard and mouse"
    }

    private func row(_ title: LocalizedStringKey, _ value: Text) -> some View {
        HStack {
            Text(title)
            Spacer()
            value.foregroundColor(.secondary)
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

private extension CBManagerState {
    var localizedLabel: LocalizedStringKey {
        switch self {
        case .unknown: return L10n.BluetoothState.unknown
        case .resetting: return L10n.BluetoothState.resetting
        case .unsupported: return L10n.BluetoothState.unsupported
        case .unauthorized: return L10n.BluetoothState.unauthorized
        case .poweredOff: return L10n.BluetoothState.poweredOff
        case .poweredOn: return L10n.BluetoothState.poweredOn
        @unknown default: return L10n.BluetoothState.unavailable
        }
    }
}

private extension KeyboardLEDs {
    var localizedLabel: String {
        var parts: [String] = []
        if contains(.numLock) {
            parts.append(L10n.KeyboardLED.numLock)
        }
        if contains(.capsLock) {
            parts.append(L10n.KeyboardLED.capsLock)
        }
        if contains(.scrollLock) {
            parts.append(L10n.KeyboardLED.scrollLock)
        }
        return parts.isEmpty ? L10n.Value.noneString : ListFormatter.localizedString(byJoining: parts)
    }
}
