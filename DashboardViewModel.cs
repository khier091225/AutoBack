using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AutoBack.Connection;

namespace AutoBack;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public sealed class Computer : ObservableObject
{
    private bool _selected;
    private bool _online;
    private DateTimeOffset? _lastSeenUtc;
    private AgentSnapshot? _snapshot;
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string MachineName { get; init; }
    public required string AgentVersion { get; set; }
    public string Address { get; private set; } = "";
    public string Team => $"{MachineName} · agent {AgentVersion}";
    public IReadOnlyList<string> SourceFolders => _snapshot?.Configuration is { } config ? config.Sources ?? (IReadOnlyList<string>)[config.Source] : [];
    public string Folder => SourceFolders.Count == 1 ? SourceFolders[0] : SourceFolders.Count > 1 ? $"{SourceFolders.Count} selected folders" : "Not configured";
    public string SourceFolderSummary => SourceFolders.Count == 0 ? "Choose backup folders on this client" : $"{SourceFolders.Count} backup folder{(SourceFolders.Count == 1 ? "" : "s")} · {DestinationMode}";
    public string SourceFoldersText => SourceFolders.Count == 0 ? "Not configured" : string.Join(Environment.NewLine, SourceFolders);
    public string Destination => _snapshot?.Configuration?.Destination ?? "Not configured";
    public string Target => _snapshot?.Configuration?.Target ?? BackupTargets.LegacyBoth;
    public string DestinationMode => BackupTargets.Label(Target);
    public bool Configured => _snapshot?.Configuration is not null;
    public HostBackupJob? LatestJob => _snapshot?.LatestJob;
    public bool IsBusy => LatestJob is not null && !BackupStates.Terminal(LatestJob.Report.State);
    public bool CanBackup => Configured && !IsBusy && (Target != BackupTargets.GoogleDrive || DriveConnected);
    public string Size => LatestJob is not null && (LatestJob.Report.TotalBytes > 0 || LatestJob.Report.State == "Completed") ? FormatBytes(LatestJob.Report.TotalBytes) : "—";
    public string Status => IsBusy && LatestJob!.CancelRequested ? Online ? "Cancelling" : "Cancel pending"
        : IsBusy && !Online ? LatestJob!.Report.State == "Queued" ? "Waiting for PC" : "Connection lost"
        : LatestJob?.Report.State == "Completed" ? LatestJob.Report.Target == BackupTargets.GoogleDrive
            ? LatestJob.Report.TemporaryArchiveRemoved ? "Temp cleaned" : "Prepared for upload" : "Local complete"
        : LatestJob?.Report.State ?? (Configured ? "Ready" : "Not configured");
    public string StatusColor => Status is "Failed" or "Connection lost" ? "#B16C13" : Status is "Ready" or "Local complete" ? "#14806D" : "#6F7890";
    public string StatusBackground => Status is "Failed" or "Connection lost" ? "#FFF5E4" : Status is "Ready" or "Local complete" ? "#EAF6F1" : "#F0F2F7";
    public string LastBackup => _snapshot?.LastSuccessfulJob?.UpdatedUtc.ToLocalTime().ToString("MMM dd, hh:mm tt") ?? "Never";
    public string LastSeenText => _lastSeenUtc?.ToLocalTime().ToString("MMM dd, hh:mm:ss tt") ?? "Awaiting heartbeat";
    public DateTimeOffset? LastSeenUtc => _lastSeenUtc;
    public bool Online => _online;
    public string Connection => Online ? "Online" : _lastSeenUtc.HasValue ? "Offline" : "Waiting";
    public string ConnectionColor => Online ? "#168472" : "#8792A2";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool IsUploading => IsBusy;
    public int Progress => LatestJob?.Report.Percent ?? 0;
    public string ProgressText => IsBusy && !Online ? $"{Status} · last reported {Progress}%" : $"{Status} · {Progress}%";
    public string BackupDetail => LatestJob?.Report.TemporaryArchiveRemoved == true ? "Google Drive upload verified; temporary archive automatically cleaned."
        : LatestJob?.Report.Detail ?? (Configured ? "Ready to back up to the selected destination." : "Choose source folders and Google Drive or Local only on this client.");
    public string CloudAccount => _snapshot?.CloudConfiguration?.Account ?? "";
    public CloudReport? LatestCloud => Target == BackupTargets.GoogleDrive ? LatestJob?.Cloud : _snapshot?.LatestCloudJob?.Cloud;
    public string CloudStatus => Target == BackupTargets.LocalOnly ? "Local only" : LatestCloud?.State == "Completed" ? "Cloud complete"
        : _snapshot?.CloudConfiguration?.State == "Needs sign-in" ? "Needs sign-in"
        : LatestCloud?.State == "Uploading" && !Online ? "Connection lost"
        : LatestCloud?.State ?? (_snapshot?.CloudConfiguration?.State == "Connected" ? "Ready" : "Not connected");
    public string CloudColor => CloudStatus is "Failed" or "Needs sign-in" or "Connection lost" ? "#B16C13" : CloudStatus == "Cloud complete" ? "#14806D" : "#397DB7";
    public string CloudBackground => CloudStatus is "Failed" or "Needs sign-in" or "Connection lost" ? "#FFF5E4" : CloudStatus == "Cloud complete" ? "#EAF6F1" : "#EDF5FF";
    public int CloudPercent => LatestCloud?.Percent ?? 0;
    public bool CloudActive => Target != BackupTargets.LocalOnly && LatestCloud?.State is "Uploading" or "Queued" or "Retrying";
    public bool DriveConnected => _snapshot?.CloudConfiguration?.State == "Connected";
    public string CloudDetail => Target == BackupTargets.LocalOnly ? "Automatic Google Drive uploads are paused. New backups stay in the selected local folder."
        : $"{CloudAccount} · {LatestCloud?.Detail ?? _snapshot?.CloudConfiguration?.Detail ?? "Run drive-connect on this client to authorize Google Drive."}";
    public RestoreSummary? LatestRestore => _snapshot?.LatestRestore;
    public string RestoreStatus => LatestRestore?.State == "Completed" ? "Verified" : LatestRestore?.State ?? "No restore reported";
    public string RestoreDestination => LatestRestore?.Destination ?? "Choose a new folder on the client when restoring.";
    public string RestoreDetail => LatestRestore is { } r ? $"{r.SourceKind} · {r.Files} files · {FormatBytes(r.Bytes)} · {r.UpdatedUtc.ToLocalTime():MMM dd, hh:mm tt} · {r.Detail}" : "Run restore on this client, then restart its agent to send the saved result here.";
    public string RestoreColor => RestoreStatus == "Verified" ? "#14806D" : RestoreStatus is "Failed" or "Interrupted" ? "#B16C13" : "#6F7890";
    public string ProtectionText => _snapshot?.Protection is { } p ? $"{(p.Encrypted ? "AES-256-GCM" : "Plain ZIP")} · Local keep: {(p.KeepLocal == 0 ? "off" : p.KeepLocal)} · Cloud keep: {(p.KeepCloud == 0 ? "off" : p.KeepCloud)}" : "Protection settings not reported yet.";
    public string MaintenanceText => _snapshot?.Protection is { } p ? $"{p.State}: {p.Detail}" : "Update and restart the client to report encryption and retention settings.";

