# Automation, encryption, retention and deployment — v0.8

New setup defaults to Google Drive with automatic temporary storage/cleanup; Local only is the explicit permanent-local option. Read [destination modes](DESTINATION-MODES.md) before setting retention. Google mode ignores permanent local-retention counts; its confirmed cache is always cleaned. Local only pauses automatic uploads/cloud retention. Existing plans and files are preserved until deliberately reconfigured. Update both endpoints to protocol 4/v0.8; saved pairings remain usable.

## Daily schedules and local backup retries

In the host, open **Settings → Daily schedule & retries**. Enable daily backups, enter an `HH:mm` time, choose 0–5 local retries and a delay of 1–1440 minutes, then **Save automation**. Settings and processed daily occurrences persist in the protected host registry. Defaults stay disabled until saved.

The displayed **host time zone** controls the schedule for all configured paired clients; clients may be in different time zones. A newly enabled/changed schedule begins with its next due occurrence. While the host is closed, no new commands are issued. On reopening, it queues one catch-up for the most recent missed occurrence, rather than replaying every missed day. Offline configured clients keep a durable queued command until they reconnect. A busy client receives no overlapping backup; an unprocessed latest daily slot can be queued when it becomes idle. During a spring-forward gap, the first valid minute is used; a repeated fall-back time runs once per local date.

Each retry is a **new backup/job ID** linked to the failed attempt. Failed attempts remain in history. Only failures reported as retryable local IO failures qualify; cancellation, authorization/key/configuration failures, revoked pairing and changed folder plans do not retry. Retries stop at the saved limit. A retry chain can be superseded by a later manual job/daily occurrence; only the latest failed job is eligible. Jobs predating the policy's activation are not automatically retried. Updating retry limits alone preserves the current daily slot. Turning off daily backups does not cancel already queued work: use **Cancel local jobs** for those.

Google uploads retain their own saved resumable sessions and network/rate-limit backoff (up to about five minutes between attempts). Authorization errors wait for sign-in; permanent errors wait for `drive-retry`. Cloud retries reuse the successful local archive and never rerun a local backup. Host reconnect has separate bounded-delay backoff. Local retries require the host to remain running.

## Encryption and recoverable keys

Stop the console agent/service first. Use the same account and `--machine` / `--data-dir` as the agent. For an installed service, use an elevated PowerShell and append `--machine` to each command. Enable encryption by creating a **new** recovery export on a separate secure device/folder:

```powershell
.\AutoBack.Agent.exe encryption-enable --recovery-file 'E:\RecoveryKeys\OfficePC-01.abkey'
.\AutoBack.Agent.exe protection-status
.\AutoBack.Agent.exe run
```

The client prompts for a **hidden passphrase of at least 12 characters**, with confirmation. An export is decrypted and checked before encryption is activated. Existing export files are never overwritten. Keep another copy of the `.abkey` export and keep its passphrase separately. Test import/restore on a different client store before relying on it for disaster recovery. Loss of both the client key store and a usable export makes those encrypted backups unrecoverable. Google sign-in alone cannot decrypt an encrypted backup.

Each client gets a random 256-bit master key saved in DPAPI-protected storage; key material is never sent to the host. New backups use `.abk`, a versioned authenticated envelope around the full ZIP. ZIP output is encrypted as it is written; a plaintext temporary ZIP is not created. Both the local archive and the directly uploaded cloud object are encrypted. Existing `.zip` backups remain unchanged. Temporary restore content and the recovered folder contain plaintext files by design: choose an appropriate protected destination/drive.

The envelope uses AES-256-GCM with independent 1 MiB authenticated chunks, 128-bit tags and a random 256-bit salt per archive. HKDF-SHA256 derives an archive-specific key; a 96-bit nonce encodes each unique chunk number. An authenticated footer commits the plaintext length; file length, order, tags and header must all agree. Seekable decryption keeps memory bounded during ZIP verification/restore. Metadata inside the ZIP is encrypted too; outer file size and a key ID remain visible. This is an application-specific format using .NET cryptography, covered by tamper/recovery tests; no independent cryptographic audit is claimed.

Recovery bundles use a separate random salt/nonce, PBKDF2-HMAC-SHA256 (600,000 iterations) and AES-256-GCM. Never put a passphrase on a command line or in a script. For automation that already securely supplies input, the explicit `--password-stdin` flag accepts one redirected line; the passphrase is not logged or stored by AutoBack.

To restore on another Windows PC/account:

```powershell
.\AutoBack.Agent.exe recovery-import --recovery-file 'E:\RecoveryKeys\OfficePC-01.abkey'
.\AutoBack.Agent.exe restore --archive 'D:\Backups\backup.abk'
# Or, after connecting the original backup Google account:
.\AutoBack.Agent.exe restore --drive-file 'DRIVE_FILE_ID'
```

