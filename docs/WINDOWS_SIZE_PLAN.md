# Windows package size investigation — 2026-09-23

User requested a much smaller Windows app, ideally 1–2 MB total, while asleep.
Preserve the tested input/clipboard implementation and current extract/open/update
experience. Do not disguise a runtime prerequisite as a total size reduction.

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

## Route to a genuinely small standalone app (not implemented)

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
