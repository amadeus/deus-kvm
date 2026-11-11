import Foundation

/// Owns the local-network pointer stream for the coordinator. The
/// listener runs while the active companion can use it and the setting is on; each new
/// listener port or target gets a fresh offer over the Bluetooth link. A companion that goes
/// away takes the listener down, so its return makes a new offer.
@MainActor
final class NetworkPointer {
    let server = InputNetworkServer()
    private(set) var state = "off"
    var onStateChange: ((String) -> Void)?
    /// Diagnostics lines: offers, stream transitions and the per-session counters.
    var note: ((String) -> Void)?
    /// An edge-return request that arrived on the stream, on the main actor.
    var onLeave: ((InputLeave) -> Void)?
    private var offered: (target: UUID, port: UInt16)?

    init() {
        server.onStateChange = { [weak self] text in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.changed(text) } }
        }
        server.onLeave = { [weak self] leave in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.onLeave?(leave) } }
        }
    }

    func refresh(enabled: Bool, target: UUID?, companion: CompanionService) {
        let setting = UserDefaults.standard.object(forKey: AppSettings.networkPointerKey) as? Bool ?? true
        let wanted = enabled && setting && target.map { companion.supportsInputNetwork($0) } == true
        server.configure(wanted)
        guard wanted, let target, let port = server.currentPort else {
            if !wanted { offered = nil }
            return
        }
        guard offered?.target != target || offered?.port != port, let offer = server.offer() else { return }
        companion.sendJSON(offer, type: .inputOffer, to: target)
        offered = (target, port)
        note?("network: pointer stream offered on port \(port)")
    }

    /// The session's stream counters, if it carried anything, to the diagnostics log.
    func logSession() {
        server.summary { [weak self] line in
            guard !line.isEmpty else { return }
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.note?("network: session \(line)") } }
        }
    }

    private func changed(_ text: String) {
        state = text
        onStateChange?(text)
        if ["up", "stalled", "closed"].contains(text) { note?("network: pointer stream \(text)") }
    }
}
