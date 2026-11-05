# AutoBack

A Windows host dashboard and background client agent for centrally controlled file and folder backups. Intended flow: **host → client command; client → Google Drive upload; client → host progress**.

## Current milestone: restore and verification

- An embedded host listener on TCP **47831**, active while the WPF host is open.
- A client agent that connects outbound and reports an authenticated heartbeat every **5 seconds**.
- Live paired-device inventory, last heartbeat, and online/offline transitions. A client is offline after **20 seconds** without a heartbeat.
- Single-use, five-minute pairing invitations, certificate-pinned TLS, per-agent credentials, and pairing revocation.
- Protected host/agent configuration, persistent device identity, automatic agent reconnection, and Windows Service support.
- Client-selected source/destination folders, host-triggered backups, real progress and errors, and cancellation.
- A new full ZIP per backup, including empty folders and a SHA-256 manifest; the saved ZIP is verified before completion is reported.
- Persistent host job history and client job journal, with idempotent delivery and interrupted-job recovery.
- Per-client Google Desktop OAuth/PKCE sign-in, DPAPI-protected refresh tokens, resumable direct uploads, retry checkpoints, and remote size/checksum verification.
- Independent local/cloud job results and a verified Drive link in the host's history. Cloud failure never rewrites a successful local result.
- Local ZIP and direct Google Drive recovery into a fresh folder, full file-set/SHA-256 readback, a saved restore report, and standalone re-verification.
- A Restore dashboard page with backup-specific client instructions and the latest authenticated recovery result per client.

There are no sample computers or simulated backups in the app. **Encryption, retention, and automatic scheduling are not implemented yet.** Scheduling remains explicitly labelled as a preview. ZIP backups are currently unencrypted. Google upload code is covered by simulated HTTP tests and a successful live console-mode test on November 5, 2026: the paired client created a ZIP from two synthetic files, uploaded directly to Google Drive, and confirmed remote size/checksum. The original client folder configuration was restored afterward. Windows Service and cross-PC rollout validation are still pending.

The same cloud backup also passed a **live download and restore drill on November 5, 2026** using the published v0.5 client: both synthetic files (239 payload bytes) recovered into a fresh folder, every SHA-256 matched the originals, standalone re-verification passed, and the actual restarted client reported completion to the paired host over TLS. Source files, the original archive and the backup configuration were unchanged. [Local validation evidence](artifacts/live-restore-test-1793862866092/result.json).

![Live host dashboard](docs/dashboard-preview.png)

## First run

On the host, open `AutoBack.slnx` in Visual Studio and run the **AutoBack** project, or:

```powershell
dotnet run --project AutoBack.csproj -c Release
```

Close any older demo window before rebuilding/running that same executable. Click **Pair computer**, enter the host's reachable computer name or IPv4 address, and save an invitation. For an initial test on this PC, use `127.0.0.1`.

Publish the client agent:

```powershell
dotnet publish src/AutoBack.Agent/AutoBack.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/agent-win-x64
```

Copy the **entire** published folder to the client, together with its invitation, and run:

```powershell
.\AutoBack.Agent.exe pair --invite .\client.autoback-invite
.\AutoBack.Agent.exe configure
.\AutoBack.Agent.exe drive-connect
.\AutoBack.Agent.exe run
```

`configure` opens source and destination folder pickers on the client. Keep them separate: neither folder may contain the other. The console stays open for this initial test. Once its configuration appears in the host, click **Backup Now** or select clients and click **Backup selected**. The published agent does not require the .NET SDK on the client.

See **[connection setup](docs/CONNECTION-SETUP.md)** for LAN/firewall setup, automatic Windows Service startup, re-pairing, and troubleshooting.
See **[local backup setup](docs/LOCAL-BACKUP.md)** for folders/permissions and **[Google Drive setup](docs/GOOGLE-DRIVE-SETUP.md)** for Google Cloud project creation, Desktop OAuth JSON, per-client sign-in, service mode, and the first live upload test. Update **both host and agent** to version 0.5 (protocol 3); existing saved pairings are reused.

For recovery, open **Restore** on the host for the selected backup's instructions. Stop the client agent, then run the appropriate command in its published folder:

```powershell
.\AutoBack.Agent.exe restore
.\AutoBack.Agent.exe restore --drive-file 'GOOGLE_FILE_ID'
.\AutoBack.Agent.exe verify-restore
.\AutoBack.Agent.exe run
```

The pickers select an archive/parent folder or an existing recovery folder for verification. Recovered source content is under `<new destination>\files`, with manifest and report alongside it. Restart the agent to send its saved result to the host. See **[restore guide](docs/RESTORE.md)** for explicit paths, failure handling, service scope, and verification limits.

