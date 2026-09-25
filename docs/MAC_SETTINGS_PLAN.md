# macOS settings organization

Scope: reorganize existing settings into Setup and Configuration. Preserve settings
keys, saved choices, actions, device selection, and Bluetooth/input behavior.
Edge switching now defaults to enabled when no value has been saved. Do not
add device selection to Configuration or tab persistence.

## Organization

- Setup: permissions; devices with connection and companion status; launch at
  login; advanced settings (developer mode and reset).
- Merge Connection into Devices. Show pairing help when the list is empty,
  Bluetooth problems above the devices, and diagnostics in developer mode.
- Configuration: current Mac/PC control and enable/disable; existing screen switching
  and Windows display settings; shortcut; clipboard sharing; scrolling.
- Preserve the existing missing-permission, unavailable-PC, and Secure Input
  messages in Configuration.
- Use the same two tabs in the main window and the macOS Settings window:
  Configuration first, Setup second. Open on Configuration when permissions are granted;
  route to Setup when opening or focusing a settings window with missing access.
- Permissions is collapsible, initially collapsed when all grants are allowed
  and expanded otherwise; update expansion when that permission state changes.
  The whole Permissions header row is a button, including its empty space.
- Right-align Switch to PC / Return to Mac beside the current-control status.
  Right-align Enable/Disable on its own row and omit the pairing byline.
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

- [x] Implement permission disclosure, permission-based opening tab, Configuration-first
  ordering, inline control buttons, Input settings, and enabled-by-default edges.
- [x] Verify formatting/lint and signed arm64 Release build for this follow-up.
- [x] Commit the verified polish changes.

## Row interaction follow-up

- [x] Couple the switch button with current-control status and move Input settings.
- [x] Replace the default disclosure with an explicit full-width Permissions button.
- [x] Verify lint, formatting, and the signed arm64 build; commit this follow-up.
- Force Service Changed is now hidden from settings. Its automatic workaround remains
  enabled by default, preserving the existing saved preference. It waits two seconds
  after a non-companion GATT read and only cycles a temporary service when there
  are no subscribed centrals. Prior reconnect tests do not isolate whether this
  fallback is still necessary with the Windows companion, so the fallback is retained.

## User checkpoint (pending)

- [ ] Check permissions initially collapse when allowed, expand when missing,
  remain manually expandable, and update after a permission change.
- [ ] Confirm Configuration opens by default and Setup opens when a permission is
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
Configuration was left open. Missing-permission behavior and full hardware input
checks remain pending; this verification does not mark them passed.

## Permissions spacing and advanced-setting cleanup

- [x] Hide the Force Service Changed toggle and explanatory text without changing
  the automatic fallback or its enabled default.
- [x] Omit the permission-help row when it has no missing-access or restart message.
  The empty VStack previously created a blank Form row, its separator, and padding.
- [x] Verify formatting/lint and the signed arm64 Release build, including the
  user's staged "Controlling PC" wording change; commit only this cleanup.
- [ ] User visual check: expanded permissions ends directly after Input Monitoring
  when all access is ready; permission/restart help still appears when needed.

## Configuration label and device title alignment

- [x] Rename the visible Controls tab to Configuration and update its documented paths.
- [x] Put the device name and info button in the same title row; status remains below.
- [x] Verify formatting/lint, signed arm64 build, and packaged wording; commit only
  these edits while preserving the user's staged wording change.
- [ ] User visual check: Configuration tab label and info button centered with the name.

## Device information popover

- [x] Replace the Setup device-info sheet with a native popover anchored to its
  info button. Show the name as its heading, live device/companion status, and
  a selectable identifier; show manufacturer only when known.
- [x] Rename inline through the existing DeviceNameStore using Save/Cancel.
  Preserve the existing connected-device rename restriction. Escape cancels
  an edit or dismisses the popover; clicking outside dismisses it.
- [x] Verify formatting/lint, signed arm64 build, and packaged wording; commit.
- [ ] User check: placement, size, rename Save/Cancel, identifier selection,
  live status updates, outside-click dismissal, and Escape.