Import merges recovery keys and leaves pairing, active encryption settings and existing keys intact. Backups from other clients can then be recovered without impersonating their identities. Password/tag/key failures never produce a verified destination. Use [Restore](RESTORE.md) for file verification and reports.

```powershell
# Re-export every stored key with a new passphrase; choose a new path:
.\AutoBack.Agent.exe recovery-export --recovery-file 'E:\RecoveryKeys\OfficePC-01-copy.abkey'
# Future backups return to plain ZIP; existing encrypted archives stay encrypted and keys are retained:
.\AutoBack.Agent.exe encryption-disable
```

Disabling encryption does not erase keys or revoke prior recovery exports. Automatic key rotation, passphrase resets without the old keys and an external key-management service are not included.

## Retention — opt in per client

Retention starts **disabled (0)** for both destinations. Configure the number of successful versions while the agent is stopped, then restart it. For example:

```powershell
.\AutoBack.Agent.exe retention-configure --keep-local 14 --keep-cloud 30
.\AutoBack.Agent.exe protection-status
.\AutoBack.Agent.exe run
```

For service mode append `--machine`. `1–10000` enables that destination; `0` disables automatic removal there. Do not enable retention until a backup/restore drill has succeeded and the recovery keys are copied off the client.

On authorized idle runs, the worker checks retention after journal changes and otherwise at most hourly. It pauses backup/upload starts during a retention pass. File validation and cloud errors surface under the client's **Settings → Client encryption & retention** status. Normal host/network loss does not mark a backup successful. An explicit host rejection cancels backup/upload activity; a retention operation already in progress can finish before that rejection is received.

- **Local:** keep the newest N successful, unpruned archives for the currently configured folder plan. Verify survivor contents and identities before deleting older versions. Exact managed paths, ZIP/manifest identity, checksums and absence of junctions/links are checked. Delete only the selected archive file, never recursively delete its directory. Empty job directories/history may remain. Failed/partial jobs, old folder plans and unrelated files are not swept. Pending/failed uploads are protected, even after sign-out; when Google is configured, a completed upload record is required before a local version is eligible. Previously confirmed cloud files expired by cloud retention need not pin local versions forever. Legacy ZIPs without an outer archive hash still require full manifest/file verification.
- **Cloud:** keep the newest N completed uploads in this client's journal for its currently connected Google account and known client folder. Recheck the account, parent folder, agent/job IDs, size and checksums for survivors and candidates. Move eligible old files to **Google Drive Trash** and confirm the result; do not call permanent delete. Google normally permanently deletes trash after **30 days**. Restore desired files in Google Drive before that period expires. AutoBack's journal marks them expired; manually untrashing an expired file does not reset its journal status. Remote files absent from the client's journal, other clients, other accounts and unexpected metadata are preserved.
- Failed validation pauses pruning and preserves remaining candidates. Policies are version counts, not age/disk-space limits. Histories record expiration separately from the original successful backup/upload result; the Restore page disables sources already reported as expired. Report changes arrive on subsequent authenticated heartbeats.
- If retention is interrupted between a delete/trash operation and journal persistence, the next pass rechecks state and completes its annotation; it does not recreate or re-upload expired backups. Turning retention off does not restore files already expired.

## Windows installers

Built MSI files are in `artifacts\installers`:

- `AutoBack-Host-0.8.3-win-x64.msi`
- `AutoBack-Agent-0.8.3-win-x64.msi`
- `SHA256SUMS.txt` — SHA-256 checksums for transfer verification.

Both are machine-wide x64 installers and require administrator access. Applications are self-contained; client PCs do not need the .NET SDK. Code is installed under **Program Files**. Start Menu entries launch the host, client setup and the rollout guide. MSI repair/uninstall handles application files and service registration; credential stores, recovery exports, source files, backup archives and cloud data are retained. Uninstall does not revoke pairing: revoke it on the host when retiring a client. The installers are unsigned pilot artifacts unless separately signed by your release process; organizational signing/distribution policy must be applied before broad rollout.

**Agent setup:** the MSI installs a stopped **Manual** `AutoBackAgent` service under `NT AUTHORITY\LocalService` with two 60-second crash restarts, then no further recovery action until a manual start (failure count resets after a day). Open **Configure AutoBack Agent** from Start Menu and approve its administrator prompt. Select a fresh host invitation, source/destination folders and optionally the Desktop OAuth JSON for Google sign-in. Pairing and settings use the machine data store. The invitation must be generated just before setup (five-minute expiry).

**Windows 7 SP1 / 32-bit:** use the separate [legacy client MSI and pilot guide](WINDOWS7-CLIENT.md), not the ordinary x64 MSI. It requires Framework 4.8 and uses a native setup launcher instead of the PowerShell helper. The current modern host is reused; Windows 7 service/TLS/browser checks remain pending. Build with `Build-Installers.ps1 -TargetPackage AgentLegacy` and inspect all three packages using `Test-InstallerPackages.ps1 -IncludeLegacy`.

