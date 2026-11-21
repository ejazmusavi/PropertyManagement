using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Accounting;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Gateway;
using RealEstate.Application.Inventory;
using RealEstate.Application.Notifications;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Jobs;

/// <summary>One recurring background job. Times are cron expressions in UTC.</summary>
public sealed record JobDefinition(string Key, string Name, string Description, string Cron, string Schedule, bool NeedsServiceUser, int MaxMinutes);

public static class JobCatalog
{
    public const string Dispatch = "notifications-dispatch";
    public const string Events = "notifications-events";
    public const string Reminders = "notifications-installment-reminders";
    public const string Digest = "reports-staff-digest";
    public const string Reservations = "reservations-expire";
    public const string Ledgers = "ledger-receivables";
    public const string Gateway = "payments-gateway-reconcile";
    public const string Postings = "accounting-postings";
    public const string Cleanup = "maintenance-cleanup";

    public static readonly IReadOnlyList<JobDefinition> All =
    [
        new(Dispatch, "Notification dispatch", "Sends queued notifications on every channel and retries failed deliveries with back-off.", "* * * * *", "Every minute", false, 10),
        new(Events, "Business event notifications", "Queues payment confirmations, booking confirmations, agreement amendments, possession notices and company announcements.", "*/5 * * * *", "Every 5 minutes", false, 10),
        new(Reminders, "Installment reminders", "Queues upcoming and overdue installment reminders for the current owners.", "0 4 * * *", "Daily 04:00 UTC", false, 30),
        new(Digest, "Daily management summary", "Queues the daily collections and overdue summary for staff who subscribed (recurring report).", "30 4 * * *", "Daily 04:30 UTC", false, 15),
        new(Reservations, "Expire property reservations", "Returns properties with expired reservations to Available (scheduled maintenance).", "*/30 * * * *", "Every 30 minutes", false, 10),
        new(Ledgers, "Ledger and receivable reconciliation", "Brings agreement and customer ledgers up to date (receivables of installments that fell due, holder transfers).", "15 * * * *", "Hourly at :15", false, 30),
        new(Gateway, "Online payment reconciliation", "Asks the payment provider about open online payments and records the outcome.", "*/15 * * * *", "Every 15 minutes", true, 20),
        new(Postings, "Automatic accounting postings", "Posts pending accounting events under the approved posting rules (failed events stay for review).", "*/20 * * * *", "Every 20 minutes", true, 30),
        new(Cleanup, "Scheduled maintenance", "Removes expired login tokens and old job history; stale notification rows are closed.", "0 2 * * *", "Daily 02:00 UTC", false, 30),
    ];

    public static JobDefinition? Find(string key) => All.FirstOrDefault(j => string.Equals(j.Key, key, StringComparison.Ordinal));
}

public sealed record JobRunDto(long Id, string JobKey, string TriggeredBy, DateTime StartedAtUtc, DateTime? FinishedAtUtc, BackgroundJobRunStatus Status, int ItemsProcessed, string? Message);

public sealed record JobDto(string Key, string Name, string Description, string Schedule, bool NeedsServiceUser, DateTime? NextRunUtc, JobRunDto? LastRun, int FailuresLast7Days, bool Scheduled);

public sealed record JobGatewayInfo(string Key, DateTime? NextRunUtc, DateTime? LastExecutionUtc, string? LastJobState);

/// <summary>The link to the job scheduler (Hangfire). Implemented in Infrastructure; a no-op implementation is used when background processing is switched off.</summary>
public interface IBackgroundJobGateway
{
    bool Enabled { get; }
    IReadOnlyList<JobGatewayInfo> Describe();
    /// <summary>Queues one immediate execution of the job (with the scheduler's retry policy). Returns the scheduler's job id.</summary>
    string Trigger(string key, string triggeredBy);
}

/// <summary>Housekeeping that needs tables outside the application model (login tokens).</summary>
public interface IMaintenanceStore
{
    Task<int> PurgeExpiredRefreshTokensAsync(DateTime olderThanUtc, CancellationToken ct = default);
}

