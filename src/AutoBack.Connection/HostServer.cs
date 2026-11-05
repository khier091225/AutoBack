using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AutoBack.Connection;

public sealed partial class HostServer : IAsyncDisposable
{
    private sealed record StoredAgent(Guid Id, string Name, string MachineName, string AgentVersion, string Address,
        string SecretHash, DateTimeOffset PairedUtc, DateTimeOffset? LastSeenUtc = null, bool Revoked = false,
        BackupConfiguration? Configuration = null, CloudConfiguration? CloudConfiguration = null, RestoreSummary? LatestRestore = null,
        string? LastScheduledSlot = null, AgentProtectionSummary? Protection = null);
    private sealed record Registry(List<StoredAgent> Agents, List<HostBackupJob>? Jobs = null, HostAutomationPolicy? Automation = null);
    private sealed record PendingInvitation(string SecretHash, DateTimeOffset ExpiresUtc);
    private readonly object _gate = new();
    private readonly ProtectedStore _store;
    private readonly FileStream _instanceLock;
    private readonly X509Certificate2 _certificate;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _offlineAfter;
    private readonly HashSet<Guid> _seenThisRun = [];
    private readonly ConcurrentQueue<ConnectionActivity> _activity = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly SemaphoreSlim _slots = new(32, 32);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;
    private Registry _registry;
    private PendingInvitation? _invitation;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private long _nextConnection;
    private volatile bool _running;
    private bool _disposed;

