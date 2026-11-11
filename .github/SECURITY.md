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

- The Bluetooth LE link: the HID keyboard/mouse service and the companion
  control channel (ownership, edge return, text clipboard, and the offers that
  start local-network streams).
- Local-network streams. The Mac opens TCP listeners on ephemeral ports for the
  pointer stream and for file paste, offers only its private IPv4 addresses,
  and sends a fresh random key with each offer over the Bluetooth link. The
  PC always connects out to the Mac. Traffic is encrypted and authenticated
  with AES-GCM under keys derived from that offer.
- The Windows service, which runs as LocalSystem, and its helper processes: the
  desktop worker in the signed-in console session and the SYSTEM worker that
  injects input on the secure desktop (UAC prompts, lock and login screens) and
  behind elevated windows. Their named pipes are restricted to SYSTEM and, for
  the desktop worker, the console user; network access is denied, and the
  service checks each pipe client's process ID.
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
