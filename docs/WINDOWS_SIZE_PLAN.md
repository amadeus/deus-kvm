# Windows package size investigation — 2026-09-23

User requested a much smaller Windows app, ideally 1–2 MB total, while asleep.
Preserve the tested input/clipboard implementation and current extract/open/update
experience. Do not disguise a runtime prerequisite as a total size reduction.

**Current checkpoint:** the complete .NET Framework 4.8 port is built and
packaged for Windows x64. It uses Windows' runtime and compiles the existing
Bluetooth, service, tray and clipboard implementation. Local regression tests
pass; actual Framework/Windows hardware execution remains pending. See the
implementation checkpoint at the end and `WINDOWS_FRAMEWORK_CHECKPOINT.md`.
The investigation below preserves the earlier measurements and decision history.

## Completed investigation

- [x] Inventory the self-contained publish and separate application code from dependencies.
- [x] Measure single-file compression, ZIP-only compression, English satellite
  resources and a framework-dependent build in isolated `.build/windows-size/` outputs.
- [x] Check current supported trimming and native compilation constraints.
- [x] Apply the safe packaging reduction to the normal project/publish path.
- [x] Run regression tests, publish and verify the final ZIP, commit.
- [ ] Windows hardware: launch/update, service startup, tray, pairing/ownership,
  edge/hotkey return and two-way text/file paste. Existing lifecycle CI remains
  applicable, but was not run remotely for this local change.

## Measured contents

SDK: .NET 10.0.401. Unbundled Release win-x64 self-contained publish:

| Group | Bytes |
| --- | ---: |
| DeusKVM EXE, assemblies and dependency/config manifests | 672,270 |
| Runtime, framework and third-party/API dependencies | 139,277,080 |
| Framework translation satellites | 8,950,232 |
| Total | 148,899,582 |

Largest individual dependencies: Microsoft.Windows.SDK.NET.dll 24,877,600;
System.Private.CoreLib.dll 16,033,576; System.Windows.Forms.dll 13,715,280;
System.Private.Xml.dll 7,788,328; System.Windows.Forms.Design.dll 6,092,584;
coreclr.dll 4,614,992 bytes. The application code is not the bulk of the download.
Mac's small app uses OS-supplied frameworks; the Windows build ships its managed
runtime/frameworks to avoid prerequisites.

## Apples-to-apples size experiments

These ZIP measurements contain only the EXE and use standard ZIP/Deflate level 9.
The user release also contains a few small Markdown instructions.

| Variant | EXE bytes | ZIP bytes | Result |
| --- | ---: | ---: | --- |
| Existing self-contained, compressed bundle, all satellites | 58,310,179 | 52,388,876 | Baseline |
| Same, English satellites only | 55,779,346 | 49,860,139 | Selected; about 4.8% smaller ZIP |
| English, no internal bundle compression, ZIP only | 132,990,353 | 49,952,624 | Worse ZIP and much larger installed EXE |
| Framework-dependent single-file, English | 26,952,864 | 6,754,467 | Requires separately installed .NET 10 Desktop Runtime |

The framework-dependent build is an experiment, not a user release. It is still
above 1–2 MB and moves most bytes into a separate runtime installation. It cannot
use single-file compression (SDK error NETSDK1176). The normal release retains
self-contained single-file compression and all runtime/API assemblies.

The companion UI already uses English strings. `SatelliteResourceLanguages=en`
removes translated framework resources only; other Windows display languages use
English fallback framework messages. ZIP compression is raised from level 6 to 9.
No runtime code or features were trimmed, and no new prerequisites were added.

## Why not turn on trimming or Native AOT?

Microsoft currently disables trimming for Windows Forms because its built-in COM
marshalling cannot be analyzed reliably. This app also uses COM-visible OLE
virtual-file objects (`VirtualFileClipboard.cs`), dynamic WScript shortcut creation
(`ServiceInstaller.cs`), reflection-based JSON serialization and the broad WinRT
SDK projection for Bluetooth. Bypassing the SDK's trimming guard would produce an
unsupported binary whose successful build says nothing about clipboard/service
correctness. We did not bypass it or manually delete runtime assemblies.

Native AOT also requires a Windows build toolchain for a Windows target; the
supported SDK cannot cross-compile a Windows Native AOT app on this Mac.

Sources checked 2026-09-23:
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities
- https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile

## Native alternatives (not implemented)

A 1–2 MB total target is unproven with all current functionality. Achieving it
requires a different dependency architecture, not another ZIP setting.

