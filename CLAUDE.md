# ProDHCPServer: notes for Claude

Windows 10/11 DHCP server tool for AV field technicians. C# on .NET 10, WPF with the Fluent theme, shipped as one self-contained single-file exe (win-x64). The user is an AV tech, not a developer: anything shown in the UI must be plain English with a suggested next step.

## Layout

- `src/ProDHCPServer.Core`: platform-independent logic (DHCP codec and engine, adapter backup/restore lifecycle, status chain, OUI lookup). No Windows APIs here.
- `src/ProDHCPServer.Windows`: everything that touches the OS (adapters, sockets, ARP, firewall, ping), behind interfaces so Core stays testable with fakes.
- `src/ProDHCPServer.App`: WPF UI (MVVM with CommunityToolkit.Mvvm), elevation manifest, crash handling.
- `tests/ProDHCPServer.Core.Tests`: unit tests, run anywhere. `tests/ProDHCPServer.Windows.Tests`: integration tests, Windows only, must never change adapter settings.
- `docs/hardware-test-phaseN.md`: the manual checklist Simon runs on real hardware at the end of each phase.

## Commands

```
dotnet test tests/ProDHCPServer.Core.Tests          # works on Linux
dotnet build ProDHCPServer.sln                      # builds everything, WPF included, on Linux
dotnet format whitespace ProDHCPServer.sln --verify-no-changes   # formatting check
dotnet publish src/ProDHCPServer.App -c Release -r win-x64 -o publish   # Windows only in practice
```

In cloud sessions the SessionStart hook (`.claude/hooks/session-start.sh`) installs the .NET 10 SDK from the Ubuntu feed (the dot.net install script is blocked) and restores packages.

## Rules

- `TreatWarningsAsErrors` is on for every project. Fix warnings; don't suppress them without a comment saying why.
- Keep Windows calls out of Core. New OS behaviour goes in the Windows project behind an interface, with a fake in the tests.
- Any change to adapter settings must go through the backup/restore session so Stop, exit and a crash all put the adapter back.
- The Windows integration tests and the UI (smoke-test screenshots) only run on the Windows CI runner. Check the CI run, not just local Linux tests, before calling a change done.
- Work is phased (1: core DHCP loop, 2: ping/logging/profiles, 3: discovery). Each phase ends with Simon's hardware test pass before the next one starts. Don't pull later-phase features forward unasked.
- Builds are downloaded from GitHub Actions artifacts. The exe is not code-signed yet.
