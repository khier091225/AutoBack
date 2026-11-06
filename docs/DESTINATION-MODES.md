# Google Drive by default, or Local only — v0.8

New setup has two choices per client:

| Mode | Setup | Result |
| --- | --- | --- |
| **Google Drive (default)** | Select source folders and connect Google. No permanent local destination to pick. | Client builds/verifies a temporary archive, uploads directly, then removes that archive after cloud confirmation and host acknowledgment. |
| **Local only** | Select source folders and a separate local destination. Google sign-in is unnecessary. | Permanent ZIP/encrypted versions remain at that destination; automatic Google uploads are paused. |

## Choose the mode

Stop the client agent first. Open **Choose AutoBack backup folders** from its installed Start Menu, or run `AutoBack.Agent.exe configure` from its published folder. Select the mode, add the folders and **Save folders**. The destination field appears only for Local only. Cancel preserves the previous setup.

Installed setup guides Google-mode clients through account connection before starting the service. In portable mode, use `drive-connect` once after pairing, then `run`. The host's Backup Now requires a connected Google account for Google mode. Local only needs no Google account even if credentials from a previous connection remain saved. An existing pending upload is paused while Local only is selected; explicit cloud restore/sign-in commands remain available.

```powershell
# Default: Google Drive, automatic temporary storage, no --destination:
.\AutoBack.Agent.exe configure --source 'C:\Users\Maria\Documents' --source 'D:\Work Files'
.\AutoBack.Agent.exe drive-connect
.\AutoBack.Agent.exe run

# Explicit local destination; repeat --source for more folders:
.\AutoBack.Agent.exe configure --mode local --source 'D:\Work Files' --destination 'E:\Backups'
.\AutoBack.Agent.exe run
```

`--mode google-drive` is optional for cloud setup. Supplying `--destination` without `--mode` selects Local only, so older local configuration commands continue working without silently enabling cloud upload. Google Drive mode rejects an explicit `--destination`. Use the same Windows account, `--machine` and/or `--data-dir` as the paired agent. Installed-service commands require elevation and `--machine`.

## Temporary storage and cleanup

Temporary archives live under the agent's ACL-protected data directory in `cloud-cache/AutoBack/{agent-id}/{job-id}`. The app chooses this path; it does not appear as a permanent backup destination in the folder setup or host. Enough free local disk space is still required to create/retry an archive. The cache must remain outside the selected source trees, so a source that contains the entire agent data directory must be narrowed to the intended data folders.

Every temporary archive is verified locally before uploading. A Google-mode job captures its mode, managed cache root, archive SHA-256 and original Google account binding. Cleanup requires all of the following:

- The client and host have acknowledged the completed preparation and cloud upload.
- Drive upload verification succeeded. The remote file is checked again at cleanup for account/folder/job/agent identity, size and checksums, and must still be available and untrashed.
- The exact local path belongs to this job under the private cache; the file is not a link and its size/hash still match the verified upload.

Only that archive file and its empty job directory are removed. Sources, permanent local backups, unrelated files, other data directories and existing legacy backups are preserved. Cleanup restarts safely if interrupted between deletion and journal annotation. History shows **Temporary archive cleaned** separately from cloud completion; Drive restore remains available afterward. Preparing an archive alone does not count as a confirmed Google backup.

Network/auth/quota errors, missing remote files, changed checksums or failed uploads preserve the temporary archive for retry/recovery. A failed upload is never treated as completion. Pending uploads may consume local space until the issue is resolved; they are not removed by local-retention counts. Force-killed preparation can leave partial/unconfirmed files for inspection. They are not silently declared successful or swept as confirmed cache files.

## Local-only behavior

New Local only jobs capture that choice in their journal and report. They are excluded from upload discovery even when a Google account is connected or a later plan uses Google Drive. Older queued cloud work is paused until cloud-capable mode is selected again. Cloud retention/cleanup does not make automatic Google requests while Local only is active. Local version retention can still be enabled explicitly. For an installed service, grant LocalService Read/list access to each source and Modify access to the chosen local destination. Google-mode staging inherits the protected agent directory permissions.

## Existing installations and compatibility

Update **both host and client to v0.8 (protocol 4)**. Existing saved pairing credentials and host certificate are reused; invitations saved with an older protocol must be recreated if pairing a new client. Older clients are rejected by the updated host rather than accepting unsupported destination semantics. New mode-aware plans deliberately fail on older agents.

Existing plans are preserved as **Local + Google Drive (existing setup)** until deliberately resaved. Reopening the picker presents Google Drive as the default choice; saving applies the selected mode to future jobs. Existing local archives are not moved/deleted when switching modes, and legacy local archives are not newly discovered for upload simply because a new cloud plan was saved. Already queued cloud uploads retain their original account binding and can resume when uploads are enabled again.

The client migrates old `jobs.dat` records to `jobs-v2.dat` and keeps the old file unchanged. New local-only jobs are written only to the new journal so an older executable cannot rediscover those records as legacy cloud candidates. Do not run old binaries against the same data directory after upgrading. Archive format 1/2 and encrypted recovery exports remain compatible; mode selection changes the delivery/storage policy, not the recovered payload layout.

Google mode ignores permanent local-retention counts and always cleans confirmed temporary archives. Cloud version retention remains opt-in. Local only applies local retention and pauses cloud retention. Encryption settings continue to apply to either destination. See [folder selection](SELECT-FOLDERS.md), [Google setup](GOOGLE-DRIVE-SETUP.md), [restore](RESTORE.md) and [automation/rollout](AUTOMATION-ROLLOUT.md).

Validation: `AutoBack.Destinations.Tests` covers the actual two-mode form, automatic cache selection, connected-account local isolation, missing Google sign-in, local-only upload-report rejection, account-bound verified cleanup, failure/restart safety, old-journal migration and actual host/client mode changes. Google HTTP failures are simulated in the automated suite. Current local test evidence: `artifacts/destination-validation/result.json`.

A published-agent live drill also passed with the connected Google account: encrypted upload from two synthetic sources, automatic cache cleanup after confirmation, switching to Local only with no additional upload, and cloud restore/readback after the cache was removed. Original client settings were preserved. Evidence: `artifacts/live-destination-test-1793920813038/result.json`. This is a console-mode test on one physical PC; Windows service and multi-PC installation checks remain pilot work.
