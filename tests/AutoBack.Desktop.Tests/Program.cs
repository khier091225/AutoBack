using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AutoBack;
using AutoBack.Connection;
using AutoBack.Backup;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-desktop-tests", Guid.NewGuid().ToString("N")));
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
        if (repo is null) throw new InvalidOperationException("Run within the repository build output.");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load the actual shared styles without triggering App's default StartupUri and production data directory.
        var markup = XDocument.Load(Path.Combine(repo.FullName, "App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            markup.Root!.Element(ns + "Application.Resources")!.Nodes());
        application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var viewModel = new DashboardViewModel(folder, port: 0);
        var window = new MainWindow(viewModel) { Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        try
        {
            window.Show();
            Until(() => viewModel.CanPair, TimeSpan.FromSeconds(15));
            Check(viewModel.TotalCount == 0, "live workspace starts without sample computers");
            Check(!viewModel.BackupSelectedCommand.CanExecute(null), "backup command stays disabled");
            Capture("dashboard-empty.png");
            var invitation = viewModel.CreateInvitation("127.0.0.1");
            var identity = AgentClient.CreateCredentials(invitation, "QA-LocalAgent");
            var agent = new AgentClient();
            Complete(agent.PairAsync(invitation, identity));
            Until(() => viewModel.TotalCount == 1, TimeSpan.FromSeconds(4));
            Check(viewModel.OnlineCount == 0, "newly paired device waits for its first heartbeat");
            Complete(agent.HeartbeatAsync(identity));
            Until(() => viewModel.OnlineCount == 1, TimeSpan.FromSeconds(4));
            Check(viewModel.Computers[0].LastSeenUtc.HasValue, "real last heartbeat shown in dashboard");
            Check(!Find<Button>(window).Single(b => b.GetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty)?.ToString() == "BackupSelected").IsEnabled,
                "backup button stays disabled until client folders are configured");
            var source = Path.Combine(folder, "qa-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "example.txt"), "Actual local backup from the dashboard test.");
            var plan = BackupPlan.Create(source, Path.Combine(folder, "qa-output"));
            var localStore = new ProtectedStore(Path.Combine(folder, "qa-agent"));
            localStore.Save("backup-plan.dat", plan);
            var coordinator = new BackupCoordinator(localStore, identity.AgentId);
            try
            {
                Complete(agent.PollAsync(identity, plan.Summary, null));
                viewModel.Computers[0].Selected = true;
                Until(() => viewModel.BackupSelectedCommand.CanExecute(null), TimeSpan.FromSeconds(4));
                var backupButton = Find<Button>(window).Single(b => b.GetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty)?.ToString() == "BackupSelected");
                Check(backupButton.IsEnabled, "configured client enables the real backup action");
                backupButton.Command.Execute(null);
                Check(viewModel.ActiveCount == 1 && !viewModel.BackupSelectedCommand.CanExecute(null), "dashboard queues a real job and blocks duplicate requests");
                Capture("dashboard-backup-queued.png");
                var poll = agent.PollAsync(identity, plan.Summary, null);
                Complete(poll);
                coordinator.Handle(poll.Result.Command);
                Until(() => coordinator.NextReport()?.State == "Completed", TimeSpan.FromSeconds(10));
                var result = agent.PollAsync(identity, plan.Summary, coordinator.NextReport());
                Complete(result);
                coordinator.Acknowledge(result.Result.AcknowledgedJobId, result.Result.AcknowledgedSequence);
                Until(() => viewModel.Computers[0].Status == "Local complete", TimeSpan.FromSeconds(4));
                Check(viewModel.Computers[0].LastBackup != "Never" && viewModel.ActiveCount == 0, "successful archive updates last backup and clears the queue");
                Check(File.Exists(viewModel.Computers[0].LatestJob!.Report.ArchivePath), "dashboard result points to an actual verified ZIP");
                var cloudJobId = viewModel.Computers[0].LatestJob!.Command.JobId;
                Complete(agent.PollAsync(identity, plan.Summary, null, cloudConfiguration: new("Connected", "qa@example.invalid", "Test authorization status."),
                    cloudReport: new(cloudJobId, 1, "Uploading", 45, 45, 100, "Test cloud progress.")));
                Until(() => viewModel.Computers[0].CloudStatus == "Uploading", TimeSpan.FromSeconds(4));
                Check(viewModel.Computers[0].Status == "Local complete" && viewModel.Computers[0].CloudPercent == 45, "dashboard separates local completion from cloud progress");
                Capture("dashboard-cloud-uploading.png");
                Complete(agent.PollAsync(identity, plan.Summary, null, cloudConfiguration: new("Needs sign-in", "qa@example.invalid", "Reconnect Google."),
                    cloudReport: new(cloudJobId, 2, "Needs sign-in", 45, 45, 100, "Test authorization expired.")));
                Until(() => viewModel.Computers[0].CloudStatus == "Needs sign-in", TimeSpan.FromSeconds(4));
                Check(viewModel.Computers[0].Status == "Local complete", "cloud authorization error leaves local success intact");
                Complete(agent.PollAsync(identity, plan.Summary, null, cloudConfiguration: new("Connected", "qa@example.invalid", "Test authorization status."),
                    cloudReport: new(cloudJobId, 3, "Completed", 100, 100, 100, "Test verified cloud result.", "qa-drive-file", "https://drive.google.com/file/d/qa-drive-file/view")));
                Until(() => viewModel.Computers[0].CloudStatus == "Cloud complete", TimeSpan.FromSeconds(4));
                Check(viewModel.BackupHistory.Any(h => h.HasCloudLink && h.CloudState == "Completed"), "history retains an independently confirmed cloud result and link");
                Capture("dashboard-preview.png");
                var recovery = new RestoreEngine().RestoreAsync(viewModel.Computers[0].LatestJob!.Report.ArchivePath!, Path.Combine(folder, "qa-recovered"), new("Local", "QA actual local backup"));
                Complete(recovery);
                var restored = recovery.Result;
                var recoverySummary = new RestoreSummary(restored.OperationId, "Completed", "Local", restored.Destination, 100, restored.Files, restored.Bytes,
                    "Restored file set read back and SHA-256 verified.", restored.VerifiedUtc, restored.BackupJobId);
                Complete(agent.PollAsync(identity, plan.Summary, null, latestRestore: recoverySummary));
                Until(() => viewModel.Computers[0].RestoreStatus == "Verified", TimeSpan.FromSeconds(4));
                Find<RadioButton>(window).Single(b => b.Tag?.ToString() == "Restore").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump();
                Check(viewModel.IsRestore && viewModel.Computers[0].RestoreDestination == restored.Destination, "Restore navigation displays a real completed recovery and destination");
                Check(viewModel.Computers[0].Status == "Local complete" && viewModel.BackupHistory.Any(h => h.CanRestore), "restore verification leaves successful local/cloud history available");
                var restoreButton = Find<Button>(window).First(b => b.GetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty)?.ToString() == "RestoreInstructions");
                Check(restoreButton.IsEnabled, "completed backup enables client restore instructions");
                var instructions = RestoreInstructionsWindow.BuildInstructions(viewModel.BackupHistory.First(h => h.CanRestore));
                Check(instructions.Contains("--drive-file 'https://drive.google.com/file/d/qa-drive-file/view'") && instructions.Contains("--archive '") && instructions.Contains("verify-restore"), "instructions include the selected local and cloud backup and verification command");
                var quoted = RestoreInstructionsWindow.BuildInstructions(new("PC", "Completed", "now", "", @"D:\O'Brien\backup.zip"));
                Check(quoted.Contains("O''Brien"), "PowerShell instructions quote apostrophes safely");
                var instructionsWindow = new RestoreInstructionsWindow(viewModel.BackupHistory.First(h => h.CanRestore)) { Owner = window, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
                instructionsWindow.Show(); Pump();
                Check(Find<TextBox>(instructionsWindow).Single().IsReadOnly, "restore instructions remain selectable for copying");
                instructionsWindow.Close();
                Capture("restore-preview.png");
                Complete(agent.PollAsync(identity, plan.Summary, null, latestRestore: recoverySummary with { OperationId = Guid.NewGuid(), State = "Failed", Detail = "Modified recovered file detected." }));
                Until(() => viewModel.Computers[0].RestoreStatus == "Failed", TimeSpan.FromSeconds(4));
                Check(viewModel.Computers[0].Status == "Local complete", "failed restore verification is separate from the original backup result");
                viewModel.Page = "Overview";
                viewModel.BackupSelectedCommand.Execute(null);
                Check(viewModel.CancelCommand.CanExecute(null), "pending real job can be cancelled");
                viewModel.CancelCommand.Execute(null);
                var cancelled = agent.PollAsync(identity, plan.Summary, null);
                Complete(cancelled);
                coordinator.Handle(cancelled.Result.Command);
                var confirmation = agent.PollAsync(identity, plan.Summary, coordinator.NextReport());
                Complete(confirmation);
                coordinator.Acknowledge(confirmation.Result.AcknowledgedJobId, confirmation.Result.AcknowledgedSequence);
                Until(() => viewModel.Computers[0].Status == "Cancelled", TimeSpan.FromSeconds(4));
                Check(viewModel.BackupHistory.Any(h => h.State == "Completed") && viewModel.BackupHistory.Any(h => h.State == "Cancelled"), "history shows confirmed completion and cancellation");
            }
            finally { Complete(coordinator.DisposeAsync().AsTask()); }
            window.Width = 1220;
            window.Height = 720;
            Pump();
            Capture("dashboard-compact.png");
            window.Width = 1440;
            window.Height = 1000;
            viewModel.Search = "not-a-machine";
            Check(viewModel.VisibleCount == 0 && viewModel.EmptyTableVisibility == Visibility.Visible, "empty search state works with live inventory");
            viewModel.Search = "qa-local";
            Check(viewModel.VisibleCount == 1, "search finds a real paired computer");
            viewModel.Search = "";
            viewModel.OpenSettingsCommand.Execute(null);
            Pump();
            Check(viewModel.IsSettings && Find<RadioButton>(window).Single(b => b.Tag?.ToString() == "Settings").IsChecked == true, "settings navigation remains synchronized");
            Capture("settings-preview.png");
            var pairingWindow = new AddComputerWindow(viewModel) { Owner = window, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            pairingWindow.Show();
            Pump();
            Check(((TextBox)pairingWindow.FindName("HostInput")).Text == Environment.MachineName, "pairing dialog defaults to the real host name");
            pairingWindow.Close();
            viewModel.Page = "Overview";
            Until(() => viewModel.OnlineCount == 0, Protocol.OfflineAfter + TimeSpan.FromSeconds(4));
            Check(viewModel.Computers[0].LastSeenUtc.HasValue && viewModel.Computers[0].Connection == "Offline", "lost heartbeat changes dashboard to offline and preserves last seen");
            Complete(agent.PollAsync(identity, plan.Summary, null));
            Until(() => viewModel.OnlineCount == 1, TimeSpan.FromSeconds(4));
            Find<RadioButton>(window).Single(b => b.Tag?.ToString() == "Activity").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Check(viewModel.IsActivity && viewModel.Activities.Any(a => a.Title == "Connection lost"), "activity records actual connection changes");
            Capture("activity-preview.png");
            viewModel.Revoke(viewModel.Computers[0]);
            Check(viewModel.TotalCount == 0, "revoked device disappears from the dashboard");
            viewModel.Page = "Overview";
            Pump();
            PresentationTraceSources.DataBindingSource.Flush();
            Check(string.IsNullOrWhiteSpace(bindingErrors.ToString()), "no runtime binding warnings: " + bindingErrors);
            Console.WriteLine("ALL DESKTOP BACKUP AND CONNECTION CHECKS PASSED");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            Complete(viewModel.DisposeAsync().AsTask());
            window.Close();
            Until(() => !window.IsVisible, TimeSpan.FromSeconds(10));
            application.Shutdown();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-desktop-tests"));
            if (Directory.Exists(folder) && Path.GetDirectoryName(folder) == parent && Guid.TryParseExact(Path.GetFileName(folder), "N", out _))
                Directory.Delete(folder, recursive: true);
        }

        void Capture(string name)
        {
            Pump();
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var outputDirectory = Path.Combine(repo.FullName, "docs");
            Directory.CreateDirectory(outputDirectory);
            using var output = File.Create(Path.Combine(outputDirectory, name));
            encoder.Save(output);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS " + description);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Until(Func<bool> condition, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > timeout) throw new TimeoutException("Dashboard did not reach the expected state.");
            Pump();
            Thread.Sleep(20);
        }
    }

    private static void Complete(Task task)
    {
        Until(() => task.IsCompleted, TimeSpan.FromSeconds(20));
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
}
