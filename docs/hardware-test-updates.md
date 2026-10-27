# Update checker test

About 15 minutes on the unmanaged laptop, with internet. No AV devices needed.

The updater can only update to a release that's already published on GitHub, so this test updates a test build to the newest published release (v1.1.0). v1.1.0 doesn't have the updater itself, so after the update they leave `ProDHCPServer.exe.old` behind; delete it by hand at the end.

Download `ProDHCPServer.exe` from this PR's latest **Build and test** run (Actions tab → run → Artifacts → `ProDHCPServer-win-x64`) and put it in a folder of its own, for example `C:\Tools\ProDHCPServer\`. Click **More info → Run anyway** on the SmartScreen warning.

Tick each box. If something fails, use **Export log…** and send me the file.

## 1. Test builds don't nag

- [ ] Start the app with internet. After 10 seconds there is **no** green update banner (test builds only check when asked).
- [ ] Open Settings (gear, or Ctrl+,) and scroll to **Updates**. It says "This is test build 0.1.… It doesn't check for updates by itself; use Check now."

## 2. Offline

- [ ] Unplug or turn off the internet connection (Wi-Fi off, no network cable to the internet).
- [ ] Press **Check now**. Within about 10 seconds it says "Couldn't reach GitHub to check for updates. That's normal on an isolated AV network…". No error banner, no popup.
- [ ] The app otherwise works as normal: **Start** and **Stop** a session on a device or an empty port.

## 3. Check and skip

- [ ] Reconnect the internet. Press **Check now**. It says "Version 1.1.0 is available. You have test build 0.1.…", and the green banner appears at the top of the main window with **Install and restart**, **What's new**, **Not now** and **Skip this version**.
- [ ] **What's new** opens the release page in your normal browser.
- [ ] **Not now** hides the banner.

## 4. Can't install while serving

- [ ] Press **Check now** again so the banner is back, then **Start** the server.
- [ ] **Install and restart** is greyed out and the banner says "Stop the server first: installing restarts ProDHCPServer."
- [ ] Press **Stop**. **Install and restart** becomes clickable.

## 5. Install

- [ ] Press **Install and restart**. The banner shows "Downloading 1.1.0… nn%" with a progress bar, then the window closes and ProDHCPServer opens again by itself within a few seconds, with **no** UAC prompt.
- [ ] The log's first line says "ProDHCPServer 1.1.0 started (administrator)."
- [ ] In the folder, `ProDHCPServer.exe` is the new version (right-click → Properties → Details → Product version 1.1.0) and `ProDHCPServer.exe.old` is the test build. Close the app and delete the `.old` file.

## Later, once a release with the updater is out

When the release after this one is published, run the earlier release with the updater and check:

- [ ] A few seconds after start, the green banner appears without pressing anything.
- [ ] **Skip this version**, then restart the app: no banner. **Check now** in Settings still offers it.
- [ ] Untick **Check for a new version when ProDHCPServer starts**, restart: no check in the log.
- [ ] After **Install and restart**, the log says "started (administrator) after updating." and `ProDHCPServer.exe.old` is gone within a few seconds.
