import Foundation

/// Explicit permission is separate from connection and current input routing.
struct HIDHostPolicy: Equatable {
    enum Input: Hashable { case mouse, keyboard, bootMouse, bootKeyboard }

    var allowed: Set<UUID> = []
    private(set) var target: UUID?
    private(set) var ready: Set<UUID> = []

    var needsAdvertising: Bool {
        target == nil
    }

    func shouldAdvertise(enabled: Bool, poweredOn: Bool, serviceAdded: Bool) -> Bool {
        enabled && poweredOn && serviceAdded && needsAdvertising
    }

    static func inputReady(_ inputs: Set<Input>) -> Bool {
        inputs.isSuperset(of: [.mouse, .keyboard]) || inputs.isSuperset(of: [.bootMouse, .bootKeyboard])
    }

    /// Report-protocol hosts (Windows) read the report keyboard; a boot copy would send
    /// every key state twice. Only boot-protocol hosts need it.
    static func needsBootKeyboard(_ inputs: Set<Input>) -> Bool {
        !inputs.contains(.keyboard)
    }

    mutating func reconcile(ready: Set<UUID>) {
        self.ready = ready
        let candidates = ready.intersection(allowed)
        if let target, candidates.contains(target) { return }
        target = candidates.min { $0.uuidString < $1.uuidString }
    }

    mutating func select(_ id: UUID) {
        guard allowed.contains(id), ready.contains(id) else { return }
        target = id
    }
}
