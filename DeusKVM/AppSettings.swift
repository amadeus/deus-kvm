import Foundation

enum AppSettings {
    static let enabledKey = "DeusKVM.enabled"
    static let clipboardEnabledKey = "DeusKVM.clipboardEnabled"
    static let allowedHostsKey = "DeusKVM.allowedHosts"
    static let autoAdvertiseKey = "DeusKVM.autoAdvertise"
    static let developerModeKey = "DeusKVM.developerMode"
    static let useServiceChangedKey = "DeusKVM.useServiceChanged"
    static let deviceNamesKey = "DeusKVM.deviceNames"
    static let hasSeenWelcomeKey = "DeusKVM.hasSeenWelcome"

    static let invertVerticalScrollKey = "DeusKVM.invertVerticalScroll"
    static let invertHorizontalScrollKey = "DeusKVM.invertHorizontalScroll"
    static let directPointerKey = "DeusKVM.directPointer"
    static let windowsPointerSpeedKey = "DeusKVM.windowsPointerSpeed"

    static let edgeSwitchEnabledKey = "DeusKVM.edgeSwitchEnabled"
    static let edgeDisplayUUIDKey = "DeusKVM.edgeDisplayUUID"
    static let edgeSideKey = "DeusKVM.edgeSide"
    static let cornerSizePxKey = "DeusKVM.cornerSizePx"
    static let toggleKeyCodeKey = "DeusKVM.toggleKeyCode"
    static let toggleModifiersKey = "DeusKVM.toggleModifiers"
    static let toggleHotkeyEnabledKey = "DeusKVM.toggleHotkeyEnabled"
    static let defaultCornerSize = 0.0

    static let repoURL = URL(string: "https://github.com/amadeus/darwin-bt-remote")!
}
