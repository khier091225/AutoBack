using System.IO;
using System.Text.Json;
using System.Windows;
using AutoBack.Connection;
using Microsoft.Win32;

namespace AutoBack;

public partial class AddComputerWindow : Window
{
    private readonly DashboardViewModel _dashboard;

    public AddComputerWindow(DashboardViewModel dashboard)
    {
        _dashboard = dashboard;
        InitializeComponent();
        HostInput.Text = Environment.MachineName;
        EndpointText.Text = $"Host: {dashboard.HostEndpoint}. Use its LAN IPv4 address if the computer name does not resolve. For a same-PC test, use 127.0.0.1.";
        Loaded += (_, _) => HostInput.Focus();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!Protocol.ValidHost(HostInput.Text.Trim())) { ResultText.Text = "Enter a host name or IP address without a URL scheme or port."; return; }
        var picker = new SaveFileDialog { FileName = "client.autoback-invite", DefaultExt = ".autoback-invite", Filter = "AutoBack pairing invitation|*.autoback-invite", Title = "Save single-use pairing invitation" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var invitation = _dashboard.CreateInvitation(HostInput.Text);
            File.WriteAllText(picker.FileName, JsonSerializer.Serialize(invitation, Protocol.Json));
            ResultText.Text = $"Invitation saved. Expires at {invitation.ExpiresUtc.ToLocalTime():hh:mm:ss tt}. Transfer this file directly to one client; delete it after use.";
            CommandsText.Text = $".\\AutoBack.Agent.exe pair --invite \".\\{Path.GetFileName(picker.FileName)}\"\n.\\AutoBack.Agent.exe run";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _dashboard.CancelInvitation();
            ResultText.Text = "The invitation could not be saved. Check the destination and host listener, then try again.";
        }
    }

    private void CancelInvitation_Click(object sender, RoutedEventArgs e)
    {
        _dashboard.CancelInvitation();
        ResultText.Text = "The outstanding invitation is cancelled. Previously exported invitation files can no longer pair a computer.";
    }
}
