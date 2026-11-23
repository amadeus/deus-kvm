import SwiftUI

extension L10n {
    enum Layout {
        static var local: LocalizedStringKey {
            "layout.local"
        }

        static var remote: LocalizedStringKey {
            "layout.remote"
        }

        static var noTarget: LocalizedStringKey {
            "layout.no_target"
        }

        static var secureInput: LocalizedStringKey {
            "layout.secure_input"
        }

        static var edgeSection: LocalizedStringKey {
            "layout.edge_section"
        }

        static var enable: LocalizedStringKey {
            "layout.enable"
        }

        static var display: LocalizedStringKey {
            "layout.display"
        }

        static var edge: LocalizedStringKey {
            "layout.edge"
        }

        static var corners: LocalizedStringKey {
            "layout.corners"
        }

        static var lock: LocalizedStringKey {
            "layout.lock"
        }

        static var hotkeySection: LocalizedStringKey {
            "layout.hotkey_section"
        }

        static var hotkeyEnabled: LocalizedStringKey {
            "layout.hotkey_enabled"
        }

        static var cancel: LocalizedStringKey {
            "layout.cancel"
        }

        static var record: LocalizedStringKey {
            "layout.record"
        }

        static var reset: LocalizedStringKey {
            "layout.reset"
        }

        static var pressShortcut: LocalizedStringKey {
            "layout.press_shortcut"
        }

        static var releaseHint: LocalizedStringKey {
            "layout.release_hint"
        }

        static var returnToMac: LocalizedStringKey {
            "layout.return_to_mac"
        }

        static var switchToPC: LocalizedStringKey {
            "layout.switch_to_p_c"
        }

        static var disabledString: String {
            String(localized: "layout.disabled")
        }

        static var cursorFailedString: String {
            String(localized: "layout.cursor_failed")
        }

        static var left: LocalizedStringKey {
            "layout.left"
        }

        static var right: LocalizedStringKey {
            "layout.right"
        }

        static var top: LocalizedStringKey {
            "layout.top"
        }

        static var bottom: LocalizedStringKey {
            "layout.bottom"
        }

        static var openControls: LocalizedStringKey {
            "layout.open_controls"
        }

        static var quit: LocalizedStringKey {
            "layout.quit"
        }

        static func keyCodeString(_ code: UInt16) -> String {
            String(format: String(localized: "layout.key_code"), Int(code))
        }
    }
}
