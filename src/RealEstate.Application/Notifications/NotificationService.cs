using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

/// <summary>Office-side notification management: history and delivery logs, templates, reminder settings, manual sending, retry and cancellation. All with permission and project checks.</summary>
public class NotificationService(IAppDbContext db, NotificationQueue queue, NotificationEventService events, CustomerService customers, ProjectAccessService access, IPermissionService permissions,
    ICurrentUser user, IAuditService audit, TimeProvider clock, IValidator<SaveTemplateRequest> templateValidator, IValidator<SendNotificationRequest> sendValidator,
    IValidator<CancelNotificationRequest> cancelValidator)
{
    // ------------------------------------------------------------------------------------------------
    // history
    // ------------------------------------------------------------------------------------------------
    private async Task<IQueryable<Notification>> ScopedAsync(CancellationToken ct)
    {
        IQueryable<Notification> q = db.Notifications.AsNoTracking();
        if (await access.HasAllAccessAsync(ct)) return q;
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var me = user.UserName;
        // Notifications of the user's projects, plus messages they sent themselves (a message to one customer has no single project).
        return q.Where(n => n.RecipientUserId == null && ((n.ProjectId != null && projects.Any(p => p.Id == n.ProjectId)) || (me != null && n.CreatedBy == me)));
    }

    public async Task<PagedResult<NotificationListItemDto>> ListAsync(NotificationListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        var q = await ScopedAsync(ct);
        if (r.Type is { } t) q = q.Where(n => n.Type == t);
        if (r.Status is { } s) q = q.Where(n => n.Status == s);
        if (r.CustomerId is { } c) q = q.Where(n => n.CustomerId == c);
        if (r.Channel is { } ch) q = q.Where(n => n.Deliveries.Any(d => d.Channel == ch));
        if (r.DeliveryStatus is { } ds) q = q.Where(n => n.Deliveries.Any(d => d.Status == ds));
        if (r.From is { } f) { var from = f.ToDateTime(TimeOnly.MinValue); q = q.Where(n => n.CreatedAtUtc >= from); }
        if (r.To is { } to) { var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(n => n.CreatedAtUtc < end); }
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var term = r.Search.Trim();
            q = q.Where(n => n.Title.Contains(term) || n.DedupeKey.Contains(term) || (n.CustomerId != null && db.Customers.Any(x => x.Id == n.CustomerId && (x.FullName.Contains(term) || x.CustomerCode.Contains(term)))));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(n => new NotificationListItemDto(n.Id, n.Type, n.Title, n.CustomerId, n.CustomerId == null ? null : db.Customers.Where(x => x.Id == n.CustomerId).Select(x => x.FullName).FirstOrDefault(),
                n.RecipientUserId, n.ProjectId, n.Status, n.CreatedAtUtc, n.Deliveries.Count, n.Deliveries.Count(d => d.Status == DeliveryStatus.Sent), n.Deliveries.Count(d => d.Status == DeliveryStatus.Failed)))
            .ToListAsync(ct);
        return new PagedResult<NotificationListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<NotificationDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        var n = await (await ScopedAsync(ct)).Include(x => x.Deliveries).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Notification not found.");
        var name = n.CustomerId is null ? null : await db.Customers.AsNoTracking().Where(c => c.Id == n.CustomerId).Select(c => c.FullName).FirstOrDefaultAsync(ct);
        return new NotificationDetailDto(n.Id, n.Type, n.Title, n.Body, n.CustomerId, name, n.RecipientUserId, n.ProjectId, n.AgreementId, n.RelatedType, n.RelatedId, n.DedupeKey, n.Status,
            n.ScheduledForUtc, n.CreatedAtUtc, n.CreatedBy, n.CancelReason,
            [.. n.Deliveries.OrderBy(d => d.Id).Select(d => new DeliveryDto(d.Id, d.Channel, d.Recipient, d.Subject, d.Body, d.Status, d.Attempts, d.MaxAttempts, d.NextAttemptAtUtc, d.LastAttemptAtUtc,
                d.SentAtUtc, d.ProviderReference, d.LastError))], n.RowVersion.ToToken());
    }

    public async Task<NotificationSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        var q = await ScopedAsync(ct);
        var counts = await q.GroupBy(n => n.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var since = clock.GetUtcNow().UtcDateTime.AddHours(-24);
        var logs = q.SelectMany(n => n.Deliveries);
        return new NotificationSummaryDto(counts.GetValueOrDefault(NotificationStatus.Pending), counts.GetValueOrDefault(NotificationStatus.Completed), counts.GetValueOrDefault(NotificationStatus.PartiallyFailed),
            counts.GetValueOrDefault(NotificationStatus.Failed), await logs.CountAsync(d => d.Status == DeliveryStatus.Retrying, ct), await logs.CountAsync(d => d.Status == DeliveryStatus.Failed, ct),
            await logs.CountAsync(d => d.Status == DeliveryStatus.Sent && d.SentAtUtc >= since, ct));
    }

    // ------------------------------------------------------------------------------------------------
    // retry / cancel
    // ------------------------------------------------------------------------------------------------
    public async Task<NotificationDetailDto> RetryDeliveryAsync(long deliveryId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsManage, ct);
        var log = await db.NotificationDeliveryLogs.FirstOrDefaultAsync(l => l.Id == deliveryId, ct) ?? throw new NotFoundException("Delivery not found.");
        var scoped = await ScopedAsync(ct);
        var n = await scoped.FirstOrDefaultAsync(x => x.Id == log.NotificationId, ct) ?? throw new NotFoundException("Delivery not found.");
        if (n.Status == NotificationStatus.Cancelled) throw new DomainException("A cancelled notification cannot be retried.");
        if (log.Status != DeliveryStatus.Failed) throw new DomainException("Only failed deliveries can be retried.");
        var now = clock.GetUtcNow().UtcDateTime;
        var before = new { Status = log.Status.ToString(), log.Attempts, log.LastError };
        log.Status = DeliveryStatus.Retrying; log.Attempts = 0; log.NextAttemptAtUtc = now; log.LastError = null;
        var tracked = await db.Notifications.FirstAsync(x => x.Id == n.Id, ct);
        tracked.Status = NotificationStatus.Pending;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Notification.DeliveryRetried", "NotificationDeliveryLog", deliveryId.ToString(), before, new { Status = "Retrying" }, UserId: user.UserId), ct);
        return await GetAsync(n.Id, ct);
    }

    public async Task<NotificationDetailDto> CancelAsync(long id, CancelNotificationRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsManage, ct);
        await cancelValidator.ValidateOrThrowAsync(r, ct);
        var scoped = await ScopedAsync(ct);
        if (!await scoped.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Notification not found.");
        var n = await db.Notifications.Include(x => x.Deliveries).FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(n, r.RowVersion);
        if (n.Status != NotificationStatus.Pending) throw new DomainException("Only a notification that is still being delivered can be cancelled.");
        foreach (var d in n.Deliveries.Where(d => d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying)) d.Status = DeliveryStatus.Cancelled;
        n.Status = NotificationStatus.Cancelled; n.CancelReason = r.Reason.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Notification.Cancelled", "Notification", id.ToString(), new { Status = "Pending" }, new { Status = "Cancelled" }, r.Reason.Trim(), UserId: user.UserId), ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------------------------------------
    // manual sending
    // ------------------------------------------------------------------------------------------------
    /// <summary>Sends an announcement, possession notice or message to one customer, to the owners of a project or of a property, or (announcements only, all-project access) to every customer.</summary>
    public async Task<SendNotificationResultDto> SendAsync(SendNotificationRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsSend, ct);
        await sendValidator.ValidateOrThrowAsync(r, ct);

        long? projectId = r.ProjectId;
        IQueryable<BookingCustomer> owners = db.BookingCustomers.AsNoTracking().Where(bc => bc.IsActive);
        IQueryable<Booking> bookings = db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Confirmed);
        List<long> customerIds;
        if (r.CustomerId is { } cid)
        {
            await customers.EnsureAccessAsync(cid, ct);
            customerIds = [cid];
        }
        else if (r.PropertyId is { } propId)
        {
            var p = await db.Properties.AsNoTracking().Where(x => x.Id == propId).Select(x => new { x.ProjectId }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Property not found.");
            await access.EnsureAccessAsync(p.ProjectId, ct);
            projectId = p.ProjectId;
            customerIds = await (from bp in db.BookingProperties.AsNoTracking() join b in bookings on bp.BookingId equals b.Id join bc in owners on b.Id equals bc.BookingId
                                 where bp.PropertyId == propId select bc.CustomerId).Distinct().ToListAsync(ct);
        }
        else if (r.ProjectId is { } pid)
        {
            await access.EnsureAccessAsync(pid, ct);
            customerIds = await (from b in bookings join bc in owners on b.Id equals bc.BookingId where b.ProjectId == pid select bc.CustomerId).Distinct().ToListAsync(ct);
        }
        else
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("A company-wide message needs access to all projects.");
            customerIds = await (from b in bookings join bc in owners on b.Id equals bc.BookingId select bc.CustomerId).Distinct().ToListAsync(ct);
        }
        if (customerIds.Count == 0) throw new DomainException("There are no customers to send this to.");
        if (customerIds.Count > 5000) throw new DomainException("A message can go to at most 5000 customers at once; narrow it to a project.");

        var batch = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? Guid.NewGuid().ToString("N") : r.IdempotencyKey.Trim();
        var names = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id) && c.Status != CustomerStatus.Blocked).Select(c => new { c.Id, c.FullName }).ToListAsync(ct);
        int queued = 0, dup = 0;
        foreach (var c in names)
        {
            var data = new Dictionary<string, string> { ["CustomerName"] = c.FullName, ["ProjectName"] = "", ["PropertyCode"] = "", ["AgreementNumber"] = "", ["Amount"] = "", ["DueDate"] = "", ["DaysOverdue"] = "0", ["ReceiptNumber"] = "" };
            if (await queue.EnqueueAsync(new NotificationDraft(r.Type, $"manual:{batch}:{(int)r.Type}:c{c.Id}", c.Id, null, projectId, null, r.Title.Trim(), r.Message.Trim(), data, "Manual", null), ct) is null) dup++; else queued++;
        }
        await audit.RecordAsync(new AuditEntry("Notification.Sent", "Notification", null, NewValues: new { r.Type, Recipients = names.Count, queued, duplicates = dup, r.ProjectId, r.PropertyId, r.CustomerId, r.Title }, UserId: user.UserId), ct);
        return new SendNotificationResultDto(names.Count, queued, dup);
    }

    // ------------------------------------------------------------------------------------------------
    // templates and settings
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        return await db.NotificationTemplates.AsNoTracking().OrderBy(t => t.Type).ThenBy(t => t.Channel).ThenBy(t => t.Language)
            .Select(t => new TemplateDto(t.Id, t.Type, t.Channel, t.Language, t.Subject, t.Body, t.IsActive, t.RowVersion.ToToken())).ToListAsync(ct);
    }

    public async Task<TemplateDto> GetTemplateAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        return await db.NotificationTemplates.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new TemplateDto(t.Id, t.Type, t.Channel, t.Language, t.Subject, t.Body, t.IsActive, t.RowVersion.ToToken())).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Template not found.");
    }

    public async Task<TemplateDto> SaveTemplateAsync(SaveTemplateRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsManage, ct);
        await templateValidator.ValidateOrThrowAsync(r, ct);
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Templates are company-wide and need access to all projects.");
        var lang = string.IsNullOrWhiteSpace(r.Language) ? NotificationQueue.DefaultLanguage : r.Language.Trim().ToLowerInvariant();
        var t = await db.NotificationTemplates.FirstOrDefaultAsync(x => x.Type == r.Type && x.Channel == r.Channel && x.Language == lang, ct);
        object? before = null;
        if (t is null) { t = new NotificationTemplate { Type = r.Type, Channel = r.Channel, Language = lang }; db.NotificationTemplates.Add(t); }
        else { db.UseRowVersion(t, r.RowVersion); before = new { t.Subject, t.Body, t.IsActive }; }
        t.Subject = r.Subject.Trim(); t.Body = r.Body.Trim(); t.IsActive = r.IsActive;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(before is null ? "NotificationTemplate.Created" : "NotificationTemplate.Updated", "NotificationTemplate", t.Id.ToString(), before,
            new { t.Type, t.Channel, t.Language, t.Subject, t.Body, t.IsActive }, UserId: user.UserId), ct);
        return await GetTemplateAsync(t.Id, ct);
    }

    public async Task<ReminderSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        return await events.GetSettingsAsync(ct);
    }

    public async Task<ReminderSettingsDto> SaveSettingsAsync(ReminderSettingsDto r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsManage, ct);
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Reminder settings are company-wide and need access to all projects.");
        var errors = new Dictionary<string, string[]>();
        if (r.UpcomingDaysBefore is < 0 or > 60) errors[nameof(r.UpcomingDaysBefore)] = ["Use 0-60 days."];
        if (r.OverdueRepeatDays is < 1 or > 60) errors[nameof(r.OverdueRepeatDays)] = ["Use 1-60 days."];
        if (r.EventLookbackDays is < 1 or > 60) errors[nameof(r.EventLookbackDays)] = ["Use 1-60 days."];
        if (errors.Count > 0) throw new RequestValidationException(errors);
        var before = await events.GetSettingsAsync(ct);
        await UpsertAsync(NotificationEventService.UpcomingDaysKey, r.UpcomingDaysBefore, "Days before the due date an installment reminder is sent", ct);
        await UpsertAsync(NotificationEventService.OverdueRepeatKey, r.OverdueRepeatDays, "Days between repeated overdue reminders", ct);
        await UpsertAsync(NotificationEventService.LookbackKey, r.EventLookbackDays, "Days an event scan looks back for new business events", ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Notification.SettingsChanged", "ApplicationSetting", "Notifications", before, r, UserId: user.UserId), ct);
        return r;
    }

    private async Task UpsertAsync(string key, int value, string description, CancellationToken ct)
    {
        var s = await db.ApplicationSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Description = description });
        else s.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------------------------------------
    // staff inbox
    // ------------------------------------------------------------------------------------------------
    public async Task<PagedResult<StaffInboxItemDto>> InboxAsync(PagedRequest r, CancellationToken ct = default)
    {
        var uid = user.UserId ?? throw new ForbiddenException("Authentication required.");
        var q = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == uid && n.Deliveries.Any(d => d.Channel == NotificationChannel.InApp && d.Status != DeliveryStatus.Cancelled));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(n => new StaffInboxItemDto(n.Id, n.Type, n.Title, n.Body, n.CreatedAtUtc, n.ReadAtUtc)).ToListAsync(ct);
        return new PagedResult<StaffInboxItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task MarkInboxReadAsync(long id, CancellationToken ct = default)
    {
        var uid = user.UserId ?? throw new ForbiddenException("Authentication required.");
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientUserId == uid, ct) ?? throw new NotFoundException("Notification not found.");
        if (n.ReadAtUtc is not null) return;
        n.ReadAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Channel choices of customers (their own, through the customer application; or edited by the office on their behalf) and of staff (digest subscription).</summary>
