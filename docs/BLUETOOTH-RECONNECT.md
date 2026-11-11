# Bluetooth reconnect investigation

> Status 2026-11-11: the recommendation below shipped. The Windows companion
> service reconnects previously verified Macs itself (uncached GATT discovery and
> `GattSession.MaintainConnection`), so the PowerShell workaround is only a manual
> fallback. Remaining reconnect checks are tracked in `windows/README.md`.

## Verified result

On 2026-09-13, Windows uncached GATT discovery restored the existing Maingear
connection to the signed macOS DeusKVM build without removing or re-pairing
the device. Windows reported `paired: True`, then `Uncached discovery: Success`
and a transition from `Disconnected` to `Connected`. The Mac log recorded the
host reading the rebuilt HID services and subscribing to the battery and all
four HID input reports. Amadeus confirmed edge switching/control worked both
during the diagnostic and after its process exited.

The saved pairing remains usable. Automatic reconnect/service rediscovery is
the failing path. The exact Windows cache/driver decision remains an inference;
the Mac log does not expose Windows' internal state.

## Windows service checkpoint

On 2026-09-13, Amadeus installed the first Windows companion service build
(`faa9d9d`) and confirmed that restarting the Mac DeusKVM app restored control
using the existing pairing. The companion UI showed Service Running, Bluetooth
Discovered, and successful uncached discovery with BLE Connected. Automatic
recovery across a normal Mac quit/relaunch is now user-verified while Windows
is signed in; the standalone PowerShell script is no longer needed for that
case. Signed-out recovery, boot before first login, and sleep/radio cycles
remain separate validation gates. See `windows/README.md` for those checks.
(Update 2026-09-14: control at the login screen after a Windows reboot was
user-confirmed. Restarting the Mac app while Windows is signed out is still
untested.)

## Reproduction and evidence

1. Pair from Windows: HID control works and Setup shows one subscribed host.
2. Quit DeusKVM normally: `bluetoothd` removes its published HID services,
   sends service-change indications that Windows acknowledges, then locally
   disconnects the encrypted BLE link as unused.
3. Relaunch: services install and advertising starts, but Windows does not
   automatically reconnect or subscribe. A manual ordinary Bluetooth reconnect
   reached Classic AVRCP rather than BLE HID. Cycling Bluetooth off/on in
   Windows did not restore HID either.
4. Run uncached discovery from Windows with the existing pairing: BLE connects,
   the Mac's existing Service Changed workaround runs, Windows subscribes again,
   and control remains usable after the diagnostic exits.

Toggling Force Service Changed on the Mac while disconnected did not recover
Windows. (The toggle is now hidden from settings; its automatic fallback stays
on by default.) It can help rediscovery once a BLE connection exists, but cannot
notify a disconnected client.

## State restoration experiment

A stable `CBPeripheralManagerOptionRestoreIdentifierKey` and a handler that
reused restored services/subscriptions did not fix normal quit. The daemon
reported `persistence: on` but still removed the services and disconnected the
host; relaunch did not call `willRestoreState`. That experiment was removed.
The original input implementation builds and its ten tests pass.

## Mac-only recovery tests with the existing connection

Further live tests on 2026-09-13 preserved the existing pairing and the PC's
Classic connection. No Windows discovery script or re-pair was requested
during these tests.

- `system_profiler` and `bluetoothd` identified MAINGEAR's connected services
  as `AVRCP ACL`; the daemon reported both Classic and BLE pairing records.
  This confirms that the general Connected status can remain true while the
  BLE connection used by HID is absent.
- A temporary signed build inspected each input characteristic's native
  `CBMutableCharacteristic.subscribedCentrals` array. All were empty, matching
  DeusKVM's own tracking. This ruled out merely missing a subscription callback
  in this session.
- At 12:13:42 PDT, the build retrieved the known PC with
  `CBCentralManager.retrievePeripherals(withIdentifiers:)` and called `connect`.
  The daemon explicitly reported `Classic GATT service is not supported` for
  this PC. It then attempted BLE instead; the peer remained `.connecting`
  after thirty seconds, and the diagnostic canceled its pending request.
  There were no HID reads or subscriptions.
- A Service Changed cycle after publishing the services did not recover HID.
- At 12:15:13 PDT, another build advertised the short `1812` UUID while keeping
  the installed services and report format unchanged. The daemon confirmed
  connectable advertising with `0x1812`; native subscribers remained empty
  and Windows did not recover. The original 128-bit advertisement was restored.

The macOS 26.5 SDK explicitly marks `registerForConnectionEvents` unavailable
on macOS. The transport-bridging option also addresses the opposite direction:
bringing up Classic profiles after an LE connection, rather than creating an
LE HID connection from an existing Classic connection.

These tests found no working Mac-only recovery through the examined public
CoreBluetooth APIs. They do not prove that every possible Mac-side solution
is impossible, nor do they reveal Windows' internal driver/cache state.
The verified recovery remains Windows-initiated uncached service discovery.
All temporary connection probes and advertising changes were removed; the
HID implementation remains unchanged.

## Manual workaround (before the companion)

Superseded by the companion's automatic recovery; kept for manual diagnosis.

Keep DeusKVM advertising on the Mac. Copy
`scripts/Test-DeusKVMConnection.ps1` to the PC and run it in a separate Windows
PowerShell 5.1 process:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-DeusKVMConnection.ps1
```

The execution-policy override applies only to this process, without changing
saved user/machine policy. Select the existing paired Mac entry. The script
prints cached/fresh discovery results and holds its references for twenty
seconds; process exit releases its temporary handles. It does not pair,
unpair, write HID reports or install anything. Successful recovery has been
verified in one live run. (At the time it was not automatic; the companion now
performs this discovery itself.)

Device enumeration needs a compiled C# `IEnumerable` bridge because Windows
PowerShell's WinRT collection indexing/method binding is unreliable here.
Optional service-vector printing is omitted because Windows exposes that
vector as an unprojected COM object. Local syntax/selection/status regressions
pass with `scripts/tests/Test-DeusKVMSelection.ps1` under portable PowerShell
7.6.6 on macOS; the complete script has now also run successfully on Windows.

## Automatic recovery recommendation

(Implemented in the Windows companion; see the status note at the top.)

Given the tested results, include reconnect/service rediscovery in M3's Windows
companion. This is the practical verified direction, rather than a proof that
a companion is fundamentally required by Bluetooth. Retain the
chosen paired BLE endpoint, request a connection when the Mac is available,
and refresh services when needed. Microsoft's documented options include
uncached discovery and `GattSession.MaintainConnection`. Validate ordinary Mac
quit/relaunch and Windows sleep/radio cycles before claiming automatic recovery.
Keep the working HID descriptor, report encoding and input path unchanged.

References:

- [Apple: restored peripheral services](https://developer.apple.com/documentation/corebluetooth/cbperipheralmanagerrestoredstateserviceskey)
- [Microsoft: Bluetooth GATT client connection behavior](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)
- [Microsoft: Bluetooth service cache](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothcachemode)
- [Apple: transport bridging direction](https://developer.apple.com/documentation/corebluetooth/cbconnectperipheraloptionenabletransportbridgingkey)
