# DeusKVM Companion: start here

The companion lets your Mac's keyboard, mouse and trackpad control this PC over the
local network, including at the login screen, the lock screen and UAC prompts. It
needs the Mac app from the same release, and both computers on the same private
network.

## Set up

1. Open `DeusKVM-Companion-win-x64-<date>.exe` and approve the administrator
   prompt. It installs or updates the service and opens its settings; nothing else
   is needed alongside it.
2. Open DeusKVM on the Mac and allow local network access if macOS asks.
3. In the companion, choose **Add Mac…** and pick your Mac.
4. On the Mac, the PC appears in **Setup → Devices**; turn on **Enable control**.

Repeat **Add Mac…** for each Mac. A Mac reached only over Tailscale or another
network without multicast must be added once on the local network; after that,
list its name or address in `C:\ProgramData\DeusKVM\mac-hosts.txt` (one
`host[:port]` per line).

## If something goes wrong

- **Add Mac… finds nothing:** check DeusKVM is running on the Mac, both are on the
  same network, and Windows Firewall is not blocking the companion.
- **Keep for a report:** `C:\ProgramData\DeusKVM\service.log` and `status.json`;
  file paste diagnostics are in `%LOCALAPPDATA%\DeusKVM\file-paste.log`.
- Use the Mac's toggle hotkey to take input back locally at any time.

More detail, including multiple Macs, clipboard, file paste and the secure desktop,
is in the companion README.
