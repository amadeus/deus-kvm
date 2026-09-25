import CoreBluetooth
import SwiftUI

struct ContentView: View {
    @State private var tab = Tab.controls

    private enum Tab {
        case controls, setup
    }

    var body: some View {
        TabView(selection: $tab) {
            ControlsSettingsView()
                .tabItem { Label(L10n.SettingsOrganization.controls, systemImage: "slider.horizontal.3") }
                .tag(Tab.controls)
            SetupView()
                .tabItem { Label(L10n.Tab.setup, systemImage: "gearshape") }
                .tag(Tab.setup)
        }
        .frame(minWidth: 420, idealWidth: 480, maxWidth: 640, minHeight: 480, idealHeight: 600)
        .onAppear { tab = permissionsGranted ? .controls : .setup }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { _ in
            if !permissionsGranted { tab = .setup }
        }
    }

    private var permissionsGranted: Bool {
        CBManager.authorization == .allowedAlways
            && AccessibilityPermission.isTrusted
            && KeyboardMonitoringPermission.isGranted
    }
}
