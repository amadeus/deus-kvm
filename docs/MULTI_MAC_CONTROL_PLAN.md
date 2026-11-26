# Multi-Mac input control

## Goal and agreed behavior

Every trusted, allowed, enabled Mac stays connected and ready. Each Mac independently
chooses local input or PC-control mode. The most recent real user input sent to Windows
selects its controller, before that input is delivered. A Mac already in PC-control mode
can resume immediately from the Windows cursor's current position. A fresh edge entry
still places the cursor using that Mac's layout.

Explicit Enable/Disable remains a user setting. Taking control never disables another
Mac or returns it to local mode. Disabling, losing the connection, and an explicit return
still restore local input. Background traffic, clipboard updates, repeated state snapshots,
and keepalives do not take control. Simultaneous real input is serialized by Windows.

Remote clipboard content belongs exclusively to the current controller. A paste shortcut
can itself take control, and must use that Mac's content. Late offers, outstanding fetches,
and content already installed in the Windows clipboard cannot leak a previous controller's
content into a new paste. Ordinary Windows-local copies remain usable. Completed pastes
are not undone. Input ownership, remote-mode state, and connection readiness are separate.
Returning locally retains the last controller as the clipboard partner, so Windows-to-Mac
text and on-demand file paste still work after EXIT. Another controller, explicit disable,
or disconnection replaces or revokes that partnership.

## Architecture and review gates

- One authority serializes Windows input ownership and injection; no competing independent
  desktop agents, polling-based takeover, arbitrary sleeps, or per-key workarounds.
- Per-Mac state retains layout, remote-mode/switch identity, and stream generation.
- Ownership transitions release held input, cancel stale edge-return work, and update
  clipboard eligibility before delivering the triggering input.
- Clipboard readiness must have an explicit ordering contract with paste delivery;
  asynchronous transfers and helper messages must be fenced by owner/session identity.
- Disconnects, helper restart, desktop changes, and explicit disable must preserve these
  invariants. A non-controller's cleanup must not release the controller's input.
- Root reviews each implementation phase and its tests before committing. The implementation
  agent owns code changes; root owns this plan, review decisions, and commits.
- Existing untracked migration plans are unrelated work and remain untouched.

## Phases

- [x] 1. Agree on concrete ownership, protocol, shared desktop and clipboard architecture;
  implement tested state models and protocol contracts.
- [x] 2. Implement persistent multi-Mac readiness and centralized input takeover, including
  held-input cleanup, per-Mac layouts, edge return, explicit disable and reconnect.
- [x] 3. Implement controller-scoped clipboard selection, stale-content invalidation and
  ordering of takeover/paste across text, images and files.
- [x] 4. Review end-to-end behavior; update documentation and diagnostics; run focused race
  regressions, full portable tests, Swift lint/format and both platform builds.
- [x] 5. Package both apps, verify macOS arm64-only/signature and Windows embedded payload,
  assess notarization status, record artifact paths and remaining validation.

Phases may be combined when required to keep the protocol and both applications consistent.
Each verified implementation block is committed independently; intermediate commits are
not described as hardware-validated releases.

## Automated validation

- Two enabled Macs stay ready, and takeover does not change either enable setting.
- Local input cannot claim ownership; real remote motion, click, scroll, key and paste can.
- Keepalives, unchanged state and background clipboard traffic cannot claim ownership.
- The triggering input is delivered after ownership change; remote resumption does not warp.
- Cleanup and late messages from the prior owner cannot affect the current owner.
- Held keys/buttons and key repeat cannot leak across takeover or disconnect.
- Edge return uses the current owner's layout and switch identity.
- Paste-as-first-input uses the new owner's text/image/file, including delayed readiness,
  stale offers, takeover during a fetch, local Windows copies and disconnected sources.
- Desktop/helper restart recovers all ready streams and rejects stale generations.

## Hardware validation — pending user testing

- [ ] Studio and MacBook stay connected/enabled simultaneously across app/service restart.
- [ ] Studio enters Windows; MacBook moves locally, enters Windows, then Studio takes over
  merely by moving. Cursor continuity and each Mac's entry/return layout are correct.
- [ ] Mouse buttons, dragging, modifiers, typing, repeat, media keys and scrolling across
  alternating controllers; no stuck inputs or unexpected takeover from idle traffic.
- [ ] Paste as the first remote input from either Mac: distinguishable text, images and files;
  no content from the other Mac, including a slow transfer interrupted by takeover.
- [ ] Windows-local copy/paste and Windows-to-Mac clipboard behavior after return.
- [ ] Explicit Disable/Enable, disconnect mid-input, Mac sleep/wake and network reconnect.
- [ ] Login/lock/UAC/elevated windows, Ctrl+Alt+Delete and edge return from both Macs.
- [ ] Install/update both packaged apps and verify normal startup/permissions.

## Progress

