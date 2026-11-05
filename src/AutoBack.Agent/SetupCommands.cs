using AutoBack.Connection;

namespace AutoBack.Agent;

internal static class SetupCommands
{
    public static async Task<int> ExecuteAsync(Dictionary<string, string?> options, ProtectedStore store)
    {
        var scope = new List<string>();
        if (options.ContainsKey("--machine")) scope.Add("--machine");
        if (options.GetValueOrDefault("--data-dir") is { } directory) { scope.Add("--data-dir"); scope.Add(directory); }
        if (store.Load<AgentCredentials>("agent.dat") is null)
        {
            var invitation = options.GetValueOrDefault("--invite") ?? FolderSelection.ChooseInvitation();
            if (invitation is null) return 2;
            var code = await Program.Main(["pair", "--invite", invitation, "--name", options.GetValueOrDefault("--name") ?? Environment.MachineName, .. scope]);
            if (code != 0) return code;
        }
        if (await Program.Main(["configure", .. scope]) is var configured && configured != 0) return configured;
        if (FolderSelection.AskDriveSetup())
        {
            var code = await Program.Main(["drive-connect", .. scope]); if (code != 0) return code;
        }
        Console.WriteLine("Client setup saved. In service mode, grant LocalService read access to the source and Modify access to the destination before starting the service.\nUse encryption-enable with a recovery export and retention-configure if desired. See docs/AUTOMATION-ROLLOUT.md.");
        return 0;
    }
}
