# DeusKVM

Use your Mac's keyboard, mouse, and trackpad to control a Windows PC. Move
through a configured screen edge to switch computers, or use a hotkey. A Windows
companion service handles reconnecting, cursor placement, edge return, and
clipboard sharing, including input control at the Windows login screen, UAC
prompts, and administrator windows.

Everything travels over your local network: the Windows companion finds the
Mac with Bonjour and connects to it, keys, pointer, scrolling and media keys
stream to the PC, and edge return, clipboard, screenshots and file paste use the
same link. Bluetooth is not used. Both computers must be on the same private
network (or the PC must list the Mac's address, for example a Tailscale name;
see the [Windows guide](windows/README.md)).

## Setup

1. Unzip **DeusKVM-mac-arm64.zip** and open **DeusKVM.app** on the Mac (Apple
   Silicon only). In **Setup → Permissions**, use each
   **Allow…** button to grant Accessibility and Input Monitoring. When macOS asks
   whether DeusKVM may find devices on your local network, allow it.
   Each button requests access or opens the relevant System Settings pane.
   If DeusKVM is missing from Accessibility or Input Monitoring, use
   **Show DeusKVM in Finder**, then add that app with **+** in System Settings.
   Reopen DeusKVM after granting Input Monitoring.
2. On Windows, extract **DeusKVM-Companion-win-x64.zip** and open the
   **DeusKVM-Companion-win-x64-*date*.exe** inside it (or a downloaded EXE
   directly). Approve the administrator prompt. The EXE installs
   or updates the service and opens its settings; no scripts or separate .NET
   installation are required.
3. Choose **Add Mac…** in the Windows companion, pick your Mac's computer name
   from the Macs found on the network, and approve the administrator prompt.
4. The PC appears in the Mac's **Setup → Devices** a few seconds later; turn on
   **Enable control** for it.
5. In **Configuration**, choose the Mac display and exit edge, leave edge switching enabled,
   and select the Windows display. **Setup → Devices** shows **Windows edge return ready**.
   Return through the opposite edge on Windows.

Once every permission is granted and a PC has **Enable control** on, DeusKVM
starts in the menu bar without opening its window; choose **Open controls…** in
the menu bar to show it. The window opens by itself at launch only while
something still needs setting up.

## Everyday use

- Windows automatically uses the first connected, enabled DeusKVM Mac. A second
  Mac connects in a disabled state. Click **Enable DeusKVM** on that Mac to take
  control: Windows disables the previous Mac before granting the new one.
  Add each Mac once with **Add Mac…**. Each Mac keeps its own layout.
  Update Windows and both Macs; see the [takeover checkpoint](docs/AUTOMATIC_MAC_CHECKPOINT.md).

- Edge crossings place the cursor at the corresponding position on the other
  display. Release held keys and mouse buttons before switching.
- Keyboard forwarding, audited QMK layouts, and remaining physical checks are
  documented in [keyboard coverage](docs/KEYBOARD_COVERAGE.md).
- **Switch to PC**, including the hotkey, centers the pointer on the selected
  Windows display. The default toggle is **Fn + Escape**; record your own in
  **Configuration**. The Mac hotkey remains the way back if Windows cannot return.
- **Configuration → Input settings → Share clipboard with Windows** shares plain text in both
  directions, up to **64 KiB of UTF-8** per copy. Clipboard sharing pauses while
  locked or signed out and skips recognized private clipboard markers. See
  [clipboard behavior and limits](docs/CLIPBOARD.md). Screenshot images also cross
  in both directions for pasting into apps such as Discord, using the local
  network (both apps must be updated). The current prototype also
  supports [on-demand file paste in both directions](docs/FILE_PASTE_CHECKPOINT.md)
  for one regular file up to 2 GB, using networking only. Windows → Mac uses
  Finder Cmd+V with DeusKVM progress/cancel UI. Contents transfer only when pasted.
- Pointer motion and clicks go to Windows at the trackpad's own rate, keys as
  whole keyboard states (no six-key limit), plus media keys and scroll steps.
  Windows repeats held keys at its own keyboard delay and rate; Ctrl+Alt+Delete
  (or Cmd+Option+Delete, for a PC with Ctrl and Win swapped) works through the
  companion's service. Keys are injected untagged, so remappers such as PowerToys
  Keyboard Manager apply to them as to a real keyboard. Windows edge return requests
  come back on the same stream. Switching to the PC waits until its input
  connection is up; if the link drops while you are on the PC, the Mac takes
  input back. With **Developer Mode** on, **Setup → Diagnostics** shows the link
  and stream state.
- **Configuration → Input settings** sets the **Scroll speed** on Windows and
  inverts vertical and horizontal scrolling independently.
