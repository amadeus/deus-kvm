import AppKit
import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var link: PCLink
    @EnvironmentObject private var names: DeviceNameStore
    @EnvironmentObject private var companion: CompanionService
    @AppStorage(AppSettings.developerModeKey) private var developerMode = false
    @StateObject private var login = LaunchAtLoginController()
    @State private var showReset = false
    @AppStorage(AppSettings.hasSeenWelcomeKey) private var hasSeenWelcome = false
    @State private var selectedInfoID: UUID?
    @EnvironmentObject private var coordinator: EdgeSwitchCoordinator

    private var linkError: String? {
        link.lastError
    }

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
            if developerMode {
                diagnosticsSection
            }
            if linkError != nil || (link.listening && coordinator.lastError != nil) {
                Section(header: Text(L10n.Section.lastError)) {
                    if let lastError = linkError {
                        Text(verbatim: lastError).foregroundColor(.red).font(.caption)
                    }
                    if link.listening, let lastError = coordinator.lastError {
                        Text(verbatim: lastError).foregroundColor(.red).font(.caption)
                    }
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

    private var devicesSection: some View {
        Section {
            if !link.listening {
                row("Network", Text(verbatim: "Not listening for PCs"))
            }
            if _connectedDevices.isEmpty {
                Text(L10n.SettingsOrganization.noDevices).foregroundStyle(.secondary)
            }
            ForEach(_connectedDevices) { connectedDeviceRow($0) }
        } header: {
            Text(L10n.SettingsOrganization.devices)
        } footer: {
            if _connectedDevices.isEmpty {
                Text(
                    "On the PC, choose Add Mac… in the DeusKVM companion and pick this Mac; it appears here once it " +
                        "connects. Then turn on Enable control."
                )
            } else {
                Text("Saved per device. When several enabled devices are ready, choose Use device to select the PC to control.")
            }
        }
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
            row(
                "Control link",
                Text(verbatim: link.listening ? "Listening on port \(link.server.port.map(String.init) ?? "?")" : "Not listening")
            )
            row("This Mac's id", Text(verbatim: link.id?.uuidString.lowercased() ?? "none"))
            row("Connected PCs", Text(companion.subscribedHosts.count, format: .number))
            row("Input stream", Text(verbatim: coordinator.networkPointerState))
        }
    }

    /// Offer hosts after a PC companion handshake; retain saved control choices while offline.
    private var _connectedDevices: [DeviceEntry] {
        companion.ready.union(companion.subscribedHosts).union(link.hostPolicy.allowed)
            .map { uuid in
                let alias = names.name(for: uuid)
                return DeviceEntry(
                    id: uuid,
                    name: alias ?? "",
                    isNamed: alias != nil,
                    isHostConnected: companion.subscribedHosts.contains(uuid),
                    isActive: link.hostPolicy.target == uuid
                )
            }
            .sorted { $0.id.uuidString < $1.id.uuidString }
    }

    private func connectedDeviceRow(_ entry: DeviceEntry) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    Text(verbatim: entry.displayName).lineLimit(1)
                    Spacer()
                    Button { selectedInfoID = entry.id } label: { Image(systemName: "info.circle") }
                        .buttonStyle(.borderless)
                        .accessibilityLabel(L10n.DeviceInfo.info)
                        .popover(isPresented: Binding(
                            get: { selectedInfoID == entry.id },
                            set: { if !$0, selectedInfoID == entry.id { selectedInfoID = nil } }
                        ), arrowEdge: .trailing) {
                            DeviceInfoPopover(
                                entry: entry,
                                status: _deviceStatus(entry),
                                companionStatus: entry.isActive ? coordinator.companionStatus : nil
                            )
                        }
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
                get: { link.hostPolicy.allowed.contains(entry.id) },
                set: { link.setAllowed(entry.id, $0) }
            ))
            .toggleStyle(.switch)
            .accessibilityLabel(Text("Enable control: \(entry.displayName)"))
            if link.hostPolicy.allowed.contains(entry.id), link.hostPolicy.ready.contains(entry.id), !entry.isActive {
                Button("Use device") { link.selectHost(entry.id) }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func _deviceStatus(_ entry: DeviceEntry) -> String {
        if entry.isActive { return "Current input device" }
        if !entry.isHostConnected { return "Disconnected" }
        if link.hostPolicy.ready.contains(entry.id) { return "Ready" }
        return "Connecting"
    }

    private func row(_ title: LocalizedStringKey, _ value: Text) -> some View {
        HStack {
            Text(title)
            Spacer()
            value.foregroundColor(.secondary)
        }
    }

    private func _resetAll() {
        link.clearAllowedHosts()
        names.clear()
        if let bundleID = Bundle.main.bundleIdentifier {
            UserDefaults.standard.removePersistentDomain(forName: bundleID)
        }
        hasSeenWelcome = false
        coordinator.setEnabled(true)
    }
}
