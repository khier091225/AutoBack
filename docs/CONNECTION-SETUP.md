# Host and client connection setup

**Accidentally removed a paired computer?** A clean reinstall is unnecessary. In client v0.8.1 use **Pair AutoBack Agent again**, or **Configure → Pair again with a host invitation**. See [pairing recovery](PAIRING-RECOVERY.md) for preserved settings/backups, retired pending work and the installed/portable steps.

For an installed **Windows 7 SP1 32-bit** client, follow the [legacy client guide](WINDOWS7-CLIENT.md). Its native **Configure AutoBack Agent** shortcut selects the same fresh host invitation and starts the service after setup/permissions. The host remains the current modern Windows application.

Pairing, live monitoring, local backup, direct Google Drive upload, and verified recovery are available. Follow [local backup setup](LOCAL-BACKUP.md) after pairing, then [Google Drive setup](GOOGLE-DRIVE-SETUP.md) to authorize each client and [restore guide](RESTORE.md) to test recovery.

## 1. Run the host

Run the AutoBack WPF application on the host PC. Keep it open: the listener runs inside this application, not as a separate host service. It listens on IPv4 TCP **47831**. Settings shows the listener status, host endpoint, certificate fingerprint, and protected data location.

The live inventory initially has no computers. Saved pairings survive host restarts, but are shown offline/waiting until a fresh authenticated heartbeat arrives. An old timestamp never counts as a live connection after restart.

## 2. Create an invitation

Click **Pair computer** and enter a name/IP reachable from the client. Use `127.0.0.1` only when both host and client run on the same PC. For separate PCs, use the host's LAN IPv4 address or a computer name that resolves on the client.

Save the `.autoback-invite` file and transfer it directly to the intended client. The file contains the host's certificate fingerprint and a secret granting permission to pair **one** new agent; it expires in **five minutes** according to the host clock. Generating another invitation invalidates the previous outstanding invitation. **Cancel invitation** also invalidates it. Successfully used invitation files should be deleted.

Deliver this file through a trusted channel: it establishes which host the agent will trust. The agent rejects another certificate even if that server is reachable at the same address. Expired host certificates are also rejected. Replacing the host identity requires fresh client pairing.

## 3. First test in console mode

Publish on a development PC:

```powershell
dotnet publish src/AutoBack.Agent/AutoBack.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/agent-win-x64
```

Copy the complete `artifacts/agent-win-x64` folder to the client. Put its invitation beside `AutoBack.Agent.exe`. In that folder:

```powershell
.\AutoBack.Agent.exe pair --invite .\client.autoback-invite --name "PC1-Accounting"
.\AutoBack.Agent.exe configure
.\AutoBack.Agent.exe run
```

`--name` is optional; the default is the client's computer name. A successful pairing registers the device, but it becomes **Online** only after the running agent sends its first heartbeat. Leave the console open. Default console pairing uses the current Windows account's local app data and DPAPI protection, so pair and run with the same account.

Other useful commands:

```powershell
.\AutoBack.Agent.exe status
.\AutoBack.Agent.exe --help
```

`status` reports saved pairing, not current connectivity. The host dashboard is the source of live connection status. For isolated tests, append the same `--data-dir C:\Dedicated\AgentFolder` to every command. Use a new, dedicated directory; unrelated nonempty directories are refused.

## 4. Allow host LAN traffic when needed

Only the host needs an inbound connection rule; agents connect outbound. No firewall rules are changed automatically.

From an elevated PowerShell on the **host**, run the included script with the actual executable you start:

```powershell
.\scripts\Enable-HostFirewall.ps1 -HostExecutable "C:\Path\To\AutoBack.exe"
```

This creates an application-specific TCP 47831 rule for the local subnet on **Private/Domain** networks. Check your office's network configuration if machines are on different subnets. A Public network profile is not covered by this rule. If you move the host executable, review the existing `AutoBack-Host-47831` rule and its Program path.

From the client:

```powershell
Test-NetConnection -ComputerName HOST-LAN-IP -Port 47831
```

A successful TCP check only establishes reachability; the agent must still authenticate the pinned host and its own credential.

## 5. Run the client as an automatic Windows Service

