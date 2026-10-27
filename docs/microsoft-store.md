# Publishing ProDHCPServer in the Microsoft Store

The Store signs MSIX packages itself, so a Store copy runs on laptops without SmartScreen's "Windows protected your PC" warning and without a code-signing certificate of our own. The GitHub exe carries on as before, unsigned.

Facts below were checked against Microsoft's pages on 27 October 2026 (Store policies version 7.20, effective 22 October 2026). Sources are listed at the end.

## The one thing that could stop it: administrator rights

ProDHCPServer needs administrator rights for everything it does (changing adapter settings, firewall rules, listening on UDP port 67). Microsoft's packaging guide says apps that need elevation "won't be accepted into the Store". The capability that allows it, `allowElevation`, is "subject to approval under strict criteria", and Microsoft asks developers to email **reportapp@microsoft.com** with a detailed justification **before** submitting.

So the first step is that email (draft below). Everything else in this guide only matters if Microsoft agrees.

A Store listing of the plain exe doesn't avoid this: for an exe or MSI, policy 10.2.9 requires it to be signed with a certificate from a trusted certificate authority, which is what we're trying to avoid.

## What the repository already does

- **Every push** (Build and test workflow) builds a test MSIX with a placeholder identity, signs it with a throwaway certificate, installs it on the Windows runner, starts it, screenshots it (`msix-settings.png`) and uninstalls it. The package and its `.cer` are in the run's `ProDHCPServer-msix-test` artifact, for the hardware test (`docs/hardware-test-msix.md`).
- **The Release workflow** also builds the real Store package, `ProDHCPServer-vX.Y.Z-x64.msix`, once the three repository variables below are set. It's unsigned on purpose (the Store signs it) and lands in the run's `release-vX.Y.Z` artifact, not on the GitHub release page.
- **A Store copy doesn't update itself.** It detects that it's running from an MSIX package, hides the GitHub update check and Install button, and Settings > Updates says the Store installs updates.
- **Privacy policy:** `PRIVACY.md`, required for desktop apps by policy 10.5.1. Its URL is https://github.com/cookingwithsimon/ProDHCPServer/blob/main/PRIVACY.md once merged.

## Steps only you can do

### 1. Create the developer account (free)

Registration is free for individuals. It needs a personal Microsoft account and an ID check (photo of a government ID plus a selfie). No credit card.

