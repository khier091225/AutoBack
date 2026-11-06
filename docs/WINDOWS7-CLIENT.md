# Windows 7 SP1 32-bit client — legacy pilot

This separate **x86 .NET Framework 4.8 agent** connects to the current v0.8 host using protocol 4 and pinned TLS 1.2. The host stays on its existing supported modern Windows PC. The ordinary `win-x64` installer cannot run on Windows 7; changing its CPU target alone is insufficient.

Package: `artifacts/installers/AutoBack-Agent-Legacy-0.8.0-win-x86.msi`.

**Status:** built for Windows 7 SP1 x86. Automated checks run in actual 32-bit Framework processes on the development Windows PC. Actual Windows 7 installation, Schannel/Google connectivity, service startup and reboot checks remain required before rollout. This is not a claim of Microsoft support: Windows 7 itself is out of support.

## Prerequisites

- Windows 7 **Service Pack 1**, 32-bit. Check Control Panel → System, or `winver`. The MSI refuses Windows 7 without SP1.
- Microsoft **.NET Framework 4.8 Runtime**, installed separately. The MSI checks the installed Framework release and gives a prerequisite message when missing; it does not download/install prerequisites automatically. .NET Framework 4.8.1 is not the Windows 7 target.
- Install applicable Windows 7 servicing/SHA-2 updates and maintain its trusted root certificates and correct clock. TLS 1.2, supported ciphers, and current certificate trust are required for Google HTTPS. The agent never disables Google certificate validation or the host certificate pin to work around obsolete OS settings.
- A system browser capable of completing the current Google OAuth consent flow for Google mode. Browser/Google sign-in must be tested on the target PC; installing the agent cannot make an obsolete browser compatible.
- Administrator access for installation and machine-wide setup. Enough free space for the temporary archive (Google) or permanent local destination (Local only).

Microsoft references: [.NET Framework system requirements](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements), [install Framework 4.8 on Windows 7 SP1](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-7), [TLS guidance](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls).

## Install and pair

1. Keep AutoBack Host v0.8 open on the host PC. Use **Pair computer**, enter the host's reachable LAN IPv4/name, then save its `.autoback-invite` file.
2. Install the **Agent-Legacy / win-x86** MSI on the Windows 7 client. Do not install the ordinary x64 client alongside it. Both editions register `AutoBackAgent`; a conflicting existing service is rejected.
3. Transfer a fresh invitation to that client. Invitations expire in five minutes, are single-use, and are replaced when another invitation is generated. Pair clients one at a time.
4. Open **Configure AutoBack Agent** in the **AutoBack Agent Legacy** Start Menu folder and approve administrator elevation. This shortcut uses a native Framework launcher; no PowerShell upgrade is needed for normal setup.
5. Choose the invitation, then choose source folders. **Google Drive is the default**, with automatic private temporary storage; select the Desktop OAuth JSON and complete sign-in. **Local only** asks for a separate permanent destination and does not require Google.
6. Grant `NT AUTHORITY\LocalService` Read/list permission on each source. For Local only, also grant Modify permission on the backup destination. Setup does not silently grant permissions to all user files.
7. Type `START` only after those permissions are ready. Setup enables delayed automatic startup and starts the service. The host should show Online after its first authenticated heartbeat.

The service is installed **stopped, Manual** until setup completes. Pairing/configuration/keys use `%PROGRAMDATA%\AutoBack\Agent` with machine DPAPI and restricted ACLs. Existing stores and backup files are retained across uninstall. Do not copy DPAPI stores between PCs. Console/current-user pairing is a separate scope.

**Choose AutoBack backup folders** opens just the mode/source picker later. Pairing persists across restarts. Keep the host open for monitoring and new scheduled/manual backup commands. Existing source, retention and encryption settings remain explicit; the installer does not enable a production schedule or encryption policy.

## Compatibility behavior

The legacy client shares the current backup, multiple-folder manifests, restore, Google OAuth/Drive resumable transfer, cleanup, retention and worker logic. Windows-specific adapters provide Framework file/stream APIs, pinned Schannel TLS 1.2 and managed AES-256-GCM/HKDF through Bouncy Castle. Archive/recovery-file formats are unchanged; no weaker legacy encryption format is introduced. File hashing, resumable chunks and authenticated archive chunks use bounded buffers rather than loading an entire backup into RAM. A 32-bit process still has a smaller address space; very large file inventories/manifests need pilot checks.

Google mode removes only its exact verified temporary archive after cloud verification and host acknowledgment. A failed/unconfirmed upload preserves it. Local-only jobs are excluded from automatic uploads even when a Google account was previously connected. Old local files are not moved/deleted simply by choosing a new destination mode.

## Required Windows 7 pilot

Record the exact Windows edition/SP1, CPU architecture, Framework release, installed Windows updates, browser, host/client versions and date. Do not record passwords, invitation secrets, OAuth tokens or encryption keys in the test report.

- [ ] Install prerequisite checks: missing Framework message and Windows 7 without SP1 rejection; successful x86 MSI installation on SP1.
- [ ] Native setup elevation, fresh invitation pairing over LAN; incorrect/expired invitation rejection; Online/Offline and host restart/reconnection.
- [ ] LocalService can read every selected user folder; inaccessible/locked files are reported accurately.
- [ ] Host **Backup Now** in Local only; permanent version retained and no Google upload.
- [ ] Google browser consent, TLS 1.2 access, real upload from the client, verified cloud result, temporary cleanup, and recovery from Drive after cleanup.
- [ ] Interrupted upload/reboot resumes safely; failed upload keeps its temporary file; incorrect host pin never connects.
- [ ] Multi-folder encrypted backup and recovery export/import; restore on both the legacy and modern client and compare source bytes.
- [ ] Large representative inventories and archive sizes on the actual 32-bit RAM/disk limits.
- [ ] Automatic delayed service startup after reboot; source access without an interactive user session; service recovery and retention policy behavior.
- [ ] Upgrade/repair/uninstall preserves protected data/recovery exports/backup files. Do not run a legacy and modern worker simultaneously against the same store.

## Build and automated checks

Build on the development PC with the .NET SDK; the targeting assemblies are restored as a build dependency. Windows 7 clients need only Framework 4.8, not the SDK.

```powershell
dotnet build AutoBack.slnx -c Release
.\tests\AutoBack.Legacy.UnitTests\bin\Release\net48\AutoBack.Legacy.UnitTests.exe
dotnet run --project tests/AutoBack.Legacy.IntegrationTests -c Release --no-build
.\scripts\Build-Installers.ps1 -TargetPackage AgentLegacy
.\scripts\Test-InstallerPackages.ps1 -IncludeLegacy
```

Framework checks cover actual x86 runtime, AES-GCM and RFC 5869 known-answer vectors, tampered-tag rejection, encrypted multi-folder archives, simulated resumable Drive upload/retry/cleanup/cloud restore and local-only isolation. Integration checks launch the real legacy worker against the current .NET 10 host and verify pinned TLS, Backup Now, encrypted archives and password-protected recovery compatibility in both directions. Evidence is written to `artifacts/legacy-validation/result.json`. These checks do not install/start a Windows Service or prove Windows 7 itself has working TLS/browser prerequisites.
