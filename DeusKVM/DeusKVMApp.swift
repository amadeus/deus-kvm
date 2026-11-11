import CoreBluetooth
import SwiftUI

@main
struct DeusKVMApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    // Owned for the life of the process but not observed here: observing them made every
    // publication, including each crossing, re-evaluate all scenes and rebuild the menu on
    // the main thread that also forwards input. Views observe what they display.
    private let lowEnergy: HIDPeripheral
    private let central: HIDCentral
    private let coordinator: EdgeSwitchCoordinator
    private let deviceNames: DeviceNameStore

    init() {
        UserDefaults.standard.register(defaults: [AppSettings.useServiceChangedKey: true])
        let peripheral = HIDPeripheral()
        let central = HIDCentral()
        let names = DeviceNameStore()
        peripheral.companion.onComputerName = { [weak names] id, name in
            names?.rememberName(name, for: id)
        }
        let coordinator = EdgeSwitchCoordinator(lowEnergy: peripheral, central: central)
        lowEnergy = peripheral
        self.central = central
        self.coordinator = coordinator
        deviceNames = names
        AppDelegate.coordinator = coordinator
    }

    var body: some Scene {
        controls
        Settings {
            ContentView()
                .modifier(AppEnvironment(lowEnergy: lowEnergy, central: central, names: deviceNames, coordinator: coordinator))
        }
        .defaultSize(width: 480, height: 600)
        .windowResizability(.contentSize)
        MenuBarExtra {
            StatusMenu(coordinator: coordinator)
        } label: {
            StatusIcon(coordinator: coordinator)
        }
    }

    /// The controls window opens at launch only when there is something to set up
    /// (`LaunchWindow`): on macOS 15 and later it is suppressed and opened on demand; before
    /// that, SwiftUI always opens it and the app delegate closes it again.
    private var controls: some Scene {
        LaunchWindow.suppressedAtLaunch(controlsWindow)
    }

    private var controlsWindow: some Scene {
        Window("DeusKVM", id: LaunchWindow.controlsID) {
            ContentView()
                .modifier(AppEnvironment(lowEnergy: lowEnergy, central: central, names: deviceNames, coordinator: coordinator))
        }
        .defaultSize(width: 480, height: 600)
        .windowResizability(.contentSize)
    }
}

/// Opening the controls window at launch only when something needs setting up
/// (`LaunchWindowPolicy`). Otherwise the app starts in the menu bar.
enum LaunchWindow {
    static let controlsID = "controls"

    @MainActor static var shouldOpenNow: Bool {
        LaunchWindowPolicy.shouldOpen(
            bluetooth: CBManager.authorization == .allowedAlways,
            accessibility: AccessibilityPermission.isTrusted,
            inputMonitoring: KeyboardMonitoringPermission.isGranted,
            pairedPC: !(UserDefaults.standard.stringArray(forKey: AppSettings.allowedHostsKey) ?? []).isEmpty
        )
    }

    /// Decided once per launch, by whichever of the menu bar icon or the app delegate gets there.
    @MainActor static var decided = false

    /// macOS 15+ only: the scene does not open by itself at launch or by state restoration.
    static func suppressedAtLaunch(_ scene: some Scene) -> some Scene {
        if #available(macOS 15.0, *) {
            return scene.defaultLaunchBehavior(.suppressed).restorationBehavior(.disabled)
        } else {
            return scene
        }
    }
}

/// Only the menu bar views observe the coordinator, so its updates stay local to them.
private struct StatusIcon: View {
    @ObservedObject var coordinator: EdgeSwitchCoordinator
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: coordinator.connectionState.symbol(isRemote: coordinator.isRemote))
            .accessibilityLabel(coordinator.connectionState.label)
            .help(coordinator.connectionState.label)
            .task { openControlsAtLaunch() }
    }

    /// macOS 15+: the controls window is suppressed at launch, so it is opened here when needed.
    private func openControlsAtLaunch() {
        guard #available(macOS 15.0, *), !LaunchWindow.decided else { return }
        LaunchWindow.decided = true
        guard LaunchWindow.shouldOpenNow else { return }
        openWindow(id: LaunchWindow.controlsID)
        NSApp.activate(ignoringOtherApps: true)
    }
}

private struct StatusMenu: View {
    @ObservedObject var coordinator: EdgeSwitchCoordinator
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(verbatim: coordinator.connectionState.label)
        if coordinator.connectionState == .secureInput, let holder = coordinator.secureInputHolder {
            Text(verbatim: "Held by \(holder)")
        }
        if coordinator.isEnabled {
            Text(coordinator.isRemote ? L10n.Layout.remote : L10n.Layout.local)
            Text(verbatim: coordinator.companionStatus)
        }
        Text(verbatim: coordinator.shortcutLabel)
        Button(L10n.Layout.openControls) {
            openWindow(id: LaunchWindow.controlsID)
            NSApp.activate(ignoringOtherApps: true)
        }
        Button(coordinator.isRemote ? L10n.Layout.returnToMac : L10n.Layout.switchToPC) { coordinator.toggle() }
            .disabled(!coordinator.isRemote && !coordinator.canSwitch)
        Toggle(L10n.Layout.lock, isOn: $coordinator.locked)
        Divider()
        // When something misbehaves (before restarting): keeps both computers' recent state.
        Button(coordinator.reportingProblem ? "Saving Problem Report…" : "Report Problem Now") {
            coordinator.reportProblem { ProblemReportAlert.show($0) }
        }
        .disabled(coordinator.reportingProblem)
        Button(coordinator.isEnabled ? "Disable DeusKVM" : "Enable DeusKVM") {
            coordinator.setEnabled(!coordinator.isEnabled)
        }
        Button(L10n.Layout.quit) { NSApp.terminate(nil) }
    }
}

@MainActor
private enum ProblemReportAlert {
    static func show(_ url: URL) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Problem report saved"
        let path = url.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")
        alert.informativeText = "Recent details from this Mac and the PC were added to \(path). You can restart DeusKVM now."
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Show in Finder")
        if alert.runModal() == .alertSecondButtonReturn {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static var coordinator: EdgeSwitchCoordinator?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if ProcessInfo.processInfo.arguments.contains("--cursor-spike") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { CursorSpike.run() }
        } else { Self.coordinator?.start() }
        closeControlsIfNotNeeded()
    }

    /// Before macOS 15 SwiftUI opens the controls window at every launch; close it again when
    /// nothing needs setting up. (Later versions suppress it and open it only when needed.)
    private func closeControlsIfNotNeeded() {
        if #available(macOS 15.0, *) { return }
        guard !LaunchWindow.decided else { return }
        LaunchWindow.decided = true
        guard !LaunchWindow.shouldOpenNow else { return }
        DispatchQueue.main.async {
            NSApp.windows.filter { $0.identifier?.rawValue.hasPrefix(LaunchWindow.controlsID) == true }.forEach { $0.close() }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        Self.coordinator?.stop()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }
}

/// Injects the shared objects without observing them; each view observes what it reads.
private struct AppEnvironment: ViewModifier {
    let lowEnergy: HIDPeripheral
    let central: HIDCentral
    let names: DeviceNameStore
    let coordinator: EdgeSwitchCoordinator

    func body(content: Content) -> some View {
        content
            .environmentObject(lowEnergy)
            .environmentObject(lowEnergy.companion)
            .environmentObject(central)
            .environmentObject(names)
            .environmentObject(coordinator)
    }
}
