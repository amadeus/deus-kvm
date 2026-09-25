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
  The whole Permissions header row is a button, including its empty space.
- Right-align Switch to PC / Return to Mac beside the current-control status.
  Keep Enable/Disable on its own row and omit the pairing byline.
- Combine clipboard sharing and scroll inversion under Input settings directly
  below Current control, before Switch at display edge.

## Implementation and local verification

- [x] Reorganize the existing views and update affected labels/help text.
- [x] Pass SwiftFormat for app/test sources and SwiftLint (86 files, zero violations).
- [x] Build and sign the Release app for arm64; verify the packaged architecture.
- [x] Commit the verified reorganization.

Current test app: `releases/DeusKVM.app`. Keep only this current macOS test
artifact in `releases/`; superseded Mac builds and duplicate ZIPs were removed.
Windows artifacts are unchanged.
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

## Row interaction follow-up

- [x] Couple the switch button with current-control status and move Input settings.
- [x] Replace the default disclosure with an explicit full-width Permissions button.
- [x] Verify lint, formatting, and the signed arm64 build; commit this follow-up.
- Force Service Changed is unchanged. Its automatic workaround waits two seconds
  after a non-companion GATT read and only cycles a temporary service when there
  are no subscribed centrals. Prior reconnect tests do not isolate whether this
  fallback is still necessary with the Windows companion. Removing its visible
  toggle while retaining the fallback is a possible follow-up, not implemented.

## User checkpoint (pending)

- [ ] Check permissions initially collapse when allowed, expand when missing,
  remain manually expandable, and update after a permission change.
- [ ] Confirm Controls opens by default and Setup opens when a permission is
  missing, including reopening the settings window.
- [x] Confirm the right-aligned switch button and Input settings section order
  in the installed app through live screenshots.
- [ ] Click the Permissions text, chevron, and empty header space to expand and
  collapse; confirm permission action buttons do not toggle the section.
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

## Installed-build verification

The screenshot reported after the row follow-up came from the older installed
11:22 build; the latest signed build was from 11:30. Replaced
`/Applications/DeusKVM.app`, verified its executable SHA-256 matched the latest
build, and reopened it. Live inspection confirmed the right-aligned switch
button, Input settings immediately below Current control, permissions initially
collapsed with all grants allowed, and expansion/collapse by clicking empty
header space. The PC reconnected and reported Windows edge return ready.
Controls was left open. Missing-permission behavior and full hardware input
checks remain pending; this verification does not mark them passed.
