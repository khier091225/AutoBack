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
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("AutoBack Agent 0.4\npair --invite <file> [--name <name>] [--replace]\nconfigure [--source <folder> --destination <folder>]\ndrive-connect [--client-secrets <Desktop OAuth JSON>]\ndrive-status\ndrive-disconnect\ndrive-retry\nrun\nstatus\n\nAll commands accept [--machine] [--data-dir <dedicated folder>].\nconfigure opens source/destination pickers; drive-connect opens Google sign-in in your browser.\nStop the agent/service before drive-connect, drive-disconnect, or drive-retry.\nUse --machine from an elevated terminal for Windows Service configuration.\nThe client uploads completed local backups directly to Google Drive when connected.");
                return 0;
            }
            var options = Parse(args.Skip(1).ToArray());
            var machine = options.ContainsKey("--machine");
            var directory = options.GetValueOrDefault("--data-dir") ?? Path.Combine(
                Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
                "AutoBack", "Agent");
            var store = new ProtectedStore(directory, machine);
            if (args[0] is "drive-connect" or "drive-status" or "drive-disconnect" or "drive-retry")
            {
                using var http = DriveHttp.CreateClient();
                var authorization = new GoogleAuthorization(store, http);
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
                var source = options.GetValueOrDefault("--source");
                var destination = options.GetValueOrDefault("--destination");
                if (source is null && destination is null)
                {
                    var folders = FolderSelection.Choose();
                    if (folders is null) { Console.WriteLine("Folder selection cancelled. Existing configuration is unchanged."); return 2; }
                    (source, destination) = folders.Value;
                }
                if (source is null || destination is null) throw new ArgumentException("Provide both --source and --destination, or omit both to open the folder pickers.");
                var plan = BackupPlan.Create(source, destination);
                Directory.CreateDirectory(plan.Destination);
                BackupPlan.CheckNoReparse(plan.Destination);
                store.Save("backup-plan.dat", plan);
                Console.WriteLine($"Local backup configured.\nSource: {plan.Source}\nDestination: {plan.Destination}\nStart/keep the agent running; the host will receive this configuration on its next heartbeat.\nThe agent account needs read access to source files and write access to the destination.");
                return 0;
            }
            if (args[0] == "status")
            {
                var saved = store.Load<AgentCredentials>("agent.dat");
                if (saved is null) { Console.WriteLine("Not paired. Create an invitation in the host app, then use pair --invite <file>."); return 2; }
                saved.Validate();
                Console.WriteLine($"Paired computer: {saved.Name}\nAgent ID: {saved.AgentId}\nHost: {saved.Host}:{saved.Port}\nHost certificate SHA-256: {saved.CertificateSha256}\nCredential store: {Path.GetFullPath(directory)}\nStatus shows saved pairing only; the host dashboard shows live connectivity.");
                var plan = store.Load<BackupPlan>("backup-plan.dat");
                Console.WriteLine(plan is null ? "Backup folders: not configured." : $"Backup source: {plan.Source}\nLocal destination: {plan.Destination}");
                return 0;
            }
            if (args[0] is not ("pair" or "run")) throw new ArgumentException("Unknown command. Use --help.");
            // Hold a per-data-directory lock so pairing cannot replace an identity under a running agent.
            using var instance = new FileStream(Path.Combine(Path.GetFullPath(directory), ".agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (args[0] == "pair")
            {
                if (store.Load<AgentCredentials>("agent.dat") is not null && !options.ContainsKey("--replace"))
                    throw new InvalidOperationException("This agent is already paired. Use run, or stop the agent and use pair --replace for a new pairing.");
                var invitationPath = options.GetValueOrDefault("--invite") ?? throw new ArgumentException("Provide --invite <file>.");
                if (new FileInfo(invitationPath).Length > Protocol.MaxFrameLength) throw new InvalidDataException("Invitation file is too large.");
                var invitation = JsonSerializer.Deserialize<PairingInvitation>(await File.ReadAllTextAsync(invitationPath), Protocol.Json)
                    ?? throw new InvalidDataException("Empty invitation.");
                invitation.Validate();
                var name = options.GetValueOrDefault("--name") ?? Environment.MachineName;
                var pending = store.Load<AgentCredentials>("pending.dat");
                var credentials = pending is not null && pending.Host == invitation.Host && pending.Port == invitation.Port &&
                    pending.CertificateSha256 == invitation.CertificateSha256 && pending.Name == name
                    ? pending : AgentClient.CreateCredentials(invitation, name);
                store.Save("pending.dat", credentials);
                await new AgentClient().PairAsync(invitation, credentials);
                store.Save("agent.dat", credentials);
                store.Delete("pending.dat");
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
            return 0;
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

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--machine" or "--replace") options.Add(key, null);
            else if (key is "--data-dir" or "--invite" or "--name" or "--source" or "--destination" or "--client-secrets")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {key}.");
                options.Add(key, args[i]);
            }
            else throw new ArgumentException("Unknown option. Use --help.");
        }
        return options;
    }
}
