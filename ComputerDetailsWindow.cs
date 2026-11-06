using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoBack;

public sealed class ComputerDetailsWindow : Window
{
    public ComputerDetailsWindow(Computer computer)
    {
        Title = "Backup folders · " + computer.Name; Width = 820; Height = 680; MinWidth = 600; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = computer.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = $"{computer.Connection} · {computer.MachineName} · Agent {computer.AgentVersion}\nLast heartbeat: {computer.LastSeenText}", Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = $"Included source folders ({computer.SourceFolders.Count})", FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBox { Name = "SourceFolderList", Text = computer.SourceFoldersText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 100, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8) });
        content.Children.Add(new TextBlock { Text = "Each selected folder includes all subfolders. These paths are on the client PC.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Backup destination · " + computer.DestinationMode, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) });
        content.Children.Add(new TextBox { Text = computer.Destination, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 });
        content.Children.Add(new TextBlock { Text = "To change this client's folders", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) });
        content.Children.Add(new TextBlock { Text = "On this client, stop the agent and run configure. Choose Google Drive (default) or Local only, then select source folders and Save. A permanent destination folder is needed only for Local only. In service mode use --machine as administrator and grant LocalService read access to the sources.", TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        content.Children.Add(new TextBox { Text = ".\\AutoBack.Agent.exe configure\n# Installed Windows service:\n.\\AutoBack.Agent.exe configure --machine", IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 10, 0, 16) });
        content.Children.Add(new TextBlock { Text = $"Last confirmed backup: {computer.LastBackup}\n{computer.BackupDetail}\n\nGoogle Drive: {computer.CloudStatus}\n{computer.CloudDetail}\n\n{computer.ProtectionText}\n{computer.MaintenanceText}", TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        content.Children.Add(new TextBlock { Text = $"Agent ID: {computer.Id}\nAddress: {computer.Address}", FontSize = 11, Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = layout;
    }
}
