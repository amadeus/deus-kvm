# Windows package size investigation — 2026-09-23

User requested a much smaller Windows app, ideally 1–2 MB total, while asleep.
Preserve the tested input/clipboard implementation and current extract/open/update
experience. Do not disguise a runtime prerequisite as a total size reduction.

**Follow-up recommendation:** try an isolated .NET Framework 4.8 port before a
native rewrite. Windows already supplies that runtime on the app's minimum
Windows 10 2004 / Windows 11 target. The dependency probe below measures 0.54 MB
zipped, making a 1–2 MB release plausible, but a complete port is not yet built
or tested. This supersedes the first pass's native-prototype-first recommendation.

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

### Tracked phases

- [x] **1 — Dependency feasibility:** verify OS inclusion/API route, compile the
  representative dependency probe, measure all output and record compatibility
  gaps. Probe builds with zero compiler warnings/errors; ZIP integrity passes.
- [ ] **2 — Isolated core port:** retain the working release; introduce narrowly
  scoped compatibility adapters, preserve protocol/policy behavior, and run core
  tests on both the current runtime and Framework on Windows. Prove encrypted
  file interoperability with the current Mac before any replacement release.
- [ ] **3 — Windows integration:** prove BLE ownership/reconnect, all worker modes,
  service startup before/after login, Raw Input, tray, hotkey/edge return, text and
  OLE on-demand file clipboard. This requires actual Windows execution/hardware.
- [ ] **4 — Packaging and update:** migrate EXE-only install/update/rollback/removal
  to the complete protected payload; test upgrade from the current installed app
  on a disposable Windows machine. Measure full ZIP and installed footprint.
  Target <= 2,000,000 ZIP bytes; report actual results even if the target is missed.
- [ ] **5 — User checkpoint:** prepare a clearly named Windows test ZIP in visible
  `releases/`, retain a recoverable known-good build, then wait for the user to
  set up and test both Macs. Check file transfers up to 2 GB, ownership stealing,
  idle CPU and pointer responsiveness. No user hardware tests have passed yet.
- [ ] **6 — Adopt:** only after those checks, switch normal publish/CI/docs and
  prune superseded release artifacts. Commit each verified implementation phase.

No production source, running app, service or release was changed by this follow-up.
The probe is research tooling, not a build for the user to install.

Sources checked 2026-09-23:
- https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies
- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps
- https://learn.microsoft.com/en-us/windows/win32/seccng/cng-algorithm-identifiers
- https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts/10.0.19041.1
- https://www.nuget.org/packages/System.Text.Json/10.0.12
- https://www.nuget.org/packages/System.Threading.Channels/10.0.11

## Reproduction

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

## Delivered checkpoint

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

The 1–2 MB total goal is **not achieved** by this packaging change. The measurements
and dependency migration options above explain the remaining work.
