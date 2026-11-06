using System.Drawing;
using System.Windows.Forms;

namespace AutoBack.Agent;

public sealed class PairingSetupForm : Form
{
    public string SelectedAction { get; private set; } = "cancel";
    public Button PairAgainButton { get; } = new() { Text = "Pair again with a host invitation", Name = "PairAgain", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    public Button FoldersButton { get; } = new() { Text = "Change backup folders / settings", Name = "ChangeFolders", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    public Button GoogleButton { get; } = new() { Text = "Connect / re-sign in to Google Drive", Name = "ReconnectGoogle", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    public PairingSetupForm(string name, string host, string status)
    {
        Text = "AutoBack client setup"; ClientSize = new Size(640, 370); MinimumSize = new Size(560, 390);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        layout.Controls.Add(new Label { Text = $"{name} → {host}\n{status}", Name = "PairingStatus", AutoSize = true, MaximumSize = new Size(590, 0), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) });
        layout.Controls.Add(PairAgainButton); layout.Controls.Add(FoldersButton); layout.Controls.Add(GoogleButton);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(0, 12, 0, 12),
            Text = "Removed this computer from the host? Create a fresh invitation there, then choose Pair again.\nYour folders, Google sign-in, encryption keys and backup files are kept. Previous jobs/uploads are archived and do not resume automatically under the new pairing." });
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel }; layout.Controls.Add(cancel); CancelButton = cancel;
        PairAgainButton.Click += (_, _) => Finish("pair-again"); FoldersButton.Click += (_, _) => Finish("folders"); GoogleButton.Click += (_, _) => Finish("google-sign-in"); Controls.Add(layout);
    }
    private void Finish(string action) { SelectedAction = action; DialogResult = DialogResult.OK; Close(); }
}
