# Pointer feel without changing Windows mouse settings

> **Withdrawn — 2026-09-22:** The user reports the pointer experiments do not
> work well and requests their removal. Both the speed slider and direct-pointer
> mode are removed. The material below is historical; its builds and test
> instructions are superseded by AUTOMATIC_MAC_CHECKPOINT.md.

Researched 2026-09-22. The user subsequently requested the speed slider; see
implementation notes below. Acceleration-curve and direct-position modes remain proposals.
The current lag checkpoint is deferred until the user's next test session.

## Requirement

Improve how Mac-forwarded pointer movement feels on Windows while preserving
Windows' existing pointer speed and acceleration for directly attached mice.
Settings should apply only to DeusKVM's outgoing movement and be saved per Mac.
Do not temporarily modify Windows mouse settings during forwarding either.

## Source findings before the slider

- InputTap captures movement from the Quartz session event tap and forwards
  mouseEventDeltaX/Y through DirectInputEvent to DirectInputController.
- DirectInputEvent converts each axis to Int8 and clamps it to -127...127.
  MouseReport then sends relative HID movement over Bluetooth. There is no
  DeusKVM sensitivity multiplier or acceleration curve today.
- Consequently, movement exceeding that per-event range loses distance. This is
  established by source inspection, but its frequency and contribution to the
  user's perceived mismatch are unmeasured. Do not call this the confirmed cause.
- HIDNotificationQueue replaces adjacent pending pure-motion reports with the
  newest one during backpressure. Motion is relative, so this can also discard
  travel. It is existing behavior; investigate with measurements before changing
  queue semantics or generating additional Bluetooth packets amid the lag report.
- The exact acceleration already represented in the captured Quartz deltas has
  not been established. Do not claim proven double acceleration or promise that
  multiplying those deltas exactly reproduces macOS tracking.

## What other software does

