# One current PC folder in Google Drive — v0.9.0

Google Drive mode now keeps **one current backup folder per client installation**. It uploads individual files instead of a ZIP:

```text
AutoBack Backups/
  KHIER/
    Desktop/
      test.xlsx
      Projects/
    Documents/
    _AutoBack.json
```

The PC folder uses the paired client name only. AutoBack reuses its Drive ID; it does not create a dated folder or ZIP for each backup. Source folders with identical names receive stable labels such as `Documents` and `Documents (2)` so their contents never collide. Private app properties and a protected installation ID establish ownership; a different PC with the same display name cannot adopt this backup.

## Backup behavior

- First backup: upload every selected file and create empty directories.
- Later backups: compare SHA-256 content hashes, verify existing cloud copies, upload new/changed files, and update the same Drive file IDs. An edited `test.xlsx` is sent again; unchanged files are not sent again. Each changed file is transferred in full, rather than sending changed spreadsheet cells or binary blocks.
- Files removed from the PC, or from the selected source list, stay in the cloud backup. A move/rename therefore adds the new path and retains the old one.
- A small `_AutoBack.json` recovery index is updated at completion. Keep this file and the managed folder metadata; restore and verification depend on them.
- If nothing changed, only the recovery index is uploaded. No source file payloads are uploaded again.
- Cloud results show counts of uploaded, unchanged and retained files. Local preparation and cloud completion remain separate statuses.

Local only continues to create permanent ZIP/ABK versions at the chosen local destination. Existing legacy plans keep their prior archive-upload policy until changed to Google Drive mode. Old ZIPs are not automatically deleted or unpacked into the new backup; the first folder backup uses the currently selected sources.

Google mode still builds a verified **temporary local snapshot** so uploads use a consistent source and can resume without rereading files that may change. Changed file staging also needs free local space. These temporary files are not uploaded as a backup ZIP. The exact local snapshot is removed only after cloud verification and host acknowledgment; failed/unconfirmed snapshots are preserved. Upload sessions and the local incremental index remain in the protected client store.

## Deleted destination recovery

AutoBack checks its cached root/PC folder IDs before upload. If a folder is missing or trashed, it searches for the same installation's active managed destination before allocating a new ID. The replacement PC folder still has the same plain PC name. Lost creation responses reuse saved IDs and lost mappings rediscover the owned folder, preventing a new folder every run. Existing legacy PC folders are renamed without changing their ownership or contents.

Deleting the destination removes the available cloud baseline. Its replacement is rebuilt from files currently available in the selected sources. **Files that existed only in the deleted Drive folder cannot be recreated from the PC**; recover the original folder from Drive Trash before it expires if those files are needed. Restore an accidentally deleted retained-only child before retrying: AutoBack reports missing retained files instead of silently dropping them. Access-denied or unexpected-ownership errors stop the backup; they do not trigger adoption of an unrelated folder.

## Interrupted updates and restore

Individual file uploads are resumable. A retry checks Google-confirmed offsets and checksums, keeps the file ID and skips files already verified. Expired sessions restart with the saved file identity. Newer folder backups wait behind unfinished older updates so a late retry cannot overwrite newer content.

This is one current backup, **not dated restore points**. During an update the live folder can contain a mix of old and new files. AutoBack marks it `updating` and refuses to publish a completed restore until every file and the recovery index verify. If an update fails, fix the reported issue and retry it using **Retry pending Google uploads** in Configure (or `drive-retry` with the worker stopped and the same scope/data directory). A later job remains queued until the earlier update completes. Google Drive may independently retain binary-file revisions; AutoBack does not expose those as backup dates.

Older folder-job entries appear as **Updated by a newer backup** and stop offering their links as historical restore sources. The latest completed entry opens the current PC folder. Cloud version retention never trashes the live folder, its recovery index or its retained files.

For a restore drill, stop the agent, use the same configuration scope, and choose a fresh destination:

```powershell
.\AutoBack.Agent.exe restore --drive-file 'https://drive.google.com/drive/folders/FOLDER_ID' --destination 'D:\Recovery\Folder-Test'
.\AutoBack.Agent.exe verify-restore --destination 'D:\Recovery\Folder-Test'
```

Installed service commands require elevation and `--machine`. Both legacy ZIP/file links and new folder links remain supported. Folder recovery downloads and verifies every blob, decrypts protected content when needed, assembles local restore staging, and publishes the recovered directory only after SHA-256 readback. The local restore staging ZIP is removed by the restore command. Original source files are never overwritten.

When encryption is enabled, each changed cloud file uses the authenticated AES-256-GCM envelope and an `.abk` suffix. Recovery on another client requires the password-protected recovery export. File/folder names, lengths, hashes and source mappings in the recovery index remain visible to the connected Drive account; this option encrypts file contents, not names or metadata. Disabling encryption retains keys needed to recover older retained files.

## Upgrade and small test

1. Close the host and stop the client service/console worker.
2. Install **both v0.9.0 host and matching agent** (modern x64, or Framework legacy x86). Protocol 5 carries folder links and superseded-history status. Existing paired credentials, certificates and Google authorization are reused; pairing again is unnecessary. Regenerate unused older invitation files.
3. Keep Google Drive selected, then restart host and client. Backups that failed on v0.8 because the Drive folder was deleted, before a cloud file ID was allocated, are migrated and retried from their saved snapshots. Other failed transfers require explicit retry.
4. Use a small source containing two files and an empty subfolder. Run Backup Now; verify the PC name and source folder structure in Drive.
5. Edit one file, add one, delete the other. Back up again. Confirm only the edited/new file payloads upload and the deleted file stays in Drive.
6. Restore to a fresh folder; verify all three expected contents and the empty directory.
7. Test folder deletion only with disposable synthetic data; the next backup should create one replacement folder and then keep reusing it.

Automated tests use simulated Google HTTP, real local files, real TLS host reporting and actual x86 Framework execution. They cover changed bytes with unchanged size/timestamp, zero-byte files, deleted-file retention, folder/root recovery, lost mappings, name collisions between PCs, encrypted upload/resume/restore, ordered retries, host history and cleanup. A live Google folder-format drill and an actual Windows 7/multiple-PC pilot are still deployment checks.

API behavior: [Drive resumable create/update uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads), [files.update](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/update), [trash inherited from a parent](https://developers.google.com/workspace/drive/api/guides/delete).
