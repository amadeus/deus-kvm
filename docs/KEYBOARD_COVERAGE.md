# Keyboard forwarding coverage

## Audited keymap

The linked [DUO keymap](https://github.com/amadeus/vial-qmk/blob/553e845e9a7038eb89be48fa5fe7199973b7b514/keyboards/lazydesigners/duo/keymaps/amadeus/keymap.c)
and the Cloud, Dimple R3, and Kradlex `amadeus` keymaps in the same PR revision
are covered by `protocol/qmk-keymap-coverage.txt`: 111 distinct host-facing QMK
keycodes and 13 firmware controls across all four boards. Numeric values were
resolved using that revision's `keycodes.h` and `keymap_us.h`, including shifted
aliases. The fixture records input/output usages, modifiers, and Quartz/NX codes.

| Keys | Forwarding and coverage |
| --- | --- |
| Letters, numbers, Tab, Escape, Enter, Space, Backspace | Quartz key down/up to Bluetooth HID keyboard reports |
| All lower-layer punctuation, including backslash and pipe | Quartz event flags travel with the key; shifted aliases need no separate character whitelist |
| Both sides of Ctrl, Shift, Alt, GUI | Eight distinct HID modifier bits; Windows interprets right Alt according to its layout |
| Delete, Home, End, Page Up/Down, arrows, F1–F12 | Physical keys with corresponding releases |
| Print Screen, Scroll Lock, Pause, Insert | Raw HID preserves PC usages instead of confusing them with Mac F13–F15/Help |
| `KC_MENU` | Raw Keyboard Menu `0x76` becomes Windows Application `0x65`; overlapping Menu/Application presses retain correct held state |
| Caps Lock | One press/release pair per macOS latch transition |
| Brightness, volume, mute, play/pause | Consumer HID reports; effects depend on Windows and the display/application |
| Layers, transparent/disabled positions, RGB, Cloud reactive mode, bootloader tap dance | Firmware-local; no host key event to forward. Layer-selected keys and Caps Word's emitted Shift/letters use normal paths |
| Cloud encoder | Base-layer volume uses the consumer path; other layers control firmware RGB |

## Corrections

Raw monitoring opens each keyboard independently, including virtual keyboards
provided by remappers such as Karabiner. An exclusive-access failure on one
physical keyboard no longer shuts down the other streams. Failed devices retry
while control is local and input is released; raw special-key routing remains
enabled for the available streams. See `PLAN.md` for the remaining physical
keyboard and remapper lifecycle checks.

Backslash used to be diverted to the raw supplement whenever its manager opened,
suppressing Quartz `0x2A`. That depended on a second callback being available for
the keyboard and paired the press with a separately sampled session modifier
state. QMK's `KC_PIPE` is Shift + Backslash, so the raw event could be sent without
its Shift. This is a confirmed routing hazard; the exact callback sequence on the
user's hardware has not been captured.

Both Backslash usages now use Quartz `0x2A`, retaining modifiers on the event.
Windows maps HID `0x31` and non-US `0x32` to scan code `0x2B`, so normalizing that
alias preserves Windows key behavior. ISO non-US Backslash `0x64` remains
distinct. QMK `KC_MENU` (`0x76`) also differs from `KC_APP` (`0x65`); Windows
supports Application as its context-menu key. DeusKVM translates Menu at report
generation without changing firmware or local Mac input. See Microsoft's
[HID-to-scan-code table](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input#scan-codes).

## Keeping coverage current

After changing a QMK layout, run:

```sh
python3 scripts/check-qmk-coverage.py /path/to/vial_qmk
```

Alternatively pass individual `keymap.c` paths. The read-only checker fails on
unclassified keycodes/controls in matrix and encoder definitions. Add each new
entry's actual emitted usage and expected behavior to the fixture, then run
`QMKKeymapCoverageTests` and the existing keyboard/raw/handoff/HID queue tests.
Adding a name alone does not establish hardware support. This source inventory
check cannot read Vial EEPROM remappings or evaluate arbitrary firmware macros.

## Boundaries and hardware checklist

The keyboard stays connected to the Mac: remote mode forwards to Windows, and
local mode leaves native Mac input in place. This does not add Windows-connected
keyboard control of a Mac. Shortcuts and characters follow the receiving OS's
layout; matching US layouts produce the symbols printed in these QMK layouts.
The configured DeusKVM toggle shortcut is intentionally consumed locally.

The HID descriptor supports six simultaneous non-modifier keys plus all eight
modifiers. More than six produces ErrorRollOver until enough keys are released.
Extended keyboard usages through `0xDD` retain the raw fallback, but Windows may
not assign them an action. This audit does not promise support for arbitrary
vendor controls or every Consumer-page usage. Input Monitoring/Accessibility
must be granted, and Secure Input can prevent capture.

Pending physical validation after opening the updated Mac app (no companion
update or Bluetooth re-pair needed):

- [ ] In Windows and again after returning to Mac, type every lower-layer symbol,
      especially alternating pipe/backslash, rapid taps, and held repeat.
- [ ] Exercise every base/raise/adjust host key on DUO; verify PC keys, context
      menu, both modifier sides, Caps Lock, and media. Mac actions remain native.
- [ ] Check brightness on the actual Windows display; transmitting a usage does
      not guarantee an external monitor implements brightness control.
- [ ] Request handoff with a held key, release it, and check for stuck input;
      verify disconnect/reconnect and the configured toggle shortcut.
- [ ] Check Vial EEPROM remappings and Cloud encoder behavior separately.

Automated verification and packaged-build results are recorded in `PLAN.md`.
