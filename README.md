# ProDHCPServer

A portable DHCP server for Windows 10/11, built for AV field technicians.

Plug your laptop into a DSP, camera, switcher, display or control processor, press **Start**, and see within seconds whether the device got an IP address. If it didn't, the app tells you why in plain English and what to try next. When you press **Stop**, your laptop's network settings go back exactly as they were.

![ProDHCPServer main window in dark mode](docs/images/main-window-dark.png)

> **Status:** Phases 1 (the core DHCP loop) and 2 (ping graphs, session log export, profiles) are done and passed their hardware tests in October 2026. Phase 3 (discovery) is next. See [Roadmap](#roadmap).

## Why

Most AV kit ships set to DHCP, and on a bench or a fresh site there's usually nothing to hand out addresses. The usual fixes (a spare router, a command-line DHCP server, guessing static IPs) are slow and leave your laptop's adapter in a mess. ProDHCPServer does the whole job in one window: it sets your adapter up, serves addresses on that adapter only, shows you the device, and puts everything back afterwards.

## Features

**Phase 1: core loop**

- **Pick your Ethernet adapter** from a list showing link state, speed and current IP. The app remembers your last choice.
- **Safe adapter changes.** Your adapter's settings are backed up before anything changes and restored on Stop, on exit, at Windows shutdown, and after a crash (offered the next time you open the app).
- **Eight-step status panel**: adapter, cable/link, static IP, DHCP server, DHCP traffic, device leased an IP, device responding to ping, WebUI reachable. Each step is green, amber or red with a plain-English reason and a suggested next step.
- **Won't fight a venue network.** Before serving, it checks for another DHCP server on the cable and refuses to start if it finds one (with a deliberate override for networks you own).
- **Device card** with IP, MAC address, manufacturer (offline lookup, no internet needed), lease time and first-seen time.
- **Open WebUI** in one click, picking http or https from whichever port is open, plus quick port buttons and Copy IP.
- **Several devices at once** behind a small switch, each with its own lease.
- **Spots a USB adapter that has stopped receiving.** Some USB Ethernet adapters keep showing "connected" but quietly stop receiving after a few seconds, which looks exactly like a silent device. If nothing has come in for 20 seconds while the laptop keeps sending, the cable/link step says so and tells you to replug the adapter. Realtek RTL8152/8153 adapters on a driver older than 2019 (including Windows' own 2015 driver) are flagged as soon as you pick them, with where to get the newer driver.
- **Wi-Fi and VPN overlap warnings**, with a one-click option to turn Wi-Fi off until you press Stop.
- **Dark and light themes**, with large status text you can read from arm's length.

**Phase 2: visibility**

- **Live ping graph for every device.** Each device with a lease is pinged once a second. The device list shows a small latency graph with the latest reply time, and the selected device shows the last two minutes with average, min, max and loss. Missed replies show as red marks. When a device stops answering, or starts again, the session log records it.
- **Session log you can filter and export.** Filter by level (everything, warnings and problems, problems only), by area (adapter, server, DHCP, device) or by text such as an IP or MAC address. **Export log…** saves what's shown as a text file for a job report, or as CSV for Excel.
- **Profiles.** Save the subnet, pool, lease time and DHCP options for each job or kind of gear, and pick one from the top bar before pressing Start. Options: gateway (3), DNS (6), domain name (15), NTP (42), vendor-specific (43, hex), TFTP/provisioning server (66), Cisco TFTP servers (150) and any other option as text, IP addresses or hex. Profiles can be duplicated, deleted, exported to a file and imported on another laptop.
- **Presets** built from each manufacturer's own documentation, with links to the pages they come from:
  - Default (192.168.100.x), for the gear that asks for DHCP out of the box: Q-SYS, Biamp, Shure, Sennheiser, Dante, Symetrix, Crestron, AMX, Kramer, Atlona and Extron NAV.
  - 192.168.254.x for Extron switchers (192.168.254.254) and IPCP Pro (192.168.254.250).
  - 192.168.0.x for Lightware, Christie GS, Sharp/NEC MultiSync M and Bose ESP/PowerMatch/CC-64.
  - 192.168.1.x for the Kramer and Atlona fallback addresses, ClickShare direct connection and AMX NX static.
  - Cisco IP phones (option 150), Poly phones (option 66), and a bench test with 2-minute leases.

  Each preset's pool leaves the documented factory addresses free. Presets can't be changed: duplicate one to make your own.

![The Profiles panel with the Default preset and links to the manufacturers' documentation](docs/images/profiles-panel-dark.png)

**Default profile:** server IP `192.168.100.1/24`, addresses `192.168.100.100` to `192.168.100.199`, 60-minute leases. Make your own profile under **Profiles** (the gear button) to change any of these.

## Download and run

1. Open the [Actions tab](https://github.com/cookingwithsimon/ProDHCPServer/actions/workflows/build.yml), pick the latest green **Build and test** run, and download **ProDHCPServer-win-x64** under Artifacts.
2. Unzip it. You get a single `ProDHCPServer.exe`; there's nothing to install and no .NET runtime needed.
3. Double-click it. Windows asks for administrator rights, because changing adapter settings and serving DHCP require them.
4. The exe isn't code-signed yet, so the first time Windows SmartScreen says "Windows protected your PC". Click **More info**, then **Run anyway**.

## Using it

1. Connect the device to your laptop's Ethernet port (directly or through a small switch) and power it on.
2. Pick your Ethernet adapter at the top of the window.
3. Press **Start**. Steps 1 to 4 go green within a few seconds.
4. Power-cycle the device or replug its cable if it doesn't ask for an address straight away. Its IP, name and manufacturer appear on the right.
5. Click **Open WebUI** to get to the device's web page.
6. Press **Stop** when you're done. Your adapter goes back to how it was.

## Troubleshooting

| What you see | What it usually means |
|---|---|
| Red banner: another DHCP server is on the link | You're plugged into a network that already hands out addresses (office, venue, home router). Unplug from it, or only use **Serve anyway** on a network you own. |
| Step 2 red, "No link" | No cable, a dead port, or the device is off. Steps 2 to 4 recover by themselves when the link comes up. |
| Step 5 amber, no DHCP requests after 30 seconds | The device probably has a static IP or DHCP turned off. |
| The device gets an offer but never accepts it | Some embedded network stacks ignore their own broadcast flag. Tick **Always broadcast replies** in your profile and try again. |
| Step 1 amber, mentions Wi-Fi or VPN | Another adapter is on the same subnet. Use **Turn it off until Stop**, or use a profile with a different server IP. |
| Step 2 says the cable shows connected but nothing has been received | The USB network adapter has stopped receiving. Unplug it from the laptop and plug it back in. If step 1 mentions an old Realtek driver, install the newer one: Windows Settings > Windows Update > Advanced options > Optional updates > Driver updates, tick the Realtek one only. |
| Ping times jump around (hundreds of ms) with AV-over-IP gear on an unmanaged switch | Probably the network, not the device or the app. See [Field notes](#field-notes). |
| The app was killed mid-session | Open it again and click **Restore now** on the banner. The backup lives in `C:\ProgramData\ProDHCPServer\adapter-backup.json` until it's restored. |

If something goes wrong, [open an issue](https://github.com/cookingwithsimon/ProDHCPServer/issues/new/choose) using the **Problem on site** form, and include the session log text if you have it.

## Field notes

Things learned on real hardware that are worth knowing on site.

- **AV-over-IP multicast slows everything on an unmanaged switch.** With an Extron NAV encoder (NAV E 101) and decoder on a small unmanaged switch, pings to both units averaged about 200 ms and jumped as high as 600 ms, with no lost packets. Windows' own `ping` showed the same, so it's the network, not the app or the device. The encoder's multicast video floods every port of an unmanaged switch. NAV, Dante video, SDVoE and other AV-over-IP gear needs a managed switch with **IGMP snooping** turned on (and an IGMP querier on the network), so multicast only goes to the ports that asked for it. Short of that, test the encoder on its own or keep the laptop off the video switch.
- **Realtek USB adapters on the old Windows driver stop receiving.** Realtek RTL8153 USB Gigabit adapters on Microsoft's 2015 inbox driver went silent after a few seconds while the link light stayed on. Realtek's current driver (from Windows Update's optional driver updates) fixed it. The app now warns about this combination and spots the stall itself (see Troubleshooting).
- **Managed work laptops may block the exe.** IT-managed laptops with Microsoft Defender's attack surface reduction rules can block any new unsigned exe, so every new build is refused. Test on an unmanaged laptop, or ask IT to allow a fixed folder (for example `C:\Tools\ProDHCPServer\`) rather than one file's hash, which changes with every build.

## Roadmap

Each phase ends with a hardware test pass on real AV equipment before the next one starts.

- **Phase 1: core loop** (done, hardware-tested October 2026).
- **Phase 2: visibility** (done, hardware-tested October 2026). Continuous ping with a latency graph per device, a full session log you can filter and export (TXT/CSV), and saved profiles for subnet, pool, lease time and DHCP options (router, DNS, NTP, option 43/66/150 and custom options).
- **Phase 3: discovery.** Find devices that already have a static IP, move your adapter onto their subnet in one click, and discover devices via mDNS (including Dante), SSDP/UPnP and LLDP/CDP.

## Files it writes

| What | Where |
|---|---|
| Your settings (last adapter, profile in use, theme) | `%LOCALAPPDATA%\ProDHCPServer\settings.json` |
| Your profiles | `%LOCALAPPDATA%\ProDHCPServer\profiles.json` |
| Adapter backup (only while a session runs, or after a crash) | `%ProgramData%\ProDHCPServer\adapter-backup.json` |

While serving, the app also adds a Windows Firewall rule so it can receive DHCP traffic (UDP 67 and 68, for this exe only), and removes the rule on Stop.

## For developers

Built with C# on .NET 10 and WPF (Fluent theme), published as one self-contained single-file exe for win-x64.

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Core tests and a full build also run on Linux and macOS; running the app needs Windows.

```
dotnet build ProDHCPServer.sln
dotnet test tests/ProDHCPServer.Core.Tests
dotnet publish src/ProDHCPServer.App -c Release -r win-x64 -o publish
```

**Layout**

| Project | What's in it |
|---|---|
| `src/ProDHCPServer.Core` | Platform-independent logic: DHCP packet codec and server engine, adapter backup/restore lifecycle, status rules, manufacturer (OUI) lookup. Fully unit-tested. |
| `src/ProDHCPServer.Windows` | Windows specifics: adapter reading and changes, the UDP 67 socket, ARP entries, firewall rule, other-server probe, ping and port checks. |
| `src/ProDHCPServer.App` | The WPF app: UI, elevation prompt, crash handling. |
| `tests/` | Core unit tests (run anywhere) and Windows integration tests (run on the Windows CI runner and never change adapter settings). |
| `docs/` | Hardware test checklists for each phase, and the screenshots used in this README (taken by the CI smoke test). |
| `tools/` | CI smoke test (launches the app and takes screenshots) and the manufacturer list updater (`python3 tools/update-oui.py`). |

**CI.** Every push and pull request runs the **Build and test** workflow on Windows: build, Core and Windows tests, publish the exe, and a smoke test that screenshots the app in light and dark mode. The exe and screenshots are uploaded as run artifacts.

**Contributing.** Work happens on branches with pull requests into `main`. See [CLAUDE.md](CLAUDE.md) for the project rules (they apply to people too), and use the PR template's checklist. Formatting follows `.editorconfig`; check it with `dotnet format whitespace ProDHCPServer.sln --verify-no-changes`.

## Licence

No licence has been chosen yet, so all rights are reserved by the author.
