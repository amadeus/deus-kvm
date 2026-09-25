import AppKit
import SwiftUI

struct ControlsSettingsView: View {
    @EnvironmentObject private var coordinator: EdgeSwitchCoordinator
    @State private var recording = false
    @AppStorage(AppSettings.invertVerticalScrollKey) private var invertVerticalScroll = false
    @AppStorage(AppSettings.invertHorizontalScrollKey) private var invertHorizontalScroll = false
    @AppStorage(AppSettings.clipboardEnabledKey) private var clipboardEnabled = true

    var body: some View {
        Form {
            Section(L10n.SettingsOrganization.currentControl) {
                HStack {
                    Label(coordinator.isRemote ? L10n.Layout.remote : L10n.Layout.local, systemImage: "computermouse")
                    Spacer()
                    Button(coordinator.isRemote ? L10n.Layout.returnToMac : L10n.Layout.switchToPC) { coordinator.toggle() }
                        .disabled(!coordinator.isRemote && !coordinator.canSwitch)
                }
                if !coordinator.isEnabled {
                    Text(L10n.SettingsOrganization.disabled).foregroundStyle(.secondary)
                }
                if coordinator.isEnabled, !coordinator.targetAvailable { Text(L10n.Layout.noTarget).foregroundStyle(.orange) }
                if coordinator.isEnabled, !coordinator.permissionGranted || !coordinator.keyboardMonitoringReady {
                    Text("Finish granting permissions in Setup to enable complete keyboard and mouse control.")
                        .font(.caption).foregroundStyle(.orange)
                }
                if coordinator.secureInput { Text(L10n.Layout.secureInput).foregroundStyle(.orange) }
                if let error = coordinator.lastError { Text(verbatim: error).foregroundStyle(.red) }
                HStack {
                    Spacer()
                    Button(coordinator.isEnabled ? "Disable DeusKVM" : "Enable DeusKVM") {
                        coordinator.setEnabled(!coordinator.isEnabled)
                    }
                }
            }
            Section {
                VStack(alignment: .leading, spacing: 8) {
                    Toggle("Share clipboard with Windows", isOn: $clipboardEnabled)
                        .toggleStyle(.switch)
                    Text(
                        "Shares text up to 64 KiB over Bluetooth and one file up to 2 GB over the local network. "
                            + "Skips marked private items and pauses while locked."
                    )
                    .font(.caption).foregroundStyle(.secondary)
                }
                Toggle("Invert vertical scrolling", isOn: $invertVerticalScroll)
                    .toggleStyle(.switch)
                Toggle("Invert horizontal scrolling", isOn: $invertHorizontalScroll)
                    .toggleStyle(.switch)
            } header: {
                Text(L10n.SettingsOrganization.inputSettings)
            }
            Section(L10n.Layout.edgeSection) {
                Toggle(L10n.Layout.enable, isOn: $coordinator.edgeEnabled)
                Picker(L10n.Layout.display, selection: $coordinator.displayID) {
                    ForEach(coordinator.displays) { display in Text(verbatim: display.name).tag(display.id) }
                }
                Picker(L10n.Layout.edge, selection: $coordinator.edge) {
                    ForEach(DisplayEdge.allCases) { edge in Text(edge.label).tag(edge) }
                }
                HStack {
                    Text(L10n.Layout.corners)
                    Slider(value: $coordinator.cornerSize, in: 0 ... 100, step: 5)
                    Text(coordinator.cornerSize, format: .number).monospacedDigit().frame(width: 40)
                }
                Toggle(L10n.Layout.lock, isOn: $coordinator.locked)
            }
            if !coordinator.pcMonitors.isEmpty {
                Section(L10n.SettingsOrganization.windowsDisplay) {
                    Picker("Display", selection: $coordinator.pcMonitorID) {
                        Text("Primary display").tag("")
                        ForEach(coordinator.pcMonitors) { monitor in
                            Text(verbatim: "\(monitor.id) (\(monitor.w) × \(monitor.h))").tag(monitor.id)
                        }
                    }
                    Text("Return through the opposite edge of this display.").font(.caption).foregroundStyle(.secondary)
                }
            }
            Section(L10n.Layout.hotkeySection) {
                Toggle(L10n.Layout.hotkeyEnabled, isOn: $coordinator.shortcut.enabled)
                HStack {
                    Text(verbatim: coordinator.shortcutLabel)
                    Spacer()
                    Button(recording ? L10n.Layout.cancel : L10n.Layout.record) { recording.toggle() }
                    Button(L10n.Layout.reset) { coordinator.shortcut = ToggleShortcut() }
                }
                if recording {
                    Text(L10n.Layout.pressShortcut)
                    ShortcutRecorder { keyCode, flags in
                        coordinator.shortcut = ToggleShortcut(keyCode: keyCode, modifiers: flags.rawValue)
                        recording = false
                    }.frame(height: 1)
                }
                Text(L10n.Layout.releaseHint).font(.caption).foregroundStyle(.secondary)
            }
        }
        .settingsFormStyle()
        .onChange(of: recording) { coordinator.recordingShortcut = $0 }
        .onDisappear { coordinator.recordingShortcut = false }
    }
}

private struct ShortcutRecorder: NSViewRepresentable {
    let onKey: (UInt16, CGEventFlags) -> Void

    func makeNSView(context: Context) -> KeyCaptureView {
        let view = KeyCaptureView()
        view.onKey = onKey
        return view
    }

    func updateNSView(_ nsView: KeyCaptureView, context: Context) {
        nsView.onKey = onKey
    }

    final class KeyCaptureView: NSView {
        var onKey: ((UInt16, CGEventFlags) -> Void)?
        override var acceptsFirstResponder: Bool {
            true
        }

        override func viewDidMoveToWindow() {
            window?.makeFirstResponder(self)
        }

        override func performKeyEquivalent(with event: NSEvent) -> Bool {
            keyDown(with: event)
            return true
        }

        override func keyDown(with event: NSEvent) {
            guard !event.isARepeat else { return }
            let flags = event.cgEvent?.flags.intersection(ToggleShortcut.modifierMask) ?? []
            onKey?(event.keyCode, flags)
        }
    }
}

extension DisplayEdge {
    var label: LocalizedStringKey {
        switch self {
        case .left: L10n.Layout.left
        case .right: L10n.Layout.right
        case .top: L10n.Layout.top
        case .bottom: L10n.Layout.bottom
        }
    }
}