    public HostServer(string dataDirectory, int port = Protocol.DefaultPort, IPAddress? bindAddress = null,
        TimeProvider? clock = null, TimeSpan? offlineAfter = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _bindAddress = bindAddress ?? IPAddress.Any;
        _clock = clock ?? TimeProvider.System;
        _offlineAfter = offlineAfter ?? Protocol.OfflineAfter;
        _store = new(dataDirectory);
        _instanceLock = new FileStream(Path.Combine(Path.GetFullPath(dataDirectory), ".host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _registry = _store.Load<Registry>("registry.dat") ?? new([]);
            _registry = _registry with { Jobs = _registry.Jobs ?? [] };
            if (_registry.Agents is null || _registry.Agents.Any(a => a.Id == Guid.Empty || !Protocol.ValidName(a.Name) || !Protocol.ValidSecret(a.SecretHash)))
                throw new InvalidDataException("The host registry is invalid. Restore the host data directory from a trusted copy.");
            _certificate = HostIdentity.LoadOrCreate(_store);
            _registry.Automation?.Validate();
        }
        catch { _instanceLock.Dispose(); throw; }
    }

    public bool IsRunning => _running;
    public int Port { get; private set; }
    public string CertificateSha256 => _certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptTask is not null) throw new InvalidOperationException("The host was already started.");
        var listener = new TcpListener(_bindAddress, _requestedPort);
        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _running = true;
        _acceptTask = AcceptAsync(_lifetime.Token);
        _automationTask = AutomationLoopAsync(_lifetime.Token);
        Log("Host started", $"Listening for authenticated clients on TCP {Port}.");
    }

    public PairingInvitation CreateInvitation(string host)
    {
        if (!IsRunning) throw new InvalidOperationException("The host listener is not running.");
        if (!Protocol.ValidHost(host)) throw new ArgumentException("Enter a reachable host name or IP address, without a port or URL scheme.");
        var secret = Protocol.NewSecret();
        var expires = _clock.GetUtcNow() + Protocol.InvitationLifetime;
        lock (_gate) _invitation = new(Protocol.Hash(secret), expires); // A new invitation invalidates the previous one.
        return new(Protocol.Version, host, Port, CertificateSha256, secret, expires);
    }

    public void CancelInvitation()
    {
        lock (_gate) _invitation = null;
    }

    public IReadOnlyList<AgentSnapshot> GetSnapshots()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
            return _registry.Agents.Where(a => !a.Revoked).Select(a => new AgentSnapshot(a.Id, a.Name, a.MachineName,
                a.AgentVersion, a.Address, a.PairedUtc, a.LastSeenUtc,
                IsRunning && _seenThisRun.Contains(a.Id) && a.LastSeenUtc.HasValue && now - a.LastSeenUtc.Value < _offlineAfter,
                a.Configuration, _registry.Jobs!.LastOrDefault(j => j.AgentId == a.Id),
                _registry.Jobs!.LastOrDefault(j => j.AgentId == a.Id && j.Report.State == "Completed"), a.CloudConfiguration,
                _registry.Jobs!.Where(j => j.AgentId == a.Id && j.Cloud is not null && !j.Cloud.Trashed).OrderByDescending(j => j.CloudUpdatedUtc).FirstOrDefault(), a.LatestRestore, a.Protection)).ToArray();
    }

    public IReadOnlyList<HostBackupJob> GetJobs()
    {
        lock (_gate) return _registry.Jobs!.ToArray();
    }

    public HostBackupJob QueueBackup(Guid agentId)
    {
        if (!IsRunning) throw new InvalidOperationException("The host is not running.");
        lock (_gate)
        {
            var agent = _registry.Agents.SingleOrDefault(a => a.Id == agentId && !a.Revoked)
                ?? throw new InvalidOperationException("This computer is no longer paired.");
            if (agent.Configuration is null) throw new InvalidOperationException("Configure source and destination folders on the client first.");
            if (_registry.Jobs!.Any(j => j.AgentId == agentId && !BackupStates.Terminal(j.Report.State)))
                throw new InvalidOperationException("This computer already has a pending backup.");
            var now = _clock.GetUtcNow();
            var command = new BackupCommand(Guid.NewGuid(), agent.Configuration.PlanId, now, Encrypt: agent.Configuration.Encrypted);
            var job = new HostBackupJob(agent.Id, agent.Name, command,
                new(command.JobId, 0, "Queued", 0, 0, 0, 0, 0, "Waiting for the client to acknowledge the job."), now);
            Save(_registry with { Jobs = [.. _registry.Jobs!, job] });
            Log("Backup queued", $"{agent.Name}: local backup requested.");
            return job;
        }
    }

    public void CancelBackup(Guid jobId)
    {
        lock (_gate)
        {
            var job = _registry.Jobs!.SingleOrDefault(j => j.Command.JobId == jobId)
                ?? throw new InvalidOperationException("Backup job not found.");
            if (BackupStates.Terminal(job.Report.State)) return;
            Save(_registry with { Jobs = _registry.Jobs!.Select(j => j.Command.JobId == jobId ? j with { CancelRequested = true, UpdatedUtc = _clock.GetUtcNow() } : j).ToList() });
            Log("Cancellation requested", $"{job.AgentName}: waiting for the client to confirm cancellation.");
        }
    }

    public IReadOnlyList<ConnectionActivity> DrainActivity()
    {
        var result = new List<ConnectionActivity>();
        while (_activity.TryDequeue(out var item)) result.Add(item);
        return result;
    }

    public void Revoke(Guid id)
    {
        lock (_gate)
        {
            var agent = _registry.Agents.SingleOrDefault(a => a.Id == id && !a.Revoked)
                ?? throw new InvalidOperationException("This computer is no longer paired.");
            Save(_registry with
            {
                Agents = _registry.Agents.Select(a => a.Id == id ? a with { Revoked = true } : a).ToList(),
                Jobs = _registry.Jobs!.Select(j => j.AgentId == id && !BackupStates.Terminal(j.Report.State)
                    ? j with { Report = j.Report with { State = "Failed", Sequence = j.Report.Sequence + 1,
                        Detail = "Pairing revoked. A successful local backup was not confirmed." }, UpdatedUtc = _clock.GetUtcNow() } : j).ToList()
            });
            _seenThisRun.Remove(id);
            Log("Pairing revoked", $"{agent.Name} must pair again before reconnecting.");
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _nextConnection);
                var task = HandleAsync(client, cancellationToken);
                _handlers[id] = task;
                _ = task.ContinueWith(completed => _handlers.TryRemove(id, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            _running = false;
            Log("Host listener stopped", "The listener encountered an error. Restart the host application.");
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Protocol.RequestTimeout);
            try
            {
                using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false
                }, timeout.Token);
                var request = await Protocol.ReadAsync<AgentRequest>(tls, timeout.Token);
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "Unknown";
                var response = Process(request, address);
                await Protocol.WriteAsync(tls, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                OperationCanceledException or JsonException or ArgumentException or InvalidDataException) { }
            catch (Exception)
            {
                Log("Client request failed", "A request could not be processed. Check host storage access and restart if the issue persists.");
            }
            finally { _slots.Release(); }
        }
    }

    private HostResponse Process(AgentRequest request, string address)
    {
        var now = _clock.GetUtcNow();
        HostResponse Denied(string message) => new(false, message, now);
        if (request.Version != Protocol.Version) return Denied("Incompatible agent protocol version.");
        if (request.AgentId == Guid.Empty || !Protocol.ValidSecret(request.Secret)) return Denied("Invalid credentials.");
        lock (_gate)
        {
            var known = _registry.Agents.SingleOrDefault(a => a.Id == request.AgentId);
            if (request.Kind == "pair")
            {
                // A lost pairing response can be retried with the same persisted, client-generated credential.
                if (known is not null)
                    return new(!known.Revoked && Protocol.SecretMatches(request.Secret, known.SecretHash),
                        !known.Revoked && Protocol.SecretMatches(request.Secret, known.SecretHash) ? "Already paired." : "Pairing denied.", now);
                if (_invitation is null || now >= _invitation.ExpiresUtc || !Protocol.SecretMatches(request.InvitationSecret, _invitation.SecretHash))
                    return Denied("Invitation is invalid, expired, or already used. Create a new invitation on the host.");
                if (!Protocol.ValidName(request.Name) || !Protocol.ValidName(request.MachineName) ||
                    string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 40 || request.AgentVersion.Any(char.IsControl))
                    return Denied("Invalid computer information.");
                if (_registry.Agents.Any(a => !a.Revoked && a.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
                    return Denied("This computer name is already paired. Choose another name or revoke the old pairing.");
                if (_registry.Agents.Count >= 500) return Denied("This host registry has reached its capacity.");
                var paired = new StoredAgent(request.AgentId, request.Name!, request.MachineName!, request.AgentVersion!,
                    address, Protocol.Hash(request.Secret), now);
                Persist([.. _registry.Agents, paired]);
                _invitation = null;
                Log("Computer paired", $"{paired.Name} ({paired.MachineName}) has securely paired with this host.");
                return new(true, "Paired. Start the agent to send heartbeats.", now);
            }
            if (request.Kind != "heartbeat") return Denied("Unsupported request.");
            if (known is null || known.Revoked || !Protocol.SecretMatches(request.Secret, known.SecretHash))
                return Denied("Agent is not authorized. Pair this client again.");
            if (!BackupStates.ValidConfiguration(request.Configuration)) return Denied("Invalid backup configuration report.");
            if (!CloudStates.ValidConfiguration(request.CloudConfiguration)) return Denied("Invalid cloud configuration report.");
            if (!RestoreStates.ValidSummary(request.LatestRestore)) return Denied("Invalid restore summary.");
            if (request.Protection is { } protection && (protection.KeepLocal is < 0 or > 10000 || protection.KeepCloud is < 0 or > 10000 ||
                !BackupStates.ValidText(protection.State, 40) || !BackupStates.ValidText(protection.Detail, 1024))) return Denied("Invalid protection summary.");
            var jobs = _registry.Jobs!;
            var report = request.Report;
            HostBackupJob? acknowledged = null;
            if (report is not null)
            {
                if (!BackupStates.ValidReport(report)) return Denied("Invalid backup progress report.");
                var job = jobs.SingleOrDefault(j => j.AgentId == known.Id && j.Command.JobId == report.JobId);
                if (job is null) return Denied("Backup report does not belong to this agent.");
                var pruned = job.Report.State == "Completed" && !job.Report.ArchivePruned && report.ArchivePruned &&
                    report with { Sequence = job.Report.Sequence, ArchivePruned = false } == job.Report;
                if (report.Sequence > job.Report.Sequence && (!BackupStates.Terminal(job.Report.State) || pruned))
                {
                    acknowledged = job with { Report = report, UpdatedUtc = now };
                    jobs = jobs.Select(j => j.Command.JobId == report.JobId ? acknowledged : j).ToList();
                }
                else acknowledged = job;
            }
            HostBackupJob? cloudAcknowledged = null;
            if (request.CloudReport is { } cloud)
            {
                if (!CloudStates.ValidReport(cloud)) return Denied("Invalid cloud upload report.");
                var job = jobs.SingleOrDefault(j => j.AgentId == known.Id && j.Command.JobId == cloud.JobId && j.Report.State == "Completed");
                if (job is null) return Denied("Cloud report does not belong to a completed local backup on this agent.");
                cloudAcknowledged = job;
                var trashed = job.Cloud is { State: "Completed", Trashed: false } && cloud.Trashed &&
                    cloud with { Sequence = job.Cloud.Sequence, Trashed = false } == job.Cloud;
                if ((job.Cloud is null || cloud.Sequence > job.Cloud.Sequence) && (job.Cloud?.State != "Completed" || trashed))
                {
                    cloudAcknowledged = job with { Cloud = cloud, CloudUpdatedUtc = now };
                    jobs = jobs.Select(j => j.Command.JobId == cloud.JobId ? cloudAcknowledged : j).ToList();
                }
            }
            var updated = known with { LastSeenUtc = now, Address = address, Configuration = request.Configuration,
                CloudConfiguration = request.CloudConfiguration,
                LatestRestore = request.LatestRestore ?? known.LatestRestore,
                Protection = request.Protection ?? known.Protection,
                AgentVersion = BackupStates.ValidText(request.AgentVersion, 40) ? request.AgentVersion! : known.AgentVersion };
            Save(_registry with { Agents = _registry.Agents.Select(a => a.Id == known.Id ? updated : a).ToList(), Jobs = jobs });
            _seenThisRun.Add(known.Id);
            if (acknowledged is not null && report is not null && BackupStates.Terminal(report.State) &&
                report.Sequence == acknowledged.Report.Sequence && _registry.Jobs!.Any(j => j.Command.JobId == report.JobId && j.Report == acknowledged.Report))
            {
                // A bounded log is session-only; persistent job history is the authoritative result.
                if (_loggedResults.Add(report.JobId)) Log("Local backup " + report.State.ToLowerInvariant(), $"{known.Name}: {report.Detail}");
            }
            var pending = jobs.FirstOrDefault(j => j.AgentId == known.Id && !BackupStates.Terminal(j.Report.State));
            if (request.LatestRestore is { } recovery && RestoreStates.Terminal(recovery.State) && _loggedRestores.Add(recovery.OperationId))
                Log("Restore " + recovery.State.ToLowerInvariant(), $"{known.Name}: {recovery.Detail}");
            if (cloudAcknowledged?.Cloud is { State: "Completed" or "Failed" or "Needs sign-in" } cloudResult &&
                _loggedCloudResults.Add((cloudResult.JobId, cloudResult.State)))
                Log("Cloud upload " + cloudResult.State.ToLowerInvariant(), $"{known.Name}: {cloudResult.Detail}");
            return new(true, "Heartbeat accepted.", now, pending is null ? null : pending.Command with { CancelRequested = pending.CancelRequested },
                acknowledged?.Command.JobId, acknowledged?.Report.Sequence ?? 0,
                cloudAcknowledged?.Command.JobId, cloudAcknowledged?.Cloud?.Sequence ?? 0);
        }
    }

    private void Persist(List<StoredAgent> agents)
    {
        Save(_registry with { Agents = agents });
    }

    private readonly HashSet<Guid> _loggedResults = [];
    private readonly HashSet<(Guid, string)> _loggedCloudResults = [];
    private readonly HashSet<Guid> _loggedRestores = [];

    private void Save(Registry registry)
    {
        _store.Save("registry.dat", registry);
        _registry = registry;
    }

    private void Log(string title, string description)
    {
        _activity.Enqueue(new(_clock.GetUtcNow(), title, description));
        while (_activity.Count > 200) _activity.TryDequeue(out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        CancelInvitation();
        await _lifetime.CancelAsync();
        _listener?.Stop();
        if (_acceptTask is not null) await _acceptTask;
        if (_automationTask is not null) await _automationTask;
        await Task.WhenAll(_handlers.Values);
        _certificate.Dispose();
        _instanceLock.Dispose();
        _lifetime.Dispose();
        _slots.Dispose();
    }
}
