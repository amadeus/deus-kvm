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

## Windows checkpoint

- [x] User confirmed the main settings window incorporates 125% display scaling.
- [ ] Confirm all settings text/buttons are aligned and aren't clipped.
- [ ] Confirm there is no large empty bottom area, drag-resizing, or maximize.
- [ ] Confirm normal status updates don't make the window jump in size; longer
  waiting/disabled Mac lists and error messages grow the window as needed.
- [ ] Check 100%, 150%, and 200%, plus moving between differently scaled monitors.
- [ ] Confirm closing/reopening through the tray and all existing service controls
  still behave normally. Check keyboard navigation at 125%.

Local cross-builds do not validate Windows rendering or per-monitor DPI changes.

## Child dialog scaling follow-up

The user confirmed settings scaling but reported that Add Mac still appeared
unscaled. All three other custom companion forms had DPI mode without a design
baseline or the Windows dialog font.

- [x] Apply the same 96-DPI baseline and Windows message-box font to Add Mac,
  installation/update, and removal forms; suspend layout during construction so
  the entire control tree is present before scaling.
- [x] Cross-build and verify the packaged single EXE. Both builds completed with
  zero warnings/errors; payload verification passed for all 14 runtime files.
- [ ] On Windows at 125%, check Add Mac instructions, device rows, buttons, and
  status text; check installation/update and removal progress dialogs when those
  workflows are next exercised.
- [ ] Check these dialogs across differently scaled monitors and at 100/150/200%.

This follow-up changes only form scaling and font initialization. Pairing,
installation, removal, and existing sizing behavior are unchanged. Windows-owned
message boxes and pairing/UAC prompts do not use these custom form constructors.

References:
- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.containercontrol.autoscaledimensions
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.autosizemode

## Delivered build

`releases/DeusKVM-Companion-win-x64-2026-09-25.exe` is 910,336 bytes.
SHA-256: `e94d404521d56b9db935abf706bdd57cee5f0b4fabbad64974e2693632c91de7`.
The prior EXE is retained under `releases/Previous/`.

Validation: companion and launcher cross-builds have zero warnings/errors;
package verification passes for the x64 launcher and all 14 extracted runtime
files. The packaged app config still enables PerMonitorV2. Portable tests from
the main settings sizing pass (not rerun for this form-initialization follow-up):
172 passed, two Windows-only CNG tests skipped on macOS. The first test attempt
was blocked by the sandbox's local socket restriction; the retry with socket
access passed. Diff whitespace check passes. Windows UI/DPI and service/input
hardware checks above remain pending. No Windows installation was performed.
