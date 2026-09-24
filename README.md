# DeusKVM

Use your Mac's keyboard, mouse, and trackpad to control a Windows PC over
Bluetooth. Move through a configured screen edge to switch computers, or use a
hotkey. A Windows companion service handles reconnecting, cursor placement,
edge return, and plain-text clipboard sharing, including input control at the
Windows login screen.

Keyboard and mouse input use Bluetooth LE HID; the companion control and clipboard channel also uses
BLE. No LAN connection is required.

## Setup

1. Open **DeusKVM.app** on the Mac. In **Setup → Permissions**, use each
   **Allow…** button to grant Bluetooth, Accessibility, and Input Monitoring.
   Each button requests access or opens the relevant System Settings pane.
   If DeusKVM is missing from Accessibility or Input Monitoring, use
   **Show DeusKVM in Finder**, then add that app with **+** in System Settings.
   Reopen DeusKVM after granting Input Monitoring.
2. On Windows, extract **DeusKVM-Companion-win-x64.zip** and open
   **DeusKVM.Companion.exe**. Approve the administrator prompt. The EXE installs
   or updates the service and opens its settings; no scripts or separate .NET
   installation are required.
3. For first-time pairing, leave **System Settings → Bluetooth** open on the
   Mac. Choose **Add Mac…** in the Windows companion and approve the
   pairing prompts. Choose your Mac's computer name. If it is missing, try
   **Show all devices**. Windows Bluetooth Settings is not needed for this flow.
4. In the Mac's **Setup** tab, turn on **Enable control** for your PC. Advertising
   stops when an allowed PC is ready and resumes when none is available.
5. In **Layout**, choose the Mac display and exit edge, enable edge switching,
   and select the Windows display. Wait for **Windows edge return ready**.
   Return through the opposite edge on Windows.

## Everyday use

- Windows automatically uses the first connected, enabled DeusKVM Mac. A second
  Mac connects in a disabled state. Click **Enable DeusKVM** on that Mac to take
  control: Windows disables the previous Mac before granting the new one.
  Both Macs stay paired and Bluetooth stays on. Each Mac keeps its own layout.
  Update Windows and both Macs; see the [takeover checkpoint](docs/AUTOMATIC_MAC_CHECKPOINT.md).

- Edge crossings place the cursor at the corresponding position on the other
  display. Release held keys and mouse buttons before switching.
- **Switch to PC**, including the hotkey, centers the pointer on the selected
  Windows display. The default toggle is **Fn + Escape**; record your own in
  **Layout**. The Mac hotkey remains the way back if Windows cannot return.
- **Settings → Share clipboard with Windows** shares plain text in both
  directions, up to **64 KiB of UTF-8** per copy. Clipboard sharing pauses while
  locked or signed out and skips recognized private clipboard markers. See
  [clipboard behavior and limits](docs/CLIPBOARD.md). The current prototype also
  supports [on-demand file paste in both directions](docs/FILE_PASTE_CHECKPOINT.md)
  for one regular file up to 2 GB, using networking only. Windows → Mac uses
  Finder Cmd+V with DeusKVM progress/cancel UI. Contents transfer only when pasted.
- Vertical and horizontal scrolling can be inverted independently in Settings.
- **Disable DeusKVM** in Settings or the menu bar restores local input and
  stops advertising, input capture, and clipboard exchange. Enabling reuses
  saved devices. The menu icon shows searching, connecting, ready, or disabled.
- Mac launch-at-login is optional. On Windows, **Start automatically with
  Windows** controls the service; **Show tray icon at sign-in** controls the
  settings UI separately. Closing either settings window leaves control running.

The Windows service supports control at the login screen and across sign-in.
Clipboard access remains limited to the signed-in desktop. See the
[Windows guide](windows/README.md) for service controls and diagnostics.

## Update or remove

Quit the old Mac app and open the new **DeusKVM.app**. Keep the app in a stable
location if using launch-at-login. On Windows, open the new downloaded EXE;
it updates the installation and closes the old companion automatically.
Existing Bluetooth pairings and preferences are retained.

**First installation after the full rename:** use the previous Windows app's
cleanup command before installing this build. On the Mac, turn off launch at
login in the previous app, quit it, and grant Bluetooth, Accessibility, and
Input Monitoring permissions to this app. Set up pairing, Enable control, and
your layout again. This build uses new app/service identities and settings;
there is no automatic migration from earlier development builds.

To remove the Windows installation, choose **Remove DeusKVM from this PC…**.
It removes the service, startup entries, installed files and settings/logs.
Wait for the completion message, then delete the downloaded EXE/ZIP. Windows
Bluetooth pairings are kept.

## Build

For macOS, install Xcode (26 or later for the current UI), `xcodegen`,
`swiftformat`, `swiftlint`, and `xcbeautify`, then run:

```sh
./build.sh
open .build/DerivedData/Build/Products/Debug/DeusKVM.app
```

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
Framework 4.8 runtime. On Windows, also run tests with `-f net48`. The ZIP is
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
and shared text clipboard support. Thanks to the original author and contributors
for making that foundation available as open source.
