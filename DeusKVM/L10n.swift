import Foundation
import SwiftUI

enum L10n {
    enum App {
        static var title: LocalizedStringKey {
            "app.title"
        }
    }

    enum Tab {
        static var settings: LocalizedStringKey {
            "tab.settings"
        }

        static var setup: LocalizedStringKey {
            "tab.setup"
        }
    }

    enum Section {
        static var lastError: LocalizedStringKey {
            "section.last_error"
        }

        static var status: LocalizedStringKey {
            "section.status"
        }

        static var connection: LocalizedStringKey {
            "section.connection"
        }

        static var devices: LocalizedStringKey {
            "section.devices"
        }
    }

    enum Value {
        static var yes: LocalizedStringKey {
            "value.yes"
        }

        static var no: LocalizedStringKey {
            "value.no"
        }

        static var none: LocalizedStringKey {
            "value.none"
        }

        static var noneString: String {
            String(localized: "value.none")
        }
    }

    enum Action {
        static var startAdvertising: LocalizedStringKey {
            "action.start_advertising"
        }

        static var stopAdvertising: LocalizedStringKey {
            "action.stop_advertising"
        }

        static var scanNearbyDevices: LocalizedStringKey {
            "action.scan_nearby_devices"
        }

        static var stopScanning: LocalizedStringKey {
            "action.stop_scanning"
        }

        static var notNow: LocalizedStringKey {
            "action.not_now"
        }

        static var done: LocalizedStringKey {
            "action.done"
        }

        static var settings: LocalizedStringKey {
            "action.settings"
        }
    }

    enum Welcome {
        static var title: LocalizedStringKey {
            "welcome.title"
        }

        static var message: LocalizedStringKey {
            "welcome.message"
        }

        static var viewGuide: LocalizedStringKey {
            "welcome.view_guide"
        }
    }

    enum Sort {
        static var title: LocalizedStringKey {
            "sort.title"
        }

        static var direction: LocalizedStringKey {
            "sort.direction"
        }

        static var ascending: LocalizedStringKey {
            "sort.ascending"
        }

        static var descending: LocalizedStringKey {
            "sort.descending"
        }
    }

    enum Device {
        static var emptyState: LocalizedStringKey {
            "device.empty_state"
        }

        static var unknownName: String {
            String(localized: "device.unknown_name")
        }

        static var inactive: LocalizedStringKey {
            "device.inactive"
        }

        static var notSubscribed: LocalizedStringKey {
            "device.not_subscribed"
        }

        static var connectedLegend: LocalizedStringKey {
            "device.connected_legend"
        }

        static func rssi(_ value: Int) -> String {
            String.localizedStringWithFormat(
                String(localized: "device.rssi_format"),
                value
            )
        }

        static func connectAccessibilityLabel(_ name: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "device.connect_accessibility_label"),
                name
            )
        }

        static func disconnectAccessibilityLabel(_ name: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "device.disconnect_accessibility_label"),
                name
            )
        }
    }
}

extension L10n {
    enum BluetoothState {
        static var unavailable: LocalizedStringKey {
            "bluetooth_state.unavailable"
        }
    }

    enum KeyboardLED {
        static var numLock: String {
            String(localized: "keyboard_led.num_lock")
        }

        static var capsLock: String {
            String(localized: "keyboard_led.caps_lock")
        }

        static var scrollLock: String {
            String(localized: "keyboard_led.scroll_lock")
        }
    }
}
