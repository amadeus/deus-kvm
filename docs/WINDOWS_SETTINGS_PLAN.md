# Windows settings sizing

Target: the companion settings window should respect Windows display scaling
(the user's screen is at about 125%), use compact native spacing, and size itself
to its contents without a user-resizable border.

## Implementation

- [x] Keep the existing .NET Framework 4.8 / PerMonitorV2 configuration.
- [x] Give SettingsForm a 96-DPI design baseline and the Windows message-box font.
- [x] Replace its hardcoded 530 x 490 area and minimum size with AutoSize /
  GrowAndShrink on the form and its layout panel; use a fixed dialog border and
  disable maximize. Minimize and ordinary close/tray behavior remain available.
- [x] Use 12 logical pixels of outer padding, explicit small row margins, and
  wrapping status/help labels. Retain the existing content and actions.
- [x] Batch status updates before layout so temporary text doesn't resize the form.
- [x] Cross-build, run portable regression tests, and verify the actual single-EXE
  payload before delivering the Windows test build.
- [x] Commit verified changes. No service, Bluetooth, or input behavior changes.

## Windows checkpoint (pending)

- [ ] At 125% display scale, open settings and confirm text/buttons are readable,
  align with Windows dialog sizing, and aren't clipped.
- [ ] Confirm there is no large empty bottom area, drag-resizing, or maximize.
- [ ] Confirm normal status updates don't make the window jump in size; longer
  waiting/disabled Mac lists and error messages grow the window as needed.
- [ ] Check 100%, 150%, and 200%, plus moving between differently scaled monitors.
- [ ] Confirm closing/reopening through the tray and all existing service controls
  still behave normally. Check keyboard navigation at 125%.

Local cross-builds do not validate Windows rendering or per-monitor DPI changes.
Other dialogs (pairing/install/removal) are outside this settings-window pass.

References:
- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.containercontrol.autoscaledimensions
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.autosizemode

## Delivered build

`releases/DeusKVM-Companion-win-x64-2026-09-24.exe` is 910,336 bytes.
SHA-256: `b3a9cf8e6640b53fc060d08922a8e7ee7c4821e94528d71f071892026a95deb4`.
The prior EXE is retained under `releases/Previous/`.

Validation: companion and launcher cross-builds have zero warnings/errors;
package verification passes for the x64 launcher and all 14 extracted runtime
files. The packaged app config still enables PerMonitorV2. Portable tests:
172 passed, two Windows-only CNG tests skipped on macOS. The first test attempt
was blocked by the sandbox's local socket restriction; the retry with socket
access passed. Diff whitespace check passes. Windows UI/DPI and service/input
hardware checks above remain pending. No Windows installation was performed.