## Verification

Requires Windows and the .NET 10 SDK. These are executable integration checks, run with `dotnet run` rather than `dotnet test`:

```powershell
dotnet build AutoBack.slnx -c Release
dotnet run --project tests/AutoBack.Connection.Tests/AutoBack.Connection.Tests.csproj -c Release
dotnet run --project tests/AutoBack.Backup.Tests/AutoBack.Backup.Tests.csproj -c Release
dotnet run --project tests/AutoBack.Drive.Tests/AutoBack.Drive.Tests.csproj -c Release
dotnet run --project tests/AutoBack.Restore.Tests/AutoBack.Restore.Tests.csproj -c Release
dotnet run --project tests/AutoBack.Desktop.Tests/AutoBack.Desktop.Tests.csproj -c Release
```

The connection suite uses real TLS sockets and an actual agent subprocess. It checks invitation replay/expiry/cancellation, concurrent pairing, rejected credentials and pins, timeouts, host restart, revocation, protected storage, malformed frames, and reconnect. The backup suite checks real archive contents/checksums, version preservation, locked/changed files, cancellation, cleanup, job replay, interrupted recovery, and a host-triggered backup using the actual agent executable. The desktop suite exercises the real Backup action and job results in WPF, including the 20-second offline transition, and refreshes screenshots. All use isolated temporary data and loopback ports.

The Drive suite simulates Google's HTTP API while using real local ZIPs and real authenticated host connections. It tests PKCE/state, protected OAuth storage, invalid refresh grants, 401 refresh, resume/restart, expired sessions, quota/rate-limit errors, checksum mismatch, endpoint validation, file-ID deduplication, and separate local/cloud results. It does not contact or modify a live Google account.

The restore suite exercises real archives, file/directory recovery, independent readback, modified/missing/extra files, no-overwrite and publication races, path traversal/collisions, link entries, format/size limits, cancellation cleanup, authenticated downloads and failure scenarios using simulated Google HTTP, actual CLI commands, protected journals, interrupted recovery, and TLS reporting. The desktop suite also validates recovery navigation and instructions with a real local restore.

Machine-wide DPAPI roundtrips and Windows Service installation need an elevated account. In a normal terminal the suite verifies that machine mode is rejected **before** altering directory permissions. The scripts can be syntax-checked without elevation; installation must be exercised separately on a client with administrator access.

## Next milestones

1. **Host–agent connection (implemented):** pairing, heartbeats, real inventory, reconnect, and revocation.
2. **Local backup (implemented):** client-selected folders, versioned ZIPs/manifests, persisted jobs, acknowledgements, cancellation, verification, and actual progress/errors.
3. **Direct Google Drive upload (implemented; live console test verified):** client authorization, protected tokens, resumable upload, retries, remote result verification, and separate local/cloud status. One upload per client; office-wide upload slots remain rollout work.
4. **Restore verification (implemented; live console drill verified):** local/Drive recovery to a fresh folder, manifest and SHA-256 verification, standalone recheck, protected history, and authenticated dashboard results.
5. **Automation and deployment:** persisted schedules, missed-run policies, encryption and recoverable keys, retention, hardened installers, service recovery, and testing on multiple physical PCs.

The backup engine covers ordinary local files/folders. It fails on inaccessible/locked files, detected source changes, and symbolic links/junctions. It is not a filesystem snapshot; live databases and full Windows images need a separate design.

## Source layout

- `MainWindow.xaml`, `DashboardViewModel.cs`: WPF host dashboard and live inventory.
- `AddComputerWindow.xaml`: invitation export and client instructions.
- `src/AutoBack.Connection`: framed TLS protocol, pairing registry, host identity, DPAPI storage, and agent transport.
- `src/AutoBack.Agent`: console pairing/status/restore commands and Windows Service worker.
- `src/AutoBack.Backup`: folder validation, ZIP creation, checksum verification, restore/readback, and durable client journals.
- `src/AutoBack.Drive`: Desktop OAuth/PKCE, token refresh, resumable Drive API uploads, verified downloads, and protected cloud job checkpoints.
- `scripts`: optional service registration/removal and host firewall setup.
- `tests`: connection, local backup, Drive, restore, and desktop integration checks.

Host identity and devices are saved under `%LOCALAPPDATA%\AutoBack\Host`, protected for the current Windows account. Console agent credentials use `%LOCALAPPDATA%\AutoBack\Agent`; service credentials use `%PROGRAMDATA%\AutoBack\Agent` with machine DPAPI and a restricted ACL. Do not commit invitation files or credential stores.

Implementation references: [Microsoft Windows Service worker guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service), [SslStream/TLS guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices), and [Windows DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope).
