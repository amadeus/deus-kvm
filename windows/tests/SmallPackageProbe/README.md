# OS-runtime dependency probe

This is an isolated size/compilation experiment, **not the companion app**. It
targets Windows x64 and the .NET Framework 4.8 runtime supplied by Windows 10
2004+ / Windows 11. It is not part of the solution, CI or normal release publish.

It references WinForms, ServiceBase, BLE enumeration/GATT/session APIs, JSON and
Channels. The build includes every transitive runtime DLL and binding redirects;
the WinRT contracts are compile-time references supplied by Windows at runtime.
No modern .NET runtime or managed Windows SDK projection is bundled.

From the repository root, using the repo-local SDK/cache:

```sh
DOTNET_CLI_HOME="$PWD/.build/dotnet-home" \
NUGET_PACKAGES="$PWD/.build/nuget" \
.build/dotnet/dotnet build windows/tests/SmallPackageProbe/SmallPackageProbe.csproj \
  -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false \
  -o .build/windows-small-probe/repro
python3 windows/tests/SmallPackageProbe/measure.py \
  .build/windows-small-probe/repro .build/windows-small-probe/repro.zip
```

Use a clean output directory when changing dependencies. `measure.py` includes
all output except PDB symbols and XML API documentation; it verifies ZIP CRCs.

On Windows, opening the EXE displays a form. Only clicking its button queries
paired BLE devices and briefly connects to the first one to discover services.
It never pairs, installs a service, changes ownership or touches the clipboard.
The empty service subclass checks the dependency reference only.

Compilation and ZIP size are verified on macOS. Windows launch, Bluetooth
operation and service execution are **not yet tested**. This probe does not
include the full app, encryption, installer, Raw Input or OLE file clipboard;
its measured size must not be reported as a completed port's release size.
See `docs/WINDOWS_SIZE_PLAN.md` for migration gates and outstanding validation.
