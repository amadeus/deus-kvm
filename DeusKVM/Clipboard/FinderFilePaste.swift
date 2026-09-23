import AppKit
import ApplicationServices
import Carbon

private final class FilePasteLease: @unchecked Sendable {
    private let lock = NSLock()
    private var active = true
    func revoke() {
        lock.withLock { active = false }
    }

    var valid: Bool {
        lock.withLock { active }
    }
}

/// Only handles plain Cmd+V in Finder, outside text fields, for our current remote offer.
@MainActor
final class FinderFilePaste: NSObject {
    private var offer: ClipboardFileOffer?
    private var revision = 0
    var local = true
    var send: (ClipboardFileOffer) -> Void = { _ in }
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var swallowed = false
    private var receiver: FileNetworkReceiver?
    private var lease: FilePasteLease?
    private var panel: NSPanel?
    private var bar: NSProgressIndicator?
    private var status: NSTextField?

    func install(_ value: ClipboardFileOffer?, revision: Int = 0) {
        clear(); offer = value; self.revision = revision
        guard value != nil, tap == nil else { return }
        let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)
        tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: CGEventMask(mask),
            callback: { _, type, event, pointer in
                guard let pointer else { return Unmanaged.passUnretained(event) }
                let suppress = MainActor.assumeIsolated {
                    let owner = Unmanaged<FinderFilePaste>.fromOpaque(pointer).takeUnretainedValue()
                    return owner.event(type, event)
                }
                return suppress ? nil : Unmanaged.passUnretained(event)
            },
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        )
        if let tap {
            source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
            CGEvent.tapEnable(tap: tap, enable: true)
        }
    }

    func clear() {
        offer = nil; lease?.revoke(); receiver?.cancel(); receiver = nil; lease = nil
        panel?.close(); panel = nil
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CFMachPortInvalidate(tap) }
        tap = nil; source = nil
    }

    private func event(_ type: CGEventType, _ event: CGEvent) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }; return false
        }
        guard event.getIntegerValueField(.keyboardEventKeycode) == 9 else { return false }
        if type == .keyUp, swallowed { swallowed = false; return true }
        guard type == .keyDown, let offer, let app = NSWorkspace.shared.frontmostApplication,
              FinderPasteGesture.matches(
                  key: 9,
                  flags: event.flags,
                  local: local,
                  revisions: (revision, NSPasteboard.general.changeCount),
                  bundle: app.bundleIdentifier
              ),
              !Self.editingText(app.processIdentifier) else { return false }
        swallowed = true
        if receiver == nil, event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
            // Suppress the native key immediately; folder lookup/download never block the event tap.
            DispatchQueue.main.async { [weak self] in self?.paste(offer) }
        }
        return true
    }

    private static func editingText(_ pid: pid_t) -> Bool {
        var focused: CFTypeRef?
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.1)
        guard AXUIElementCopyAttributeValue(app, kAXFocusedUIElementAttribute as CFString, &focused) ==
            .success,
            let focused else { return true }
        let element = unsafeDowncast(focused, to: AXUIElement.self)
        var role: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role) == .success else { return true }
        return [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole].contains(role as? String ?? "")
    }

    private func paste(_ copied: ClipboardFileOffer) {
        guard local, receiver == nil, offer?.sequence == copied.sequence, NSPasteboard.general.changeCount == revision,
              NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.apple.finder" else { return }
        var error: NSDictionary?
        let script = NSAppleScript(source: "tell application id \"com.apple.finder\" to POSIX path of (insertion location as alias)")
        guard let path = script?.executeAndReturnError(&error).stringValue, error == nil else {
            showError("Allow DeusKVM to control Finder in System Settings → Privacy & Security → Automation, then paste again."); return
        }
        let folder = URL(fileURLWithPath: path, isDirectory: true)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            showError("Open a writable folder in Finder and paste again."); return
        }
        let destination = folder.appendingPathComponent(copied.name)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            showError("A file named \(copied.name) already exists in this folder. Rename it or paste into another folder."); return
        }
        let token = FilePasteLease(), expected = revision
        lease = token
        showProgress(copied)
        let next = FileNetworkReceiver(offer: copied, destination: destination, canAccess: {
            token.valid && !IsSecureEventInputEnabled() && NSPasteboard.general.changeCount == expected
        }, ready: { [weak self] request in
            Task { @MainActor in
                guard token.valid, let self else { return }; self.send(request)
            }
        }, progress: { [weak self] bytes in
            Task { @MainActor in
                guard token.valid, let self else { return }
                self.bar?.doubleValue = Double(bytes)
                self.status?.stringValue = "\(ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)) of " +
                    ByteCountFormatter.string(fromByteCount: Int64(copied.size), countStyle: .file)
            }
        }, completion: { [weak self] message in
            Task { @MainActor in
                guard token.valid, let self else { return }
                self.receiver = nil; self.lease = nil; self.panel?.close(); self.panel = nil
                if let message { self.showError(message) }
            }
        })
        receiver = next; next.start()
    }

    private func showProgress(_ file: ClipboardFileOffer) {
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 420, height: 130),
            styleMask: [.titled],
            backing: .buffered,
            defer: false
        )
        panel.title = "Pasting from Windows"
        let name = NSTextField(labelWithString: file.name); name.frame = NSRect(x: 20, y: 90, width: 380, height: 20)
        name.lineBreakMode = .byTruncatingMiddle
        let bar = NSProgressIndicator(frame: NSRect(x: 20, y: 64, width: 380, height: 14))
        bar.isIndeterminate = false; bar.minValue = 0; bar.maxValue = Double(max(1, file.size))
        let status = NSTextField(labelWithString: "Connecting…"); status.frame = NSRect(x: 20, y: 25, width: 280, height: 20)
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelPaste))
        cancel.frame = NSRect(x: 315, y: 16, width: 85, height: 32)
        for view in [name, bar, status, cancel] {
            panel.contentView?.addSubview(view)
        }
        self.panel = panel; self.bar = bar; self.status = status
        panel.center(); panel.makeKeyAndOrderFront(nil)
    }

    @objc private func cancelPaste() {
        lease?.revoke(); receiver?.cancel(); receiver = nil; lease = nil; panel?.close(); panel = nil
    }

    private func showError(_ message: String) {
        let alert = NSAlert(); alert.messageText = "Couldn't paste the file"; alert.informativeText = message
        alert.addButton(withTitle: "OK"); alert.runModal()
    }
}
