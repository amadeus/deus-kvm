# Faster on-demand file paste

The user confirms the Mac-to-Windows paste interaction works and wants exactly
that interaction with faster local-network file transfer. Do not change native
Explorer paste, pre-transfer files, expand to folders/multiple files, or change HID.

- [x] Design: retain one regular file up to 10 MiB; metadata and a fresh per-offer
  secret travel through the encrypted paired Bluetooth channel. Windows connects
  on the first native stream read, preferring local IPv4 TCP with authenticated
  AES-256-GCM records. No cloud, discovery service, port forwarding or permanent key.
- [x] Implement Mac listener with bounded clients/records, fresh connection
  nonces, source/revision validation and immediate revocation on lifecycle changes.
- [x] Implement Windows lazy network reader with larger blocks and the existing
  Bluetooth fallback; preserve native COM clipboard behavior.
- [x] Verify cross-language cryptography, actual socket transfer, tampering,
  cancellation, stale sources, fallback and existing regressions.
- [x] Package Windows x64 and signed arm64-only Mac ZIPs, keep only current
  platform artifacts in visible releases, and commit verified work.
- [ ] Hardware: same copy/paste interaction with the 1.9 MB file, network speed,
  cancellation/ownership/lock, and Bluetooth fallback when LAN is unavailable.

Protocol: metadata carries local IPv4 addresses, ephemeral port, and a random
256-bit per-offer key. TCP starts with independent 32-byte client and server
nonces. HKDF-SHA256 derives separate client/server keys using both nonces as salt
and direction-specific protocol labels. AES-GCM authenticates each bounded record
with a 128-bit tag and monotonically increasing 96-bit nonce (zero prefix plus
64-bit counter). Requests contain only epoch, clipboard revision, offset and count;
no arbitrary paths. Each authenticated response contains status plus requested
bytes. Both nonces prevent an unauthenticated peer forcing key/nonce reuse across
connections. Source validity and clipboard lifecycle gates apply to every request.

Network starts only on content reads. Failed connection setup falls back to the
existing Bluetooth stream; a network failure after successful data delivery fails
that paste instead of silently switching transports mid-copy. Re-copy to retry.
No file content is staged to disk by DeusKVM.


Local verification:
- 81 Mac and 138 .NET regression tests passed; strict SwiftLint passed.
- Shared Swift/C# vectors cover both directions, sequential records, tampering,
  wrong keys, nonce changes and replay rejection.
- `scripts/test-file-network.sh` runs the production Swift server and C# native
  stream against each other without launching either app or touching the clipboard.
  A 2 MiB + 37 byte source matched byte-for-byte, including seeking; the latest run
  completed its full read and comparison in 65 ms on one Mac. This is not a claim
  about real Windows hardware/LAN speed. Metadata and wrong-key attempts read no
  source bytes. Changed-source rejection, active connection revocation, cancellation
  during handshake and unavailable-LAN Bluetooth fallback passed.
- Listener and source reads run on the clipboard queue. No HID, pointer processing,
  native clipboard formats, Explorer paste trigger or file-size limit changed.
- First version advertises up to four private/link-local IPv4 addresses; IPv6-only
  networks and guest isolation use Bluetooth fallback. Re-copy after network or
  permission changes; metadata captured before listener readiness can lack a LAN offer.


Release verification: both publishes succeeded. Both ZIP CRC checks passed;
Windows archive contains the four expected files and an x64 PE executable. The
extracted Mac app contains only `arm64`; deep/strict signature verification passed,
and the packaged local-network usage description is present. Only these two ZIPs
remain in visible `releases/`. Neither production app was launched or restarted.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| DeusKVM-Companion-win-x64-file-paste-network-2026-09-22.zip | 52411304 | `414c0cedc977b293e4d8b317ccae19b98de024860f21393bc0a4b2c7b1e9d429` |
| DeusKVM-mac-arm64-file-paste-network-2026-09-22.zip | 989339 | `355561e8f98b14c2c36dfaaea300a84700423dfdb4308c2628bb5ce2f2772591` |


Hardware checkpoint is pending user setup with both new apps. Preserve the current
single-file flow and wait for actual Mac-to-Windows LAN feedback before expanding
scope. No measured Bluetooth/LAN comparison is claimed.
