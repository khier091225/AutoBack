using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace AutoBack;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashboard;
    private bool _closedAfterShutdown;
    private bool _shutdownRequested;

    public MainWindow() : this(new DashboardViewModel()) { }

    public MainWindow(DashboardViewModel dashboard)
    {
        _dashboard = dashboard;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        DataContext = _dashboard;
        Loaded += async (_, _) => await _dashboard.InitializeAsync();
        Closing += ShutdownHost;
    }

    private async void ShutdownHost(object? sender, CancelEventArgs e)
    {
        if (_closedAfterShutdown) return;
        e.Cancel = true;
        if (_shutdownRequested) return;
        _shutdownRequested = true;
        IsEnabled = false;
        await _dashboard.DisposeAsync();
        _closedAfterShutdown = true;
        // Queue Close so a synchronously completed shutdown never re-enters the current Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) _dashboard.Page = page;
    }

    private void AddComputer_Click(object sender, RoutedEventArgs e)
    {
        if (!_dashboard.CanPair) return;
        new AddComputerWindow(_dashboard) { Owner = this }.ShowDialog();
    }

    private void RevokeComputer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Computer computer }) return;
        if (MessageBox.Show(this, $"Revoke pairing for {computer.Name}?\n\nIts agent will no longer be authorized. To reconnect, create a new invitation and pair the agent again.",
            "Revoke computer pairing", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _dashboard.Revoke(computer); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _dashboard.Notice = "Pairing could not be revoked. Check host storage access and try again."; }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Computer computer })
            new ComputerDetailsWindow(computer) { Owner = this }.ShowDialog();
    }

    private void OpenCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string link } && Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host == "drive.google.com" && uri.AbsolutePath.StartsWith("/file/d/", StringComparison.Ordinal))
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { _dashboard.Notice = "The browser could not be opened. The verified Google Drive link is retained in job history."; }
        }
    }

    private void RestoreInstructions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BackupHistoryEntry { CanRestore: true } backup })
            new RestoreInstructionsWindow(backup) { Owner = this }.ShowDialog();
    }
}
