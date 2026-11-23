import Foundation
import SwiftUI

extension L10n {
    enum DeviceInfo {
        static var info: LocalizedStringKey {
            "device_info.info"
        }

        static var name: LocalizedStringKey {
            "device_info.name"
        }

        static var identifier: LocalizedStringKey {
            "device_info.identifier"
        }
    }

    enum Settings {
        static var advanced: LocalizedStringKey {
            "settings.advanced"
        }

        static var developerMode: LocalizedStringKey {
            "settings.developer_mode"
        }

        static var reset: LocalizedStringKey {
            "settings.reset"
        }

        static var resetConfirm: LocalizedStringKey {
            "settings.reset_confirm"
        }
    }
}