- Plan created after the README attribution commit `e2a69e4`. Implementation agent is
  inspecting the current network architecture; no control code has changed yet.
- Baseline portable Windows tests: 217 passed, two Windows-native crypto tests skipped
  on macOS. Local .NET SDK is `~/.dotnet/dotnet`; tests require local socket access.
- Architecture review selected a single shared desktop router/agent and clipboard helper.
  Input protocol will distinguish capture identity and synthetic reset from physical input;
  independent control/input sockets must not lose the first event or accept an old capture.
  Clipboard design review is checking a typed snapshot plus delayed rendering so pointer
  motion need not wait for image/file payloads.
- Phase 1 reviewed: portable `InputOwnership` separates per-Mac remote state, capture and
  stream identities from the current owner. Real changed input selects before forwarding;
  resets/keepalives/background resume do not acquire. Held modifiers can be restored without
  replaying ordinary keys, lock toggles or mouse downs. A new capture invalidates pending
  work even for the same owner. Eleven focused scenarios pass; full portable suite passes
  228 tests with two Windows-native skips. Integration is the next phase, not yet active.
- Integration review requires capture-scoped entry acknowledgement before input leaves the
  Mac, stale-operation fencing on Windows, and safe clipboard behavior across helper crashes
  and secure/default desktop transitions. Saved disabled settings remain respected; Macs
  disabled by an earlier build may need enabling once after update.
- Phase 2 review in progress. Initial shared-agent rewrite was not accepted as complete:
  retain native injection failure feedback, delivery-stall recovery, latency/arrival/edge
  diagnostics and fast stream edge return; cancel buffered input on synthetic reset; release
  held input promptly at takeover. The implementation agent is addressing these gates.
  An independent test agent is checking actual cross-platform protocol contracts and races.
- Independent protocol validation: seven C# codec/framing tests and 34 selected Swift tests
  pass using shared v3 fixtures, high-bit capture identities, ownership values above 2^53,
  exact control message lengths, and maximum-size text through real framing. The Windows
  app cross-build passes with zero warnings/errors. Full integrated verification is pending.
- Review caught a cross-stream RESET/EXIT race: resetting synthetic input must discard queued
  input without renewing clipboard ownership. Normal EXIT retains the clipboard partner.
  Restart generation and delayed helper callbacks remain explicit review gates.
- Phases 2 and 3 accepted together after root review: a shared desktop agent serializes input
  and ownership; capture-scoped entry acknowledgements release queued Mac input only after
  placement and clipboard readiness. Controller-bound clipboard leases revoke outstanding
  reads and stale imports, with text/file/image snapshots and lazy Windows rendering.
  Background snapshots, resets and resumed capture metadata cannot acquire ownership.
- Restart/source/stream/capture/clipboard-epoch fences, atomic edge-return feedback and
  latched injection failure reporting are covered by portable regressions. Changed live
  input offers force local recovery; duplicate offers are idempotent. An independent
  watchdog covers injection, deferred desktop operations and key repeat without relying
  on the potentially blocked window thread.
- Final automated checks (2026-11-25): 260 portable Windows tests passed; two native CNG
  tests skipped on macOS. Windows Release and net48 test assemblies build with zero
  warnings/errors. The final arm64 Mac Debug build and 152 selected Swift tests passed.
  SwiftFormat reports zero files needing formatting; strict SwiftLint reports zero
  violations. `git diff --check` passes.
- Eleven Mac clipboard runtime tests remain pending: the full-suite attempt failed when
  Secure Input was active and even an isolated named pasteboard rejected writes. A
  standalone probe confirmed the desktop limitation; existing handoff tests failed the
  same initial snapshot checks. The final selected run explicitly excludes those tests;
  privacy guards and tests were not weakened. Rerun the full suite in an unlocked normal
  desktop session. Windows-native OLE tests and all hardware checks above remain pending.
- Integrated input/clipboard implementation committed as `928161d`. User documentation
  now describes independent readiness, activity-driven takeover, controller-only clipboard
  data, and preserving explicit disabled settings. The old automatic-disable checkpoint
  is labeled historical.
- Both local release packages built successfully. The Windows packaging verifier checked
  the actual launcher's embedded ZIP/hash, all 14 runtime files and OS-only bootstrap
  references. The extracted Mac ZIP passed strict/deep signature verification, and every
  packaged Mach-O is arm64 only. The publish script now cleans before building, preventing
  old resources and notarization tickets from surviving in the product.
- Unlocked-session retry (2026-11-25): the existing `deuskvm` profile became accessible
  without recreating credentials. Apple accepted submission
  `5077c0ae-2f37-4192-a9d9-86963197b559`, with no issues in the notarization log.
  The app was stapled and the ZIP regenerated. A quarantined ZIP extraction passes
  ticket validation, strict/deep signature verification and Gatekeeper as
  `Notarized Developer ID`; all Mach-O binaries remain arm64 only.
  The complete Mac suite now passes all 163 tests, including the eleven previously
  blocked clipboard tests. The earlier "missing profile" result was an access problem
  while the Mac was locked, not proof that the stored profile was absent.
