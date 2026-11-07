using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoBack;

public sealed class RestoreInstructionsWindow : Window
{
    public RestoreInstructionsWindow(BackupHistoryEntry backup)
    {
        Title = "Restore backup · " + backup.Computer;
        Width = 780; Height = 600; MinWidth = 580; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new DockPanel { Margin = new Thickness(24) };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); content.Children.Add(close);
        content.Children.Add(new TextBox { Text = BuildInstructions(backup), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 13 });
        Content = content;
    }

    public static string BuildInstructions(BackupHistoryEntry backup)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var instructions = $"On client {backup.Computer}, open PowerShell in its agent folder.\n\n1. Stop the running agent with Ctrl+C, or stop its AutoBackAgent Windows service. Use the same Windows account, --machine and --data-dir options as the agent.\n\n2. Run ONE of the restore commands below. Choose a parent folder when prompted; AutoBack creates a new recovery folder. An existing destination is never overwritten.\n\n";
        if (backup.HasCloudLink) instructions += (backup.FolderBased ? "Current Google Drive folder (includes retained files; older backup dates are not separate restore points):\n" : "Google Drive (use the account connected on this client):\n") + ".\\AutoBack.Agent.exe restore --drive-file " + Quote(backup.CloudLink) + "\n\n";
        if (backup.ArchivePath.Length > 0 && !backup.LocalPruned) instructions += "Local backup (this path is on the source client):\n.\\AutoBack.Agent.exe restore --archive " + Quote(backup.ArchivePath) + "\n\n";
        return instructions + "For an encrypted .abk from another PC, first run recovery-import --recovery-file <export> and enter its passphrase. Keys stay on the client.\n\nFor an explicit destination append --destination 'D:\\Recovery\\NewFolder'. It must be a new local folder outside the original source.\n\n3. Wait for RESTORE VERIFIED. Recovered content is in <new folder>\\files. The recovery folder also contains manifest.json and restore-report.json.\n\n4. To recheck later:\n.\\AutoBack.Agent.exe verify-restore --destination 'D:\\Recovery\\NewFolder'\n\n5. Restart the agent:\n.\\AutoBack.Agent.exe run\n\nThe saved result appears on the host Restore page after reconnection. See docs/RESTORE.md.\n";
    }
}
