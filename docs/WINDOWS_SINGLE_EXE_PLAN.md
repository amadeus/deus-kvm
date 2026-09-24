# Single downloadable Windows EXE — 2026-09-23

The user confirmed the small Framework build works and requested the previous
one-EXE download/open experience, retaining the smaller size.

## Scope

Deliver one small downloadable EXE. It embeds the complete Framework package,
extracts into a unique private temporary directory, launches the existing
companion/update flow, waits for handoff, and removes temporary files. The actual
installed app retains its dependency DLLs/config under Program Files. The
launcher is not the service or a persistent background process.

No companion runtime source or Mac code changes are part of this step. User
confirmation applies to the preceding Framework app, not yet to the new launcher.

## Phases

- [x] Add an x64 Framework launcher with only OS assembly references, using the
  existing app icon. Embed the compressed payload and its SHA-256.
- [x] Verify the archive hash before extraction; reject unsafe/duplicate names,
  incomplete packages and excessive sizes; use a private ACL and unique staging
  directory. Forward arguments without shell interpretation and preserve exit codes.
- [x] Wait for the existing setup/launch process before cleanup, allowing its
  normal UAC prompt and handoff to the installed app. Internal service/worker modes
  must continue to launch the installed companion directly.
- [x] Add extraction/argument regression tests and compile both test targets.
- [x] Run final publish and inspect the actual EXE's metadata/resources; prove
  it has no non-OS DLL references, compare extracted payload hashes with publish,
  record the final size and retain the confirmed Framework ZIP as fallback.
- [ ] Windows: verify launcher from a folder containing only this EXE, update
  the existing installation, reopen it, decline a UAC update, and check temp cleanup.
- [ ] Windows CI: exercise fresh install/update/reopen/removal using the launcher,
  verifying the installed payload and ACLs against separate expected build output.
- [ ] User checkpoint: launch the single EXE, then verify normal switching/paste.

## Design details

- The launcher is a small separate `net48` WinExe with BCL/WinForms references.
  It does not load the companion DLLs into its own process or modify their binding.
- `BundlePayload` is linked into tests and the build-time verifier so the exact
  extraction implementation can execute on the build host. Windows-specific ACL,
  process/UAC and service paths still require Windows execution.
- Its generated `.exe.config` contains only the supported-runtime declaration;
  it is not shipped. The PE targets CLR v4 and has no binding redirects or runtime
  configuration requirements. The embedded companion retains its complete config.
- Each launch stages under `%TEMP%\DeusKVM-Bundle-<random-guid>` with access limited
  to the launching user, SYSTEM and Administrators. Successful normal launch
  finishes when the installed app has started; service/UI execution never depends
  on these temporary files afterward.
- Temporary deletion retries briefly for transient file locks. If the launcher
  is forcibly killed or Windows shuts down mid-update, temporary files may remain;
  they are not reused or trusted by the next launch. No permanent cleanup poller
  or extra background service is added.
- Launch from the Windows system directory so the installed tray cannot inherit
  the temporary directory as its working directory and prevent its deletion.
  Interactive handoff uses no redirected diagnostic pipe that could keep the
  launcher waiting on the long-lived tray.
- Existing settings, service startup/running state, Bluetooth pairings, protocols,
  clipboard behavior and Mac build remain owned by the working companion.

References:
- https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/startup/supportedruntime-element
- https://github.com/dotnet/docs/blob/main/docs/standard/io/zip-tar-best-practices.md
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput

## Delivered checkpoint

`releases/DeusKVM-Companion-win-x64-2026-09-23.exe` is **910,336 bytes (0.91 MB)**.
SHA-256: `d31d7131af1cd896e9420c21a62febba5e08cfc88b2c080ec9cd3b7b7e55115a`.
It is a single Windows x64 EXE; nothing needs to sit beside it. The installed
runtime payload remains 1,871,605 bytes across 14 files in Program Files.

Validation: 172 portable tests pass, two Windows-only CNG tests remain skipped
on macOS, and both net48/net10.0 test targets compile with zero warnings/errors.
The launcher builds without warnings/errors. The publish verifier reads resources
from the actual PE, checks Windows x64 and the five OS assembly references,
executes the exact extraction code, and checks all 14 runtime hashes against
published files. The delivered EXE matches that verified output byte-for-byte.
Shell syntax and diff whitespace checks pass. The Windows CI lifecycle script is
updated to run this EXE from a directory without adjacent DLLs/config, compare
installed files against the payload, and check that staging directories are gone.
That Windows job has not been executed here.

Companion/Core source is unchanged from `86676e6`; rebuilt binaries carry the
current source revision metadata. The working Framework ZIP is retained in
`releases/Previous/DeusKVM-Companion-win-x64-framework-2026-09-23.zip` with its
original SHA-256 verified. No running app or service was installed/restarted.

Next user action: open the EXE on Windows and approve its usual update prompt,
then check reopening, edge/hotkey switching and paste. Keep the current Mac app.
Wait for user setup/testing before calling the new launcher Windows-validated.