- Added optional `DEUSKVM_NOTARY_KEYCHAIN` selection to the publish script and documented
  storing a profile in a dedicated file-based keychain for unattended notarization.
  Shell syntax and diff checks pass. No credentials or keychain lock settings were
  changed; actual locked-screen access with that optional configuration remains untested.

## Test artifacts — 2026-11-25

- Mac app: `DeusKVM.app` (0.0.4, build 4), inside the verified ZIP.
- Mac archive: `releases/DeusKVM-mac-arm64.zip`.
  SHA-256: `ffdc267373c79ed4b90b345d3e6c84e1deabec85309ef5c97ccc3180b70a6ac1`.
- Windows installer: `releases/DeusKVM-Companion-win-x64-2026-11-25.exe`.
  Rebuilt with the login-screen correction; this is the single current Windows installer.
  SHA-256: `83ba6ac5637fa08b3bb9fb12000b9d0c17af5e58af76d9d98723bd298dfd4a1c`.
- The Mac app has a timestamped Developer ID signature, hardened runtime and a validated
  stapled notarization ticket. Gatekeeper accepts the refreshed archive's extracted app
  as `Notarized Developer ID`, including with a download quarantine flag.
- These are local artifacts, not a published release. Neither packaged app was installed
  or launched for hardware testing. Update the Windows companion and every Mac together.

## Outstanding validation

- [x] Access the existing notarization profile locally, notarize this build,
  staple the accepted ticket, repackage and verify Gatekeeper acceptance.
- [x] Run all Mac tests in an unlocked normal desktop session, including the eleven
  named-pasteboard tests excluded from the final selected run.
- [ ] Execute the net48 tests on Windows, including native OLE/image clipboard cases.
- [ ] Complete the multi-machine hardware checklist above.

## Login-screen regression — 2026-11-25

- User hardware testing failed on Mac Studio: crossing onto the PC's login/lock screen
  hid the Mac cursor, sent no usable input and could not edge-return. The local hotkey
  still returned control. This initial failure was followed by the successful user retest below.
- Live Mac diagnostics at 21:55–21:56 show captured motion but zero forwarded events and
  zero input frames, followed by five-second disconnect recovery or the six-second
  `Windows entry timed out` fallback. The entry acknowledgement gate never opened.
- The 21:59 problem report, saved while the user confirmed Windows was still locked,
  reports the Default desktop and a connected/available clipboard helper. Therefore
  desktop name alone is insufficient to decide whether login input can proceed without
  clipboard preparation. After the timed-out captures the agent had no owner/pending work.
- Review also found one-shot readiness loss after delayed desktop changes or attachment
  failure, and a clipboard-thread race that could discard the new owner's sole snapshot
  before its selection command was processed. The clipboard inbox correction is committed
  as `3b48a07`; it retains a selected owner's snapshot until its matching selection is processed.
- Windows now queries its own interactive session's native WTS lock state as well as the
  attached desktop. A locked session or Winlogon desktop can prepare input without a user
  clipboard. Pending readiness retries, and helper recovery preserves an unacknowledged
  entry placement without replaying completed entries or old queued input.
- Unlocking revokes readiness granted without clipboard preparation and requires a fresh
  controller clipboard generation. Queued actions and key repeat recheck this boundary;
  regression coverage includes an attachment failure followed by unlock during entry.
- Root and independent review completed. All 278 portable Windows tests pass; two native
  CNG tests remain skipped on macOS. The net48 test assembly and Release app build with zero
  warnings/errors. The replacement installer's actual embedded ZIP/hash, all 14 runtime
  files and OS-only bootstrap references pass verification. `git diff --check` passes.
- No Mac source changes or installed Mac update are needed for this correction. The
  replacement Windows installer was built locally for the user's retest. During artifact
  verification, the existing Mac ZIP differed from the earlier
  recorded archive: its signature was valid and Gatekeeper accepted it, but its ticket was
  absent. Restored the existing accepted ticket and repackaged without rebuilding. The
  resulting ZIP extraction passes signature, stapled-ticket and Gatekeeper checks and
  contains only the arm64 executable. The artifact checksum above reflects this repair.
- [x] User retested the replacement Windows installer and reported that it resolved the
  login-screen control/return regression: "that seemed to fix it, everything good to push".
- [ ] Retest lock/unlock while controlling, then clipboard paste and takeover between both
  Macs. Confirm only the current controller supplies remote clipboard data.
- The reported login-screen regression is resolved according to the user's hardware retest.
  Windows-native tests and the broader lock/unlock, clipboard and multi-Mac checklist remain
  pending; the successful regression retest does not establish those separate checks.
