# Microsoft Store package (MSIX) test

About 30 minutes on the **unmanaged** laptop (not the work laptop), with one AV device or any DHCP client and a switch or cable. Run this before the first Store submission.

It tests the Store package with a throwaway test certificate, because only the Store can sign the real one. The point is to check that the packaged app behaves like the exe: the admin prompt, the firewall rule and putting the adapter back.

## Setup

1. Download the `ProDHCPServer-msix-test` artifact from this PR's latest **Build and test** run (Actions tab > run > Artifacts) and unzip it. It has `ProDHCPServer-test.msix` and `ProDHCPServer-test.cer`.
2. If the GitHub exe is running, close it.
3. Trust the test certificate: open **PowerShell as administrator** in the unzipped folder and run
   `Import-Certificate -FilePath .\ProDHCPServer-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`

Tick each box. If something fails, export the log (**Export…** above the log) and send me the file, or a photo of the message.

## 1. Install and start

- [ ] Double-click `ProDHCPServer-test.msix`. The App Installer window shows ProDHCPServer with **Install**. Install it.
- [ ] Start **ProDHCPServer** from the Start menu. The app's own "Administrator rights needed" window appears first.
- [ ] Press **Restart as administrator**. The Windows UAC prompt appears; choose **Yes**. The main window opens.
- [ ] Open Settings (gear, or Ctrl+,) and scroll to **Updates**. It says "The Microsoft Store installs updates for this copy", with no Check now button and no tick box.
- [ ] Close the app and start it again from Start. Your look and dark/light choice are remembered.

## 2. Serve DHCP

- [ ] Choose the wired adapter and press **Start**. It starts serving with no firewall or port 67 error.
- [ ] Plug in the device (or power-cycle it). It gets an address and shows in the list with its manufacturer and ping.
- [ ] Open **Windows Defender Firewall with Advanced Security** > Inbound Rules. The two "ProDHCPServer" rules are there and their program is under `C:\Program Files\WindowsApps\`.
- [ ] Press **Stop**. The adapter goes back to how it was (check `ipconfig`), and the two firewall rules are gone.

## 3. Find devices

- [ ] Run the device finder on a device with a static IP. It appears, and **Reach it** works.
- [ ] If Npcap is installed, the switch port shows. If not, the app says Npcap is missing.

## 4. Crash recovery

- [ ] Press **Start**, then end ProDHCPServer in Task Manager (Details tab > ProDHCPServer.exe > End task).
- [ ] Start it again from Start. It offers **Restore adapter**; press it, and check `ipconfig`.

## 5. Uninstall

- [ ] Settings > Apps > Installed apps > ProDHCPServer > Uninstall.
- [ ] In the administrator PowerShell, remove the test certificate:
  `Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=ProDHCPServer Test' | Remove-Item`
