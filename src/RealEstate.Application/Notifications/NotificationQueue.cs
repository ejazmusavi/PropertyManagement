using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Users;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

/// <summary>
/// The single place where notifications are queued (system level: used by the scheduled jobs and by the office services after their own permission checks).
/// Queuing is idempotent per <see cref="NotificationDraft.DedupeKey"/>: the same event never creates a second notification, even when a job runs twice or
/// two workers race (the unique index decides). Text is rendered here from the active templates and kept on each delivery log.
/// </summary>
public class NotificationQueue(IAppDbContext db, IUserQueryService users, TimeProvider clock)
{
    public const string DefaultLanguage = "en";
    private static readonly NotificationChannel[] AllChannels = [NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Push];

    /// <summary>Queues the notification; returns null when one with the same key already exists.</summary>
    public async Task<Notification?> EnqueueAsync(NotificationDraft d, CancellationToken ct = default)
    {
        if ((d.CustomerId is null) == (d.UserId is null)) throw new ArgumentException("A notification has exactly one recipient (customer or user).");
        if (await db.Notifications.AnyAsync(n => n.DedupeKey == d.DedupeKey, ct)) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        var ownerKey = NotificationRules.OwnerKey(d.CustomerId, d.UserId);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => p.OwnerKey == ownerKey).Select(p => new { p.Channel, p.TypeKey, p.IsEnabled }).ToListAsync(ct);
        var prefTuples = prefs.Select(p => (p.Channel, p.TypeKey, p.IsEnabled)).ToList();

        var templates = await db.NotificationTemplates.AsNoTracking().Where(t => t.Type == d.Type && t.IsActive && t.Language == DefaultLanguage).ToListAsync(ct);
        var candidates = templates.Select(t => t.Channel).Where(c => d.OnlyChannels is null || d.OnlyChannels.Contains(c)).ToList();
        // Staff digests are opt-in: only the channels the user switched on.
        var channels = d.Type == NotificationType.StaffDigest
            ? candidates.Where(c => prefs.Any(p => p.Channel == c && p.TypeKey == (int)NotificationType.StaffDigest && p.IsEnabled)).ToList()
            : [.. NotificationRules.ResolveChannels(d.Type, candidates, prefTuples)];

        (string? Email, string? Mobile) contact = (null, null);
        if (d.CustomerId is { } cid)
        {
            var c = await db.Customers.AsNoTracking().Where(x => x.Id == cid).Select(x => new { x.Email, x.Mobile }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Customer not found.");
            contact = (c.Email, c.Mobile);
        }
        else if (d.UserId is { } uid)
        {
            var u = (await users.LookupAsync([uid], ct)).FirstOrDefault();
            contact = (u?.Email, null);
        }

        var notification = new Notification
        {
            Type = d.Type, CustomerId = d.CustomerId, RecipientUserId = d.UserId, ProjectId = d.ProjectId, AgreementId = d.AgreementId,
            Title = Trim(d.Title, 200), Body = Trim(d.Message, 2000), DataJson = JsonSerializer.Serialize(d.Data), RelatedType = d.RelatedType, RelatedId = d.RelatedId,
            DedupeKey = d.DedupeKey, ScheduledForUtc = d.ScheduledForUtc ?? now, Status = NotificationStatus.Pending, CreatedAtUtc = now,
        };
        foreach (var channel in channels)
        {
            var t = templates.First(x => x.Channel == channel);
            var data = new Dictionary<string, string>(d.Data) { ["Title"] = d.Title, ["Message"] = d.Message };
            var log = new NotificationDeliveryLog
            {
                Channel = channel, Subject = Trim(NotificationRules.Render(t.Subject, data), 200), Body = Trim(NotificationRules.Render(t.Body, data), 2000),
                NextAttemptAtUtc = notification.ScheduledForUtc, CreatedAtUtc = now, Status = DeliveryStatus.Pending,
            };
            switch (channel)
            {
                case NotificationChannel.InApp: log.Recipient = "in-app"; break;
                case NotificationChannel.Email: Address(log, contact.Email, "No e-mail address on file", now); break;
                case NotificationChannel.Sms or NotificationChannel.WhatsApp: Address(log, contact.Mobile, "No mobile number on file", now); break;
                case NotificationChannel.Push: log.Recipient = d.CustomerId is { } c2 ? $"customer:{c2}" : $"user:{d.UserId:N}"; break;
            }
            notification.Deliveries.Add(log);
        }
        if (notification.Deliveries.Count > 0 && notification.Deliveries.All(x => x.Status == DeliveryStatus.Skipped)) notification.Status = NotificationStatus.Completed;
        if (notification.Deliveries.Count == 0) notification.Status = NotificationStatus.Completed;

        db.Notifications.Add(notification);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex, "DedupeKey"))
        {
            db.Entry(notification).State = EntityState.Detached;
            foreach (var l in notification.Deliveries) db.Entry(l).State = EntityState.Detached;
            return null;
        }
        return notification;
    }

    private static void Address(NotificationDeliveryLog log, string? address, string missing, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(address)) { log.Status = DeliveryStatus.Skipped; log.LastError = missing; log.LastAttemptAtUtc = now; return; }
        log.Recipient = address.Trim();
    }

    private static string Trim(string v, int max) => v.Length > max ? v[..max] : v;
}