    public void Update(AgentSnapshot snapshot)
    {
        _online = snapshot.Online;
        _lastSeenUtc = snapshot.LastSeenUtc;
        _snapshot = snapshot;
        AgentVersion = snapshot.AgentVersion;
        Address = snapshot.Address;
        foreach (var property in new[] { nameof(Online), nameof(Connection), nameof(ConnectionColor), nameof(LastSeenUtc), nameof(LastSeenText), nameof(Address), nameof(AgentVersion), nameof(Team), nameof(Folder), nameof(Destination), nameof(Configured), nameof(LatestJob), nameof(IsBusy), nameof(CanBackup), nameof(Size), nameof(Status), nameof(StatusColor), nameof(StatusBackground), nameof(LastBackup), nameof(IsUploading), nameof(Progress), nameof(ProgressText), nameof(BackupDetail) }) Changed(property);
        foreach (var property in new[] { nameof(CloudAccount), nameof(LatestCloud), nameof(CloudStatus), nameof(CloudColor), nameof(CloudBackground), nameof(CloudPercent), nameof(CloudActive), nameof(DriveConnected), nameof(CloudDetail) }) Changed(property);
        foreach (var property in new[] { nameof(LatestRestore), nameof(RestoreStatus), nameof(RestoreDestination), nameof(RestoreDetail), nameof(RestoreColor) }) Changed(property);
        Changed(nameof(ProtectionText)); Changed(nameof(MaintenanceText));
        Changed(nameof(SourceFolders)); Changed(nameof(SourceFolderSummary)); Changed(nameof(SourceFoldersText));
        Changed(nameof(Target)); Changed(nameof(DestinationMode));
    }

    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
}

