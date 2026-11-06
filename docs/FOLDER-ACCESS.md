# Check and prepare backup folder access — v0.8.3

**Windows error 5 during service verification:** update the client to v0.8.3. Windows may reject direct LocalService logon even from elevated setup. The fallback obtains the real identity from a temporary Windows service instead; it does not change logon privileges or substitute the administrator's file access. Administrator validation passed for both modern and x86 Framework clients: `artifacts/service-identity-pilot-modern.json` and `artifacts/service-identity-pilot-legacy.json`.

Open **Choose AutoBack backup folders** (or Configure → Change backup folders/settings) from the installed client's Start Menu and approve its administrator prompt. Add the folders, select Google Drive or Local only, then use:

1. **Check access** — tests the selected folders as the Agent service's actual `NT AUTHORITY\LocalService` account. It enumerates every selected source tree and opens each file for reading. It also creates, writes, reads back and removes a tiny scratch directory/file in the backup destination.
2. **Grant required access** — prepares permissions on the selected paths and performs the same check again. Source directories receive inheritable Read/list/traverse access; inaccessible ordinary files receive Read access. Parent folders receive only the metadata/traverse rights needed for path validation, applying to that folder only. Local-only destinations receive inheritable Modify access. Google temporary storage uses the existing private agent data directory.
3. Resolve every reported issue and check again. Save requires a successful service-account result for the current selection. Changing a source, destination or mode invalidates that result; a result over one minute old is rechecked on Save.
4. Save folders. Installed setup performs another service-account check immediately before offering `START`. A failed/unavailable check leaves the service stopped and explains how to return to the folder manager.

Permissions are applied when **Grant required access** is clicked, even if you later cancel the folder-configuration window. Cancelling an in-progress operation stops further processing; grants already made remain recorded. Folder settings are saved only after a successful check. The progress/result area shows the checked-file count and up to 100 issue examples, with the complete issue count. It never silently marks an inaccessible selection ready or skips those files as a successful backup.

## What a check can and cannot establish

This is an access preflight for the time it runs. It does not create/upload a full backup and does not promise that files will remain unlocked, unchanged or present later. Run a real **Backup Now** and verify the confirmed local/cloud result. The backup engine still stops on unreadable, locked, missing, changed or unsupported files and verifies the saved archive; a later failure is not reported as completion.

The checker opens each file and reads its first byte to establish data access; it does not copy every byte a second time. Cloud OAuth validity, Drive quota/network access, all future free-space requirements and successful recovery still require the backup/upload/restore checks. Source reparse links/junctions/cloud placeholders are rejected rather than followed. Windows EFS-encrypted files need a copy the service can decrypt; a Windows ACL grant does not supply the user's EFS key. Hard-linked files may be read, but automatic ACL changes on them are refused because another path shares the same security descriptor. Explicit deny rules are retained and may still block access.

Granting does not take ownership, remove deny rules, grant write access on sources, change the service account to Administrator, or change unrelated siblings. Protected inheritance settings are preserved. A selected source tree is processed explicitly, without following links. For Local only, the Modify grant covers the selected destination and future inherited entries; protected/locked old versions may still need their owner's attention during retention.

## Audit and service identity

Before each ACL change, an encrypted audit record is durably appended to `permission-audit-{guid}.dat` inside the existing ACL-protected agent store. Records contain the exact path, original/proposed DACL and timestamp; entries are DPAPI protected. The grants are processed one object at a time so large trees do not require holding all original ACLs in memory. A failed audit write prevents that ACL mutation. No background sweep automatically deletes these permission audit files or resets permissions when folders are removed.

Service checks use scoped LocalService impersonation. If Windows refuses a direct LocalService logon from elevated setup (including Windows error 5), setup starts a uniquely named, demand-start `AutoBackAccess-{guid}` helper service as LocalService. The helper supplies only its identity through a private local named pipe; it receives no backup paths or credentials. Setup verifies the pipe client's service process ID, handshake and LocalService SID before retaining the token. The helper is stopped and removed after that exchange. Source/destination probes then run under that token, while explicit permission grants remain in the elevated setup process. No logon privileges are assigned to the administrator, and the installed backup service's account/startup settings are not changed by verification.

If Windows also denies obtaining/using the helper identity, the result is **not ready**, with the Windows error and an administrator/service-policy hint; it never substitutes the interactive administrator's access and calls it verified. Console/current-user setup checks the current user's identity instead. Service installation remains LocalService, and changing folder access does not alter the host pairing, Google authorization, backup mode, encryption keys or retention policy.

References: [Microsoft LocalService logon example](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-logonuserw), [object-specific file security updates](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-setfilesecurityw). The latter API is used intentionally for exact-object DACL changes; selected descendants are visited explicitly and unrelated ancestor subtrees are not recursively rewritten.

## Commands

Stop the worker/service first. Use the original account, scope and optional `--data-dir`:

```powershell
# Elevated installed client, saved plan:
.\AutoBack.Agent.exe access-check --machine
.\AutoBack.Agent.exe access-grant --machine

# New service folder selection; grant, verify, then save only if ready:
.\AutoBack.Agent.exe configure --machine --grant-access --source 'D:\Work' --mode google-drive
.\AutoBack.Agent.exe configure --machine --grant-access --source 'D:\Work' --mode local --destination 'E:\Backups'

# Portable current-user client:
.\AutoBack.Agent.exe access-check
```

Machine-mode command-line configuration is also checked before replacing the saved plan. `configure --grant-access` deliberately requests the permissions step; an unsuccessful check keeps the previous folder plan. `access-grant` applies only to the already saved plan. `folder-access.dat` stores the latest protected result and its plan ID.

## Validation

`AutoBack.Access.Tests` covers actual file-open and destination operations, locked-file failures, cancellation, helper argument/identity rejection, exact minimal parent grants, sibling/owner/inheritance preservation, refusing a grant after audit failure, hard-link/junction refusal, UI result invalidation and service Save gating. Its administrator-only `--service-pilot <agent.exe> <report.json>` mode uses isolated ProgramData fixtures to verify real LocalService acquisition, failures before grants, administrator grants followed by successful worker probes, repeated independent checks, locked-file detection, scratch cleanup and helper-service removal. This pilot passed on the development PC; see `artifacts/service-identity-fix/pilot-result.json`. The x86 Framework suite runs its native file checks and locked-file case as well. Actual Windows 7 setup remains a target-PC pilot check. See `artifacts/access-validation/result.json` and the [Windows 7 pilot](WINDOWS7-CLIENT.md).
