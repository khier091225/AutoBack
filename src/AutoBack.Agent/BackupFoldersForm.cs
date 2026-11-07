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
    private readonly FolderAccess _access;
    private readonly bool _serviceMode;
    private readonly ProtectedStore? _store;
    private CancellationTokenSource? _operation;
    private string? _checkedPlan;
    private bool _busy, _closeAfterCancel;
    private readonly List<Control> _sourceActions = [];
    public Button CheckAccessButton { get; } = new() { Name = "CheckAccess", Text = "Check access", AutoSize = true };
    public Button GrantAccessButton { get; } = new() { Name = "GrantAccess", Text = "Grant required access", AutoSize = true };
    public TextBox AccessDetails { get; } = new() { Name = "AccessDetails", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 96, Dock = DockStyle.Fill };
    public FolderAccessResult? AccessResult { get; private set; }

    public BackupFoldersForm(BackupPlan? existing = null, string? dataDirectory = null, bool serviceMode = false, ProtectedStore? store = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBack", "Agent");
        _access = new(serviceMode, store); _serviceMode = serviceMode; _store = store;
        Text = "AutoBack — backup folders on " + Environment.MachineName;
        Font = new Font("Segoe UI", 10); Size = new Size(860, Math.Min(810, Screen.PrimaryScreen!.WorkingArea.Height - 30)); MinimumSize = new Size(720, 600);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
        for (var i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
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
        _sourceActions.AddRange(actions.Controls.Cast<Control>());
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
        _sourceActions.AddRange(destination.Controls.OfType<Button>());
        layout.Controls.Add(destination, 0, 5); layout.Controls.Add(Feedback, 0, 6);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var accessButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 7, 0, 6) };
        var accessActions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        accessActions.Controls.Add(CheckAccessButton); accessActions.Controls.Add(GrantAccessButton); accessButtons.Controls.Add(accessActions);
        accessButtons.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 4, 0, 4), Text = serviceMode
            ? "Grant adds Read access on sources, limited access on parent folders, and Modify on a local backup destination. Permission changes are kept if you cancel."
            : "This checks your Windows account. Installed service setup checks the Agent service account." });
        GrantAccessButton.Visible = serviceMode;
        layout.Controls.Add(accessButtons, 0, 7); layout.Controls.Add(AccessDetails, 0, 8);
        footer.Controls.Add(cancel); footer.Controls.Add(SaveButton); layout.Controls.Add(footer, 0, 9);
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
                : "One PC folder in Google Drive. Upload new/changed files only; keep files removed from this PC. Temporary local snapshots clear after verification.";
            Feedback.Text = ""; InvalidateAccess(); UpdateState();
        }
        GoogleDriveMode.CheckedChanged += (_, _) => ApplyMode(); LocalOnlyMode.CheckedChanged += (_, _) => ApplyMode();
        DestinationInput.TextChanged += (_, _) => { Feedback.Text = ""; InvalidateAccess(); UpdateState(); };
        CheckAccessButton.Click += async (_, _) => await RunAccessAsync(false);
        GrantAccessButton.Click += async (_, _) => await RunAccessAsync(true);
        SaveButton.Click += async (_, _) =>
        {
            if (!TryBuildPlan(out var plan)) return;
            if (_serviceMode && (_checkedPlan != plan!.PlanId || AccessResult is null || DateTimeOffset.UtcNow - AccessResult.CheckedUtc > TimeSpan.FromMinutes(1)))
            {
                await RunAccessAsync(false);
                if (IsDisposed || _closeAfterCancel || !TryBuildPlan(out plan) || _checkedPlan != plan!.PlanId) return;
            }
            SelectedPlan = plan; DialogResult = DialogResult.OK; Close();
        };
        ApplyMode();
    }

    public bool AddFolder(string path)
    {
        if (_busy) return false;
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
            SourceFolders.Items.Add(normalized); Feedback.Text = ""; InvalidateAccess(); UpdateState(); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = ex.Message; return false; }
    }

    public void RemoveSelectedFolders()
    {
        if (_busy) return;
        foreach (var path in SourceFolders.SelectedItems.Cast<string>().ToArray()) SourceFolders.Items.Remove(path);
        Feedback.Text = ""; InvalidateAccess(); UpdateState();
    }
    public bool TryBuildPlan(out BackupPlan? plan)
    {
        try { plan = BackupPlan.CreateForTarget(SourceFolders.Items.Cast<string>(), GoogleDriveMode.Checked ? BackupTargets.GoogleDrive : BackupTargets.LocalOnly,
            LocalOnlyMode.Checked ? DestinationInput.Text : null, _dataDirectory); Feedback.Text = ""; return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = ex.Message; plan = null; return false; }
    }
    public async Task RunAccessAsync(bool grant)
    {
        if (_busy || !TryBuildPlan(out var plan)) return;
        // The folder picker uses ShowDialog on its own STA thread, without Application.Run.
        // Capture a WinForms context explicitly so progress/results and Save stay on that UI thread.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _checkedPlan = null; AccessResult = null; _busy = true;
        _operation = new(); UpdateState(); AccessDetails.Text = grant ? "Granting the required folder permissions, then checking access..." : "Checking the backup account's access...";
        var operation = _operation;
        var progress = new Progress<string>(message => { if (!IsDisposed && _busy && ReferenceEquals(_operation, operation) && AccessResult is null) AccessDetails.Text = message; });
        try
        {
            var token = _operation.Token;
            var result = await Task.Run(() => grant ? _access.GrantAndCheck(plan!, token, message => ((IProgress<string>)progress).Report(message))
                : _access.Check(plan!, token, message => ((IProgress<string>)progress).Report(message)), token);
            AccessResult = result;
            _store?.Save("folder-access.dat", result);
            if (result.Ready) _checkedPlan = result.PlanId;
            AccessDetails.Text = result.Summary + Environment.NewLine + string.Join(Environment.NewLine, result.Issues.Select(i => i.Path + ": " + i.Reason)) +
                (result.AuditFile is null ? "" : Environment.NewLine + "Permission changes recorded: " + result.AuditFile) +
                Environment.NewLine + "Access can change later. Locked, missing or changed files still stop a backup; files are never silently skipped.";
            Feedback.Text = result.Ready ? "Access checked. You can save these folders." : "Resolve the listed issues, then Check access again. Settings have not been saved.";
            Feedback.ForeColor = result.Ready ? Color.DarkGreen : Color.Firebrick;
        }
        catch (OperationCanceledException) { AccessDetails.Text = "Check cancelled. Any permissions already granted are retained and recorded; folder settings have not been saved."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { AccessDetails.Text = ex.Message; Feedback.Text = "Access could not be verified. Settings have not been saved."; }
        finally
        {
            _busy = false; _operation.Dispose(); _operation = null;
            if (!IsDisposed) { UpdateState(); if (_closeAfterCancel) { DialogResult = DialogResult.Cancel; Close(); } }
        }
    }
    private void InvalidateAccess()
    {
        _checkedPlan = null; AccessResult = null; Feedback.ForeColor = Color.Firebrick;
        AccessDetails.Text = _serviceMode ? "Agent service access has not been checked for this selection. Use Check access, or Grant required access to prepare the folders."
            : "Check access tests the current Windows account. Installed service setup checks the Agent service account.";
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; _closeAfterCancel = true; _operation?.Cancel(); }
        base.OnFormClosing(e);
    }
    private void UpdateState()
    {
        var valid = SourceFolders.Items.Count > 0 && (GoogleDriveMode.Checked || !string.IsNullOrWhiteSpace(DestinationInput.Text));
        CheckAccessButton.Enabled = !_busy && valid; GrantAccessButton.Enabled = !_busy && valid && _serviceMode;
        SaveButton.Enabled = !_busy && valid && (!_serviceMode || AccessResult is null || AccessResult.Ready);
        SourceFolders.Enabled = GoogleDriveMode.Enabled = LocalOnlyMode.Enabled = !_busy;
        DestinationInput.Enabled = !_busy && LocalOnlyMode.Checked;
        foreach (var control in _sourceActions) control.Enabled = !_busy;
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
}
