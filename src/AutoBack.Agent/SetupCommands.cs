using AutoBack.Connection;

namespace AutoBack.Agent;

internal static class SetupCommands
{
    public static async Task<int> ExecuteAsync(Dictionary<string, string?> options, ProtectedStore store)
    {
        var scope = new List<string>();
        if (options.ContainsKey("--machine")) scope.Add("--machine");
        if (options.GetValueOrDefault("--data-dir") is { } directory) { scope.Add("--data-dir"); scope.Add(directory); }
        if (options.ContainsKey("--replace"))
        {
            if (options.GetValueOrDefault("--invite") is { } invite) { scope.Add("--invite"); scope.Add(invite); }
            if (options.GetValueOrDefault("--name") is { } name) { scope.Add("--name"); scope.Add(name); }
            return await Program.Main(["re-pair", .. scope]);
        }
        var saved = store.Load<AgentCredentials>("agent.dat");
        if (saved is not null)
        {
            var state = "Saved pairing";
            try { Console.WriteLine("Checking the saved host connection..."); await new AgentClient().HeartbeatAsync(saved); state = "Connected to host"; }
            catch (AgentRejectedException) { state = "Host rejected this pairing. It may have been removed from the host."; }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            { state = "Could not verify the host connection. Check the host app, network and invitation address."; }
            var action = FolderSelection.ChooseSetupAction(saved, state);
            if (action == "cancel") return 2;
            if (action == "pair-again") return await Program.Main(["re-pair", .. scope]);
            if (action == "google-sign-in") return await Program.Main(["drive-connect", .. scope]);
        }
        if (saved is null)
        {
            var invitation = options.GetValueOrDefault("--invite") ?? FolderSelection.ChooseInvitation();
            if (invitation is null) return 2;
            var code = await Program.Main(["pair", "--invite", invitation, "--name", options.GetValueOrDefault("--name") ?? Environment.MachineName, .. scope]);
            if (code != 0) return code;
        }
        if (await Program.Main(["configure", .. scope]) is var configured && configured != 0) return configured;
        var plan = store.Load<AutoBack.Backup.BackupPlan>("backup-plan.dat")!;
        if (plan.Target == BackupTargets.GoogleDrive || (plan.Target == BackupTargets.LegacyBoth && FolderSelection.AskDriveSetup()))
        {
            using var http = AutoBack.Drive.DriveHttp.CreateClient();
            if (new AutoBack.Drive.GoogleAuthorization(store, http).Status().State != "Connected")
            { var code = await Program.Main(["drive-connect", .. scope]); if (code != 0) return code; }
        }
        Console.WriteLine("Client setup saved. Installed setup checks the Agent service account's access again before starting. Use Check access / Grant required access when changing folders.");
        Console.WriteLine("Use encryption-enable with a recovery export and retention-configure if desired. See docs/AUTOMATION-ROLLOUT.md.");
        return 0;
    }
}
