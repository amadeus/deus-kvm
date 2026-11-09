import CoreGraphics
import Foundation

extension EdgeSwitchCoordinator {
    var shortcutLabel: String {
        guard shortcut.enabled else { return L10n.Layout.disabledString }
        let flags = CGEventFlags(rawValue: shortcut.modifiers)
        var label = ""
        if flags.contains(.maskControl) { label += "⌃" }
        if flags.contains(.maskAlternate) { label += "⌥" }
        if flags.contains(.maskShift) { label += "⇧" }
        if flags.contains(.maskCommand) { label += "⌘" }
        if flags.contains(.maskSecondaryFn) { label += "fn " }
        return label + (shortcut.keyCode == 53 ? "⎋" : L10n.Layout.keyCodeString(shortcut.keyCode))
    }
}