1. Go to [Partner Center](https://partner.microsoft.com/dashboard) and sign up for the Microsoft Store as an **Individual** developer. (A company account is also free now, but needs business verification; an individual account is enough.)
2. Choose the publisher display name carefully: it shows on the Store page, for example "Simon Henley".

### 2. Ask Microsoft about admin rights

Send the email below from the address on your developer account. Wait for a yes before submitting.

> **To:** reportapp@microsoft.com
> **Subject:** allowElevation justification for a new desktop app: ProDHCPServer
>
> Hello,
>
> I'm preparing to submit ProDHCPServer, a free desktop (WPF, full trust) app packaged as MSIX, and I'd like approval to use the allowElevation restricted capability.
>
> What the app does: it's a portable DHCP server for audio-visual field technicians. A technician plugs a laptop into a projector, display or control processor that has no network yet, presses Start, and the app gives the device an IP address so it can be configured. It also finds devices that already have a static address.
>
> Why it needs administrator rights (there's no unelevated alternative for these):
> - It changes the Ethernet adapter's IPv4 settings to a static address for the session, and puts the original settings back when the session stops, the app closes or after a crash.
> - It listens on UDP port 67 (DHCP server) and adds an inbound Windows Firewall rule for its own exe while serving, removed on stop.
> - It adds static ARP entries so a device can be reached before it has replied.
>
> How it elevates: it starts unelevated, explains in plain words why it needs administrator rights, and only then restarts itself with the standard UAC prompt. It installs no driver and no service. Packet capture for switch-port details (LLDP/CDP) is optional and uses Npcap only if the user has installed it separately.
>
> Source code: https://github.com/cookingwithsimon/ProDHCPServer
> Privacy policy: https://github.com/cookingwithsimon/ProDHCPServer/blob/main/PRIVACY.md
>
> Could you tell me whether this use would be approved, or what you'd need from me?
>
> Thanks,
> Simon

### 3. Reserve the name

Partner Center > **Apps and games** > **New product** > **MSIX or PWA app**, and reserve **ProDHCPServer**. Reserving is free and holds the name while you wait for Microsoft's answer.

### 4. Copy the package identity into GitHub

In Partner Center, open the app > **Product management** > **Product identity**. Copy three values into the repository's variables (GitHub > the repo > **Settings** > **Secrets and variables** > **Actions** > **Variables** tab > **New repository variable**). They're not secret.

| Variable | Partner Center value |
|---|---|
| `STORE_IDENTITY_NAME` | Package/Identity/Name |
| `STORE_PUBLISHER` | Package/Identity/Publisher (starts with `CN=`) |
| `STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |

### 5. Build the Store package

Run **Actions** > **Release** > **Run workflow** with the version, as for any release (add `.github/release-notes/vX.Y.Z.md` first). When it finishes, download the run's `release-vX.Y.Z` artifact; the `.msix` is in its `store` folder.

Each Store submission needs a higher version than the last one, so make a new release for each.

### 6. Fill in the submission

In Partner Center, start a submission for ProDHCPServer:

- **Pricing and availability:** Free. Markets as you like.
- **Properties:** category **Developer tools** (or **Utilities & tools**), and the privacy policy URL from above.
- **Age ratings:** answer the questionnaire. It's a utility with no user content or chat.
- **Packages:** upload the `.msix` from step 5.
- **Store listing:** description below, plus screenshots. The CI smoke-test screenshots (`main-dark.png`, `main-light.png`, `settings-dark.png` on the `ci-screenshots` branch) are a good start.
- **Submission options > Restricted capabilities:** Partner Center asks why the package uses `runFullTrust` and `allowElevation`. Suggested text:
  - *runFullTrust:* "ProDHCPServer is a full-trust WPF desktop app."
  - *allowElevation:* "Changing the Ethernet adapter's IP settings, serving DHCP on UDP port 67 and adding a firewall rule need administrator rights. The app asks once at start with the standard UAC prompt. Discussed with reportapp@microsoft.com on [date]."
- **Notes for certification:** "No account or hardware needed to test. Start the app, choose Yes on the UAC prompt, choose a wired adapter and press Start. With nothing plugged in it shows 'Waiting for a device'. Npcap is optional; without it the app says so and only the switch-port details are missing."

Submit. Certification can take several business days, and Microsoft emails the result.

### Store description (draft)

The first line is required: policy 10.2.4 says a dependency on other software must be disclosed at the start of the description.

> Switch-port details (LLDP/CDP) need Npcap, a free separate download from npcap.com. Everything else works without it.
>
> ProDHCPServer is a portable DHCP server for AV field technicians. Plug your laptop into a projector, display, DSP or control processor, press Start, and see straight away whether it got an IP address, and if not, why.
>
> - Serves DHCP on the wired adapter you pick, and puts your adapter's settings back when you stop, close the app or after a crash.
> - Shows each device's manufacturer, address and a live ping graph.
> - Finds devices that already have a static IP and reaches their subnet in one click.
> - Saved profiles for subnet, pool, lease time and DHCP options (router, DNS, NTP, option 43/66/150 and custom options).
> - Plain-English status and troubleshooting, and a session log you can export.
>
> Needs administrator rights, because it changes adapter settings and serves DHCP.

## What stays the same, and what to watch

- The GitHub release exe keeps its own update checker. Store copies get updates from the Store.
- Npcap can't be bundled, because MSIX packages can't install drivers, so the app still offers the npcap.com link.
- Untested until the MSIX hardware test: whether the elevated restart from an MSIX install behaves exactly like the exe, and whether the firewall rule works with the app's install path under `C:\Program Files\WindowsApps`. Run `docs/hardware-test-msix.md` on the unmanaged laptop before submitting.
- Microsoft's packaging guide also discourages starting command-line tools, and the app runs `netsh` for adapter and firewall changes. Policy 7.20 doesn't forbid it, but certification might ask about it.

## Sources

- [Free developer registration for individual developers on Microsoft Store](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/) (Windows Developer Blog, September 2025)
- [Publish to Microsoft Store as a company, now with free registration](https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/) (Windows Developer Blog, May 2026)
- [Microsoft Store Policies](https://learn.microsoft.com/windows/uwp/publish/store-policies), version 7.20: 10.2.3, 10.2.4, 10.2.9, 10.5.1, 10.6
- [App capability declarations](https://learn.microsoft.com/windows/apps/package-and-deploy/app-capability-declarations): allowElevation and runFullTrust
- [Prepare to package a desktop application](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-prepare): elevation and command-line utilities
