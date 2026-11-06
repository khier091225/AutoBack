using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using AutoBack.Agent;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Legacy;

// Native launcher avoids requiring Windows Management Framework/PowerShell upgrades on Windows 7.
internal static class InstalledSetup
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is not ("--folders-only" or "--pair-again" or "--reconnect-google"))) throw new ArgumentException("Unknown installed setup option.");
        var foldersOnly = args.Contains("--folders-only");
        var pairAgain = args.Contains("--pair-again");
        var reconnectGoogle = args.Contains("--reconnect-google");
        if (new[]{foldersOnly,pairAgain,reconnectGoogle}.Count(value=>value)>1) throw new ArgumentException("Choose one installed setup action.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                "installed-setup" + (foldersOnly ? " --folders-only" : pairAgain ? " --pair-again" : reconnectGoogle ? " --reconnect-google" : "")) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            try { Process.Start(start); return 0; }
            catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("Administrator access is required for installed client setup."); return 2; }
        }
        try
        {
            using var service = new ServiceController("AutoBackAgent");
            var wasRunning = service.Status == ServiceControllerStatus.Running;
            if (service.Status != ServiceControllerStatus.Stopped)
            { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            var result = await Program.Main([foldersOnly ? "configure" : pairAgain ? "re-pair" : reconnectGoogle ? "drive-connect" : "setup", "--machine"]);
            if (result != 0)
            {
                if (result == 2 && wasRunning) service.Start();
                Console.WriteLine("Setup cancelled or failed. Existing configuration retained."); return result;
            }
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoBack", "Agent");
            var store = new ProtectedStore(directory, true);
            var plan = store.Load<BackupPlan>("backup-plan.dat") ?? throw new InvalidOperationException("Choose backup folders first.");
            if (plan.Target == BackupTargets.GoogleDrive && await Program.Main(["drive-ensure", "--machine"]) != 0) return 1;
            if (await Program.Main(["access-check", "--machine"]) != 0)
            { Console.Error.WriteLine("Backup access is not ready. Open Choose AutoBack backup folders, use Grant required access and resolve all listed issues."); return 1; }
            Console.Write("Backup access check passed. Type START to enable automatic startup, or Enter to leave stopped: ");
            if (Console.ReadLine() == "START")
            {
                using var sc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
                    "config AutoBackAgent start= delayed-auto") { UseShellExecute = false, CreateNoWindow = true });
                sc!.WaitForExit(); if (sc.ExitCode != 0) throw new InvalidOperationException("Could not enable automatic service startup.");
                service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Console.WriteLine("Agent started. Check Online in the host dashboard, then test backup and restore.");
            }
            else Console.WriteLine("Agent service remains stopped.");
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        { Console.Error.WriteLine("Client service setup failed. Check installation, administrator access and service status."); return 1; }
        finally { Console.Write("Press Enter to close."); Console.ReadLine(); }
    }
}
