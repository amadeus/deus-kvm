import Foundation
import IOKit

extension KeyboardMonitoringStatus {
    var message: String? {
        switch self {
        case .ready: nil
        case .waitingForDevice: String(localized: "keyboard_monitoring.waiting")
        case .permissionRequired: String(localized: "keyboard_monitoring.permission")
        case .unavailable(kIOReturnExclusiveAccess): String(localized: "keyboard_monitoring.exclusive")
        case .unavailable: String(localized: "keyboard_monitoring.failed")
        }
    }
}
