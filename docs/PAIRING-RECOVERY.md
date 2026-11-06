# Removed a computer from the host? Pair again — v0.8.1

No clean reinstall or credential-store deletion is needed. Removing/revoking a computer on the host disables its old credential, but the client still has that saved pairing. In v0.8.0, Configure skipped the invitation picker whenever a credential existed. v0.8.1 adds an explicit recovery path to both modern and Windows 7 legacy clients.

## Recover an installed client

1. Upgrade the client to **v0.8.1** using its matching MSI. Keep its protected data directory and existing backup/recovery files. The host can stay on v0.8.x/protocol 4; this recovery needs no host protocol change.
2. Keep the host open. Click **Pair computer**, enter the host's reachable LAN address/name and save a fresh `.autoback-invite`. Transfer it to the client. It expires in five minutes and is single-use; create one per client.
3. On the client, open **Pair AutoBack Agent again** from Start Menu and approve administrator elevation. Alternatively, **Configure AutoBack Agent** now checks the saved host connection and offers **Pair again with a host invitation** and **Change backup folders / settings**.
4. Select the new invitation. Pairing recovery keeps the configured folders/mode, Google authorization, encryption/recovery keys, retention policy and backup files. It keeps the existing client alias and enrolls a fresh identity.
5. Confirm source permissions are still ready and type `START` when the installed setup asks. The service starts and the new pairing should become Online in the host.
6. Request a new **Backup Now**, check the selected destination's confirmed completion, and test restore. Existing Google sign-in is reused when still valid; unrelated Google authorization errors still require sign-in.

No backup folder picker or repeat Google consent is required just to re-pair a configured client. If the client has never been configured, use Configure first. An unreachable host does not automatically replace the identity; check host/network/address and deliberately select Pair again only when needed. Cancelling before successful pairing keeps the original identity/settings and restores a previously running service.

## What happens to old history and pending work?

Host removal is a revocation, not an undo. A fresh invitation creates a **new identity**; it does not merge the old host job history into that identity.

The old local jobs, cloud checkpoints and account-to-Drive-folder mapping are saved in a DPAPI-protected `pairing-history-{old-agent-id}-{new-agent-id}.dat` file in the same protected store. Old source files, local ZIP/ABK archives, temporary cache files and cloud backups are not deleted. Keys and Google grants are retained. Old backups remain usable through the existing restore commands.

Old pending jobs/uploads are **retired from automatic processing**. They are not resent as the new identity, automatically uploaded to a replacement host, or counted toward its active retention policy. New backups use the new identity and its own Drive client folder. This prevents old unacknowledged progress reports or cached folder ownership from blocking reconnection. Preserve any unconfirmed old temporary archives for manual recovery/inspection; they are not silently removed as successful uploads.

The update first stores a protected recovery transition, then preserves old journals before clearing the active queues and committing the new credential. If interrupted, the next command completes the pending change under the same client lock before a worker can start. Failed/expired invitation authentication leaves the existing identity, journals, settings and backups in place.

## Portable/command-line recovery

Stop the running worker/service first. Use the same account and data-store scope as the existing pairing:

```powershell
# Current-user portable client:
.\AutoBack.Agent.exe re-pair --invite 'C:\Staging\fresh.autoback-invite'
.\AutoBack.Agent.exe run

# Installed service — elevated terminal, using its installed executable:
Stop-Service AutoBackAgent
.\AutoBack.Agent.exe re-pair --invite 'C:\Staging\fresh.autoback-invite' --machine
Start-Service AutoBackAgent
```

Append the existing `--data-dir` to every command for an explicitly configured custom store. `pair --replace --invite ...` and `setup --replace --invite ...` use the same history-preserving transition in v0.8.1. Ordinary `pair` still refuses to overwrite a saved pairing accidentally. A still-active client alias on the host must be revoked first or intentionally changed with `--name`; recovery does not silently revoke another computer.

Do not uninstall, delete `%PROGRAMDATA%\AutoBack\Agent`, remove encryption keys, or copy another computer's credential store to fix this issue. Reinstallation alone retains protected configuration and does not resolve revocation.

## Validation

`AutoBack.Pairing.Tests` exercises the actual setup action buttons and both modern and x86 Framework client processes against isolated real TLS hosts: removal, invalid invitation preservation, fresh recovery, settings/key/archive preservation, retired report/upload isolation, new host-triggered encrypted backup, old backup restore and interrupted transition completion. Evidence: `artifacts/pairing-recovery-validation/result.json`. Installed service/UAC/reboot and actual Windows 7 checks remain in the target-PC pilot.
