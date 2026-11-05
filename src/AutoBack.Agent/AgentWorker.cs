using AutoBack.Connection;
using AutoBack.Backup;
using AutoBack.Drive;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoBack.Agent;

internal sealed class AgentWorker(AgentCredentials credentials, AgentClient client, ProtectedStore store, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunWorkerAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            logger.LogCritical("Agent stopped after a fatal configuration or journal failure. Check protected storage and application permissions.");
            Environment.ExitCode = 1;
            // SCM recovery observes an unexpected process exit; normal service stops do not trigger retries.
            if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) Environment.Exit(1);
            throw;
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await using var backups = new BackupCoordinator(store, credentials.AgentId);
        RestoreHistory.RecoverInterrupted(store);
        using var driveHttp = DriveHttp.CreateClient();
        await using var uploads = new CloudUploadCoordinator(store, driveHttp, credentials.AgentId, credentials.Name);
        await using var maintenance = new AgentMaintenance(store, credentials.AgentId, backups, uploads, driveHttp);
        var failures = 0;
        var wasConnected = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (backups.Faulted) throw new InvalidOperationException("The local backup journal could not be updated. Check credential-store disk access before restarting the agent.");
            if (uploads.Faulted) throw new InvalidOperationException("The cloud upload journal could not be updated. Check credential-store disk access before restarting the agent.");
            if (maintenance.Faulted) throw new InvalidOperationException("The protection journal could not be saved. Check client storage.");
            try
            {
                var plan = backups.LoadPlan();
                var configuration = plan is null ? null : plan.Summary with { Encrypted = EncryptionVault.Load(store).Enabled };
                var response = await client.PollAsync(credentials, configuration, backups.NextReport(), stoppingToken, uploads.Configuration, uploads.NextReport(), RestoreHistory.Latest(store), maintenance.Summary);
                backups.Acknowledge(response.AcknowledgedJobId, response.AcknowledgedSequence);
                if (!maintenance.IsBusy) backups.Handle(response.Command);
                uploads.Acknowledge(response.AcknowledgedCloudJobId, response.AcknowledgedCloudSequence);
                uploads.SetHostAuthorized(true);
                if (!maintenance.IsBusy) uploads.Tick(backups.CompletedJobs());
                maintenance.Tick();
                if (!wasConnected) logger.LogInformation("Connected to host {Host}:{Port} as {Name}.", credentials.Host, credentials.Port, credentials.Name);
                failures = 0;
                wasConnected = true;
                await Task.Delay(backups.IsBusy || backups.NextReport() is not null || uploads.IsBusy || uploads.NextReport() is not null ? Protocol.JobPollInterval : Protocol.HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
                System.Security.Authentication.AuthenticationException or OperationCanceledException or AgentRejectedException or System.Text.Json.JsonException or InvalidDataException)
            {
                failures = Math.Min(failures + 1, 5);
                wasConnected = false;
                if (ex is AgentRejectedException) { backups.CancelCurrent(); uploads.SetHostAuthorized(false); }
                var seconds = Math.Min(30, 1 << failures) + Random.Shared.NextDouble();
                var reason = ex switch
                {
                    AgentRejectedException => "host rejected the request; check pairing and update both host and agent to the same version",
                    System.Security.Authentication.AuthenticationException => "host certificate verification failed",
                    OperationCanceledException => "request timed out",
                    _ => "host unavailable"
                };
                logger.LogWarning("Connection lost: {Reason}. Retrying in {Seconds:F0} seconds.", reason, seconds);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
