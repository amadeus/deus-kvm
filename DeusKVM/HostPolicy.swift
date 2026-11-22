import Foundation

/// Which PC gets input. Explicit permission ("Enable control") is separate from connection: a
/// PC is `ready` once its companion completes HELLO, and the target is a ready, allowed PC
/// (sticky while it stays ready, else the lowest id).
struct HostPolicy: Equatable {
    var allowed: Set<UUID> = []
    private(set) var target: UUID?
    private(set) var ready: Set<UUID> = []

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
