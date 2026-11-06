using AutoBack.Backup;
using AutoBack.Connection;
using System.Drawing;
using System.Windows.Forms;

namespace AutoBack.Agent;

public sealed class BackupFoldersForm : Form
{
    public ListBox SourceFolders { get; } = new() { Name = "SourceFolders", Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true, IntegralHeight = false };
    public TextBox DestinationInput { get; } = new() { Name = "Destination", Dock = DockStyle.Fill };
    public Label Feedback { get; } = new() { Name = "Feedback", AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(740, 0), Margin = new Padding(0, 10, 0, 10) };
    public Button SaveButton { get; } = new() { Name = "SaveFolders", Text = "Save folders", AutoSize = true };
    public BackupPlan? SelectedPlan { get; private set; }
    public RadioButton GoogleDriveMode { get; } = new() { Name = "GoogleDriveMode", Text = "Google Drive (default)", AutoSize = true, Checked = true };
    public RadioButton LocalOnlyMode { get; } = new() { Name = "LocalOnlyMode", Text = "Local only", AutoSize = true };
    private readonly string _dataDirectory;

    public BackupFoldersForm(BackupPlan? existing = null, string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBack", "Agent");
        Text = "AutoBack — backup folders on " + Environment.MachineName;
        Font = new Font("Segoe UI", 10); Size = new Size(860, 660); MinimumSize = new Size(720, 550);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 8 };
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        var heading = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        heading.Controls.Add(new Label { Text = "Where should this computer's backups go?", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        var modes = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        modes.Controls.Add(GoogleDriveMode); modes.Controls.Add(LocalOnlyMode); heading.Controls.Add(modes);
        var modeHelp = new Label { AutoSize = true, MaximumSize = new Size(740, 0) };
        heading.Controls.Add(modeHelp); layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(new Label { Text = $"Selecting as {Environment.UserDomainName}\\{Environment.UserName}. Each source includes all subfolders.\nAdd up to 20 separate folders; removing a selection never deletes its files.", AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 1);
        layout.Controls.Add(SourceFolders, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 16) };
        var add = Button("Add folder…", () =>
        {
            using var picker = FolderSelection.CreateFolderPicker("Choose a source folder to INCLUDE in backup");
            picker.ShowNewFolderButton = false;
            if (picker.ShowDialog(this) == DialogResult.OK) AddFolder(picker.SelectedPath);
        });
        var remove = Button("Remove selected", RemoveSelectedFolders);
        actions.Controls.AddRange([add, remove,
            Button("My Documents", () => AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))),
            Button("My Desktop", () => AddFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))]);
        layout.Controls.Add(actions, 0, 3);
        var destinationLabel = new Label { Text = "Local backup destination (separate from every source)", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(destinationLabel, 0, 4);
        var destination = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.Controls.Add(DestinationInput, 0, 0);
        destination.Controls.Add(Button("Browse…", () =>
        {
            using var picker = FolderSelection.CreateFolderPicker("Choose a SEPARATE local destination for backup archives");
            if (picker.ShowDialog(this) == DialogResult.OK) DestinationInput.Text = picker.SelectedPath;
        }), 1, 0);
        layout.Controls.Add(destination, 0, 5); layout.Controls.Add(Feedback, 0, 6);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(cancel); footer.Controls.Add(SaveButton); layout.Controls.Add(footer, 0, 7);
        Controls.Add(layout); AcceptButton = SaveButton; CancelButton = cancel;
        if (existing is not null)
        {
            SourceFolders.Items.AddRange(existing.Sources.Cast<object>().ToArray()); DestinationInput.Text = existing.Destination;
            LocalOnlyMode.Checked = existing.Target == BackupTargets.LocalOnly;
        }
        void ApplyMode()
        {
            destinationLabel.Visible = destination.Visible = LocalOnlyMode.Checked;
            DestinationInput.Enabled = LocalOnlyMode.Checked;
            modeHelp.Text = LocalOnlyMode.Checked ? "Save a permanent backup on this PC or a local drive. Google Drive uploads are paused."
                : "Upload to your connected Google Drive. Temporary archives are managed automatically and cleared after verified upload.";
            Feedback.Text = ""; UpdateState();
        }
        GoogleDriveMode.CheckedChanged += (_, _) => ApplyMode(); LocalOnlyMode.CheckedChanged += (_, _) => ApplyMode();
        DestinationInput.TextChanged += (_, _) => { Feedback.Text = ""; UpdateState(); };
        SaveButton.Click += (_, _) =>
        {
            if (!TryBuildPlan(out var plan)) return;
            SelectedPlan = plan; DialogResult = DialogResult.OK; Close();
        };
        ApplyMode();
    }

    public bool AddFolder(string path)
    {
        try
        {
            if (SourceFolders.Items.Count >= 20) throw new ArgumentException("Choose up to 20 source folders.");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("That folder is unavailable for this Windows user. Use Add folder to choose its actual location.");
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose a full local source folder path.");
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException("The folder is missing or inaccessible.");
            BackupPlan.CheckNoReparse(normalized);
            if (SourceFolders.Items.Cast<string>().Any(s => BackupPlan.Contains(s, normalized) || BackupPlan.Contains(normalized, s)))
                throw new ArgumentException("That folder is already included, or overlaps a selected parent/subfolder.");
            SourceFolders.Items.Add(normalized); Feedback.Text = ""; UpdateState(); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = ex.Message; return false; }
    }

    public void RemoveSelectedFolders()
    {
        foreach (var path in SourceFolders.SelectedItems.Cast<string>().ToArray()) SourceFolders.Items.Remove(path);
        Feedback.Text = ""; UpdateState();
    }
    public bool TryBuildPlan(out BackupPlan? plan)
    {
        try { plan = BackupPlan.CreateForTarget(SourceFolders.Items.Cast<string>(), GoogleDriveMode.Checked ? BackupTargets.GoogleDrive : BackupTargets.LocalOnly,
            LocalOnlyMode.Checked ? DestinationInput.Text : null, _dataDirectory); Feedback.Text = ""; return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = ex.Message; plan = null; return false; }
    }
    private void UpdateState() => SaveButton.Enabled = SourceFolders.Items.Count > 0 && (GoogleDriveMode.Checked || !string.IsNullOrWhiteSpace(DestinationInput.Text));
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
}
