import Foundation

/// A PC in the device list: one whose companion connected, or one allowed earlier.
struct DeviceEntry: Identifiable, Equatable {
    let id: UUID
    let name: String
    let isNamed: Bool
    /// Its companion is connected over the control link now.
    let isHostConnected: Bool
    let isActive: Bool

    var displayName: String {
        isNamed ? name : "Windows PC"
    }
}
