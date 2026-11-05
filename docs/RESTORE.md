# Restore and verification (v0.7)

Restore a local AutoBack `.zip`/encrypted `.abk` or download one directly from Google Drive into a **new local folder**. Every recovered file is checked against the manifest, then read back from disk and checked again before the recovery folder is published. Original files and existing destination folders are preserved. Encrypted archives authenticate all chunks and the committed length before extraction. On a different client, first import the backup's password-protected recovery export; see [encryption and recovery keys](AUTOMATION-ROLLOUT.md).

## On the client

1. Stop the console agent with **Ctrl+C**, or stop the `AutoBackAgent` Windows service if installed. Restore and verification take the same data-directory lock as the running agent.
2. Open PowerShell in the published `artifacts\agent-win-x64` folder. Use the same Windows account and `--machine` / `--data-dir` options used by the agent. Machine-scope data needs an elevated terminal.
3. Choose a backup and a destination:

```powershell
# File picker for a local backup, then a parent-folder picker:
.\AutoBack.Agent.exe restore

# Explicit local archive; omit --destination to open the parent-folder picker:
.\AutoBack.Agent.exe restore --archive 'D:\Backups\backup.zip' --destination 'D:\Recovery\Recovered-20261105'

# Download directly using the Google account already connected on this client:
.\AutoBack.Agent.exe restore --drive-file 'GOOGLE_FILE_ID' --destination 'D:\Recovery\FromDrive-20261105'
# A https://drive.google.com/file/d/ID/view link is also accepted.
```

The explicit destination **must not exist**, even as an empty folder. Choose a local folder outside the original source tree. With the folder picker, select an existing parent: AutoBack creates a unique `AutoBack-Restore-...` child. Network destinations, junctions, symbolic links, Windows device names, and ambiguous/traversing archive paths are rejected.

4. Wait for `RESTORE VERIFIED` and exit code 0. The result looks like:

```text
Recovered-20261105/
  files/                  <- recovered source content, including empty directories
  manifest.json           <- original manifest from the backup
  restore-report.json     <- backup identity, archive SHA-256, counts, and verification time
```

Open the recovered documents with their usual applications to confirm application-level usability. File verification proves byte integrity against the manifest; it cannot prove that a database or an application was consistent when originally backed up. Restore regular files, empty directories and file modification times. NTFS ACLs, ownership, alternate streams, hard links and live-database snapshots are not captured by this backup format.

For a multi-folder backup, `files` contains separate numbered source folders (for example `01-Documents` and `02-Desktop`). The format-2 manifest and restore report include each original source path and its prefix; every source participates in validation and source-overlap protection. Use v0.7 restore tools for format 2. Existing format-1 single-folder backups remain supported. See [folder selection](SELECT-FOLDERS.md).

5. Recheck the recovery folder later (read-only):

```powershell
.\AutoBack.Agent.exe verify-restore --destination 'D:\Recovery\Recovered-20261105'
.\AutoBack.Agent.exe restore-status
.\AutoBack.Agent.exe run
```

`verify-restore` checks the complete payload file/directory set, lengths and SHA-256 hashes against the saved manifest. Missing, modified or extra payload files fail verification. It records the new check in the protected client journal; the original `restore-report.json` stays unchanged. Keep the manifest and report alongside `files`. Matching checksums are an integrity check; manifests and ZIPs are not cryptographically signed.

## Host dashboard

Open **Restore**, choose a completed backup, then **Restore instructions…**. The selectable instructions include its client archive path and Google Drive link when available. These paths refer to the client PC. Restore runs on the client, where the operator chooses the recovery location.

The client saves its latest 100 restore/verification operation records in DPAPI-protected storage. Restart its agent after restore: the next authenticated heartbeat sends the latest result to the host. The host shows **Verified** only for a completed full verification. Local backup, cloud upload and recovery results stay independent. The host does not show live progress while the agent is stopped; progress is printed in the restore terminal.

## Google Drive recovery

Connect the backup's Google account with `drive-connect` first; see [Google Drive setup](GOOGLE-DRIVE-SETUP.md). A downloaded backup must be accessible to that OAuth app/account, have AutoBack metadata, and match its remote length, MD5 and SHA-256. The ZIP's manifest must also match its Google Drive backup/agent identity. Recovery can use an archive from another client without replacing this client's pairing. Encrypted `.abk` objects use `application/octet-stream`; matching recovery keys are still required after Google authorization. The restore report's archive SHA-256 refers to the original stored/encrypted object.

The download uses a temporary sibling ZIP and is streamed to disk. Only a fully checked download is passed to the restore engine. The downloaded temporary ZIP is removed after the attempt; the remote backup is retained. A failed download can be retried from the beginning; download resumption is not implemented. An unconnected/unauthorized Google account fails clearly and does not produce a successful restore.

## Failures and limits

- Ctrl+C cancels; normal cancellation or failure removes the operation's temporary ZIP/staging directory. No recovery destination is published until checks pass. A destination that appears during restore is preserved and causes failure.
- A forced termination/power loss may leave `.autoback-restore-<operation>.partial` or `.autoback-download-...` siblings. They are incomplete work, not verified restores. The next agent/restore command marks an unfinished journal entry **Interrupted**. Retry with a fresh folder. Remove only confirmed leftover staging files after inspecting their paths; recovery never deletes unrelated folders.
- Default limits: 250,000 ZIP entries (including manifest/directories), 64 MiB manifest, 1 TiB recovered content and 1 TiB cloud archive. Every file is streamed within declared limits; available local disk space is checked. Long paths still depend on the Windows volume/tool configuration.
- Byte counts are uncompressed payload bytes. All manifest entries, including empty directories, must be present; ZIP extras, duplicate/case-colliding paths, links, unsupported format versions and invalid checksums are rejected.
- Credentials and local recovery keys remain DPAPI protected. [Automation and rollout](AUTOMATION-ROLLOUT.md) covers authenticated archive encryption, portable recovery exports, retention, schedules and installers. History marks expired local/cloud sources separately; restore instructions offer only available sources. Restore from Drive Trash requires restoring the file in Google Drive first (before Google's trash expiry).

API and format references: [Google Drive downloads](https://developers.google.com/workspace/drive/api/guides/manage-downloads), [Windows file naming](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file), [.NET archive best practices](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices).

## Completed live drill

On November 5, 2026, the published v0.5 agent downloaded the previously uploaded synthetic backup from the connected Google account. It recovered two files (239 payload bytes) into a new folder, matched all original SHA-256 hashes, passed the standalone `verify-restore` command and reported its saved result to the existing host through the actual restarted agent. Original source files, archive and backup configuration remained unchanged. The test worker was stopped afterward. Local evidence: `artifacts/live-restore-test-1793862866092/result.json`; recovered payload: `artifacts/live-restore-test-1793862866092/recovered/files`. Windows Service and cross-PC recovery drills remain rollout validation.