1. Build a separate native Win32/C++/WinRT feasibility prototype on Windows with
   just service startup, BLE discovery/connect and a tray. Measure its standalone
   Release size before committing to a migration. C++/WinRT binds to OS APIs rather
   than shipping the managed Windows SDK projection and .NET Desktop Runtime.
2. Prove the hard paths in that prototype: BLE recovery before/after login,
   ownership, raw-input edge detection, OLE on-demand file streams, networking
   and authenticated file framing. Reuse the current wire protocol and fixtures.
3. Only if the size/functionality checkpoint succeeds, port installation/removal,
   UI and remaining policies. Run the existing lifecycle/hardware checklist against
   both implementations before replacing the working companion.

An alternative is a trim/AOT-compatible C# redesign: replace Windows Forms, use
source-generated JSON/COM interop and validate WinRT AOT support. That retains
more policy code but its final size must be measured; it is not a promised 1–2 MB
solution. No speculative native rewrite was merged into the tested companion.

## Follow-up: use the runtime Windows already supplies

The first investigation considered framework-dependent **modern .NET**, but
missed **.NET Framework 4.8**. These are different runtimes. Windows 10 2004+
includes Framework 4.8; Windows 11 includes 4.8 or its compatible 4.8.1 update.
This route would use the OS runtime, rather than ask the user to install .NET 10.
Microsoft documents calling WinRT from Framework through
`Microsoft.Windows.SDK.Contracts`. This avoids shipping both the modern runtime
and `Microsoft.Windows.SDK.NET.dll` while retaining C#, WinForms and built-in COM.

### Measured feasibility checkpoint

Reproducible source: `windows/tests/SmallPackageProbe/` (separate from the app).
Release build with .NET SDK 10.0.401, net48, x64:

| Contents | Bytes |
| --- | ---: |
| Probe EXE + binding-redirect config | 11,614 |
| JSON, Channels and all transitive runtime DLLs | 1,395,656 |
| Total unpacked runtime files | 1,407,270 |
| ZIP, Deflate level 9 | 542,323 |

The probe compiles actual calls to paired-device enumeration,
`BluetoothLEDevice`, `GattSession.MaintainConnection`, uncached GATT discovery,
WinForms and JSON/Channels. It also references `ServiceBase`; it does not run a
service. No WinMDs, modern runtime or Windows SDK managed projection are emitted
as runtime dependencies. Measurement excludes only compiler symbols and XML API
documentation, includes all emitted DLLs/config, and checks ZIP CRCs.

**This is a dependency budget, not the full companion's size.** The original app's
own code is small enough that a couple-MB ZIP is now a reasonable target. Final
size, runtime compatibility, CPU usage and performance remain unproven.

### Port scope and tradeoffs

An isolated compile of the existing source against net48 immediately identifies
missing `AesGcm`, `TimeProvider`, `IReadOnlySet<T>` and a COM `STATSTG` ambiguity.
That build deliberately fails; its first errors are not a complete migration
inventory. Source inspection also identifies these work areas:

- Replace newer runtime helpers (process path, timeout waits, exact stream reads,
  memory-based I/O, random/hash/hex helpers, range APIs, pipe ACL creation).
  Preserve cancellation, timeouts, bounded queues and existing event-driven work.
- Keep AES-256-GCM and the current HKDF/framing protocol. Use Windows CNG for
  authenticated encryption and a tested HMAC-SHA256 HKDF adapter; do not implement
  AES or weaken authentication. Verify byte-for-byte vectors against the existing
  implementation, including rejected tags, truncation and nonce sequencing.
- Keep the WinForms/service/OLE architecture, with Framework startup/DPI setup,
  record compatibility and explicit COM types. Verify WinRT cancellation and
  disposal semantics on Windows instead of assuming the two projections match.
- The installer currently stages and hashes one EXE. The small build has DLLs and
  an EXE config. Stage, validate and roll back the entire versioned application
  directory with the same protected ACLs; preserve settings/service identity and
  test removal. Do not ship an EXE-only update that silently omits dependencies.

Framework is an older, Windows-only runtime. Microsoft continues servicing it
but recommends modern .NET for new development. We would accept some compatibility
code and an older runtime to remove the download overhead. It is **not** a CPU
optimization; latency, allocations and idle behavior need separate measurement.
Native C++/WinRT stays the fallback if critical Framework runtime paths fail.

### Tracked phases (implementation checkpoint)

- [x] **1 — Dependency feasibility:** verify OS inclusion/API route, compile the
  representative dependency probe, measure all output and record compatibility
  gaps. Probe builds with zero compiler warnings/errors; ZIP integrity passes.
