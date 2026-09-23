# Direct-pointer prototype — 2026-09-22

> **Withdrawn — 2026-09-22:** The user reports the pointer experiments do not
> work well and requests their removal. Both the speed slider and direct-pointer
> mode are removed. The material below is historical; its builds and test
> instructions are superseded by AUTOMATIC_MAC_CHECKPOINT.md.

Approved: try option 2 from POINTER_FEEL_RESEARCH.md. Preserve the existing HID
mode and all global Windows mouse settings. macOS packages remain arm64-only.

## Phases

- [x] Inspect capture, companion framing, desktop injection and ownership cleanup.
- [x] Add opt-in Mac setting and capability-negotiated direct entry.
- [x] Send ordered mouse movement/buttons/scroll over the companion channel;
      retain keyboard HID. Bound pending traffic and preserve cumulative travel.
- [x] Inject absolute Windows desktop coordinates; observe return edges, rebase
      on actual cursor position, reject stale sessions, release injected buttons.
- [x] Verify protocol, routing, fractional travel and backpressure; inspect cleanup;
      run both platform suites, lint and release builds; commit verified work.
- [x] Publish dated visible Windows x64 and Mac arm64 ZIPs with receipts.
- [ ] User hardware checkpoint: slow/fast feel, drags, scrolling, edge/hotkey,
      both Macs/takeover, physical Windows mouse, DPI/multiple displays and loss.

## Boundaries

This is an experimental normal-desktop mode, off by default. It bypasses Windows
relative acceleration; it does not yet establish an exact native macOS response
curve. The existing speed multiplier remains useful for scale. Test initially at
1.00x. Unsupported companions/unavailable desktop use HID on entry. A direct
session failure returns locally rather than mixing two mouse transports mid-drag.
Elevated apps/secure desktop may block SendInput: use HID mode for those cases.
No global pointer settings are written, even temporarily.

## Protocol and safety

Capability `directPointer: 1`; new direct entry and mouse/ack messages. Cumulative
signed wrapping 24.8 source coordinates retain fractional motion and high-speed
travel. Mouse packets carry button state and discrete scroll; motion-only pending
samples coalesce, button/scroll transitions do not. One packet in flight awaits
Windows injection acceptance, limiting BLE/desktop backlog. A bounded pending queue and
ack timeout return locally. Entry is explicitly acknowledged before mouse packets.
Windows scopes injection to the current handoff and attempts injected-button release on
exit, reset, desktop loss and pipe shutdown. Worker shutdown precedes ownership
transfer. Hardware failure handling remains unverified until the checkpoint.


### Wire layout (control stream, little endian)

- `ENTER_DIRECT` (`0x1C`): switch ID, edge, fraction u16, center boolean (5 bytes).
- `POINTER` (`0x1D`): switch ID, cumulative X i32, cumulative Y i32, buttons u8,
  wheel i8, horizontal scroll i8, serial u8 (13 bytes; one 20-byte BLE frame).
- `POINTER_ACK` (`0x1E`): switch ID, serial, success boolean (3 bytes). Serial
  zero acknowledges entry; mouse serials increment modulo 256 from one. Entry
  versus movement acknowledgements are scoped by the local session state.
- Coordinates have eight fractional bits and wrapping arithmetic. Windows adds
  their difference to the actual cursor in physical desktop pixels, retains the
  fractional remainder, and normalizes against the entire virtual desktop.
  PerMonitorV2 prevents DPI virtualization; no automatic Mac/Windows DPI gain or
  inferred native Mac acceleration curve is introduced.
- A half-second mouse heartbeat preserves held state without replaying scroll.
  Two-second missing acknowledgements return the Mac locally. The Windows
  one-second maintenance tick expires inactive direct input after three seconds.
  Unsent motion coalesces; discrete transitions are bounded at 64 pending samples.
  Acknowledgements immediately flush waiting input instead of adding a timer wait.
- Reset/display changes invalidate direct mode; it is never resumed blindly after
  worker recreation. The user can re-enter or turn the option off for HID.

## Verification — 2026-09-22

- Mac: 91 tests passed, including direct/HID exclusivity, retained keyboard HID,
  signed/fractional and coalesced travel, click/drag/scroll ordering, stale ACKs,
  queue overflow, timeout, stopped-session rejection and idle scroll non-replay.
- .NET: 116 tests passed, including the shared wire vector, coordinate/serial
  wrap, fractions, stale sessions and negative-origin desktop normalization.
- Windows release compile: zero warnings/errors. SwiftLint strict and SwiftFormat
  checks pass. Both platform release builds pass.
- Native Windows SendInput, edge timing, actual transport latency, graceful worker
  release and takeover are not executable on this Mac. They require the hardware
  checkpoint. SendInput can reject releases on a protected desktop; live workers
  retry failed releases. Forced worker termination/OS failure can prevent cleanup.
- The speed slider was already reported functional. Direct-pointer feel and exact
  Mac curve matching remain unverified, as do the earlier two-Mac lag cases.


## Release receipts

Visible `releases/` packages, ZIP CRCs verified:

- `DeusKVM-mac-arm64-direct-pointer-2026-09-22.zip` — 962,663 bytes;
  SHA-256 `e499fee92a3b144230e4b795e5e7ed5ed8ca5e5257d26d2596e015d01e90f452`.
  Extracted executable is arm64 only; strict deep code-signature verification passed.
- `DeusKVM-Companion-win-x64-direct-pointer-2026-09-22.zip` — 52,400,283 bytes;
  SHA-256 `b3f6178c865856df4dc7efe7a07f0e8194841d8e27ba7890d52a7639fe2cdc24`.
  PE architecture is x64; ZIP contains the executable, README and checkpoint.

Generic latest ZIP aliases contain the same builds. Await user installation/setup
before the hardware walkthrough. No hardware phase is marked passed by this commit.
