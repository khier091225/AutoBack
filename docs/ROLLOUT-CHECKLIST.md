# Physical-PC pilot and rollout checklist

This is an **unexecuted physical-PC checklist**. The development test uses three real agent processes on one machine. Complete this checklist before calling the office rollout verified. Use synthetic files first, disable retention initially, and keep separate recovery exports/passwords.

## Inventory and prerequisites

Record host computer/operator, host LAN IPv4/DNS name, agent PC names, Windows build/architecture, AutoBack version, installer SHA-256, Google account/OAuth project used and test date. Do not record OAuth tokens, pairing secrets, encryption keys or recovery passphrases. Use stable host DNS/address on a trusted Private/Domain network. Check that the Google consent/test-user configuration permits the intended accounts.

| Check | Host | Client A | Client B |
| --- | --- | --- | --- |
| 64-bit Windows, installer hash matches SHA256SUMS | Pending | Pending | Pending |
| MSI installation, Start Menu and installed file inventory | Pending | Pending | Pending |
| Readiness report from Test-ClientReadiness.ps1 | — | Pending | Pending |
| Pairing with distinct fresh invitation and correct host certificate | — | Pending | Pending |
| LocalService source Read / backup destination Modify, no unnecessary access | — | Pending | Pending |
| Direct Google sign-in in the correct machine scope | — | Pending | Pending |
| Separate recovery export copied safely off client | — | Pending | Pending |

The host and a client can share one PC for a small pilot, but at least one agent must be on another physical machine to exercise LAN/firewall behavior.

## Functional and failure drills

1. Launch the host as its intended operator. Pair clients one at a time. Stop an agent for over 20 seconds: only that client becomes Offline and retains last seen. Restart it: its original identity reconnects. Run the readiness script against the host LAN name and save the non-secret JSON evidence.
2. Back up different synthetic sources simultaneously from both clients. Include nested/empty folders, a zero-byte file and a binary file. Confirm unique local versions, correct client ownership and separate cloud status. Verify uploaded files with a real cloud download/restore; compare original file hashes and open recovered documents.
3. Enable encryption on Client A with a new recovery export. Confirm its new `.abk` archive is not a readable ZIP and cloud stores the encrypted object. On Client B using a fresh dedicated test data store, try restore without keys (must fail), then import A's recovery export/passphrase and restore successfully to a new folder. Try an incorrect passphrase and a modified copy of the encrypted archive; neither may publish a successful recovery. Retain the valid export off the clients.
4. Set a daily time a few minutes ahead in the displayed host time zone, save, restart the host and confirm the setting persists. Keep clients online for one occurrence. Ensure one new job per configured client and no overlaps. Close the host across the next scheduled time: reopening should catch up once. Leave B offline across an occurrence: its queued job must finish once after reconnecting.
5. Set local retry limit 2 and delay 1 minute for synthetic folders. Lock a source file without sharing, trigger a backup, confirm Failed remains in history and the new retry has a new ID/attempt. Release the lock and confirm success. Keep it locked through the limit: retries must stop. Cancel a queued/running job: cancellation must not retry. Change a plan/key setting while a request is queued: fail safely and request a fresh job.
6. Interrupt networking during an upload; local completion stays successful. Reconnect/restart and confirm resumable upload finishes without another remote file. Revoke the Google grant: show Needs sign-in without repeatedly refreshing. Reconnect the original account and resume. An incorrect host certificate or revoked pairing must not be silently trusted.
7. Use a **dedicated synthetic plan** and enable local keep 2 / cloud keep 3. Produce at least five successful versions. Confirm only eligible managed older archives expire, pending/failed uploads are preserved, unrelated files remain, the newest copies are recoverable and remote expired objects are in Trash. Do not permanently empty Google Trash during the pilot. Corrupt a *copy* selected as a survivor in the isolated test store and confirm retention pauses before discarding prior good versions.
8. Confirm Windows service behavior: delayed automatic start after setup, correct LocalService identity and folder access after reboot without interactive client sign-in. A controlled crash in the isolated pilot should trigger at most two configured restarts; a clean Stop should remain stopped. Capture Windows Application/System events without secrets. The host must still be running for scheduling.
9. Test MSI upgrade/repair on both PCs with saved data/keys. Check identity, source plan, Google authorization and recovery keys survive. Re-enable automatic service startup through setup after an agent upgrade. Reject a downgrade. Test uninstall on one pilot client: application/service removed, backups/protected stores/recovery exports retained. Revoke the retired pairing separately.
10. Run for 24–72 hours with representative file sizes/counts. Record durations, CPU/RAM/disk consumption, network usage, disk-full/inaccessible-source behavior, retry counts, restore durations and any incomplete staging files after forced termination. Set office concurrency/bandwidth and storage policies based on measured results before widening deployment.

## Acceptance evidence

Keep installer hashes, readiness JSON per client, host job/restore screenshots, restore reports, independent file-hash comparisons, reboot/service results and MSI upgrade/uninstall observations. Record each failure and fix/retest it. Sign-off fields: test date ___; host ___; client machines ___; verified by ___; unresolved issues ___; rollout batch approved ___ .

Roll out in small batches after the pilot passes. Encryption and retention remain explicit per-client choices; do not copy an existing DPAPI store to another machine to provision it. Generate fresh pairing invitations and independent recovery exports for new clients.
