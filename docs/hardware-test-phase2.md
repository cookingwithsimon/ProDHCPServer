# Phase 2 hardware test

About 20 minutes. You need: the unmanaged laptop, two AV devices on a small unmanaged switch (the NAV E and Desk Pro are ideal), and a USB stick or a second laptop for the profile import step if you have one.

Download `ProDHCPServer.exe` from the latest successful **Build and test** run on the Phase 2 pull request (Actions tab → run → Artifacts → `ProDHCPServer-win-x64`). Click **More info → Run anyway** on the SmartScreen warning.

Tick each box. If something fails, use **Export log…** (section 2) and send me the file.

## 1. Ping graph
- [ ] Both devices on the switch. Press **Start**. Once each has a lease, its row under **All devices** shows a small graph and a reply time like "1 ms" within a few seconds.
- [ ] The device shown at the top has a **Ping** line ("Last minute: … avg (… to …), 0% loss") and a wider graph under its details.
- [ ] Unplug one device's cable for about 10 s, then plug it back in. Its graph shows red marks for the missed pings, its reply time turns red ("No reply"), and the session log has two lines: "… stopped answering ping at …" and "… is answering ping again after … without replies".
- [ ] The other device's graph stays steady the whole time.
- [ ] Click the other device in the list: the top panel, ping line and big graph switch to it.
- [ ] Press **Stop**: the graphs and rows go away.

## 2. Session log filter and export
- [ ] Open the **Session log**. Set the first box to **Problems only**: only red lines are left, and the count says "N of M entries match".
- [ ] Set it back to **Everything**, pick **DHCP** in the second box: only DHCP lines are left.
- [ ] Type the NAV's IP (or part of its MAC) in the search box: only lines mentioning it are left. **Show everything** clears all three.
- [ ] **Export log…** as **Text**. Open the file in Notepad: it starts with the app version, laptop name, adapter (with driver), profile and start time, then one line per entry with the full date and time.
- [ ] **Export log…** as **CSV**. Open it in Excel: columns Time, Severity, Category, Message, Detail, one row per entry, nothing garbled.

## 3. Profiles
- [ ] The top bar has a **Profile** box showing "Default (preset)". Open it: the five presets are listed.
- [ ] Pick **Extron factory subnet**. The status line says Start will set 192.168.254.1/24. Press **Start**: the devices get 192.168.254.100 and up. Press **Stop**.
- [ ] Press the gear button. The panel is titled **Profiles**, shows the preset's note, and its fields are greyed out with "This is a preset… Press Duplicate".
- [ ] Press **Duplicate**: "Extron factory subnet copy" is created and selected, and the fields can now be changed. Rename it (for example "Bench rack") and set the pool to .150 to .160. Press **Save profile**.
- [ ] Type a bad value (pool end 192.168.1.5) and click into another field: a red box explains the problem. **Discard changes** puts the saved values back.
- [ ] Open **More DHCP options** and set NTP to 192.168.254.1. **Add an option** with code 66 and click into another field: a red box says option 66 has its own field. Change the code to 160, type Text, value `http://192.168.254.1/`. Save.
- [ ] Start with this profile. In the log, select the ACK line for a device: the detail shows options 42 and 160 were sent. Stop.
- [ ] **Export…** the profile to the desktop. **Delete** it: the confirmation names the profile. It's gone and Default is selected.
- [ ] **Import…** the file you exported: the profile is back with the same settings. (If you have a second laptop, import it there too.)
- [ ] Close and reopen the app: your profile is still there and still selected.
- [ ] While the server is running, the top-bar profile box and the panel's fields are greyed out, and the panel says to stop the server to change profiles. The theme can still be changed.

## 4. Phase 1 still works
- [ ] Quick repeat of the Phase 1 main case: one device straight on the cable, Start, lease, ping and WebUI green, **Open WebUI**, Stop, adapter back to its original settings.
