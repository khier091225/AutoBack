namespace AutoBack.Connection;

public sealed record HostAutomationPolicy(bool Enabled, string DailyTime, string TimeZoneId, int MaxRetries = 0,
    int RetryDelayMinutes = 5, Guid Revision = default, DateTimeOffset ActivatedUtc = default)
{
    public void Validate()
    {
        if (!TimeOnly.TryParseExact(DailyTime, "HH:mm", out _) || MaxRetries is < 0 or > 5 || RetryDelayMinutes is < 1 or > 1440)
            throw new ArgumentException("Use HH:mm, 0–5 backup retries and a retry delay of 1–1440 minutes.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    public (string Slot, DateTimeOffset Utc)? LatestDue(DateTimeOffset now)
    {
        if (!Enabled) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var time = TimeOnly.ParseExact(DailyTime, "HH:mm");
        for (var n = 0; n < 3; n++, day = day.AddDays(-1))
        {
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var utc = new DateTimeOffset(local, offset).ToUniversalTime();
            if (utc <= now) return utc < ActivatedUtc ? null : ($"{Revision:N}/{day:yyyy-MM-dd}", utc);
        }
        return null;
    }
}

public sealed record AgentProtectionSummary(bool Encrypted, int KeepLocal, int KeepCloud, string State,
    string Detail, DateTimeOffset? LastRunUtc = null);

public sealed partial class HostServer
{
    private Task? _automationTask;
    public HostAutomationPolicy AutomationPolicy
    {
        get { lock (_gate) return _registry.Automation ?? new(false, "18:00", TimeZoneInfo.Local.Id); }
    }

    public void SetAutomation(HostAutomationPolicy policy)
    {
        policy.Validate();
        lock (_gate)
        {
            var old = AutomationPolicy;
            // Editing retry limits alone does not create another occurrence for today's schedule.
            var sameSchedule = policy.Enabled == old.Enabled && policy.DailyTime == old.DailyTime && policy.TimeZoneId == old.TimeZoneId && old.Revision != Guid.Empty;
            policy = policy with { Revision = sameSchedule ? old.Revision : Guid.NewGuid(), ActivatedUtc = sameSchedule ? old.ActivatedUtc : _clock.GetUtcNow() };
            Save(_registry with { Automation = policy });
            Log("Automation saved", $"Daily backups {(policy.Enabled ? policy.DailyTime + " (" + policy.TimeZoneId + ")" : "disabled")}; maximum {policy.MaxRetries} automatic retries.");
        }
    }

    public void TickAutomation()
    {
        if (!IsRunning) return;
        lock (_gate)
        {
            var policy = AutomationPolicy;
            var now = _clock.GetUtcNow();
            var due = policy.LatestDue(now);
            foreach (var agent in _registry.Agents.Where(a => !a.Revoked && a.Configuration is not null).ToArray())
            {
                if (_registry.Jobs!.Any(j => j.AgentId == agent.Id && !BackupStates.Terminal(j.Report.State))) continue;
                var failed = _registry.Jobs!.LastOrDefault(j => j.AgentId == agent.Id);
                if (policy.MaxRetries > 0 && failed is { Report.State: "Failed", Report.Retryable: true, CancelRequested: false } &&
                    failed.Command.CreatedUtc >= policy.ActivatedUtc && failed.Command.Attempt < policy.MaxRetries &&
                    failed.Command.PlanId == agent.Configuration!.PlanId && now >= failed.UpdatedUtc.AddMinutes(policy.RetryDelayMinutes) &&
                    !_registry.Jobs!.Any(j => j.Command.RetryOf == failed.Command.JobId))
                {
                    QueueAutomated(agent, now, "Retry", failed);
                    continue;
                }
                if (due is { } occurrence && agent.LastScheduledSlot != occurrence.Slot)
                    QueueAutomated(agent, now, "Schedule", slot: occurrence.Slot);
            }
        }
    }

    private void QueueAutomated(StoredAgent agent, DateTimeOffset now, string trigger, HostBackupJob? retry = null, string? slot = null)
    {
        var command = new BackupCommand(Guid.NewGuid(), agent.Configuration!.PlanId, now,
            RetryOf: retry?.Command.JobId, Attempt: retry is null ? 0 : retry.Command.Attempt + 1, Encrypt: agent.Configuration.Encrypted);
        var job = new HostBackupJob(agent.Id, agent.Name, command,
            new(command.JobId, 0, "Queued", 0, 0, 0, 0, 0, $"{trigger}: waiting for the client."), now, Trigger: trigger);
        Save(_registry with
        {
            Jobs = [.. _registry.Jobs!, job],
            Agents = slot is null ? _registry.Agents : _registry.Agents.Select(a => a.Id == agent.Id ? a with { LastScheduledSlot = slot } : a).ToList()
        });
        Log("Backup queued", $"{agent.Name}: {trigger.ToLowerInvariant()} backup queued (attempt {command.Attempt + 1}).");
    }

    private async Task AutomationLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { TickAutomation(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Log("Automation failed", "A schedule could not be saved. Check host storage; it will be checked again."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