/// <summary>
/// Runs one job and records the run. Jobs are idempotent: they only read business tables and write through keyed, de-duplicated operations, so a retry
/// after a crash or a second concurrent run cannot duplicate anything. The jobs that call permission-checked services need the configured service user.
/// </summary>
public class ScheduledJobService(IAppDbContext db, NotificationDispatchService dispatch, NotificationEventService events, PropertyService properties, PaymentSupport payments,
    GatewayReconciliationService gateway, PostingEngineService postings, IMaintenanceStore maintenance, ICurrentUser user, IPermissionService permissions, IBackgroundJobGateway scheduler,
    IAuditService audit, TimeProvider clock, ILogger<ScheduledJobService> logger)
{
    public async Task<IReadOnlyList<JobDto>> ListAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.JobsView, ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-7);
        var info = scheduler.Describe().ToDictionary(i => i.Key);
        var runs = await db.BackgroundJobRuns.AsNoTracking().Where(r => r.StartedAtUtc >= since).OrderByDescending(r => r.Id).Take(2000).ToListAsync(ct);
        var latestAny = await db.BackgroundJobRuns.AsNoTracking().GroupBy(r => r.JobKey).Select(g => new { g.Key, Id = g.Max(r => r.Id) }).ToListAsync(ct);
        var latestRows = await db.BackgroundJobRuns.AsNoTracking().Where(r => latestAny.Select(l => l.Id).Contains(r.Id)).ToDictionaryAsync(r => r.JobKey, ct);
        return [.. JobCatalog.All.Select(d => new JobDto(d.Key, d.Name, d.Description, d.Schedule, d.NeedsServiceUser, info.GetValueOrDefault(d.Key)?.NextRunUtc,
            latestRows.TryGetValue(d.Key, out var l) ? Map(l) : null, runs.Count(r => r.JobKey == d.Key && r.Status == BackgroundJobRunStatus.Failed), info.ContainsKey(d.Key)))];
    }

    public async Task<PagedResult<JobRunDto>> RunsAsync(string? key, PagedRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.JobsView, ct);
        var q = db.BackgroundJobRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(key)) q = q.Where(x => x.JobKey == key);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<JobRunDto>([.. rows.Select(Map)], r.Page, r.PageSize, total);
    }

    /// <summary>Starts an immediate run through the scheduler (so it gets the same retry policy and single-run protection).</summary>
    public async Task<string> RunNowAsync(string key, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.JobsManage, ct);
        if (JobCatalog.Find(key) is null) throw new NotFoundException("Job not found.");
        if (!scheduler.Enabled) throw new DomainException("Background processing is switched off on this server (BackgroundProcessing:Enabled). Start the API host with it enabled.");
        var id = scheduler.Trigger(key, user.UserName ?? "user");
        await audit.RecordAsync(new AuditEntry("Job.RunRequested", "BackgroundJob", key, NewValues: new { JobId = id }, UserId: user.UserId), ct);
        return id;
    }

    /// <summary>Executes the job and records the run. Called by the scheduler; throws on failure so the scheduler retries.</summary>
    public async Task<JobRunDto> ExecuteAsync(string key, string triggeredBy, CancellationToken ct = default)
    {
        var def = JobCatalog.Find(key) ?? throw new NotFoundException($"Unknown job '{key}'.");
        var now = clock.GetUtcNow().UtcDateTime;
        // Single run per job: a second trigger while one is in progress (or a retry racing a slow run) does nothing.
        var since = now.AddMinutes(-def.MaxMinutes);
        if (await db.BackgroundJobRuns.AsNoTracking().AnyAsync(r => r.JobKey == key && r.Status == BackgroundJobRunStatus.Running && r.StartedAtUtc >= since, ct))
            return new JobRunDto(0, key, triggeredBy, now, now, BackgroundJobRunStatus.Succeeded, 0, "Skipped: the previous run is still in progress.");
        var run = new BackgroundJobRun { JobKey = key, TriggeredBy = triggeredBy, StartedAtUtc = now, Status = BackgroundJobRunStatus.Running };
        db.BackgroundJobRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var runId = run.Id;
        int items = 0; string? message;
        try
        {
            if (def.NeedsServiceUser && user.UserId is null) message = "Skipped: no service user is configured (BackgroundProcessing:RunAsUserName).";
            else (items, message) = await RunCoreAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Background job {Job} failed", key);
            await FinishAsync(runId, BackgroundJobRunStatus.Failed, 0, Cut(ex.Message, 2000), CancellationToken.None);
            throw;
        }
        await FinishAsync(runId, BackgroundJobRunStatus.Succeeded, items, message, ct);
        return Map(await db.BackgroundJobRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct));
    }

    /// <summary>Closes the run by id (a set-based update): jobs may reset the shared change tracker, so the tracked row cannot be trusted to save.</summary>
    private async Task FinishAsync(long runId, BackgroundJobRunStatus status, int items, string? message, CancellationToken ct)
    {
        var finished = clock.GetUtcNow().UtcDateTime;
        await db.BackgroundJobRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status).SetProperty(r => r.ItemsProcessed, items)
            .SetProperty(r => r.Message, message).SetProperty(r => r.FinishedAtUtc, finished), ct);
    }

    private async Task<(int, string)> RunCoreAsync(string key, CancellationToken ct)
    {
        switch (key)
        {
            case JobCatalog.Dispatch:
            {
                var d = await dispatch.DispatchDueAsync(200, null, ct);
                return (d.Processed, $"{d.Sent} sent, {d.Retrying} to retry, {d.Failed} failed, {d.Skipped} skipped.");
            }
            case JobCatalog.Events: { var n = await events.QueueEventNotificationsAsync(ct); return (n, $"{n} notification(s) queued."); }
            case JobCatalog.Reminders: { var n = await events.QueueInstallmentRemindersAsync(ct); return (n, $"{n} reminder(s) queued."); }
            case JobCatalog.Digest: { var n = await events.QueueStaffDigestAsync(ct); return (n, $"{n} summary(ies) queued."); }
            case JobCatalog.Reservations: { var n = await properties.ExpireOverdueAsync(ct); return (n, $"{n} reservation(s) expired."); }
            case JobCatalog.Ledgers: return await LedgersAsync(ct);
            case JobCatalog.Gateway: { var g = await gateway.ReconcileAsync(ct); return (g.Checked, $"{g.Checked} checked, {g.Changed} changed."); }
            case JobCatalog.Postings: { var p = await postings.RunAsync(false, null, ct); return (p.Processed, $"{p.Fed} fed, {p.Posted} posted, {p.Failed} failed, {p.Skipped} skipped."); }
            case JobCatalog.Cleanup: return await CleanupAsync(ct);
            default: throw new NotFoundException($"Unknown job '{key}'.");
        }
    }

    /// <summary>Syncs the ledgers of active agreements that have an installment past its due date (the lazy receivable posting becomes scheduled).</summary>
    private async Task<(int, string)> LedgersAsync(CancellationToken ct)
    {
        var today = await payments.TodayAsync(ct);
        var ids = await (from i in db.PaymentScheduleItems.AsNoTracking()
                         join s in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals s.Id
                         join a in db.CustomerAgreements.AsNoTracking() on s.AgreementId equals a.Id
                         where s.Status == PaymentScheduleStatus.Active && a.Status == AgreementStatus.Active && i.DueDate <= today
                         select a.Id).Distinct().OrderBy(id => id).Take(500).ToListAsync(ct);
        var done = 0; var failed = 0;
        foreach (var id in ids)
        {
            try { await payments.EnsureLedgerCurrentAsync(id, ct); done++; }
            catch (Exception ex) when (ex is ConcurrencyException or DbUpdateException)
            {
                failed++;   // another money operation was busy with this agreement; the next run picks it up
                logger.LogInformation(ex, "Ledger sync of agreement {AgreementId} deferred", id);
            }
        }
        return (done, $"{done} agreement(s) checked, {failed} deferred.");
    }

    private async Task<(int, string)> CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tokens = await maintenance.PurgeExpiredRefreshTokensAsync(now.AddDays(-30), ct);
        var oldRuns = await db.BackgroundJobRuns.Where(r => r.StartedAtUtc < now.AddDays(-90)).ToListAsync(ct);
        db.BackgroundJobRuns.RemoveRange(oldRuns);
        // A delivery lease that was never released (worker crashed while sending) is retried by the dispatch job; nothing to do. Notifications whose deliveries are all
        // finished but whose status was not refreshed are corrected here.
        var stale = await db.Notifications.Where(n => n.Status == NotificationStatus.Pending && !n.Deliveries.Any(d => d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Retrying)).Take(500).ToListAsync(ct);
        foreach (var n in stale)
        {
            var statuses = await db.NotificationDeliveryLogs.AsNoTracking().Where(l => l.NotificationId == n.Id).Select(l => l.Status).ToListAsync(ct);
            n.Status = NotificationRules.Aggregate(statuses);
        }
        await db.SaveChangesAsync(ct);
        return (tokens + oldRuns.Count + stale.Count, $"{tokens} expired token(s) and {oldRuns.Count} old run record(s) removed, {stale.Count} notification status(es) corrected.");
    }

    private static JobRunDto Map(BackgroundJobRun r) => new(r.Id, r.JobKey, r.TriggeredBy, r.StartedAtUtc, r.FinishedAtUtc, r.Status, r.ItemsProcessed, r.Message);
    private static string Cut(string v, int max) => v.Length > max ? v[..max] : v;
}
