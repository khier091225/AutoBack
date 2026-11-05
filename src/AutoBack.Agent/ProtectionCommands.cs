using System.Globalization;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Agent;

internal static class ProtectionCommands
{
    public static int Execute(string command, Dictionary<string, string?> options, ProtectedStore store)
    {
        if (command == "protection-status")
        {
            var vault = EncryptionVault.Load(store); var retention = RetentionPolicy.Load(store);
            Console.WriteLine($"New backup encryption: {(vault.Enabled ? "AES-256-GCM" : "Disabled")}\nRecovery keys available: {vault.Keys.Count}\nLocal versions to keep: {retention.KeepLocal} (0 = disabled)\nCloud versions to keep: {retention.KeepCloud} (0 = disabled)");
            var last = store.Load<AgentProtectionSummary>("protection-status.dat");
            if (last is not null) Console.WriteLine($"Last retention check: {last.LastRunUtc:O}\n{last.State}: {last.Detail}");
            return 0;
        }
        if (command == "retention-configure")
        {
            var old = RetentionPolicy.Load(store);
            int Count(string option, int fallback) => options.TryGetValue(option, out var value) ? int.Parse(value!, CultureInfo.InvariantCulture) : fallback;
            if (!options.ContainsKey("--keep-local") && !options.ContainsKey("--keep-cloud")) throw new ArgumentException("Specify --keep-local and/or --keep-cloud (0 disables that destination).");
            var policy = new RetentionPolicy(Count("--keep-local", old.KeepLocal), Count("--keep-cloud", old.KeepCloud)); policy.Validate();
            store.Save("retention.dat", policy);
            Console.WriteLine($"Retention saved: local={policy.KeepLocal}, cloud={policy.KeepCloud} (0=disabled).\nOn authorized idle runs, older managed local archives can be deleted and older confirmed cloud files moved to Drive Trash.\nPending uploads are protected. Newest copies must verify first. Restart the agent to apply. See docs/AUTOMATION-ROLLOUT.md.");
            return 0;
        }
        if (command == "encryption-disable")
        {
            EncryptionVault.Disable(store);
            Console.WriteLine("Encryption disabled for NEW backups. Existing encrypted archives and all recovery keys are retained."); return 0;
        }
        var file = options.GetValueOrDefault("--recovery-file") ?? throw new ArgumentException("Provide --recovery-file <path>. Keep this export and its passphrase separately from the client.");
        var password = ReadPassword(options.ContainsKey("--password-stdin"), command != "recovery-import");
        if (command == "encryption-enable") EncryptionVault.Enable(store, file, password);
        else if (command == "recovery-export") EncryptionVault.Export(EncryptionVault.Load(store), file, password);
        else if (command == "recovery-import") EncryptionVault.Import(store, file, password);
        else throw new ArgumentException("Unknown protection command.");
        Console.WriteLine(command == "encryption-enable" ? "Encryption enabled for new backups. The password-protected recovery export was decrypted and checked before activation.\nKeep a separate copy of the export and passphrase; import it on another PC for recovery. Existing ZIP backups are unchanged."
            : command == "recovery-import" ? "Recovery keys imported for restore. New-backup encryption settings and pairing are unchanged." : "Password-protected recovery export created and verified.");
        return 0;
    }

    private static string ReadPassword(bool fromStdin, bool confirm)
    {
        if (fromStdin)
        {
            if (!Console.IsInputRedirected) throw new ArgumentException("--password-stdin requires redirected standard input.");
            return Console.ReadLine() ?? throw new ArgumentException("No recovery passphrase received on standard input.");
        }
        if (Console.IsInputRedirected) throw new ArgumentException("Use the interactive passphrase prompt, or explicitly opt into --password-stdin. Never put a passphrase in command-line arguments.");
        string Read(string prompt)
        {
            Console.Write(prompt); var buffer = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(buffer.ToArray()); }
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
                if (key.Key == ConsoleKey.Backspace) { if (buffer.Count > 0) buffer.RemoveAt(buffer.Count - 1); }
                else if (!char.IsControl(key.KeyChar) && buffer.Count < 1024) buffer.Add(key.KeyChar);
            }
        }
        var password = Read("Recovery passphrase (hidden): ");
        if (confirm && Read("Confirm recovery passphrase (hidden): ") != password) throw new ArgumentException("The recovery passphrases do not match.");
        return password;
    }
}
