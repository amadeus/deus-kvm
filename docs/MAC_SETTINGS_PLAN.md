# macOS settings organization

Scope: reorganize existing settings into Setup and Controls. Preserve settings
keys, saved choices, actions, device selection, and Bluetooth/input behavior.
Edge switching now defaults to enabled when no value has been saved. Do not
add device selection to Controls or tab persistence.

## Organization

- Setup: permissions; devices with connection and companion status; launch at
  login; advanced settings (developer mode, Force Service Changed, reset).
- Merge Connection into Devices. Show pairing help when the list is empty,
  Bluetooth problems above the devices, and diagnostics in developer mode.
- Controls: current Mac/PC control and enable/disable; existing screen switching
  and Windows display settings; shortcut; clipboard sharing; scrolling.
- Preserve the existing missing-permission, unavailable-PC, and Secure Input
  messages in Controls.
- Use the same two tabs in the main window and the macOS Settings window:
  Controls first, Setup second. Open on Controls when permissions are granted;
  route to Setup when opening or focusing a settings window with missing access.
- Permissions is collapsible, initially collapsed when all grants are allowed
  and expanded otherwise; update expansion when that permission state changes.
- Keep Switch to PC / Return to Mac before Enable/Disable on the same row.
  Remove the pairing byline from Current control.
- Combine clipboard sharing and scroll inversion under Input settings.

## Implementation and local verification

- [x] Reorganize the existing views and update affected labels/help text.
- [x] Pass SwiftFormat for app/test sources and SwiftLint (86 files, zero violations).
- [x] Build and sign the Release app for arm64; verify the packaged architecture.
- [x] Commit the verified reorganization.

Release app: `releases/settings-polish/DeusKVM.app`.
Archive: `releases/DeusKVM-mac-arm64-settings-polish-2026-09-24.zip`.
The packaged app passes strict code-signature verification with the existing
Apple Development identity; its executable contains only arm64.

Repository-wide SwiftFormat also reports pre-existing formatting violations in
`scripts/tests/FileNetworkSmoke.swift` and `scripts/tests/ReverseFileSmoke.swift`.
Those unrelated scripts are unchanged. No new unit tests were added for this
settings polish; the interactive checks below remain pending.

## Polish follow-up

- [x] Implement permission disclosure, permission-based opening tab, Controls-first
  ordering, inline control buttons, Input settings, and enabled-by-default edges.
- [x] Verify formatting/lint and signed arm64 Release build for this follow-up.
- [x] Commit the verified polish changes.

## User checkpoint (pending)

- [ ] Check permissions initially collapse when allowed, expand when missing,
  remain manually expandable, and update after a permission change.
- [ ] Confirm Controls opens by default and Setup opens when a permission is
  missing, including reopening the settings window.
- [ ] Confirm the inline buttons and combined Input settings look correct.
- [ ] Confirm fresh settings start with edge switching enabled and a saved off
  preference remains off.
- [ ] Review both tabs at the normal window size, including scrolling to the
  lower sections; confirm grouping, labels, and spacing.
- [ ] Confirm the existing PC and saved settings are retained, device information
  remains accessible, and connection/companion status appears under Devices.
- [ ] Confirm enable/disable, screen switching, shortcut, clipboard, and scrolling
  still work on the Mac/Windows hardware.
- [ ] Check developer diagnostics and the reset confirmation (cancel to preserve
  settings); confirm launch-at-login state and macOS Settings entry point.

A successful build or commit does not complete these visual/hardware checks.
