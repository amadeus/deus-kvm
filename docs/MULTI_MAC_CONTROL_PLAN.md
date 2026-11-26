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
- [ ] 2. Implement persistent multi-Mac readiness and centralized input takeover, including
  held-input cleanup, per-Mac layouts, edge return, explicit disable and reconnect.
- [ ] 3. Implement controller-scoped clipboard selection, stale-content invalidation and
  ordering of takeover/paste across text, images and files.
- [ ] 4. Review end-to-end behavior; update documentation and diagnostics; run focused race
  regressions, full portable tests, Swift lint/format and both platform builds.
- [ ] 5. Package both apps, verify macOS arm64-only/signature/notarization and Windows
  embedded payload, record artifact paths and remaining hardware validation.

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
