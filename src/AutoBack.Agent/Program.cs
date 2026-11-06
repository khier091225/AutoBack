using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Connection;
using AutoBack.Backup;
using AutoBack.Drive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoBack.Agent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
#if NETFRAMEWORK
            if (args.Length > 0 && args[0] == "installed-setup") return await AutoBack.Legacy.InstalledSetup.RunAsync(args.Skip(1).ToArray());
#endif
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
#if NETFRAMEWORK
                Console.WriteLine("Windows 7 SP1 x86 legacy client (.NET Framework 4.8). Pilot testing on Windows 7 required.");
#endif
                Console.WriteLine("""
                    AutoBack Agent 0.8.1
                    setup                             Interactive pairing, folders and optional Google sign-in
                    re-pair [--invite <file>]          Pair again; keep folders, Google, keys and backup files
                    pair --invite <file> [--name <name>] [--replace]
                    configure [--mode google-drive|local] [--source <folder> ...] [--destination <local folder>]
                    drive-connect [--client-secrets <Desktop OAuth JSON>]
                    drive-status | drive-disconnect | drive-retry
                    encryption-enable --recovery-file <NEW .abkey file>
                    encryption-disable
                    recovery-export --recovery-file <NEW .abkey file>
                    recovery-import --recovery-file <.abkey file>
                    retention-configure [--keep-local <count>] [--keep-cloud <count>]
                    protection-status
                    restore [--archive <.zip or .abk> | --drive-file <ID or link>] [--destination <NEW folder>]
                    verify-restore [--destination <restored folder>]
                    restore-status | status | run

                    All commands accept [--machine] [--data-dir <dedicated folder>].
                    Stop the agent/service before setup, restore, or changing protection/Google authorization.
                    Encryption prompts for a hidden recovery passphrase; never pass one as an argument.
                    Automated callers can explicitly supply --password-stdin with redirected input.
                    Retention 0 = disabled; 1–10000 = successful versions to keep at that destination.
                    Restore writes to <new folder>/files and verifies every file. Existing folders are preserved.
                    Restart the agent after changes to report settings/results to the host.
                    See docs/AUTOMATION-ROLLOUT.md and docs/RESTORE.md.
                    """);
                return 0;
            }
            var options = Parse(args.Skip(1).ToArray(), out var sourcePaths);
            var machine = options.ContainsKey("--machine");
            var directory = options.GetValueOrDefault("--data-dir") ?? Path.Combine(
                Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
                "AutoBack", "Agent");
            var store = new ProtectedStore(directory, machine);
            if (store.Load<PairingTransition>(PairingRecovery.TransitionFile) is not null)
            {
                using var recoveryLock = new FileStream(Path.Combine(store.DirectoryPath, ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                PairingRecovery.Complete(store);
            }
            if (args[0] == "destination-status")
            { Console.WriteLine(store.Load<BackupPlan>("backup-plan.dat")?.Target ?? "NotConfigured"); return 0; }
            if (args[0] == "setup") return await SetupCommands.ExecuteAsync(options, store);
            if (args[0] == "re-pair")
            {
                var invitation = options.GetValueOrDefault("--invite") ?? FolderSelection.ChooseInvitation();
                if (invitation is null) return 2;
                var scope = new List<string> { "pair", "--replace", "--invite", invitation, "--data-dir", directory };
                if (machine) scope.Add("--machine");
                scope.Add("--name"); scope.Add(options.GetValueOrDefault("--name") ?? store.Load<AgentCredentials>("agent.dat")?.Name ?? Environment.MachineName);
                return await Main(scope.ToArray());
            }
            if (args[0] is "encryption-enable" or "encryption-disable" or "recovery-export" or "recovery-import" or "retention-configure" or "protection-status")
            {
                if (args[0] == "protection-status") return ProtectionCommands.Execute(args[0], options, store);
                using var protectionLock = new FileStream(Path.Combine(Path.GetFullPath(directory), ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return ProtectionCommands.Execute(args[0], options, store);
            }
            if (args[0] is "restore" or "verify-restore" or "restore-status")
            {
                if (args[0] == "restore-status") return await RestoreCommands.ExecuteAsync(args[0], options, store);
                using var restoreLock = new FileStream(Path.Combine(Path.GetFullPath(directory), ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return await RestoreCommands.ExecuteAsync(args[0], options, store);
            }
            if (args[0] is "drive-connect" or "drive-ensure" or "drive-status" or "drive-disconnect" or "drive-retry")
            {
                using var http = DriveHttp.CreateClient();
                var authorization = new GoogleAuthorization(store, http);
                if (args[0] == "drive-ensure" && authorization.Status().State == "Connected")
                { Console.WriteLine("Google Drive is already connected on this client."); return 0; }
                if (args[0] == "drive-status")
                {
                    var cloud = authorization.Status();
                    Console.WriteLine($"Google Drive: {cloud.State}\nAccount: {cloud.Account}\n{cloud.Detail}");
                    return 0;
                }
                using var driveLock = new FileStream(Path.Combine(Path.GetFullPath(directory), ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (args[0] == "drive-disconnect")
                {
                    authorization.Disconnect();
                    Console.WriteLine("Google Drive credentials removed from this client. Existing local/cloud backups remain. Other clients are unaffected.");
                    return 0;
                }
                if (args[0] == "drive-retry")
                {
                    Console.WriteLine($"{CloudUploadCoordinator.RetrySaved(store)} saved upload(s) queued for retry. Start the agent again; local backups will be reused.");
                    return 0;
                }
                if (store.Load<AgentCredentials>("agent.dat") is null) throw new InvalidOperationException("Pair this client with the host before connecting Google Drive.");
                var clientFile = options.GetValueOrDefault("--client-secrets") ?? FolderSelection.ChooseOAuthFile();
                if (clientFile is null) { Console.WriteLine("Google Drive setup cancelled. Existing credentials are unchanged."); return 2; }
                Console.WriteLine("Opening Google sign-in. Authorize the Drive account that should receive this client's backups.");
                await authorization.ConnectAsync(clientFile);
                Console.WriteLine($"Google Drive connected as {authorization.Status().Account}. Start the agent: completed local backups will be uploaded automatically.");
                return 0;
            }
            if (args[0] == "configure")
            {
                using var configurationLock = new FileStream(Path.Combine(store.DirectoryPath, ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var destination = options.GetValueOrDefault("--destination");
                BackupPlan plan;
                if (sourcePaths.Count == 0 && destination is null && !options.ContainsKey("--mode"))
                {
                    var selection = FolderSelection.ChoosePlan(store.Load<BackupPlan>("backup-plan.dat"), store.DirectoryPath);
                    if (selection is null) { Console.WriteLine("Folder selection cancelled. Existing configuration is unchanged."); return 2; }
                    plan = selection;
                }
                else
                {
                    if (sourcePaths.Count == 0) throw new ArgumentException("Choose at least one --source, or omit options to open the folder manager.");
                    var mode = options.GetValueOrDefault("--mode") ?? (destination is null ? "google-drive" : "local");
                    var target = mode switch { "google-drive" => BackupTargets.GoogleDrive, "local" => BackupTargets.LocalOnly, _ => throw new ArgumentException("Use --mode google-drive or --mode local.") };
                    if (target == BackupTargets.GoogleDrive && destination is not null) throw new ArgumentException("Google Drive manages its temporary archive automatically. Omit --destination, or use --mode local.");
                    plan = BackupPlan.CreateForTarget(sourcePaths, target, destination, store.DirectoryPath);
                }
                Directory.CreateDirectory(plan.Destination);
                BackupPlan.CheckNoReparse(plan.Destination);
                store.Save("backup-plan.dat", plan);
                Console.WriteLine($"Backup configured: {BackupTargets.Label(plan.Target)}.\nSource folders ({plan.Sources.Count}):\n{string.Join("\n", plan.Sources.Select(s => "  " + s))}\nDestination: {plan.Summary.Destination}\nStart the agent to report this selection.\n" +
                    (plan.Target == BackupTargets.GoogleDrive ? "Connect Google Drive first with drive-connect. Temporary archives are kept for retries and cleared after confirmed upload." : "Backups stay in the selected local folder. Google Drive uploads are paused."));
                return 0;
            }
            if (args[0] == "status")
            {
                var saved = store.Load<AgentCredentials>("agent.dat");
                if (saved is null) { Console.WriteLine("Not paired. Create an invitation in the host app, then use pair --invite <file>."); return 2; }
                saved.Validate();
                Console.WriteLine($"Paired computer: {saved.Name}\nAgent ID: {saved.AgentId}\nHost: {saved.Host}:{saved.Port}\nHost certificate SHA-256: {saved.CertificateSha256}\nCredential store: {Path.GetFullPath(directory)}\nStatus shows saved pairing only; the host dashboard shows live connectivity.");
                var plan = store.Load<BackupPlan>("backup-plan.dat");
                Console.WriteLine(plan is null ? "Backup folders: not configured." : $"Mode: {BackupTargets.Label(plan.Target)}\nBackup sources ({plan.Sources.Count}):\n{string.Join("\n", plan.Sources.Select(s => "  " + s))}\nDestination: {plan.Summary.Destination}");
                return 0;
            }
            if (args[0] is not ("pair" or "run")) throw new ArgumentException("Unknown command. Use --help.");
            // Hold a per-data-directory lock so pairing cannot replace an identity under a running agent.
            using var instance = new FileStream(Path.Combine(Path.GetFullPath(directory), ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (args[0] == "pair")
            {
                var previous = store.Load<AgentCredentials>("agent.dat");
                if (previous is not null && !options.ContainsKey("--replace"))
                    throw new InvalidOperationException("This agent has a saved pairing. If removed from the host, stop the worker and use re-pair with a fresh host invitation, or open Pair AutoBack Agent again. Settings and backup files are kept.");
                var invitationPath = options.GetValueOrDefault("--invite") ?? throw new ArgumentException("Provide --invite <file>.");
                if (new FileInfo(invitationPath).Length > Protocol.MaxFrameLength) throw new InvalidDataException("Invitation file is too large.");
                var invitation = JsonSerializer.Deserialize<PairingInvitation>(await File.ReadAllTextAsync(invitationPath), Protocol.Json)
                    ?? throw new InvalidDataException("Empty invitation.");
                invitation.Validate();
                var name = options.GetValueOrDefault("--name") ?? previous?.Name ?? Environment.MachineName;
                var pending = store.Load<AgentCredentials>("pending.dat");
                var credentials = pending is not null && pending.Host == invitation.Host && pending.Port == invitation.Port &&
                    pending.CertificateSha256 == invitation.CertificateSha256 && pending.Name == name && pending.AgentId != previous?.AgentId
                    ? pending : AgentClient.CreateCredentials(invitation, name);
                var archive = previous is null ? null : PairingRecovery.Capture(store, previous);
                store.Save("pending.dat", credentials);
                await new AgentClient().PairAsync(invitation, credentials);
                if (archive is not null)
                {
                    PairingRecovery.Replace(store, credentials, archive);
                    Console.WriteLine("Paired again. Folders, Google sign-in, encryption keys and backup files were kept. Old jobs/uploads are preserved in protected pairing-history; they will not replay under the new identity.");
                }
                else { store.Save("agent.dat", credentials); store.Delete("pending.dat"); }
                Console.WriteLine($"Paired {credentials.Name} with {credentials.Host}:{credentials.Port}. Start with 'run' using the same --machine/--data-dir options. Delete the used invitation file.");
                return 0;
            }

            var identity = store.Load<AgentCredentials>("agent.dat") ?? throw new InvalidOperationException("Agent is not paired. Use pair --invite <file> first.");
            identity.Validate();
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddWindowsService(configuration => configuration.ServiceName = "AutoBackAgent");
            builder.Services.AddSingleton(identity);
            builder.Services.AddSingleton<AgentClient>();
            builder.Services.AddSingleton(store);
            builder.Services.AddHostedService<AgentWorker>();
            using var host = builder.Build();
            await host.RunAsync();
            return Environment.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            JsonException or InvalidDataException or CryptographicException or DriveException or KeyNotFoundException or FormatException or System.Security.Authentication.AuthenticationException or AgentRejectedException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            // Never print serialized credentials, invitation contents, or arbitrary exception bodies.
            var message = ex switch
            {
                AgentRejectedException => ex.Message,
                DriveException => ex.Message,
                KeyNotFoundException or FormatException => "The configuration or Google response is incomplete. Check the Desktop OAuth client JSON and try again.",
                System.Security.Authentication.AuthenticationException => "Host identity could not be verified. Check the invitation and host certificate; do not disable certificate validation.",
                CryptographicException => "The protected configuration could not be read. Use the same Windows account and machine scope as pairing.",
                UnauthorizedAccessException => "Access denied. Machine-wide pairing requires an elevated terminal.",
                System.Net.Sockets.SocketException => "Host is unreachable. Check the address, TCP port, and host firewall.",
                OperationCanceledException => "The operation was cancelled or timed out. Check connectivity or retry Google sign-in.",
                IOException => "A file or network operation failed. Check paths, host connectivity, and whether another agent is using this data directory.",
                _ => ex.Message
            };
            Console.Error.WriteLine("AutoBack: " + message);
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(string[] args, out List<string> sourcePaths)
    {
        sourcePaths = [];
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--machine" or "--replace" or "--password-stdin") options.Add(key, null);
            else if (key is "--data-dir" or "--invite" or "--name" or "--source" or "--destination" or "--client-secrets" or "--archive" or "--drive-file" or "--recovery-file" or "--keep-local" or "--keep-cloud" or "--mode")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {key}.");
                if (key == "--source") { sourcePaths.Add(args[i]); options[key] = args[i]; }
                else options.Add(key, args[i]);
            }
            else throw new ArgumentException("Unknown option. Use --help.");
        }
        return options;
    }
}
