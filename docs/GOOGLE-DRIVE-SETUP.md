# Google Drive setup — AutoBack 0.8

Google Drive ang default sa bagong setup. Client mismo ang gumagawa/verifies ng temporary archive at nag-a-upload; automatic itong nililinis pagkatapos ng confirmed upload at host acknowledgment. Walang permanent local destination na pipiliin. Local only ang hiwalay na option para sa permanent local versions at walang automatic upload. Tingnan ang [destination modes](DESTINATION-MODES.md). Tokens at archives ay hindi ipinapadala sa host.

**Current validation:** pumasa ang automated tests at isang live Google Drive test noong November 5, 2026. Gumawa ang paired client ng ZIP mula sa dalawang synthetic sample files, direktang nag-upload sa Google Drive, at nagtugma ang remote file size at checksum sa local archive. Parehong Completed ang local at cloud result. Same-PC console mode ang live test; kailangan pa ang Windows Service at cross-PC office validation.

## A. One-time Google Cloud setup

1. Buksan ang [Google Cloud Console](https://console.cloud.google.com/). Gumawa o pumili ng project, halimbawa **AutoBack**.
2. Sa **APIs & Services → Library**, hanapin ang **Google Drive API** at click **Enable**.
3. Buksan ang **Google Auth platform**. Sa **Branding**, ilagay ang app name **AutoBack**, support email, at contact email.
4. Sa **Audience**, gamitin ang **External** kung regular Gmail ang account. Para sa eligible Google Workspace organization, maaaring **Internal** ang naaangkop. Sa External/Testing, idagdag sa **Test users** ang Google account na tatanggap ng backups.
5. Sa **Data Access**, idagdag ang scope `https://www.googleapis.com/auth/drive.file` kung hinihingi ng setup. AutoBack requests this scope at sign-in; hindi nito kailangan ang full-Drive scope.
6. Sa **Clients → Create client**, piliin ang application type na **Desktop app**. Pangalanan itong **AutoBack Windows Agent**.
7. I-download ang client JSON. Itago ito sa labas ng source repository, halimbawa `C:\AutoBackSetup\desktop-client.json`.

Gamitin ang parehong Desktop client JSON para sa office client PCs na gumagamit ng parehong AutoBack app. Bawat PC ay magse-sign in at magkakaroon ng sariling refresh token. Huwag gumawa ng Web application client, API key, o service-account JSON para sa flow na ito. Ang mga Google Console steps ay sumusunod sa [official Desktop client setup](https://developers.google.com/workspace/drive/api/quickstart/go#authorize_credentials_for_a_desktop_application).

`drive.file` limits access to files created/opened with this app; ginagamit ng AutoBack ang sarili nitong backup folders. Tingnan ang [Google scope documentation](https://developers.google.com/workspace/drive/api/guides/api-specific-auth).

**Testing mode:** External apps in Testing receive refresh grants that expire after seven days for these scopes. Bago unattended office use, ayusin ang appropriate Production/Internal publishing setup at organization policies. Maaari pa ring ma-revoke o mag-expire ang authorization; ipapakita ng agent ang **Needs sign-in**. [Google refresh-token expiration rules](https://developers.google.com/identity/protocols/oauth2/#expiration).

## B. Update both host and client

Gamitin ang version **0.8 / protocol 4** sa host at agent. Saved pairings, backups at recovery keys are retained; old invitation files need regeneration for new pairing.

Sa development PC, kung kailangan mag-publish:

```powershell
dotnet publish AutoBack.csproj -c Release -r win-x64 --self-contained true -o artifacts/host-win-x64
dotnet publish src/AutoBack.Agent/AutoBack.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/agent-win-x64
```

Isara muna ang lumang host at ihinto ang lumang agent/service bago palitan ang executable folders. Kopyahin ang **buong** agent folder papunta sa client.

## C. Connect Google on a console client

1. I-pair ang agent at i-configure ang source/destination folders kung hindi pa ito nagagawa. Tingnan ang [pairing guide](CONNECTION-SETUP.md) at [local backup guide](LOCAL-BACKUP.md).
2. Ihinto ang running agent gamit ang **Ctrl+C**. Google account changes require the agent to be stopped.
3. Sa agent folder sa client PC:

```powershell
.\AutoBack.Agent.exe drive-connect --client-secrets "C:\AutoBackSetup\desktop-client.json"
```

Puwede ring `drive-connect` lang: may JSON file picker. Bubukas ang system browser; piliin ang Google account na tatanggap ng backups at payagan ang requested Drive access. Ang callback ay automatic sa `127.0.0.1`; wala kang kailangang i-copy/paste na authorization code. Ang app ay gumagamit ng PKCE at random state ayon sa [Google Desktop OAuth flow](https://developers.google.com/identity/protocols/oauth2/native-app).

4. Kapag lumabas ang connected account, tingnan at simulan ang agent:

```powershell
.\AutoBack.Agent.exe drive-status
.\AutoBack.Agent.exe run
```

Kung gumamit ng `--data-dir` sa pairing, gamitin ang parehong option sa lahat ng commands. Google credentials ay naka-DPAPI encryption sa client data store; short-lived access tokens stay in memory.

**Existing backups:** new Local only jobs are never discovered for automatic upload. New Google-mode setup does not retroactively queue older local/legacy archives. Existing legacy plans retain their former behavior until deliberately resaved; already queued uploads preserve their original Google account binding. Local only pauses that pending cloud work. Reconnect the original account to resume eligible uploads in cloud-capable mode.

## D. Connect Google for Windows Service mode

In v0.8.3, open **Reconnect AutoBack Google Drive** from the Start Menu, or choose **Connect / re-sign in to Google Drive** in Configure AutoBack Agent. This opens sign-in directly without redoing folder selection. Existing clients reuse their protected OAuth Desktop app configuration; first-time setup still asks for the Desktop client JSON. Cancelling or failing sign-in preserves the previously saved account. If the browser does not open automatically, use the sign-in link printed in the setup window in a browser on the same PC while that window remains open. The callback still uses the local listener and PKCE; no authorization code is pasted into AutoBack.

Install the v0.8.3 Agent MSI to retain the LocalService verification fix across repair/reinstallation. Reinstalling the old v0.8.2 MSI restores its old verifier.

Console mode and service mode have separate configuration. Sa elevated PowerShell **sa client PC**:

```powershell
Stop-Service AutoBackAgent
& "$env:ProgramFiles\AutoBack Agent\AutoBack.Agent.exe" drive-connect --machine --client-secrets "C:\AutoBackSetup\desktop-client.json"
& "$env:ProgramFiles\AutoBack Agent\AutoBack.Agent.exe" drive-status --machine
Start-Service AutoBackAgent
```

Sign-in happens in your interactive browser; the background LocalService worker later uses machine-scoped protected credentials. Existing restricted ACLs allow only SYSTEM, Administrators, and LocalService to read the service's credential store. Hindi magbubukas ng browser ang service habang nagba-backup. Source/destination folder permissions for LocalService are still required.

## E. First live upload test

1. Gumamit muna ng maliit na test source folder, at panatilihing bukas ang host at running ang client.
2. Sa host, click **Backup Now**.
3. Hintayin ang **Prepared for upload**, kasunod ang cloud upload. Google mode uses automatic private staging; no permanent local backup folder is required.
4. Sa **Cloud Upload**, makikita ang Queued → Uploading → Cloud complete. Google Drive must confirm the uploaded size/checksum before completion is reported.
5. Sa **Activity**, may preparation result, cloud result, automatic temporary-cleanup status at **Open in Google Drive** para sa confirmed upload. Restore from Drive remains available after cache cleanup.
6. Sa signed-in account's My Drive, hanapin:

```text
AutoBack Backups/
  {Computer name}-{Agent ID}/
    {Job ID}-backup-{UTC timestamp}.zip
```

Ang root folder ay nire-reuse kapag nakikita ng parehong OAuth app/account; bawat agent ay may sariling subfolder. Shared Drives and an arbitrary existing destination folder picker are not included in this milestone.

**Cloud complete confirms the upload.** Para sa restore drill, sundin ang [Restore and verification guide](RESTORE.md): download the selected Drive backup directly to the client and recover into a fresh folder with full SHA-256 readback. Plain ZIPs remain unencrypted; [Automation and rollout](AUTOMATION-ROLLOUT.md) explains authenticated `.abk` encryption/recovery exports and opt-in version retention. Encrypted archives upload as `application/octet-stream` with the same confirmed size/checksum and AutoBack identity checks.

## F. Interrupted uploads, errors, and retry

- One upload at a time per client. Host-wide bandwidth/concurrency policies are later work.
- Uploads use 1 MiB chunks, server-confirmed offsets, and encrypted session checkpoints. After interruption/restart, the agent checks the saved session and resumes. An expired session restarts using the same preallocated Drive file ID, preventing duplicate files from lost completion responses. This follows [Google's resumable upload protocol](https://developers.google.com/workspace/drive/api/guides/manage-uploads).
- Temporary network/rate-limit/server errors retry with saved backoff. Local success remains unchanged.
- **Needs sign-in:** stop the agent/service, run `drive-connect` again for the original account, then start it. Authorization failures wait for a new grant rather than continuously refreshing a rejected one.
- **Failed:** Activity shows a bounded error description. Free Drive space or fix the underlying issue, then retry from the stopped client:

```powershell
.\AutoBack.Agent.exe drive-retry
.\AutoBack.Agent.exe run
```

For service mode, stop the service, run `drive-retry --machine` as administrator using the installed executable, then restart the service.

- `drive-retry` reuses the local archive and upload identity. It does not make another local backup or overwrite an unexpected cloud file.
- A missing/modified/corrupt local archive or a mismatching cloud checksum is not counted as cloud success. If the archive changed after upload began, create a new backup instead of overwriting the existing version.
- If a backup folder was trashed/moved unexpectedly, restore it in Google Drive before retrying. AutoBack does not delete cloud files or modify sharing permissions.
- Closing the host may prevent status reporting, but an already running client upload can finish and save its result. An explicit host authorization rejection pauses uploads.

## G. Disconnect a client

Stop the agent, then:

```powershell
.\AutoBack.Agent.exe drive-disconnect
```

Use `--machine` for the service store. Local credentials are removed; existing local/cloud backups remain. This does **not** revoke the app's Google grant for other clients. To revoke access at Google, manage the app in your Google account's connected-app settings; that may affect other PCs sharing the same app/account.

## Troubleshooting

| Problem | Check |
| --- | --- |
| Google blocks sign-in | Correct Cloud project, Desktop client type, consent setup, test-user email, and Workspace admin policy. For “app is currently being tested / developer-approved testers / 403 access_denied”, open Audience → Test users → Add users, add the exact sign-in email, and Save; then start a fresh drive-connect attempt. |
| JSON rejected | Use the downloaded Desktop app JSON with an `installed` section, not a Web client/service account/API key. |
| Account changes rejected while running | Stop the console agent/service first; account management uses the agent instance lock. |
| Local complete, cloud not connected | Run `drive-connect` on that client with the same data scope as `run`. |
| Needs sign-in after a week | External/Testing refresh-grant expiry; review publishing setup and reconnect. |
| Client switched Google account | Old pending uploads require their original account; new jobs can bind to the new account. |
| Drive storage full / quota error | Resolve storage/account limits, then run `drive-retry`. |
| Local file missing or changed | Keep the original archive available for resume, or start a fresh backup job. |
| No cloud progress on host | Check agent connectivity and client logs. Last reported progress is retained on disconnect. |

A live successful upload has been verified using the configured Google account. The test agent was stopped and the original backup-folder configuration restored afterward; test source files and the ZIP were retained. Follow section E for another client or after deployment changes, then use the [restore guide](RESTORE.md) to verify recovery.
