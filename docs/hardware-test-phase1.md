# Phase 1 hardware test

About 20 minutes. You need: the laptop, one DHCP-capable AV device (anything with a WebUI), a small unmanaged switch with a second device if you have one, and access to any network that already has DHCP (office or home router).

Download `ProDHCPServer.exe` from the latest successful **Build and test** run (Actions tab → run → Artifacts → `ProDHCPServer-win-x64`). Windows SmartScreen will say "Windows protected your PC" because the exe is unsigned: click **More info → Run anyway**.

Before you start, note what your Ethernet adapter is set to now (Settings → Network → Ethernet → IP assignment). Every test below should end with it back to exactly that.

Tick each box. If something fails, open the **Session log**, select the red line, and send me the text from the details pane (or a screenshot).

## 1. Launch and elevation
- [ ] Double-click the exe. A window explains why admin rights are needed before Windows asks.
- [ ] Click **Continue**, then **Yes** on the Windows prompt. The main window opens.
- [ ] Try launching a second copy: it says one is already running and closes.

## 2. One device, straight on the cable (the main case)
- [ ] Device plugged into the laptop and powered on. Pick your Ethernet adapter. Step 2 shows link speed.
- [ ] Press **Start**. Within about 10 s: steps 1–4 green (adapter, link, 192.168.100.1/24, listening).
- [ ] Power-cycle the device (or replug the cable). Step 5 shows `DISCOVER → OFFER → REQUEST → ACK` with times; step 6 names the device with 192.168.100.100.
- [ ] Device card shows IP, MAC, manufacturer, lease time left, first seen.
- [ ] Step 7 (ping) and step 8 (WebUI) go green, or amber with a sensible reason if the device blocks ping.
- [ ] **Open WebUI** opens the device page in your normal browser (not an admin browser). Try the port buttons and **Copy**.
- [ ] Press **Stop**. The log says the adapter is back to its original settings, and Windows Settings agrees.

## 3. Cable and link edge cases
- [ ] Unplug the cable, press **Start**. Step 2 red "No link"; step 3 amber "saved, applied once a cable is connected".
- [ ] Plug in. Steps 2–4 turn green by themselves; the device gets a lease.
- [ ] While running, unplug for ~10 s and replug. The server comes back by itself and re-checks for other DHCP servers.
- [ ] A device with a static IP (or DHCP off): after 30 s step 5 is amber, saying no requests were seen and it may have a static IP.

## 4. Several devices through a switch
- [ ] Two or three devices on a small switch. Each gets its own address; all appear under **All devices**.
- [ ] Click a device in the list: it becomes the one shown at the top, and ping/WebUI follow it.

## 5. Safety: another DHCP server
- [ ] Plug the laptop into a network that already has DHCP (office port or home router) and press **Start**.
- [ ] A red banner says another DHCP server is on the link, names it, and the server does **not** serve.
- [ ] **Stop** puts the adapter back. (Only try **Serve anyway…** on a network you own, e.g. your home router; it asks for confirmation.)

## 6. Crash recovery
- [ ] Press **Start**, then kill ProDHCPServer from Task Manager (End task).
- [ ] Windows Settings now shows the adapter on 192.168.100.1 (expected: the app couldn't restore).
- [ ] Launch ProDHCPServer again. A banner offers to restore the original settings. Click **Restore now**; Windows Settings shows the original settings.
- [ ] Repeat with the window's close button while running: it restores before closing, no banner next launch.

## 7. Wi-Fi and VPN
- [ ] Connect Wi-Fi to a network on 192.168.100.x if you can (or set the server IP in **Settings** to match your Wi-Fi subnet). Step 1 turns amber naming the Wi-Fi adapter; while running, the banner offers **Turn it off until Stop**. Use it, then **Stop**: Wi-Fi comes back on.
- [ ] With a VPN connected, step 1 mentions it.

## 8. Settings and display
- [ ] **Settings**: change the server IP to 10.10.10.1, click **Suggest** for the pool, **Save**, **Start**: the device gets 10.10.10.100.
- [ ] Type an invalid pool (e.g. outside the subnet): saving shows a plain-English error.
- [ ] Theme: Dark and Light both readable; status readable from arm's length.
- [ ] A device that never accepts offers: tick **Always broadcast replies** and retry.

## What to send back
For anything that failed: which step, what you expected, and the log details text. The adapter's original settings are also kept in `C:\ProgramData\ProDHCPServer\adapter-backup.json` while a session is running.
