# ProDHCPServer privacy policy

Last updated: 27 October 2026

ProDHCPServer is a DHCP server and device finder that runs on your own Windows laptop. It has no account, no analytics, no advertising and no telemetry. It doesn't send anything about you or your devices to the developer or to anyone else.

## What it keeps on your laptop

- Your settings and saved profiles, in `%LOCALAPPDATA%\ProDHCPServer`.
- A backup of your network adapter's settings while a session runs (and after a crash, until they are put back), in `%ProgramData%\ProDHCPServer`.
- Crash reports, in `%ProgramData%\ProDHCPServer`. They stay on the laptop unless you send one yourself.
- The session log, in memory. It is only saved to a file if you press **Export log**.

These files and an exported log contain network details such as IP addresses, MAC addresses and device names from the network you plug into. None of it leaves the laptop unless you share an exported file yourself.

## What it sends over the network

- On the network you plug the laptop into: DHCP replies, pings, ARP and the discovery requests that find AV devices (for example mDNS, and UPnP requests that read a device's description from the device itself). This traffic stays on that local network.
- The copy downloaded from GitHub checks GitHub's public API for a newer release when it starts, if you leave that turned on in Settings > Updates. GitHub sees your IP address, as with any website. The Microsoft Store copy doesn't do this; the Store handles its updates.
- Links in the app (for example to the Npcap download or a release page) open in your web browser.

## Contact

Questions about this policy: open an issue at https://github.com/cookingwithsimon/ProDHCPServer/issues.
