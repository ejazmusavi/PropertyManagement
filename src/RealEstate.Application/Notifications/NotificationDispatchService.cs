using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

/// <summary>The built-in in-app channel: customers get a <see cref="CustomerNotification"/> (the list the customer application already reads); staff read the Notification itself in the staff inbox.</summary>
public class InAppNotificationSender(IAppDbContext db, TimeProvider clock) : INotificationChannelSender
{
    public NotificationChannel Channel => NotificationChannel.InApp;

    public async Task<SendResult> SendAsync(DeliveryMessage m, CancellationToken ct = default)
    {
        if (m.CustomerId is not { } customerId) return SendResult.Sent("staff-inbox");
        var existing = await db.CustomerNotifications.AsNoTracking().Where(x => x.RelatedType == "Notification" && x.RelatedId == m.NotificationId && x.CustomerId == customerId)
            .Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return SendResult.Sent($"customer-notification:{id}");   // idempotent: a retry never shows twice
        const NotificationKind kind = NotificationKind.General;
        var row = new CustomerNotification
        {
            CustomerId = customerId, Kind = kind, Title = m.Subject.Length > 200 ? m.Subject[..200] : m.Subject, Message = m.Body.Length > 2000 ? m.Body[..2000] : m.Body,
            RelatedType = "Notification", RelatedId = m.NotificationId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.CustomerNotifications.Add(row);
        await db.SaveChangesAsync(ct);
        return SendResult.Sent($"customer-notification:{row.Id}");
    }
}

/// <summary>
/// Sends the due deliveries (system level, called by the dispatch job). Every attempt first claims the row with its concurrency token and a short lease,
/// so two workers never send the same delivery; a transient failure is retried with back-off up to the maximum number of attempts; a channel that is
/// switched off or not configured ends as Skipped (not as an error). Safe to run repeatedly.
/// </summary>
public class NotificationDispatchService(IAppDbContext db, IEnumerable<INotificationChannelSender> senders, TimeProvider clock, ILogger<NotificationDispatchService> logger)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly Dictionary<NotificationChannel, INotificationChannelSender> byChannel = senders.GroupBy(s => s.Channel).ToDictionary(g => g.Key, g => g.First());

    public async Task<DispatchResultDto> DispatchDueAsync(int batchSize = 100, long? notificationId = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.NotificationDeliveryLogs.AsNoTracking()
            .Where(l => (l.Status == DeliveryStatus.Pending || l.Status == DeliveryStatus.Retrying) && l.NextAttemptAtUtc <= now)
            .Where(l => notificationId == null || l.NotificationId == notificationId)
            .Join(db.Notifications.AsNoTracking().Where(n => n.Status == NotificationStatus.Pending), l => l.NotificationId, n => n.Id, (l, n) => l.Id)
            .OrderBy(id => id).Take(Math.Clamp(batchSize, 1, 500)).ToListAsync(ct);
        int sent = 0, retrying = 0, failed = 0, skipped = 0;
        foreach (var id in ids)
        {
            switch (await DeliverAsync(id, ct))
            {
                case DeliveryStatus.Sent: sent++; break;
                case DeliveryStatus.Retrying: retrying++; break;
                case DeliveryStatus.Failed: failed++; break;
                case DeliveryStatus.Skipped: skipped++; break;
            }
        }
        return new DispatchResultDto(ids.Count, sent, retrying, failed, skipped);
    }

    /// <summary>Sends one delivery now (also used by the manual retry). Returns the resulting status, or Pending when another worker holds it.</summary>
    public async Task<DeliveryStatus> DeliverAsync(long deliveryId, CancellationToken ct = default)
    {
        var log = await db.NotificationDeliveryLogs.FirstOrDefaultAsync(l => l.Id == deliveryId, ct);
        if (log is null || log.Status is not (DeliveryStatus.Pending or DeliveryStatus.Retrying)) return log?.Status ?? DeliveryStatus.Cancelled;
        var notification = await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == log.NotificationId, ct);
        if (notification.Status == NotificationStatus.Cancelled) return log.Status;

        var now = clock.GetUtcNow().UtcDateTime;
        if (log.NextAttemptAtUtc > now) return log.Status;   // not due yet, or leased by a worker that is sending it right now
        // Claim: attempts + lease, protected by the row version.
        log.Attempts++; log.LastAttemptAtUtc = now; log.NextAttemptAtUtc = now + Lease;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.Entry(log).State = EntityState.Detached; return DeliveryStatus.Pending; }

        SendResult result;
        try
        {
            result = byChannel.TryGetValue(log.Channel, out var sender)
                ? await sender.SendAsync(new DeliveryMessage(log.Id, log.NotificationId, log.Channel, log.Recipient ?? "", log.Subject, log.Body, notification.CustomerId, notification.RecipientUserId, null), ct)
                : SendResult.Skip($"No adapter is installed for the {log.Channel} channel.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notification delivery {DeliveryId} failed on {Channel}", log.Id, log.Channel);
            result = SendResult.Transient(ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }

        var done = clock.GetUtcNow().UtcDateTime;
        if (result.Success) { log.Status = DeliveryStatus.Sent; log.SentAtUtc = done; log.ProviderReference = Cut(result.ProviderReference, 200); log.LastError = null; }
        else if (result.Skipped) { log.Status = DeliveryStatus.Skipped; log.LastError = Cut(result.Error, 1000); }
        else if (result.Permanent || log.Attempts >= log.MaxAttempts) { log.Status = DeliveryStatus.Failed; log.LastError = Cut(result.Error, 1000); }
        else { log.Status = DeliveryStatus.Retrying; log.NextAttemptAtUtc = done + NotificationRules.Backoff(log.Attempts); log.LastError = Cut(result.Error, 1000); }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // The lease expired and another worker took the delivery over; its outcome counts (at-least-once delivery).
            logger.LogWarning("Delivery {DeliveryId} was taken over by another worker while it was being sent", log.Id);
            db.Entry(log).State = EntityState.Detached;
            return DeliveryStatus.Pending;
        }
        await RefreshStatusAsync(log.NotificationId, ct);
        return log.Status;
    }

    public async Task RefreshStatusAsync(long notificationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var n = await db.Notifications.FirstAsync(x => x.Id == notificationId, ct);
            if (n.Status == NotificationStatus.Cancelled) return;
            var statuses = await db.NotificationDeliveryLogs.AsNoTracking().Where(l => l.NotificationId == notificationId).Select(l => l.Status).ToListAsync(ct);
            var next = NotificationRules.Aggregate(statuses);
            if (next == n.Status) return;
            n.Status = next;
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateConcurrencyException) { db.Entry(n).State = EntityState.Detached; }   // another worker updated it first: recompute from the current rows
        }
    }

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}
