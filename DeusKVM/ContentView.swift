import SwiftUI

struct ContentView: View {
    @State private var tab = Tab.setup

    private enum Tab {
        case setup, controls
    }

    var body: some View {
        TabView(selection: $tab) {
            SetupView()
                .tabItem { Label(L10n.Tab.setup, systemImage: "gearshape") }
                .tag(Tab.setup)
            ControlsSettingsView()
                .tabItem { Label(L10n.SettingsOrganization.controls, systemImage: "slider.horizontal.3") }
                .tag(Tab.controls)
        }
        .frame(minWidth: 420, idealWidth: 480, maxWidth: 640, minHeight: 480, idealHeight: 600)
    }
}