public class NotificationPreferenceService(IAppDbContext db, PortalContext portal, CustomerService customers, IPermissionService permissions, ICurrentUser user, IAuditService audit,
    IValidator<SavePreferencesRequest> validator)
{
    private static readonly NotificationType[] CustomerTypes =
        [NotificationType.InstallmentUpcoming, NotificationType.InstallmentOverdue, NotificationType.PaymentConfirmation, NotificationType.BookingConfirmation,
         NotificationType.AgreementAmendment, NotificationType.PossessionNotice, NotificationType.CompanyAnnouncement];
    private static readonly NotificationChannel[] CustomerChannels = [NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Push];
    private static readonly NotificationChannel[] StaffChannels = [NotificationChannel.InApp, NotificationChannel.Email];

    // ----- customer -----
    public async Task<IReadOnlyList<PreferenceDto>> GetMineAsCustomerAsync(CancellationToken ct = default) => await CustomerMatrixAsync((await portal.RequireAsync(ct)).CustomerId, ct);

    public async Task<IReadOnlyList<PreferenceDto>> SaveMineAsCustomerAsync(SavePreferencesRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await validator.ValidateOrThrowAsync(r, ct);
        await SaveCustomerAsync(me.CustomerId, r, ct);
        await audit.RecordAsync(new AuditEntry("NotificationPreference.Changed", "Customer", me.CustomerId.ToString(), NewValues: r.Preferences, UserId: me.UserId), ct);
        return await CustomerMatrixAsync(me.CustomerId, ct);
    }

    public async Task<IReadOnlyList<PreferenceDto>> GetForCustomerAsync(long customerId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsView, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        return await CustomerMatrixAsync(customerId, ct);
    }

    public async Task<IReadOnlyList<PreferenceDto>> SaveForCustomerAsync(long customerId, SavePreferencesRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.NotificationsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        await validator.ValidateOrThrowAsync(r, ct);
        await SaveCustomerAsync(customerId, r, ct);
        await audit.RecordAsync(new AuditEntry("NotificationPreference.ChangedByOffice", "Customer", customerId.ToString(), NewValues: r.Preferences, UserId: user.UserId), ct);
        return await CustomerMatrixAsync(customerId, ct);
    }

    private async Task<IReadOnlyList<PreferenceDto>> CustomerMatrixAsync(long customerId, CancellationToken ct)
    {
        var key = NotificationRules.OwnerKey(customerId, null);
        var rows = await db.NotificationPreferences.AsNoTracking().Where(p => p.OwnerKey == key).Select(p => new { p.Channel, p.TypeKey, p.IsEnabled }).ToListAsync(ct);
        var list = new List<PreferenceDto> { new(NotificationChannel.InApp, null, true, true) };
        foreach (var ch in CustomerChannels)
            foreach (var t in CustomerTypes)
            {
                var specific = rows.FirstOrDefault(p => p.Channel == ch && p.TypeKey == (int)t);
                var general = rows.FirstOrDefault(p => p.Channel == ch && p.TypeKey == 0);
                list.Add(new PreferenceDto(ch, t, specific != null ? specific.IsEnabled : general == null || general.IsEnabled, false));
            }
        return list;
    }

    private async Task SaveCustomerAsync(long customerId, SavePreferencesRequest r, CancellationToken ct)
    {
        var allowed = r.Preferences.Where(p => p.Channel != NotificationChannel.InApp && CustomerChannels.Contains(p.Channel) && (p.Type is null || CustomerTypes.Contains(p.Type.Value))).ToList();
        await UpsertAsync(NotificationRules.OwnerKey(customerId, null), customerId, null, allowed, ct);
    }

    // ----- staff -----
    public async Task<IReadOnlyList<PreferenceDto>> GetMineAsStaffAsync(CancellationToken ct = default)
    {
        var uid = user.UserId ?? throw new ForbiddenException("Authentication required.");
        var key = NotificationRules.OwnerKey(null, uid);
        var rows = await db.NotificationPreferences.AsNoTracking().Where(p => p.OwnerKey == key && p.TypeKey == (int)NotificationType.StaffDigest).ToListAsync(ct);
        // Staff digests are opt-in: no row means off.
        return [.. StaffChannels.Select(ch => new PreferenceDto(ch, NotificationType.StaffDigest, rows.Any(p => p.Channel == ch && p.IsEnabled), false))];
    }

    public async Task<IReadOnlyList<PreferenceDto>> SaveMineAsStaffAsync(SavePreferencesRequest r, CancellationToken ct = default)
    {
        var uid = user.UserId ?? throw new ForbiddenException("Authentication required.");
        await validator.ValidateOrThrowAsync(r, ct);
        if (r.Preferences.Any(p => p.IsEnabled) && !(await permissions.HasPermissionAsync(uid, Permissions.ReportsView, ct) && await permissions.HasPermissionAsync(uid, Permissions.ProjectsAccessAll, ct)))
            throw new ForbiddenException("The daily summary shows company-wide figures: it needs the reports permission and access to all projects.");
        var picked = r.Preferences.Where(p => StaffChannels.Contains(p.Channel)).Select(p => new SavePreferenceRequest(p.Channel, NotificationType.StaffDigest, p.IsEnabled)).ToList();
        await UpsertAsync(NotificationRules.OwnerKey(null, uid), null, uid, picked, ct);
        await audit.RecordAsync(new AuditEntry("NotificationPreference.Changed", "User", uid.ToString(), NewValues: picked, UserId: uid), ct);
        return await GetMineAsStaffAsync(ct);
    }

    private async Task UpsertAsync(string ownerKey, long? customerId, Guid? userId, IReadOnlyList<SavePreferenceRequest> items, CancellationToken ct)
    {
        var rows = await db.NotificationPreferences.Where(p => p.OwnerKey == ownerKey).ToListAsync(ct);
        // General choices (all types) first, so that a specific choice is compared with the effective general one.
        foreach (var i in items.GroupBy(x => (x.Channel, Type: x.Type is null ? 0 : (int)x.Type.Value)).Select(g => g.Last()).OrderBy(x => x.Type is null ? 0 : 1))
        {
            var typeKey = i.Type is null ? 0 : (int)i.Type.Value;
            var row = rows.FirstOrDefault(p => p.Channel == i.Channel && p.TypeKey == typeKey);
            if (row is not null) { row.IsEnabled = i.IsEnabled; continue; }
            // Customers: on is the default, so a row is only needed when the choice differs from what would apply anyway (all-types choice, else on).
            var inherited = typeKey == 0 ? true : rows.FirstOrDefault(p => p.Channel == i.Channel && p.TypeKey == 0)?.IsEnabled ?? true;
            if (userId is null && i.IsEnabled == inherited) continue;
            row = new NotificationPreference { CustomerId = customerId, UserId = userId, OwnerKey = ownerKey, Channel = i.Channel, TypeKey = typeKey, IsEnabled = i.IsEnabled };
            db.NotificationPreferences.Add(row);
            rows.Add(row);
        }
        await db.SaveWithConcurrencyAsync(ct);
    }
}