- [ShareMouse output settings](https://www.sharemouse.com/doc/settings/output/)
  exposes pointer and scroll speed adjustments. Its public page establishes the
  feature, not the underlying transformation or an exact macOS matching curve.
- [Deskflow Windows implementation](https://github.com/deskflow/deskflow/blob/master/src/lib/platform/MSWindowsDesks.cpp)
  has an absolute-coordinate injection path in deskMouseMove. Its separate
  deskMouseRelativeMove path reads Windows mouse parameters, temporarily changes
  them for relative injection, and restores them. That latter technique violates
  this task's constraint, even if the original settings are restored afterward.
- [Microsoft MOUSEINPUT documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)
  distinguishes relative injection, affected by pointer settings, from absolute
  normalized desktop coordinates. This documents an injection API, not a
  per-device acceleration override for our existing Bluetooth HID mouse.
- [Input Director's mirroring options](https://inputdirector.com/quickstart-usage.html)
  also distinguish absolute and relative movement. Those documented options are
  specifically for mirroring; do not assume identical behavior in normal handoff.

## Recommended staged design

1. First verify the prepared lag fix. Compare slow precision movement with fast
   sweeps, using the same source mouse/trackpad and noting the two display scales.
   Separate overall speed, speed-dependent feel, lost distance, and stutter.
2. Add a Mac setting named **Windows pointer speed**, with a proposed 0.25x–2x
   range, default 1x, and Reset. Transform only captured remote movement. Leave
   local Mac motion, scroll/buttons, and Windows system settings alone. This is
   the smallest useful tuning control, but cannot by itself match an entire
   acceleration curve.
3. Preserve sub-count fractions when scaling down so slow movements do not
   disappear or become uneven. Perform scaling before final report quantization;
   audit the existing Int8 clamp rather than applying a slider after lost data.
   Decide how to handle large movements with a bounded transport strategy and
   held-button ordering intact. Avoid unbounded packet splitting/backlog.
4. If slow and fast movement still need different corrections, evaluate an
   optional **Acceleration adjustment** with neutral default. Derive speed from
   capture timestamps rather than main-thread delivery intervals, preserve
   direction, and reset motion state at handoff/disable/target changes. A curve
   applied before Windows processing is a feel adjustment, not a guaranteed
   cancellation of Windows acceleration.
5. Consider an experimental direct-position mode only if accurate matching
   remains necessary. It needs separate design for captured Mac movement,
   points/pixels and DPI, multiple displays, local mouse coexistence, secure
   desktop behavior, edge return, and preventing duplicate HID/injected motion.
   Absolute software injection or an absolute HID collection would change our
   architecture; they are not small slider patches. Preserve the working HID
   fallback and existing pairing behavior until those consequences are tested.

## Tracking

- [x] Inspect local input capture, report range, and queue behavior.
- [x] Research primary vendor documentation and Deskflow source.
- [x] Record the no-global-Windows-settings constraint and proposed stages.
- [ ] User validation of the current lag checkpoint (deferred until tomorrow).
- [x] Implement the user-selected pointer-speed slider; hardware feel measurement remains pending.
- [x] Prepare and verify the ARM-only Mac build; no new Windows build needed.
- [ ] Verify fine motion, fast sweeps, drags, handoffs, and directly attached
      Windows mice with the user's hardware.

## Slider implementation — 2026-09-22

The user requested implementation while deferring hardware tests until tomorrow.
Settings now contains **Windows pointer speed**, 0.25×–2× in 0.05× steps, default
1×, with a numeric value and Reset. AppStorage persists it independently on each
Mac. It applies to captured remote movement only; no Windows changes are needed.

Motion stays wide internally until scaling. Fractional counts are accumulated
per axis, so low-speed fine movements survive quantization. A speed change and
capture stop/start clear fractions, covering disable, handoff and target changes.
Drag button state and scroll behavior remain independent of speed.

Transport remains one report at most per source event, using the existing signed
8-bit descriptor range. Scaling now precedes saturation; extreme movement is
still capped at ±127 per axis and excess is not replayed later. This intentionally
avoids new packet bursts or queued travel while the lag checkpoint is pending.
The existing queue/coalescing policy, HID descriptor and pairings are unchanged.
The 1× path preserves previous integer motion output, including range limits.
This is sensitivity tuning, not an exact macOS acceleration match.

### Next hardware check

1. Install the new Mac app; use the previously prepared Windows input-lag build.
2. In Settings, compare 1× with 0.5× and 1.5×, then Reset. Try slow precision
   movement, fast sweeps and dragging on Windows. Verify scrolling feels unchanged.
3. Check local Mac movement and a directly attached Windows mouse retain their
   previous behavior. Restart the Mac app to confirm its chosen speed is saved.
4. Switch ownership between Macs with different saved speeds. Check each setting
   follows its Mac and old fractional movement does not carry into a new session.

Do not mark hardware behavior verified until the user reports these results.

Validation: all **85 Mac tests passed**, including eight new pointer-speed tests.
Strict SwiftLint, SwiftFormat and diff checks passed. Signed Release built and
the delivered archive passed ZIP CRC, arm64-only architecture, and extracted-app
signature checks. Native UI interaction and physical pointer feel remain pending.

Artifact: `releases/DeusKVM-mac-arm64-pointer-speed-2026-09-22.zip`
(951,700 bytes). SHA-256:
`7a66efb06ee4b2e7a23d5b4af18894d7364325c9b1f9022028323823beb16d6a`.

## User result and acceleration alternatives — 2026-09-22

The user confirms the slider works functionally but does not correct the
Windows-side acceleration feel. This is feedback on pointer tuning, not proof
that every takeover, lag or hardware checkpoint has passed. They request options
for correcting the response curve while preserving other Windows mice's settings.
The user subsequently approved option 2. Implementation and hardware checkpoint
are tracked in [DIRECT_POINTER_PLAN.md](DIRECT_POINTER_PLAN.md).

Two viable directions:

1. **Curve adjustment on the current relative HID path.** Apply speed-dependent
   gain using source timestamps and measured motion. Smaller change and retains
   current HID behavior, but Windows still processes the resulting reports.
   Therefore this is approximate compensation, not a true per-device override
   of Windows acceleration. A second slider alone should not be sold as an exact
   Mac match. Transport clipping/coalescing must remain distinguishable from the
   response curve during measurement.
2. **Optional direct-position mode (recommended prototype).** Send movement data
   over the existing Bluetooth companion link, calculate the intended desktop
   position, and inject absolute coordinates in the Windows desktop worker.
   Do not also send those deltas as HID mouse motion, or they will move twice.
   Absolute positioning avoids the relative-injection acceleration path without
   writing global Windows mouse parameters. Retain negotiated HID fallback for
   unavailable desktop/companion paths. This is a cross-platform change, not a
   reinterpretation of the speed slider.

Source checks relevant to the prototype:

- CursorConcealer.hide calls CGAssociateMouseAndMouseCursorPosition(0). Apple's
  local SDK CGRemoteOperation.h documents fixed absolute event positions with
  delta data in this state. Therefore diffing captured event positions alone
  cannot recover the normal Mac cursor trajectory. Compare source deltas,
  unaccelerated movement fields when supported, and actual local cursor travel
  across slow/fast movement before claiming a macOS-equivalent curve.
- The SDK declares eventUnacceleratedPointerMovementX/Y separately. Availability
  and behavior on supported macOS versions/input devices require checking; these
  fields do not themselves provide the Mac acceleration curve.
- Microsoft's MOUSEINPUT documentation distinguishes absolute coordinates from
  the relative motion affected by mouse settings; Deskflow's deskMouseMove uses
  the absolute path. Deskflow's relative path still modifies global parameters,
  so it remains excluded from our design.
- [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
  require explicit testing of elevated applications and secure desktops; normal
  desktop software injection cannot be assumed to match HID accessibility there.
- Existing Windows edge return is driven by selected-device raw mouse reports.
  A direct-position mode must supply equivalent edge observations explicitly,
  preserve button/drag ordering, and coexist with directly attached mouse input.
  It also needs DPI/multi-monitor conversion, bounded low-latency Bluetooth
  transport, takeover cancellation, and clean capability-negotiated fallback.

Recommended next work: measure local versus remote slow/fast response, then
prototype direct-position mode behind an option for a normal-desktop checkpoint.
Keep the current HID path available. No promise of exact native Mac acceleration
until the source mapping and hardware tests support it.