- [x] **2 — Core port implementation:** retain net10.0 as a regression target,
  add net48, compatibility adapters and Windows CNG AES-GCM. Shared protocol
  vectors and the HKDF RFC vector pass locally using the reference AES backend.
  Both test targets compile; Framework execution and native CNG tests remain open.
- [x] **3 — Windows integration implementation:** the complete app compiles with
  WinRT contracts, Framework services/WinForms, per-monitor DPI configuration,
  COM/OLE objects, worker jobs and secure named pipes. Core control policies and
  event-driven behavior are retained.
- [ ] **3 — Windows runtime validation:** BLE ownership/reconnect, all worker
  modes, service startup before/after login, Raw Input, tray, hotkey/edge return,
  text and OLE on-demand file clipboard. Requires Windows/hardware execution.
- [x] **4 — Packaging/update implementation:** stage and SHA-256-check every
  payload file before stopping the service, preserve destination ACL inheritance,
  and restore replaced files on failure. Local tests cover legacy single-EXE
  rollback, full-package update, obsolete DLL removal, partial activation failure,
  damaged downloads and unsafe manifest names. Publish verifies ZIP/hash contents.
- [ ] **4 — Windows installer validation:** existing lifecycle CI now verifies
  every installed dependency and its ACL. Actual install/update/removal and
  downgrade still need execution on Windows. Multi-file replacement rolls back
  caught errors; it is not an atomic transaction across an OS/power crash. Backup
  directories are retained if rollback itself fails.
- [x] **5 — User build ready:** create the small ZIP in visible `releases/`, retain
  the previous Windows ZIP in `releases/Previous/`, and include `START_HERE.md`.
- [ ] **5 — User hardware checkpoint:** wait for setup/testing of both Macs;
  verify 2 GB file behavior, ownership, edge return, CPU and pointer responsiveness.
- [x] **6 — Build pipeline:** normal publish, README and Windows CI target the new
  Framework build. The old release stays available as a fallback during testing.
- [ ] **6 — Accept as validated replacement:** await Windows tests before removing
  the fallback or describing the migration as hardware-verified.

The implementation follows the user's subsequent request to produce a usable
new build. No running Mac app or Windows service was changed from this machine.

Sources checked 2026-09-23:
- https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies
- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps
- https://learn.microsoft.com/en-us/windows/win32/seccng/cng-algorithm-identifiers
- https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts/10.0.19041.1
- https://www.nuget.org/packages/System.Text.Json/10.0.12
- https://www.nuget.org/packages/System.Threading.Channels/10.0.11

## Historical .NET 10 experiment reproduction

These commands apply to the pre-port source at `98d7fe6`. For the current build,
use `windows/publish.sh win-x64` and the Framework checkpoint instructions.

Use the repo-local dotnet with `DOTNET_CLI_HOME=.build/dotnet-home` and
`NUGET_PACKAGES=.build/nuget`. Sequentially run `dotnet publish` on
`windows/DeusKVM.Companion/DeusKVM.Companion.csproj` with:

- `-c Release -r win-x64 --disable-build-servers -m:1 -p:UseSharedCompilation=false`
- `-p:DebugType=None`, a separate `-o .build/windows-size/<variant>` per experiment.
- Inventory: `--self-contained true -p:PublishSingleFile=false -p:SatelliteResourceLanguages=`.
- Baseline: `--self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=`.
- English: baseline with `-p:SatelliteResourceLanguages=en`.
- ZIP-only: English with `-p:EnableCompressionInSingleFile=false`.
- Framework-dependent: ZIP-only with `--self-contained false`.

ZIP each EXE with Python `zipfile.ZIP_DEFLATED, compresslevel=9`. Experimental
outputs remain under ignored `.build/`; only the supported release is delivered.

## Historical first delivered checkpoint

`DeusKVM-Companion-win-x64-smaller-2026-09-23.zip`: 49,873,502 bytes (49.87 MB decimal),
SHA-256 `2e8805ca145d54bbe12a32ce368cc3a7b91021c3818a4707aa53a39d93522072`. Previous full release ZIP: 52,416,716 bytes;
saving: 2,543,214 bytes (4.85%). Installed EXE:
55,779,346 bytes, down from 58,310,179 bytes. The final EXE is byte-identical to
the measured English-resource experiment. Its PE header identifies Windows x64.
ZIP CRC validation passes and its EXE matches the publish output byte-for-byte.