- **Disable DeusKVM** in **Configuration** or the menu bar restores local input and
  stops input capture and clipboard exchange; the PC sees the Mac as paused. Enabling reuses
  saved devices. The menu bar icon shows the connection state (searching,
  connecting, waiting for Windows, ready, Secure Input, or disabled), and the
  menu adds a short companion status such as **Edge return ready**.
- If something misbehaves, choose **Report Problem Now** in the menu bar before
  restarting either app. It saves recent details from the Mac and the PC.
- Mac launch-at-login is optional under **Setup → Startup**. On Windows, **Start automatically with
  Windows** controls the service; **Show tray icon at sign-in** controls the
  settings UI separately. Closing either settings window leaves control running.

The Windows service supports control at the login screen and across sign-in.
A SYSTEM helper injects the pointer on UAC prompts, the lock screen, and
administrator windows, which ordinary input cannot reach; if the helper cannot
run there, the desktop worker injects where it can.
Clipboard access remains limited to the signed-in desktop. See the
[Windows guide](windows/README.md) for service controls and diagnostics.

## Update or remove

Quit the old Mac app and open the new **DeusKVM.app**. Keep the app in a stable
location if using launch-at-login. On Windows, open the new downloaded EXE;
it updates the installation and closes the old companion automatically.
Preferences are retained.

**Updating from a Bluetooth build:** the PC and the Mac now know each other by
network identities, so add the Mac again with **Add Mac…** and turn on
**Enable control** for the PC again. Old Bluetooth pairings are no longer used:
remove the Mac from the PC's **Settings → Bluetooth & devices** once the network
build works (the Mac has nothing to remove).

**First installation after the full rename:** use the previous Windows app's
cleanup command before installing this build. On the Mac, turn off launch at
login in the previous app, quit it, and grant Accessibility and Input
Monitoring permissions to this app. Add the Mac, Enable control, and
your layout again. This build uses new app/service identities and settings;
there is no automatic migration from earlier development builds.

To remove the Windows installation, choose **Remove DeusKVM from this PC…**.
It removes the service, startup entries, installed files and settings/logs.
Wait for the completion message, then delete the downloaded EXE/ZIP.

## Build

For macOS, install Xcode (26 or later for the current UI), `xcodegen`, and
`xcbeautify`, then run:

```sh
./build.sh
open .build/DerivedData/Build/Products/Debug/DeusKVM.app
xcodebuild test -project DeusKVM.xcodeproj -scheme DeusKVM -destination 'platform=macOS,arch=arm64'
```

`build.sh` lints with SwiftFormat and SwiftLint before building. It downloads
the exact versions pinned in `ci_scripts/lint-tools.env` (checksum-verified, into
`.build/tools/bin`), the same ones CI uses, so a Homebrew install is not used.
To upgrade either tool, change its version and checksum there.

`project.yml` contains the development signing team; use your own signing
configuration when building on another Mac. The deployment target is macOS 13.

The project defaults to Apple Silicon. To package a signed optimized app for
Apple Silicon (arm64) only into the visible `releases/` folder:

```sh
./scripts/publish-mac.sh
```

For a direct Apple Silicon Release build:

```sh
xcodegen generate
xcodebuild -project DeusKVM.xcodeproj -scheme DeusKVM -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath .build/ReleaseDerivedData build
```

The app is in `.build/ReleaseDerivedData/Build/Products/Release/DeusKVM.app`.
Release enables the hardened runtime and omits the debugger entitlement.
Public distribution still requires Developer ID signing and notarization;
the configured Apple Development identity is for local builds.

The original keyboard app artwork is generated for both platforms with
`swift scripts/artwork/GenerateAppIcon.swift` on macOS.

The Windows companion requires the .NET 10 SDK to build. It can be cross-built
on macOS:

```sh
dotnet build windows/DeusKVM.Companion.sln -c Release
dotnet test windows/DeusKVM.Companion.Tests -c Release -f net10.0
./windows/publish.sh win-x64
```

The small Framework build targets Windows x64 and uses the OS-provided .NET
Framework 4.8 runtime. On Windows, also run tests with `-f net48`. A single EXE is
written to the visible `releases/` folder at the repository root. Intermediate build files remain in `.build/`.
The companion targets Windows 10 version 2004 or later. Native Windows
installation and desktop behavior are checked separately from the portable
policy tests. [PLAN.md](PLAN.md) records implementation scope and validation.

## License

DeusKVM is licensed under [AGPL-3.0-only](LICENSE).

## Acknowledgments

DeusKVM was forked from [BTRemote by jqssun](https://github.com/jqssun/darwin-bt-remote).
We built a new Mac-to-Windows KVM app on top of its Bluetooth HID foundation,
adding screen-edge switching, a Windows companion service, login-screen control,
and shared text clipboard support, and later moved everything onto the local
network. Thanks to the original author and contributors
for making that foundation available as open source.
