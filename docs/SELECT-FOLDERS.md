# Choose backup folders per computer — v0.7

Each client keeps its **own list of 1–20 source folders** and one separate local backup destination. Configure it once on that client, then use the host's Backup Now or daily schedule. Every source includes all of its subfolders; no Windows username/path template is assumed.

## Simple setup on each client

1. Use the updated v0.7 agent. For an MSI-installed client, open **Start Menu → AutoBack Agent → Choose AutoBack backup folders** and approve the administrator prompt. This opens folder configuration without repeating pairing/Google sign-in. The helper stops the service while you edit; after saving, confirm its permissions and type `START` to resume. Cancel preserves the previous configuration and restarts a service that had been running.

For the portable console agent, open PowerShell in its published folder and run:

```powershell
.\AutoBack.Agent.exe configure
# For an installed Windows service, use elevated PowerShell and its machine scope:
.\AutoBack.Agent.exe configure --machine
```

2. In **Backup folders**, click **Add folder…** for each actual location used by this computer's user. Example:

```text
PC Accounting:
  C:\Users\Maria\Documents
  C:\Users\Maria\Desktop
  D:\Accounting Files

PC Sales:
  C:\Users\Juan\Documents
  D:\Quotations
```

**My Documents** and **My Desktop** resolve the current Windows account's actual Windows special-folder locations, including supported redirection. The window displays which Windows account is selecting folders. If using a different administrator account, choose the intended user's folders explicitly with **Add folder…**. AutoBack does not assume every user saves files in the same place or automatically include other profiles.

3. Use **Remove selected** to exclude a listed folder. This only removes it from the selection; its files are preserved. Select multiple rows with Ctrl/Shift if needed. Reopening configure loads the saved folders and destination; Cancel preserves the previous setup.
4. Choose one **local backup destination** outside every selected source, for example `E:\AutoBack Archives`. Click **Save folders**.
5. Keep/start the client agent or restart its installed service. In the host's Computers/Overview table, the computer shows its folder count. Click **↗ computer details** to view the complete reported source list and destination. The host displays paths reported by that client; it does not open its own local picker to select a different PC's folders.

The running console agent can receive a new configuration on its next heartbeat. A running backup keeps the plan it captured; a queued command tied to an older plan fails safely instead of silently using a different selection. For predictable changes, finish/cancel pending work first. Existing pairing, Google authorization, encryption keys and retention settings are preserved when changing folders.

For service mode, grant **NT AUTHORITY\LocalService** Read/list access to **every** selected source and Modify access to the destination. Administrator selection does not automatically grant service access. Inaccessible/locked/missing selected folders fail the backup; no source is silently omitted. A folder may appear in the picker under your Windows account but still require permissions for the service.

## Selection rules

- Select 1–20 existing local folders. One parent already includes all descendants; duplicate, overlapping parent/child selections and junctions/symbolic links are rejected.
- The destination and each source must stay separate; neither may contain the other. Network shares/mapped network drives remain unsupported.
- Source paths are bounded to 1024 characters each and 20,000 UTF-8 bytes combined so the host receives the full configuration. Normal Windows path/file-system limits also apply.
- One source keeps the existing ZIP layout and legacy plan identity. Multiple sources use manifest format 2. Update **both host and agent to v0.7**; old v0.6 restore tools reject format 2. Saved multi-folder plans deliberately fail on old agents instead of silently backing up only one source. Existing single-folder configurations and format-1 backups still work.
- Changing the selection/order creates a new plan identity. Retention handles the currently configured plan; older plans are preserved for deliberate review rather than silently swept.

## Backup and restore layout

With several selected roots, one full archive contains all of them in separate numbered folders:

```text
files/
  01-Documents/
  02-Desktop/
  03-Accounting Files/
manifest.json
```

Two different source folders named `Documents`, or files named `report.xlsx` under them, stay distinct (for example `01-Documents/report.xlsx` and `02-Documents/report.xlsx`). Empty selected roots remain present. The manifest records each original source path and its archive prefix; it is inside the encrypted envelope when encryption is enabled.

Restore into a new folder as usual. The recovered payload uses the same numbered subfolders, and `restore-report.json` includes the original source mapping. All roots participate in source-overlap protection, complete file-set/size/SHA-256 verification and re-verification. To place recovered files back in their original locations, review that mapping and copy them deliberately; restore never overwrites original folders automatically.

## Optional command-line setup

Repeat `--source` for multiple folders; quote paths containing spaces:

```powershell
.\AutoBack.Agent.exe configure --source 'C:\Users\Maria\Documents' --source 'D:\Accounting Files' --destination 'E:\AutoBack Archives'
.\AutoBack.Agent.exe status
```

Use the same Windows account, `--machine` and/or `--data-dir` as pairing/running the agent. Invalid configuration leaves the last saved plan unchanged. Desktop/MSI setup uses this same folder manager.

Validation: `tests/AutoBack.Folders.Tests` exercises the actual picker form, saved selections, add/remove/save/cancel, repeated CLI sources, old single-folder compatibility, duplicate/overlap/missing-root failures, collision-free format-2 ZIP/restore, encryption, simulated Drive upload/download, retention and a real host-triggered agent backup. Desktop checks cover the full folder list and computer details. Local evidence is `artifacts/multi-folder-validation/result.json`.
