# macOS settings organization

Scope: reorganize existing settings into Setup and Controls. Preserve settings
keys, defaults, actions, device selection, and Bluetooth/input behavior. Do not
add device selection to Controls, new navigation actions, or tab persistence.

## Organization

- Setup: permissions; devices with connection and companion status; launch at
  login; advanced settings (developer mode, Force Service Changed, reset).
- Merge Connection into Devices. Show pairing help when the list is empty,
  Bluetooth problems above the devices, and diagnostics in developer mode.
- Controls: current Mac/PC control and enable/disable; existing screen switching
  and Windows display settings; shortcut; clipboard sharing; scrolling.
- Preserve the existing missing-permission, unavailable-PC, and Secure Input
  messages in Controls.
- Use the same two tabs in the main window and the macOS Settings window.

## Implementation and local verification

- [x] Reorganize the existing views and update affected labels/help text.
- [x] Pass SwiftFormat for app/test sources and SwiftLint (86 files, zero violations).
- [x] Build and sign the Release app for arm64; verify the packaged architecture.
- [x] Commit the verified reorganization.

Release app: `releases/settings-review/DeusKVM.app`.
Archive: `releases/DeusKVM-mac-arm64-settings-2026-09-24.zip`.
The packaged app passes strict code-signature verification with the existing
Apple Development identity; its executable contains only arm64.

Repository-wide SwiftFormat also reports pre-existing formatting violations in
`scripts/tests/FileNetworkSmoke.swift` and `scripts/tests/ReverseFileSmoke.swift`.
Those unrelated scripts are unchanged. No new unit tests were added for this
view-only reorganization; the interactive checks below remain pending.

## User checkpoint (pending)

- [ ] Review both tabs at the normal window size, including scrolling to the
  lower sections; confirm grouping, labels, and spacing.
- [ ] Confirm the existing PC and saved settings are retained, device information
  remains accessible, and connection/companion status appears under Devices.
- [ ] Confirm enable/disable, screen switching, shortcut, clipboard, and scrolling
  still work on the Mac/Windows hardware.
- [ ] Check developer diagnostics and the reset confirmation (cancel to preserve
  settings); confirm launch-at-login state and macOS Settings entry point.

A successful build or commit does not complete these visual/hardware checks.