Use this after confirming console connectivity. Stop the console agent first. Create a fresh invitation: console and service mode use separate credential stores, so revoke the console test pairing or choose another alias for the service.

Copy `scripts/Install-AgentService.ps1` to the client along with the published agent folder. From an **elevated PowerShell on the client**:

```powershell
.\Install-AgentService.ps1 -PublishedAgentDirectory "C:\Staging\agent-win-x64" -InvitationPath "C:\Staging\client.autoback-invite" -ComputerName "PC1-Accounting"
```

The script copies the published files into `%PROGRAMFILES%\AutoBack Agent`, pairs with `--machine`, and registers **AutoBackAgent** with Automatic startup under **NT AUTHORITY\LocalService**. Credentials are saved in `%PROGRAMDATA%\AutoBack\Agent`, encrypted with machine DPAPI, with access limited to SYSTEM, Administrators, and LocalService. Regular user accounts cannot initialize machine mode. Configure backup folders using `AutoBack.Agent.exe configure --machine` from an elevated terminal. The LocalService account needs read access to the chosen source and modify access to the destination; folder permissions are not changed automatically.

Service inspection:

```powershell
Get-Service AutoBackAgent
Stop-Service AutoBackAgent
Start-Service AutoBackAgent
& "$env:ProgramFiles\AutoBack Agent\AutoBack.Agent.exe" status --machine
```

Run machine-mode commands from an elevated terminal. Agent logs go to the Application event log using source `AutoBack.Agent`; console mode also shows connection/retry messages. Credentials and invitation secrets are not written to logs.

To unregister the service, run `Uninstall-AgentService.ps1` as administrator. It stops and unregisters the service while retaining the published files and protected configuration. Revoke the pairing in the dashboard when retiring the computer. Reinstallation with existing service credentials requires the install script's explicit `-ReplacePairing` switch and a fresh invitation.

## 6. Check disconnect and recovery behavior

- **Heartbeat:** every 5 seconds while connected.
- **Offline:** no authenticated heartbeat for 20 seconds; the last heartbeat remains visible. This indicates missing agent reports, not proof that the PC is powered off.
- **Host outage:** the agent retries automatically with exponential backoff capped near 30 seconds plus jitter.
- **Host restart:** its certificate and registry are retained; existing agents reconnect without another invitation.
- **Agent restart:** stored credentials are reused.
- **Revoke:** the host rejects that identity immediately and persists the revocation. The device is removed from active inventory. Stop its agent, create a fresh invitation, and use `pair --replace` (plus `--machine` for the service) to give it a new identity before starting again.

Connection activity is session history; device/job/automation/recovery/protection data persist. Use the v0.9.0 host and agent (protocol 5) for incremental Drive folder backups and current-folder history. Saved pairing credentials/certificates are reused; regenerate older-protocol invitation files for new pairing. Google operations require per-client authorization; Local only does not. See [incremental Drive](INCREMENTAL-DRIVE.md), [destination modes](DESTINATION-MODES.md), [automation/rollout](AUTOMATION-ROLLOUT.md) and [physical-PC checklist](ROLLOUT-CHECKLIST.md).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Host unavailable | Another host window may own TCP 47831 or the same data directory. Close it and restart. Check Windows account and host data access. |
| Agent cannot reach host | Host is open, correct LAN IPv4/hostname, TCP port reachable, firewall Program path and network profile. |
| Certificate verification fails | Use a fresh invitation from the intended host. Check clocks and whether host identity changed. Do not bypass validation. |
| Invitation rejected | It may be expired, cancelled, replaced, or used. Create a new one. |
| Computer name already paired | Revoke an obsolete pairing or use a unique client alias. |
| Agent is already paired | Use `run`; use `pair --replace` only when intentionally replacing the pairing. |
| Access denied with `--machine` | Use elevated PowerShell for initial pairing/inspection. The service itself uses LocalService. |
| Agent remains offline | Check whether `run` or the Windows Service is active and reporting; pairing by itself is not a heartbeat. |
| Cannot read protected data | Use the original Windows account/scope and machine. Existing identity files are never silently replaced after a decryption/parse failure. |

Service installation and real cross-PC connectivity still need validation on your target client environment. The automated suite verifies actual host/agent networking on loopback without installing services or changing firewall rules.
