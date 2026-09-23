import AppKit

@MainActor
final class MacClipboardSync {
    private let service: CompanionService
    private let transfer = ClipboardTransfer()
    private var retryTimer: Timer?
    private var retryDeadline: TimeInterval?
    private var target: UUID?
    private var epoch: UInt32 = 0
    private var generation: UInt64 = 0
    private var peerAvailable = false
    private var enabled = false
    private var primed = false
    private var preference = true
    private var awake = true
    private var available = true
    private var remote = false
    private var revision = 0
    private var fileOffer: Data?
    private var peerSequence: UInt32?
    private var peerRevision = 0
    private var deliveredFile: UInt32?
    private let finder = FinderFilePaste()
    private var observers: [NSObjectProtocol] = []
    private lazy var pasteboard = ClipboardPasteboard(snapshot: { [weak self] snapshot in
        DispatchQueue.main.async { [weak self] in self?.observe(snapshot) }
    }, yielded: { [weak self] epoch, generation in
        DispatchQueue.main.async { [weak self] in self?.yield(epoch, generation: generation) }
    })

    private func observe(_ snapshot: ClipboardPasteboard.Snapshot) {
        guard enabled, snapshot.epoch == epoch, snapshot.generation == generation else { return }
        finder.clear()
        revision = snapshot.revision
        transfer.observe(snapshot.text, announce: snapshot.announce)
        fileOffer = snapshot.file.flatMap {
            try? JSONEncoder().encode(ClipboardFileOffer(
                epoch: epoch,
                clipboardSequence: transfer.sequence,
                sequence: UInt32(truncatingIfNeeded: revision),
                name: $0.url.lastPathComponent,
                size: $0.size,
                network: target.map { service.supportsFileNetwork($0) } == true ? snapshot.network : nil
            ))
        }
        if snapshot.announce { offerFile() }
        if !primed {
            primed = true
            if let target { acknowledge(target, enabled: true) }
        }
    }

    private func yield(_ epoch: UInt32, generation: UInt64) {
        guard enabled, self.epoch == epoch, self.generation == generation else { return }
        transfer.yield()
        offerFile()
    }

    private func offerFile() {
        if let target, enabled, service.supportsFiles(target), let fileOffer {
            service.sendClipboard(.fileOffer, payload: fileOffer, to: target)
        }
    }

    init(service: CompanionService) {
        self.service = service
        transfer.onDeadlineChanged = { [weak self] in self?.scheduleRetry() }
        pasteboard.remoteFile = { [weak self] offer, revision, generation in
            Task { @MainActor in
                guard let self, self.enabled, self.generation == generation, self.epoch == offer.epoch,
                      self.peerSequence == offer.clipboardSequence,
                      self.deliveredFile == offer.sequence, NSPasteboard.general.changeCount == revision else { return }
                self.fileOffer = nil; self.transfer.observe(nil, announce: false)
                self.finder.install(offer, revision: revision)
            }
        }
        finder.send = { [weak self] request in
            guard let self, enabled, let target, request.epoch == epoch,
                  let data = try? JSONEncoder().encode(request) else { return }
            service.sendClipboard(.fileAccept, payload: data, to: target)
        }
        transfer.send = { [weak self] type, data in
            guard let self, enabled, let target else { return }
            service.sendClipboard(type, payload: data, to: target)
        }
        transfer.apply = { [weak self] data in
            guard let self, enabled else { return }
            finder.clear()
            pasteboard.write(data, epoch: epoch, revision: revision)
        }
        service.onClipboard = { [weak self] id, type, data in try self?.receive(id, type: type, data: data) }
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.setAwake(false) }
            })
        }
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.setAwake(true) }
            })
        }
    }

    func update(target: UUID?, remote: Bool, enabled: Bool, available: Bool) {
        finder.local = !remote
        let next = target.flatMap { service.supportsClipboard($0) ? $0 : nil }
        if self.target != next {
            if let old = self.target { acknowledge(old, enabled: false) }
            self.target = next; peerAvailable = false; self.enabled = false; epoch = 0
            finder.clear(); peerSequence = nil; deliveredFile = nil
            transfer.reset(0); generation = pasteboard.configure(epoch: 0, enabled: false)
        }
        let entering = !self.remote && remote
        self.remote = remote; preference = enabled; self.available = available
        reconcile()
        transfer.setActive(!remote, now: ProcessInfo.processInfo.systemUptime)
        if entering, self.enabled { pasteboard.yield() }
    }

    private func scheduleRetry() {
        let next = transfer.retryDeadline
        guard next != retryDeadline else { return }
        retryTimer?.invalidate(); retryTimer = nil; retryDeadline = next
        guard let next else { return }
        retryTimer = Timer.scheduledTimer(
            withTimeInterval: max(0.001, next - ProcessInfo.processInfo.systemUptime), repeats: false
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.retryTimer = nil; self?.retryDeadline = nil
                self?.transfer.tick(ProcessInfo.processInfo.systemUptime)
            }
        }
    }

    func stop() {
        preference = false; reconcile()
        for observer in observers {
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
        }
        observers.removeAll()
    }

    private func setAwake(_ value: Bool) {
        awake = value; reconcile()
    }

    private func reconcile() {
        let wanted = target != nil && preference && available && awake && peerAvailable
        guard wanted != enabled else { return }
        finder.clear(); peerSequence = nil; deliveredFile = nil
        enabled = wanted; primed = false; fileOffer = nil; transfer.reset(epoch); generation = pasteboard.configure(
            epoch: epoch,
            enabled: wanted
        )
        if !wanted, let target { acknowledge(target, enabled: false) }
        if wanted, remote { pasteboard.yield() }
    }

    private func acknowledge(_ id: UUID, enabled: Bool) {
        service.sendClipboard(.clipState, payload: CompanionProtocol.u32(epoch) + Data([enabled ? 1 : 0]), to: id)
    }

    private func receive(_ id: UUID, type: CompanionProtocol.Message, data: Data) throws {
        guard target == id else { return }
        if type == .clipState {
            guard data.count == 5, data[4] <= 1 else { throw CompanionProtocol.Failure.malformed }
            let next = CompanionProtocol.read32(data, 0)
            let ready = data[4] == 1
            guard next != epoch || peerAvailable != ready else { return }
            finder.clear(); peerSequence = nil; deliveredFile = nil
            epoch = next; peerAvailable = ready; enabled = false
            transfer.reset(epoch); generation = pasteboard.configure(epoch: epoch, enabled: false)
            reconcile()
            if !enabled { acknowledge(id, enabled: false) }
        } else if enabled, primed, type == .fileOffer {
            guard data.count <= 4096, let offer = try? JSONDecoder().decode(ClipboardFileOffer.self, from: data), offer.valid,
                  offer.epoch == epoch, offer.clipboardSequence == peerSequence, deliveredFile != offer.sequence else { return }
            deliveredFile = offer.sequence
            pasteboard.writeFile(offer, revision: peerRevision)
        } else if type == .fileGet || type == .fileData {
            // File contents are network-only, including requests from older peers.
            return
        } else if enabled, primed {
            if type == .clipGrab, data.count == 12, CompanionProtocol.read32(data, 0) == epoch {
                let incoming = CompanionProtocol.read32(data, 4)
                if peerSequence != incoming {
                    peerSequence = incoming; peerRevision = revision; deliveredFile = nil; finder.clear()
                }
            }
            try transfer.receive(type, payload: data, now: ProcessInfo.processInfo.systemUptime)
        }
    }
}
