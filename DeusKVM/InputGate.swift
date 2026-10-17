import Foundation

/// Orders captured input around crossings on the Bluetooth queue. Input for a remote session
/// the coordinator has not approved yet waits here and is released in capture order once it
/// starts; a stop, a refused crossing or a newer session discards it.
struct InputGate<Event> {
    /// Caps waiting droppable input (motion, scroll). Keys and buttons always wait, so none is
    /// left held on the host.
    static var capacity: Int {
        512
    }

    private(set) var active: Int?
    private var waiting: [Event] = []
    private var waitingGeneration: Int?

    /// Events to forward now, in capture order.
    mutating func receive(_ event: Event, generation: Int, droppable: Bool = false) -> [Event] {
        if generation == active { return [event] }
        if let active, generation < active { return [] }
        if let waitingGeneration, generation < waitingGeneration { return [] }
        if waitingGeneration != generation {
            waiting.removeAll()
            waitingGeneration = generation
        }
        if !droppable || waiting.count < Self.capacity { waiting.append(event) }
        return []
    }

    /// The coordinator approved this session; anything already captured for it goes first.
    /// Input already waiting for a newer session keeps waiting for that session.
    mutating func start(_ generation: Int) -> [Event] {
        active = generation
        guard let waitingGeneration, waitingGeneration <= generation else { return [] }
        defer { waiting.removeAll(); self.waitingGeneration = nil }
        return waitingGeneration == generation ? waiting : []
    }

    mutating func stop() {
        active = nil
        waiting.removeAll()
        waitingGeneration = nil
    }
}
