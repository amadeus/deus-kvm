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
        Window("DeusKVM", id: "controls") {
            ContentView()
                .modifier(AppEnvironment(lowEnergy: lowEnergy, central: central, names: deviceNames, coordinator: coordinator))
        }
        .defaultSize(width: 480, height: 600)
        .windowResizability(.contentSize)
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
}

/// Only the menu bar views observe the coordinator, so its updates stay local to them.
private struct StatusIcon: View {
    @ObservedObject var coordinator: EdgeSwitchCoordinator

    var body: some View {
        Image(systemName: coordinator.connectionState.symbol(isRemote: coordinator.isRemote))
            .accessibilityLabel(coordinator.connectionState.label)
            .help(coordinator.connectionState.label)
    }
}

private struct StatusMenu: View {
    @ObservedObject var coordinator: EdgeSwitchCoordinator
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(verbatim: coordinator.connectionState.label)
        if coordinator.isEnabled {
            Text(coordinator.isRemote ? L10n.Layout.remote : L10n.Layout.local)
            Text(verbatim: coordinator.companionStatus)
        }
        Text(verbatim: coordinator.shortcutLabel)
        Button(L10n.Layout.openControls) {
            openWindow(id: "controls")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button(coordinator.isRemote ? L10n.Layout.returnToMac : L10n.Layout.switchToPC) { coordinator.toggle() }
            .disabled(!coordinator.isRemote && !coordinator.canSwitch)
        Toggle(L10n.Layout.lock, isOn: $coordinator.locked)
        Divider()
        Button(coordinator.isEnabled ? "Disable DeusKVM" : "Enable DeusKVM") {
            coordinator.setEnabled(!coordinator.isEnabled)
        }
        Button(L10n.Layout.quit) { NSApp.terminate(nil) }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static var coordinator: EdgeSwitchCoordinator?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if ProcessInfo.processInfo.arguments.contains("--cursor-spike") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { CursorSpike.run() }
        } else { Self.coordinator?.start() }
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
