import Foundation

extension HIDInput {
    @MainActor
    static func make(lowEnergy: HIDPeripheral, central: HIDCentral) -> HIDInput {
        HIDInput(
            sendMouse: { lowEnergy.sendMouse($0) },
            sendKeyboard: { lowEnergy.sendKeyboard($0) },
            sendConsumer: { lowEnergy.sendConsumer($0) },
            isActive: lowEnergy.isHIDServiceAdded,
            isConnected: lowEnergy.hostPolicy.target != nil,
            activeError: lowEnergy.lastError ?? central.lastError
        )
    }
}