public sealed record ActivityEntry(string Title, string Description, string Time, string Symbol, string Color);
public sealed record BackupHistoryEntry(string Computer, string State, string Time, string Detail, string ArchivePath,
    string CloudState = "Not queued", string CloudDetail = "", string CloudLink = "", bool LocalPruned = false, bool CloudTrashed = false, string Trigger = "Manual", int Attempt = 0,
    string Target = BackupTargets.LegacyBoth, bool TemporaryRemoved = false)
{
    public bool HasCloudLink => CloudLink.Length > 0 && !CloudTrashed;
    public bool CanRestore => State == "Completed" && ((Target != BackupTargets.GoogleDrive && !LocalPruned && ArchivePath.Length > 0) || HasCloudLink);
    public string LocalDisplay => TemporaryRemoved ? "Temporary archive cleaned" : Target == BackupTargets.GoogleDrive && State == "Completed" ? "Prepared for upload" : LocalPruned ? "Expired (retention)" : State;
    public string CloudDisplay => Target == BackupTargets.LocalOnly ? "Not used (local only)" : CloudTrashed ? "In Drive Trash (retention)" : CloudState;
    public string TriggerText => $"{Trigger} · attempt {Attempt + 1}";
}

public sealed class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _dataDirectory;
    private readonly int _port;
    private HostServer? _host;
    private Task<HostServer>? _startup;
    private bool _disposed;
    private string _page = "Overview";
    private string _search = "";
    private string _filter = "All computers";
    private string _notice = "Starting the host listener…";
    private string _hostStatus = "Starting host";
    private bool _scheduleEnabled;
    private string _scheduleTime = "18:00";
    private string _retryCount = "0", _retryDelay = "5", _scheduleZone = TimeZoneInfo.Local.Id;

    public DashboardViewModel(string? dataDirectory = null, int port = Protocol.DefaultPort)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBack", "Host");
        _port = port;
        ComputersView = CollectionViewSource.GetDefaultView(Computers);
        ComputersView.Filter = item => item is Computer c && $"{c.Name} {c.Team}".Contains(Search, StringComparison.OrdinalIgnoreCase) &&
            (Filter switch { "Online" => c.Online, "Offline" => !c.Online, _ => true });
        BackupSelectedCommand = new(_ => QueueBackups(Computers.Where(c => c.Selected).ToArray()), _ => CanPair && Computers.Any(c => c.Selected && c.CanBackup));
        BackupOneCommand = new(p => { if (p is Computer computer) QueueBackups([computer]); }, p => CanPair && p is Computer { CanBackup: true });
        CancelCommand = new(_ => CancelBackups(), _ => CanPair && Computers.Any(c => c.IsBusy && c.LatestJob?.CancelRequested == false));
        SelectVisibleCommand = new(_ =>
        {
            var visible = ComputersView.Cast<Computer>().ToArray();
            var selected = !visible.All(c => c.Selected);
            foreach (var computer in visible) computer.Selected = selected;
        });
        OpenSettingsCommand = new(_ => Page = "Settings");
        SaveScheduleCommand = new(_ =>
        {
            if (!TimeOnly.TryParseExact(ScheduleTime, "HH:mm", out var time)) { Notice = "Enter a valid 24-hour time, for example 18:00."; return; }
            try
            {
                if (!int.TryParse(RetryCount, out var retries) || !int.TryParse(RetryDelay, out var delay)) throw new ArgumentException("Enter whole numbers for retry count and minutes.");
                (_host ?? throw new InvalidOperationException("The host is not running.")).SetAutomation(new(ScheduleEnabled, time.ToString("HH:mm"), _scheduleZone, retries, delay));
                Notice = $"Automation saved. Daily backups {(ScheduleEnabled ? ScheduleTime + " (" + _scheduleZone + ")" : "disabled")}; up to {retries} retries. Keep this host open.";
                Changed(nameof(ScheduleSummary));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Notice = ex.Message; }
        });
        _timer.Tick += Tick;
    }

    public async Task InitializeAsync()
    {
        if (_startup is not null || _disposed) return;
        _startup = Task.Run(async () =>
        {
            var host = new HostServer(_dataDirectory, _port);
            try { host.Start(); return host; }
            catch { await host.DisposeAsync(); throw; }
        });
        try
        {
            _host = await _startup;
            if (_disposed) return;
            var policy = _host.AutomationPolicy;
            ScheduleEnabled = policy.Enabled; ScheduleTime = policy.DailyTime; RetryCount = policy.MaxRetries.ToString(); RetryDelay = policy.RetryDelayMinutes.ToString(); _scheduleZone = policy.TimeZoneId;
            Changed(nameof(ScheduleSummary));
            Notice = $"Host listening on TCP {_host.Port}. Pair a computer to begin live monitoring.";
            SyncHost();
            _timer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
            System.Net.Sockets.SocketException or InvalidOperationException or System.Text.Json.JsonException or InvalidDataException)
        {
            HostStatus = "Host unavailable";
            Notice = ex is System.Net.Sockets.SocketException
                ? $"TCP port {_port} is unavailable. Close another AutoBack host using that port, then reopen this app."
                : "The host could not open its protected data. Check file access and use the same Windows account. Existing identity data has been preserved.";
            AddActivity("Host could not start", Notice, "!", "#B16C13");
            Refresh();
        }
    }

    public PairingInvitation CreateInvitation(string address)
    {
        var invitation = (_host ?? throw new InvalidOperationException("The host is not running.")).CreateInvitation(address.Trim());
        Notice = "Pairing invitation created. It expires in five minutes and can pair one computer.";
        return invitation;
    }

    public void CancelInvitation()
    {
        _host?.CancelInvitation();
        Notice = "The outstanding pairing invitation has been cancelled.";
    }

    public void Revoke(Computer computer)
    {
        (_host ?? throw new InvalidOperationException("The host is not running.")).Revoke(computer.Id);
        Notice = $"Pairing revoked for {computer.Name}. Its old credentials are no longer accepted.";
        SyncHost();
    }

    private void QueueBackups(Computer[] computers)
    {
        var count = 0;
        try
        {
            foreach (var computer in computers.Where(c => c.CanBackup)) { _host!.QueueBackup(computer.Id); count++; }
            Notice = $"{count} local backup(s) queued. Clients connected to Google Drive will upload the verified archive afterward.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Notice = $"Queued {count} backup(s). Could not queue another job: {ex.Message}"; }
        SyncHost();
    }

    private void CancelBackups()
    {
        try
        {
            foreach (var computer in Computers.Where(c => c.IsBusy)) _host!.CancelBackup(computer.LatestJob!.Command.JobId);
            Notice = "Cancellation requested. Waiting for clients to confirm; offline clients will receive it when they reconnect.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Notice = "Could not persist every cancellation request: " + ex.Message; }
        SyncHost();
    }

    public ObservableCollection<Computer> Computers { get; } = [];
    public ICollectionView ComputersView { get; }
    public ObservableCollection<Computer> Jobs { get; } = [];
    public ObservableCollection<Computer> CloudJobs { get; } = [];
    public ObservableCollection<ActivityEntry> Activities { get; } = [];
    public ObservableCollection<BackupHistoryEntry> BackupHistory { get; } = [];
    public string[] Filters { get; } = ["All computers", "Online", "Offline"];
    public RelayCommand BackupSelectedCommand { get; }
    public RelayCommand BackupOneCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand SaveScheduleCommand { get; }
    public bool CanPair => _host?.IsRunning == true && !_disposed;
    public string HostStatus { get => _hostStatus; private set => Set(ref _hostStatus, value); }
    public string HostEndpoint => _host is null ? "Not listening" : $"{Environment.MachineName}:{_host.Port}";
    public string HostFingerprint => _host?.CertificateSha256 ?? "Unavailable";
    public string HostDataDirectory => _dataDirectory;
    public string ConnectionBanner => CanPair
        ? $"Local backups and direct Google Drive uploads are tracked separately on TCP {_host!.Port}. Authorize Google Drive on each client to enable cloud uploads."
        : "The host listener is unavailable. Restart the host to pair clients, monitor connections, and send local backup requests.";
    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            foreach (var property in new[] { nameof(IsOverview), nameof(IsComputers), nameof(IsActivity), nameof(IsRestore), nameof(IsSettings), nameof(ShowComputerTable), nameof(PageTitle), nameof(PageDescription) }) Changed(property);
        }
    }
    public bool IsOverview => Page == "Overview";
    public bool IsComputers => Page == "Computers";
    public bool IsActivity => Page == "Activity";
    public bool IsRestore => Page == "Restore";
    public bool IsSettings => Page == "Settings";
    public bool ShowComputerTable => IsOverview || IsComputers;
    public string PageTitle => Page switch { "Computers" => "Your computers", "Activity" => "Backup & connection activity", "Restore" => "Recover with confidence.", "Settings" => "Host settings", _ => "Your office, backed up." };
    public string PageDescription => Page switch
    {
        "Computers" => "Real paired computers, with connection status reported by their agents.",
        "Activity" => "Saved local backup jobs and connection events from this session.",
        "Restore" => "Recover a local or Google Drive backup into a new folder, then verify every file.",
        "Settings" => "Daily schedules, retry limits, client protection, and host identity.",
        _ => "Start local backups and follow their progress, all from one place."
    };
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Refresh(); } }
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public bool ScheduleEnabled { get => _scheduleEnabled; set => Set(ref _scheduleEnabled, value); }
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    public string RetryCount { get => _retryCount; set => Set(ref _retryCount, value); }
    public string RetryDelay { get => _retryDelay; set => Set(ref _retryDelay, value); }
    public string ScheduleSummary => $"Time zone: {_scheduleZone}. The host must remain open. Missed occurrences catch up once; offline clients wait in the queue.";
    public int TotalCount => Computers.Count;
    public int OnlineCount => Computers.Count(c => c.Online);
    public int AttentionCount => Computers.Count(c => !c.Online || c.LatestJob?.Report.State == "Failed" || c.CloudStatus is "Failed" or "Needs sign-in");
    public int ActiveCount => Computers.Count(c => c.IsBusy);
    public int SelectedCount => Computers.Count(c => c.Selected);
    public int VisibleCount => ComputersView.Cast<Computer>().Count();
    public string SelectionText => $"{SelectedCount} selected";
    public string BackupButtonText => $"Backup selected ({SelectedCount})";
    public string ComputerCountText => $"{TotalCount} securely paired computer{(TotalCount == 1 ? "" : "s")}";
    public string QueueText => Jobs.Count == 0 ? "No backups in progress." : $"{Jobs.Count} local backup job(s) pending.";
    public string DriveSummary => $"{Computers.Count(c => c.DriveConnected)} client(s) authorized · {CloudJobs.Count} cloud upload(s) pending";
    public string EmptyInventoryText => TotalCount == 0 ? "No computers paired yet. Click Pair computer to create an invitation." : "No computers match your search.";
    public Visibility EmptyTableVisibility => VisibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyQueueVisibility => Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Tick(object? sender, EventArgs e) => SyncHost();

    private void SyncHost()
    {
        if (_host is null || _disposed) return;
        HostStatus = _host.IsRunning ? "Host listening" : "Host unavailable";
        foreach (var activity in _host.DrainActivity())
        {
            var failed = activity.Title.Contains("failed", StringComparison.OrdinalIgnoreCase);
            AddActivity(activity.Title, activity.Description, failed ? "!" : "·", failed ? "#B16C13" : "#168472", activity.TimeUtc);
            if (activity.Title.StartsWith("Local backup ", StringComparison.Ordinal) || activity.Title.StartsWith("Cloud upload ", StringComparison.Ordinal)) Notice = activity.Description;
        }
        var snapshots = _host.GetSnapshots();
        foreach (var removed in Computers.Where(c => snapshots.All(s => s.Id != c.Id)).ToArray())
        {
            removed.PropertyChanged -= ComputerChanged;
            Computers.Remove(removed);
        }
        foreach (var snapshot in snapshots)
        {
            var computer = Computers.SingleOrDefault(c => c.Id == snapshot.Id);
            var wasOnline = computer?.Online ?? false;
            if (computer is null)
            {
                computer = new() { Id = snapshot.Id, Name = snapshot.Name, MachineName = snapshot.MachineName, AgentVersion = snapshot.AgentVersion };
                computer.PropertyChanged += ComputerChanged;
                Computers.Add(computer);
            }
            computer.Update(snapshot);
            if (snapshot.Online != wasOnline)
            {
                var title = snapshot.Online ? "Computer online" : "Connection lost";
                var description = snapshot.Online ? $"{snapshot.Name} is reporting authenticated heartbeats." : $"{snapshot.Name} has stopped reporting. Last seen: {computer.LastSeenText}.";
                AddActivity(title, description, snapshot.Online ? "✓" : "!", snapshot.Online ? "#168472" : "#B16C13");
                Notice = description;
            }
        }
        foreach (var removed in Jobs.Where(c => !c.IsBusy || !Computers.Contains(c)).ToArray()) Jobs.Remove(removed);
        foreach (var computer in Computers.Where(c => c.IsBusy && !Jobs.Contains(c))) Jobs.Add(computer);
        foreach (var removed in CloudJobs.Where(c => !c.CloudActive || !Computers.Contains(c)).ToArray()) CloudJobs.Remove(removed);
        foreach (var computer in Computers.Where(c => c.CloudActive && !CloudJobs.Contains(c))) CloudJobs.Add(computer);
        var history = _host.GetJobs().OrderByDescending(j => j.UpdatedUtc).Take(100).Select(j => new BackupHistoryEntry(
            j.AgentName, !BackupStates.Terminal(j.Report.State) && j.CancelRequested ? "Cancelling" : j.Report.State,
            j.UpdatedUtc.ToLocalTime().ToString("MMM dd, hh:mm:ss tt"), j.Report.TemporaryArchiveRemoved ? "Google Drive upload verified; temporary archive cleaned." : j.Report.Detail,
            j.Report.Target == BackupTargets.GoogleDrive ? "" : j.Report.ArchivePath ?? "",
            j.Cloud?.State ?? (j.Report.State == "Completed" ? "Not queued" : BackupStates.Terminal(j.Report.State) ? "Not applicable" : "Waiting for local backup"), j.Cloud?.Detail ?? "", j.Cloud?.WebLink ?? "",
            j.Report.ArchivePruned, j.Cloud?.Trashed ?? false, j.Trigger, j.Command.Attempt, j.Report.Target, j.Report.TemporaryArchiveRemoved)).ToArray();
        if (!BackupHistory.SequenceEqual(history))
        {
            BackupHistory.Clear();
            foreach (var entry in history) BackupHistory.Add(entry);
        }
        Refresh();
    }

    private void AddActivity(string title, string description, string symbol, string color, DateTimeOffset? time = null)
    {
        Activities.Insert(0, new(title, description, (time ?? DateTimeOffset.Now).ToLocalTime().ToString("hh:mm:ss tt"), symbol, color));
        if (Activities.Count > 200) Activities.RemoveAt(Activities.Count - 1);
    }

    private void ComputerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Computer.Selected)) Refresh();
    }

    private void Refresh()
    {
        ComputersView.Refresh();
        foreach (var name in new[] { nameof(TotalCount), nameof(OnlineCount), nameof(AttentionCount), nameof(ActiveCount), nameof(SelectedCount), nameof(VisibleCount), nameof(SelectionText), nameof(BackupButtonText), nameof(ComputerCountText), nameof(EmptyTableVisibility), nameof(EmptyInventoryText), nameof(CanPair), nameof(HostEndpoint), nameof(HostFingerprint), nameof(ConnectionBanner), nameof(QueueText), nameof(EmptyQueueVisibility) }) Changed(name);
        Changed(nameof(DriveSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        foreach (var computer in Computers) computer.PropertyChanged -= ComputerChanged;
        if (_startup is not null)
        {
            try { await (await _startup).DisposeAsync(); }
            catch (Exception) { /* Startup failures are already reported; closing must still complete. */ }
        }
    }
}
