# Phase 3 hardware test

About 30 minutes. You need:

- The unmanaged laptop.
- Two AV devices on a small unmanaged switch (the NAV E and NAV SD are ideal).
- One device you can set to a **static IP on a different subnet**. For example, set the NAV SD to 192.168.1.50 /24 in its web page, or use anything still on a factory static address such as an Extron switcher at 192.168.254.254.
- A managed switch with LLDP or CDP turned on, if you have one, for section 5.

Download `ProDHCPServer.exe` from the latest successful **Build and test** run on `main` (Actions tab → run → Artifacts → `ProDHCPServer-win-x64`). Click **More info → Run anyway** on the SmartScreen warning.

Tick each box. If something fails, use **Export log…** and send me the file.

## 1. Found on the network

- [ ] Press **Start** with both devices on the switch, one of them on its static IP. Under the device list there's a **Found on the network** section saying it's listening.
- [ ] Within a minute, the static device appears there with its IP, an amber light and "Static IP on 192.168.1.x, which this laptop isn't on yet". If it doesn't, power-cycle it.
- [ ] The DHCP device is **not** listed there (it's in **All devices** above).
- [ ] After 30 s with no DHCP request from the static device, step 5 names it: "No DHCP requests…, but 192.168.1.50 (…) is on the cable with a static IP."
- [ ] The session log has a "Found 192.168.1.50 … It has a static IP on another subnet" line.

## 2. Reach it

- [ ] Press **Reach it** on the static device. Within a few seconds its light turns green, the line says "This laptop can reach it", and **Open WebUI** appears.
- [ ] The log says "Added 192.168.1.249/24 to "Ethernet" so this laptop can reach 192.168.1.50. It's removed when you press Stop."
- [ ] **Open WebUI** opens the device's web page.
- [ ] The DHCP device still works the whole time (its ping graph keeps going).
- [ ] Press **Stop**, then check the adapter in Windows (`ipconfig`): 192.168.1.249 is gone and the adapter is back to how it was.

## 3. Scan for static IPs

- [ ] Unplug the static device, Start, and plug it back in only after the server is running. If it doesn't show up by itself, press **Scan for static IPs**.
- [ ] The line under the button counts through the subnets ("Scanning 192.168.1.x (2 of 5)…") and ends with "Scan finished: N devices answered." It takes a minute or two.
- [ ] The static device is listed with its MAC and manufacturer, found by "subnet scan".
- [ ] While it scans, `ipconfig` shows one extra temporary address at a time; when it finishes, none are left.
- [ ] Press **Scan for static IPs** again and press **Stop** while it's still scanning: the adapter goes back to its original settings with no leftover addresses.

## 4. Discovery protocols

Tick what applies to the gear you have; skip the rest.

- [ ] A device that advertises itself (Dante, Q-SYS, Sennheiser, Shure, a ClickShare or most network players) shows its own name, found by mDNS, UPnP, Q-SYS or Shure.
- [ ] A projector or display with PJLink turned on shows up, found by PJLink, within about 30 s.
- [ ] Hover over a found device: the tooltip lists what it said (services, model, firmware).

## 5. Switch port (needs Npcap)

Only if you're happy to install [Npcap](https://npcap.com/#download) on the test laptop (free, a normal installer).

- [ ] Without Npcap: the section says "Install Npcap to see which switch port you're plugged into…", and **Get Npcap** opens the download page.
- [ ] With Npcap, through a managed switch with LLDP or CDP on: within about 30 s a line says "Plugged into <switch> port <port>, VLAN <n>". The log has the same line.
- [ ] With Npcap, power-cycle the static device: it appears within seconds of booting, found by ARP.

## 6. Phases 1 and 2 still work

- [ ] Quick repeat: one device straight on the cable, Start, lease, ping graph and WebUI green, **Open WebUI**, **Export log…**, Stop, adapter back to its original settings.

## Results
Passed 2026-10-16, every item except the PJLink one in section 4, which wasn't tried because no PJLink projector or display was on hand. Run that item the next time one is available.
