import Foundation
import SwiftUI

extension L10n {
    enum DeviceInfo {
        static var title: LocalizedStringKey {
            "device_info.title"
        }

        static var info: LocalizedStringKey {
            "device_info.info"
        }

        static var device: LocalizedStringKey {
            "device_info.device"
        }

        static var advertisement: LocalizedStringKey {
            "device_info.advertisement"
        }

        static var services: LocalizedStringKey {
            "device_info.services"
        }

        static var name: LocalizedStringKey {
            "device_info.name"
        }

        static var identifier: LocalizedStringKey {
            "device_info.identifier"
        }

        static var signal: LocalizedStringKey {
            "device_info.signal"
        }

        static var connectable: LocalizedStringKey {
            "device_info.connectable"
        }

        static var txPower: LocalizedStringKey {
            "device_info.tx_power"
        }

        static var done: LocalizedStringKey {
            "device_info.done"
        }
    }

    enum Setup {
        static var activeLegend: LocalizedStringKey {
            "setup.active_legend"
        }

        static var deviceNameLimitation: LocalizedStringKey {
            "setup.device_name_limitation"
        }
    }

    enum Settings {
        static var connection: LocalizedStringKey {
            "settings.connection"
        }

        static var advanced: LocalizedStringKey {
            "settings.advanced"
        }

        static var developerMode: LocalizedStringKey {
            "settings.developer_mode"
        }

        static var forceServiceChanged: LocalizedStringKey {
            "settings.force_service_changed"
        }

        static var forceServiceChangedHint: LocalizedStringKey {
            "settings.force_service_changed_hint"
        }

        static var sourceCode: LocalizedStringKey {
            "settings.source_code"
        }

        static var reset: LocalizedStringKey {
            "settings.reset"
        }

        static var resetConfirm: LocalizedStringKey {
            "settings.reset_confirm"
        }
    }

    enum ErrorMessage {
        static func peripheralNotRetained(_ identifier: UUID) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.peripheral_not_retained"),
                identifier.uuidString
            )
        }

        static func failedToConnect(_ reason: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.failed_to_connect"),
                reason
            )
        }

        static func deviceNotPaired(_ name: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.device_not_paired"),
                name
            )
        }

        static func openConnectionFailed(_ code: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.open_connection_failed"),
                code
            )
        }

        static func openL2CAPFailed(_ psm: UInt16, _ code: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.open_l2cap_failed"),
                psm,
                code
            )
        }

        static func writeFailed(_ code: String) -> String {
            String.localizedStringWithFormat(
                String(localized: "error.write_failed"),
                code
            )
        }
    }
}