All 143 Windows core tests pass. Release publish, `bash -n windows/publish.sh`
and `git diff --check` pass. No Windows hardware or Windows lifecycle CI run was
performed locally. No app or service was installed/restarted during this task.
Only the current Windows ZIP and existing arm64 Mac ZIP remain in `releases/`.

To test: extract the Windows ZIP and launch `DeusKVM.Companion.exe`, approving its
normal update prompt. Keep the current Mac app. Verify tray/settings, service
restart, edge/hotkey return, ownership switching, text and on-demand files in both
directions. The only intended visible change is English fallback for framework
messages on non-English Windows installations. Hardware validation stays open.

The first resource-only packaging change did **not** achieve the 1–2 MB goal.
The subsequent Framework implementation below does meet the size target; runtime
validation remains separate.


## Full Framework test build — 2026-09-23

Delivered `releases/DeusKVM-Companion-win-x64-framework-2026-09-23.zip`:

- ZIP: **830,660 bytes (0.83 MB)**, versus the prior 49,873,502-byte ZIP (98.33% smaller).
- Runtime payload: **1,871,605 bytes (1.87 MB)** in 14 EXE/DLL/config files.
- Entire extracted archive, including manifest and instructions: **1,891,301 bytes**.
- Main EXE: **323,072 bytes**; Windows x64 PE, Framework 4.8 target.
- SHA-256: `808d66c35ca657663fe2a645a79172f5a754e94e3cc894c1187cac80d4e5f273`.

All 158 portable regression tests pass; two native Windows CNG tests are explicitly
skipped on macOS. The complete solution, including net48 and net10.0 tests, builds
with zero warnings/errors. ZIP CRC, each manifest hash, packaged/published file
identity, PE architecture, shell/Python packaging syntax and diff whitespace
checks pass. The new HKDF adapter matches both the RFC 5869 test vector and the
existing shared Swift/Windows encrypted protocol vectors with the .NET 10 AES
backend. Actual Windows CNG execution remains pending; net48 tests run that backend.

The production migration includes no protocol changes or Mac changes. Crypto
uses Windows CNG with 32-byte keys, 12-byte sequence nonces and 16-byte tags, using
the same HKDF context and record framing. Stream cancellation closes an outstanding
Framework socket/pipe operation to preserve deadlines without polling. Service
shutdown terminates its owned job and drains redirected output; Framework process
exit waits use events. The installer verifies the full staged payload before
interrupting the old app, and rolls back replaced files if activation/configuration
fails. Existing app settings and service identity stay in their previous locations.

The previous large Windows ZIP is retained at
`releases/Previous/DeusKVM-Companion-win-x64-smaller-2026-09-23.zip`, with its original
SHA-256 verified. The current arm64 Mac ZIP is unchanged. This build is ready for
the user checkpoint, not hardware-validated: Windows CI/lifecycle, Framework runtime,
BLE, clipboard, login and downgrade checks have not been executed on this Mac.
Do not delete the fallback until the user confirms the new build.

Implementation references:
- https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/ns-bcrypt-bcrypt_authenticated_cipher_mode_info
- https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptgeneratesymmetrickey
- https://www.rfc-editor.org/rfc/rfc5869


## User confirmation and single-EXE packaging

The user reported that the smaller build worked on 2026-09-23. This confirms
basic functionality on their setup; it does not enumerate every stress, login,
2 GB transfer or lifecycle case above. The next authorized step is a single
small downloadable EXE. See `WINDOWS_SINGLE_EXE_PLAN.md`; the companion runtime
source is unchanged, and the confirmed Framework ZIP is retained as fallback.

## CI test follow-up (2026-09-25)

Run 36222730075 stopped on Swift formatting in two smoke-test scripts and a
two-second timeout in the Framework cancellation test. The unchanged Windows
tests passed in run 36223316374, confirming that timeout was intermittent.

- [x] Format both Swift smoke-test scripts so the repository-wide lint gate passes.
- [x] Give the deliberately blocking fake file reader a dedicated thread. Wait
  for its startup before disposing the session, then require it to finish with
  IOException. Use separate ten-second startup/completion deadlines and release
  the fake reader in cleanup even when an assertion fails.
- [x] Full SwiftFormat and strict SwiftLint checks pass; all 95 macOS tests pass.
- [x] .NET 10 tests: 172 passed, two Windows-only CNG tests skipped on macOS.
- [x] Framework 4.8 test assembly builds with zero warnings/errors.
- [ ] Run the revised Framework tests on Windows CI; local compilation does not
  establish Framework runtime success. Existing hardware checkpoints remain pending.

Production clipboard and cancellation code is unchanged.
