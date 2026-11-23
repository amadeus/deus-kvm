import Foundation

enum AppSettings {
    static let enabledKey = "DeusKVM.enabled"
    static let clipboardEnabledKey = "DeusKVM.clipboardEnabled"
    /// PC ids (control-link identities) allowed to take input. The old DeusKVM.allowedHosts list
    /// (Bluetooth addresses) is ignored.
    static let allowedHostsKey = "DeusKVM.allowedPCs"
    static let developerModeKey = "DeusKVM.developerMode"
    static let deviceNamesKey = "DeusKVM.deviceNames"
    static let hasSeenWelcomeKey = "DeusKVM.hasSeenWelcome"

    static let invertVerticalScrollKey = "DeusKVM.invertVerticalScroll"
    static let invertHorizontalScrollKey = "DeusKVM.invertHorizontalScroll"
    /// Multiplier on wheel/pan steps while controlling Windows (ScrollAccumulator range).
    static let scrollSpeedKey = "DeusKVM.scrollSpeed"

    static let edgeSwitchEnabledKey = "DeusKVM.edgeSwitchEnabled"
    static let edgeDisplayUUIDKey = "DeusKVM.edgeDisplayUUID"
    static let edgeSideKey = "DeusKVM.edgeSide"
    static let cornerSizePxKey = "DeusKVM.cornerSizePx"
    static let toggleKeyCodeKey = "DeusKVM.toggleKeyCode"
    static let toggleModifiersKey = "DeusKVM.toggleModifiers"
    static let toggleHotkeyEnabledKey = "DeusKVM.toggleHotkeyEnabled"
    static let defaultEdgeSwitchEnabled = true
    static let defaultCornerSize = 0.0

    static let repoURL = URL(string: "https://github.com/amadeus/deus-kvm")!
}
