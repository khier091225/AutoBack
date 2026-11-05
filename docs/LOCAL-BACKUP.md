# Actual local backup

The host requests a job; the authenticated client creates and verifies a new ZIP in its own configured local destination. Archives do not pass through the host. Direct Google Drive upload is available after [client Google setup](GOOGLE-DRIVE-SETUP.md); the local result remains independent of the cloud result.

## 1. Update the host and agent

Use the version 0.7 host **and** agent (protocol 3). Close the previous host window and stop the previous agent before replacing its executable folder. Saved identities and pairing credentials are retained. Version 0.7 reports the complete per-client source list and supports multi-folder archive format 2. See [folder selection](SELECT-FOLDERS.md) and [Automation and rollout](AUTOMATION-ROLLOUT.md).

Publish from the repository if needed:

```powershell
dotnet publish AutoBack.csproj -c Release -r win-x64 --self-contained true -o artifacts/host-win-x64
dotnet publish src/AutoBack.Agent/AutoBack.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/agent-win-x64
```

Copy the entire agent output directory to the client. For an installed Windows Service, stop `AutoBackAgent`, update its Program Files directory from an elevated terminal, and restart it. Re-pairing is unnecessary when the existing data store and host identity are retained.

## 2. Choose source and destination on the client

For console mode, open PowerShell in the agent directory:

```powershell
.\AutoBack.Agent.exe configure
```

A folder manager opens with existing selections prefilled. Use Add folder / Remove selected, choose a separate destination and Save folders. Cancel preserves the previous configuration. Repeat `--source` for multiple folders with the CLI, or use a single source as before:

```powershell
.\AutoBack.Agent.exe configure --source "D:\OfficeFiles" --destination "E:\OfficeBackups"
.\AutoBack.Agent.exe run
```

Use the same Windows account and `--data-dir` as pairing. Each client may select 1–20 separate source folders, with all their subfolders included. Each source needs appropriate read permissions. The chosen destination may be on another local drive. A backup on the same physical disk does not protect against failure of that disk.

The destination cannot be inside the source, equal to it, or contain it. Network shares/mapped network drives and paths through junctions/symbolic links are unsupported. Backups fail when a junction or symbolic link is encountered inside the source, rather than silently excluding it.

The configuration is saved separately from credentials and can be updated while the agent is running. The next poll advertises it to the host. A queued job is tied to the exact configuration that was advertised when it was queued: if folders change before the job starts, that job fails and a new job must be requested. A running job keeps its captured configuration.

## Windows Service mode

From an elevated PowerShell on the client:

```powershell
& "$env:ProgramFiles\AutoBack Agent\AutoBack.Agent.exe" configure --machine
```

Machine mode has its own configuration, separate from console mode. The service runs as **NT AUTHORITY\LocalService**. Using the folder's Windows Security settings, grant that account:

- Read/list access to the chosen source folder and files.
- Modify access to the chosen destination folder.

Selecting folders as an administrator does not grant access to the service account. AutoBack does not change source/destination permissions automatically. It reports access errors as failed backups. If appropriate permissions cannot be granted, test in console mode under an account that already has access.

## 3. Trigger from the host

After the agent's next poll, the client becomes **Ready**. Click **Backup Now** beside it, or select configured clients and click **Backup selected**. One pending backup per client is allowed; duplicate clicks do not create duplicate active jobs.

Offline clients with a saved configuration can be queued; they start after reconnecting. The host persists the command before delivering it. A running client polls every second for commands and sends actual progress; idle agents poll every five seconds. Local backup work runs separately from network polling.

States:

| State | Meaning |
| --- | --- |
| Queued / Waiting for PC | Persisted by the host, awaiting the client. |
| Preparing | Client has durably accepted the job and is scanning folders. |
| Copying | Files are being added to the ZIP; byte/file counts reflect actual work. |
| Verifying | ZIP is closed and flushed; saved contents are checked against SHA-256 hashes. |
| Local complete | Client has published the verified archive and the host has saved its completion report. |
| Failed | A real error occurred; Activity shows the reason. The previous successful backup is retained. |
| Cancelling / Cancel pending | Request saved; waiting for the client to confirm. Offline clients receive it after reconnecting. |
| Cancelled | Client confirmed cancellation before publishing a complete archive. |
| Connection lost | No recent heartbeat; the last progress/result is retained. Loss of connectivity never counts as completion. |

The **Activity** page lists the latest 100 saved jobs, their result/error, and the archive path on the client. **Last local backup** reflects the most recent successful job, even when a later job fails or is cancelled. Job history remains after host restart.

## 4. Find the archive

Each full backup has a separate job directory under the configured destination:

```text
E:\OfficeBackups\AutoBack\{agent-id}\{job-id}\backup-{UTC-timestamp}.zip
```

With encryption enabled, the extension is `.abk`: an authenticated encrypted envelope containing the same ZIP structure, written without a plaintext ZIP staging file. The ZIP contains:

- `files/`: source files and directories, including empty directories and zero-byte files.
- `manifest.json`: format version, job and agent IDs, source path, creation time, directory list, and each file's length, original modification time, and SHA-256 hash.

ZIP paths are relative to the selected source. Payload files live under `files/`, so a source file named `manifest.json` cannot collide with backup metadata. New versions never overwrite an existing one. Optional client retention can expire eligible older archives after verifying the copies to keep; it is disabled by default.

With multiple sources, format-2 manifests map each original source to a unique numbered payload folder such as `01-Documents/` and `02-Documents/`. Identical folder/file names from different locations remain separate. Single-source archives keep format 1 and the existing layout. See [folder selection and restore layout](SELECT-FOLDERS.md).

While a job runs, its output has a `.partial` suffix. Only a closed, flushed, verified archive is renamed to `.zip`. Failed/cancelled jobs attempt to remove only their own partial file. A force-killed agent may leave a partial file; the journal marks interrupted work **Failed** on restart and never treats that file as a successful backup. Start a new job to retry; an interrupted job is not silently executed again.

## Reliability and current limits

- Stable job IDs and a protected client journal prevent a repeated command from making another backup. Lost final reports are resent until acknowledged.
- If the host becomes unavailable while copying, the client can finish locally and saves the result for the next successful connection.
- Cancellation is best effort until publication. If the archive finished before the cancel command arrived, the result can still be Completed.
- No files are silently skipped: locked/unreadable files or detected source changes fail the job. Files are opened read-only without permitting concurrent writers. A second scan checks additions/removals and size/time changes. This is **not** a transactional filesystem/VSS snapshot; close applications editing the source before a backup.
- This milestone supports ordinary files/folders, not live databases, complete Windows images, NTFS permissions/alternate data streams, or snapshot recovery.
- Plain ZIP archives remain **unencrypted**. Opt into authenticated `.abk` encryption and recovery exports using [Automation and rollout](AUTOMATION-ROLLOUT.md); that guide also covers schedules/retries, opt-in retention and installers. Use the [restore guide](RESTORE.md) to recover local/Drive versions into a new folder and verify every file. Archive upload verification alone does not replace a restore drill.
- Windows Service folder access and multi-PC LAN behavior still need validation in the target office environment. Automated tests exercise real filesystem archives and real host/agent processes on loopback.

Implementation references: [Microsoft ZipArchive API](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive) and [FileShare semantics](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare).
