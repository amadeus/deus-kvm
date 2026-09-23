import AppKit
import CoreBluetooth
import SwiftUI

/// Permission requests belong to onboarding, and remain usable while DeusKVM is disabled.
struct PermissionsSection: View {
    @EnvironmentObject private var coordinator: EdgeSwitchCoordinator
    @State private var bluetoothAuthorization = CBManager.authorization
    @State private var keyboardGranted = KeyboardMonitoringPermission.isGranted
    @State private var permissionManager: CBCentralManager?

    var body: some View {
        Section {
            permissionRow(
                "Bluetooth",
                detail: "Connect to your Windows PC.",
                granted: bluetoothAuthorization == .allowedAlways,
                restricted: bluetoothAuthorization == .restricted,
                action: requestBluetooth
            )
            permissionRow(
                "Accessibility",
                detail: "Switch screens and control the pointer.",
                granted: coordinator.permissionGranted,
                action: AccessibilityPermission.request
            )
            permissionRow(
                "Input Monitoring",
                detail: "Forward every mapped keyboard key.",
                granted: keyboardGranted,
                action: KeyboardMonitoringPermission.request
            )
        } header: {
            Text("Permissions")
        } footer: {
            VStack(alignment: .leading, spacing: 8) {
                if !coordinator.permissionGranted || !keyboardGranted {
                    Text("If DeusKVM is missing from Accessibility or Input Monitoring, use + to add this app, then enable its switch.")
                    Button("Show DeusKVM in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([Bundle.main.bundleURL])
                    }
                }
                if coordinator.isEnabled, keyboardGranted, coordinator.permissionGranted, !coordinator.keyboardMonitoringReady {
                    Text("Quit and reopen DeusKVM to activate keyboard access.")
                }
            }
        }
        .onAppear(perform: refreshPermissions)
        .onReceive(NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didActivateApplicationNotification)) { _ in
            refreshPermissions()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshPermissions()
        }
        .task(id: bluetoothAuthorization == .notDetermined || !keyboardGranted) {
            // Only permission setup needs retries; an already-authorized view has no timer.
            while bluetoothAuthorization == .notDetermined || !keyboardGranted {
                do { try await Task.sleep(for: .seconds(1)) } catch { return }
                refreshPermissions()
            }
        }
    }

    private func refreshPermissions() {
        let bluetooth = CBManager.authorization
        let keyboard = KeyboardMonitoringPermission.isGranted
        if bluetoothAuthorization != bluetooth { bluetoothAuthorization = bluetooth }
        if keyboardGranted != keyboard { keyboardGranted = keyboard }
    }

    private func permissionRow(
        _ title: String,
        detail: String,
        granted: Bool,
        restricted: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: title)
                Text(verbatim: detail).font(.caption).foregroundStyle(.secondary)
                Text(granted ? "Allowed" : restricted ? "Restricted by macOS" : "Permission needed")
                    .font(.caption).foregroundStyle(granted ? Color.secondary : .orange)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Button(granted || restricted ? "Open Settings…" : "Allow…", action: action)
                .accessibilityLabel("\(title): \(granted || restricted ? "Open Settings" : "Allow access")")
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func requestBluetooth() {
        if CBManager.authorization == .notDetermined {
            // Creating a manager requests Bluetooth access without scanning or advertising.
            // Retain it while the system prompt is outstanding, even if the app is disabled.
            permissionManager = CBCentralManager(
                delegate: nil, queue: nil, options: [CBCentralManagerOptionShowPowerAlertKey: false]
            )
        } else if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Bluetooth") {
            NSWorkspace.shared.open(url)
        }
    }
}