**Removed/revoked client:** v0.8.1 adds **Pair AutoBack Agent again** and a pairing recovery option inside Configure. A fresh host invitation replaces the revoked identity while keeping folders, Google grants, recovery keys and backup files. Old pending jobs/uploads are archived instead of replayed under the new identity. Follow [pairing recovery](PAIRING-RECOVERY.md); a clean reinstall or store deletion is unnecessary.

In v0.8.2 use **Check access** / **Grant required access** in the folder window. Service-mode Save requires a successful check as LocalService, and installed setup checks again before offering `START`. Permission changes are limited to selected sources/destination and required parent metadata access, with a protected audit. A failed check leaves the service stopped; resolve its listed issues first. `START` enables delayed automatic service startup and starts the worker. Cancel preserves saved settings and restarts a previously running service. See [folder access checks](FOLDER-ACCESS.md), then perform the pilot backup/restore checks before enabling schedules or retention.

**Host setup:** launch **AutoBack Host** under the intended Windows operator account; its DPAPI identity/registry is per Windows account. Keep the same account during upgrades. The GUI must stay open for scheduling; configure Windows sign-in/startup for that account if appropriate. LAN clients need the host's TCP 47831 reachable on the Private/Domain network; the optional installed `scripts\Enable-HostFirewall.ps1` helper must be run deliberately by an administrator. Public-network access is not opened automatically.

**Upgrade:** close the host/stop the agent, retain protected data and recovery exports, and install a newer MSI with the same product upgrade code. Downgrades are blocked. After an agent upgrade, rerun its setup helper to confirm settings/permissions and enable delayed automatic startup. For the first MSI installation over a legacy script-installed service, unregister that service with the existing uninstall script first; retain its protected data. The MSI refuses to replace an unrelated existing service registration. Console/current-user and machine/service stores are separate: installing the service does not migrate console credentials/keys. Pair, sign in and create/import recovery keys for the intended scope explicitly.

## Build and validate packages

On the development PC with Windows and .NET 10 SDK:

```powershell
.\scripts\Build-Installers.ps1
.\scripts\Test-InstallerPackages.ps1
```

The build pins WiX Toolset 4.0.6 and its matching service extension, publishes both self-contained applications, stages an allowlist of runtime files/docs/setup helpers, writes an installed file inventory and builds native Windows Installer packages. User OAuth JSON, DPAPI `.dat` stores, invitations, `.abkey` files, test archives and recovery folders are excluded. Tool binaries/build stages remain under ignored `artifacts`; the extension cache is managed by WiX in the current user's profile. Building/inspecting packages does not install the product or register a service.

Run the eight executable test suites in README, then use [the rollout checklist](ROLLOUT-CHECKLIST.md) on at least two physical PCs. The local automated pilot uses three isolated client processes/stores and real TLS sockets on **one** physical PC; it is not a substitute for LAN, ACL, reboot, installer/service and long-duration testing on target machines.

## Validation evidence

Twelve executable test suites cover the release, including folder access/ACL preparation, pairing recovery, x86 Framework compatibility, default Google Drive/local-only isolation and verified temporary cleanup. The three-process pilot completed six scheduled backups, recovered each client's files, and exercised offline queuing/reconnect; evidence is in `artifacts/automation-validation/multi-client-result.json`. Package inspection is in `artifacts/installers/package-validation.json`. Per-client source selection is described in [Choose folders](SELECT-FOLDERS.md). Elevated service-identity grant/check and actual Windows 7 installation remain target-PC checks.

A live Google account drill on November 5, 2026 used a new isolated validation client/store and synthetic files only. The published client created and uploaded two authenticated encrypted versions, then kept one local/cloud version using explicit test retention settings. Only that validation client's old local file was pruned and old remote file moved to Drive Trash. A separate client store imported the recovery export and downloaded/decrypted/restored the newest version (two files, 110 bytes), passed independent source-hash comparisons and `verify-restore`. The original client settings and prior backups were preserved. Evidence: `artifacts/live-automation-test-1793864952400/result.json`. Validation-only keys/account remain in dedicated DPAPI stores under `%LOCALAPPDATA%\AutoBack\Validation`; no key/passphrase is written to evidence files. This live drill does not claim physical-PC or service-install validation.

References: [.NET AES-GCM](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt), [.NET HKDF](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hkdf), [.NET PBKDF2](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2), [Drive Trash behavior](https://developers.google.com/workspace/drive/api/guides/delete), [WiX service recovery configuration](https://docs.firegiant.com/wix/schema/util/serviceconfig/).
