# ProDHCPServer

A Windows 10/11 DHCP server for AV field technicians: plug the laptop into a DSP, camera, switcher or control processor, press **Start**, and see in seconds whether the device got an address, and if not, why.

## What it does (Phase 1)

- Pick the Ethernet adapter; ProDHCPServer backs up its IP settings, sets a static server IP, serves DHCP, and puts everything back on Stop, exit, crash (offered on next launch) or Windows shutdown.
- An eight-step status panel (adapter, link, static IP, server, DHCP traffic, lease, ping, WebUI), each green/amber/red with a plain-English reason and what to do next.
- Refuses to serve if another DHCP server answers on the link (e.g. a venue network), with an explicit override.
- Device card with IP, MAC, manufacturer (offline IEEE lookup), lease time and first-seen time; **Open WebUI** picks http or https from whichever port is open.
- RFC 2131 server: DISCOVER/OFFER/REQUEST/ACK/NAK/RELEASE/DECLINE/INFORM, BOOTP, relays, broadcast vs unicast replies with an automatic broadcast fallback for clients that ignore their own flag.

Coming next: Phase 2 (continuous ping graphs, log export, profiles) and Phase 3 (static-IP finder, mDNS/Dante/LLDP discovery).

## Download

Every push builds a single self-contained exe: **Actions → Build and test → latest run → Artifacts → ProDHCPServer-win-x64**. No installer or .NET runtime needed. It is not code-signed yet, so SmartScreen asks you to confirm the first time (More info → Run anyway).

## Build from source

Requires the .NET 10 SDK.

```
dotnet test tests/ProDHCPServer.Core.Tests
dotnet publish src/ProDHCPServer.App -c Release -r win-x64 -o publish
```

## Layout

| Project | What's in it |
|---|---|
| `src/ProDHCPServer.Core` | Platform-independent logic: DHCP packet codec and server engine, adapter backup/restore lifecycle, status rules, OUI lookup. Fully unit-tested. |
| `src/ProDHCPServer.Windows` | Windows specifics: adapter reading (.NET + registry) and changes (netsh), UDP 67 socket, ARP entries (IP Helper), firewall rule, other-server probe, ping/port checks. |
| `src/ProDHCPServer.App` | WPF UI (Fluent theme, dark/light), elevation prompt, crash handling. |
| `tests/` | Core unit tests (run anywhere) and Windows integration tests (run on the CI runner; they never change adapter settings). |
| `docs/hardware-test-phase1.md` | The checklist for testing on real hardware. |

Files the app writes: settings in `%LOCALAPPDATA%\ProDHCPServer\settings.json`; the adapter backup in `%ProgramData%\ProDHCPServer\adapter-backup.json` (exists only while a session is running or after a crash).

The manufacturer list is refreshed with `python3 tools/update-oui.py`.
