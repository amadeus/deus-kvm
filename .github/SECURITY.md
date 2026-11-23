# Security Policy

## Supported Versions

Security fixes target the current DeusKVM code on `main`: the Mac app and the
Windows companion. Please include the version or commit you are using when
reporting an issue in this repository.

| Version | Supported |
| ------- | --------- |
| Latest release (`DeusKVM-mac-arm64.zip` and `DeusKVM-Companion-win-x64.zip` built from `main`) | :white_check_mark: |
| Older releases and development builds | :x: |

## Scope

DeusKVM controls your own Windows PC from your Mac. Reports are most useful for:

- The control link. The Mac listens on TCP port 53400 (or an ephemeral port)
  and advertises it with Bonjour as `_deuskvm._tcp`; the PC connects out to it.
  Each side has a P-256 identity key; the handshake is an ECDH agreement over
  those keys and fresh nonces, and every frame is sealed with AES-GCM under
  per-direction keys derived from it. Trust is set once on each side: Add Mac
  on the PC saves the Mac's key id, and Enable control on the Mac approves the
  PC's. The link carries ownership, edge return, clipboard, and the offers that
  start the other streams.
- Input and file streams. The Mac opens TCP listeners on ephemeral ports for
  keys and pointer and for file paste, offers only its private IPv4 addresses,
  and sends a fresh random key with each offer over the control link. The PC
  always connects out to the Mac. Traffic is encrypted and authenticated with
  AES-GCM under keys derived from that offer.
- The Windows service, which runs as LocalSystem, and its helper processes: the
  session agent (SYSTEM in the console session), which receives the Mac's input
  stream and injects it on every desktop (the user's, elevated windows, UAC
  prompts, lock and login screens), and the clipboard helper (the signed-in
  user). Their named pipes are restricted to SYSTEM and, for the clipboard
  helper, the console user; network access is denied, and the service checks
  each pipe client's process ID (and each helper the server's).
- The service's control pipe (`\\.\pipe\DeusKVM.Control`), the tray's remote
  control. SYSTEM and Administrators have full access and interactive (console)
  users read/write; network clients are denied. It accepts one bounded JSON
  request per connection: status or a status watch, saving a Mac chosen in Add
  Mac, the service and tray startup preferences, and stopping the service, each
  validated as the elevated command line validates it. Installing, updating and
  removing still require elevation. The service's DACL also lets interactive
  users start and stop it. The tray does not verify the pipe's server process, so
  a local process that claimed the name before the service could answer it.
- Clipboard sharing, including private clipboard markers and paste of files and
  screenshot images.

## Reporting Vulnerabilities

You may report low-severity security vulnerabilities through GitHub issues. 

For critical security issues, please report them via GitHub's [vulnerability reporting](https://github.com/amadeus/deus-kvm/security/advisories/new).

1. Open the [Security tab](https://github.com/amadeus/deus-kvm/security) of this repository.
2. Select **Report a vulnerability**.
3. Describe the issue.

To help triage, please include as much as you can:

- Affected version and platform (macOS or Windows, and OS version)
- The vulnerability and its impact
- Steps to reproduce or a proof of concept
- Any suggested mitigation

We aim to acknowledge reports promptly and will coordinate a fix and disclosure with you. Please allow reasonable time to address the issue.
