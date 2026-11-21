using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RealEstate.Application.Common;
using RealEstate.Application.Notifications;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 11 notifications against MySQL: queueing, preferences, dispatch with retry, event scans, reminders, digest, authorization.</summary>
[Collection("phase1")]
public class Phase11NotificationTests(Phase1Fixture fx)
{
    private readonly Phase11Kit k = new(fx);
    private static T Svc<T>(IServiceScope s) where T : notnull => Phase11Kit.Svc<T>(s);

    /// <summary>A channel adapter whose outcome the test scripts.</summary>
    private sealed class Scripted(NotificationChannel channel, Func<DeliveryMessage, SendResult> script) : INotificationChannelSender
    {
        public int Calls;
        public NotificationChannel Channel => channel;
        public async Task<SendResult> SendAsync(DeliveryMessage m, CancellationToken ct = default) { Interlocked.Increment(ref Calls); await Task.Delay(50, ct); return script(m); }
    }

    private static NotificationDispatchService Dispatcher(IServiceScope s, params INotificationChannelSender[] senders) =>
        new(Svc<IAppDbContext>(s), senders, TimeProvider.System, NullLogger<NotificationDispatchService>.Instance);

    private static NotificationDraft Draft(long customerId, string key, NotificationType type = NotificationType.InstallmentUpcoming, params NotificationChannel[] only) =>
        new(type, key, customerId, null, null, null, "Reminder", "Your installment is due.", new Dictionary<string, string> { ["CustomerName"] = "Ali Khan", ["Amount"] = "Rs 10,000.00", ["DueDate"] = "2026-12-01", ["AgreementNumber"] = "AG-1" },
            OnlyChannels: only.Length == 0 ? null : only);

    private async Task<Notification?> QueueAsync(NotificationDraft d)
    {
        using var s = fx.Scope();
        return await Svc<NotificationQueue>(s).EnqueueAsync(d);
    }

    private async Task<Notification> LoadAsync(long id)
    {
        using var s = fx.Scope();
        return await Svc<AppDbContext>(s).Notifications.AsNoTracking().Include(n => n.Deliveries).FirstAsync(n => n.Id == id);
    }

    // ------------------------------------------------------------------------------------------------ seed data
    [MySqlFact]
    public async Task SeedData_TemplatesAndPermissions_AreInPlace()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        Assert.Equal(NotificationTemplateDefaults.All.Count, await db.NotificationTemplates.CountAsync());
        foreach (var t in Enum.GetValues<NotificationType>().Where(t => t != NotificationType.StaffDigest))
            foreach (var c in Enum.GetValues<NotificationChannel>())
                Assert.True(await db.NotificationTemplates.AnyAsync(x => x.Type == t && x.Channel == c && x.Language == "en" && x.IsActive), $"{t}/{c}");
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        var director = await Perms(RoleNames.Director);
        foreach (var p in new[] { Permissions.NotificationsView, Permissions.NotificationsManage, Permissions.NotificationsSend, Permissions.JobsManage, Permissions.GisVerify }) Assert.Contains(p, director);
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
        // seeding again changes nothing (idempotent)
        await Svc<DatabaseInitializer>(s).SyncSystemDataAsync();
        Assert.Equal(NotificationTemplateDefaults.All.Count, await db.NotificationTemplates.CountAsync());
        Assert.Equal(4, await db.SpatialReferenceSystems.CountAsync(x => x.IsSystem));
    }

    // ------------------------------------------------------------------------------------------------ queueing
    [MySqlFact]
    public async Task Enqueue_RendersPerChannel_AndIsIdempotentPerKey()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var key = "test-" + Guid.NewGuid().ToString("N");
        var n = await QueueAsync(Draft(c, key));
        Assert.NotNull(n);
        Assert.Null(await QueueAsync(Draft(c, key)));                 // the same business event is never queued twice

        var loaded = await LoadAsync(n!.Id);
        Assert.Equal(NotificationStatus.Pending, loaded.Status);
        Assert.Equal(5, loaded.Deliveries.Count);
        Assert.Equal(["Email", "InApp", "Push", "Sms", "WhatsApp"], loaded.Deliveries.Select(d => d.Channel.ToString()).Order());
        var email = loaded.Deliveries.Single(d => d.Channel == NotificationChannel.Email);
        Assert.Equal("c@example.com", email.Recipient);
        Assert.Contains("Ali Khan", email.Body);
        Assert.Contains("Rs 10,000.00", email.Body);
        Assert.Equal("Installment due on 2026-12-01", email.Subject);
        Assert.Equal("0300-1234567", loaded.Deliveries.Single(d => d.Channel == NotificationChannel.Sms).Recipient);
        Assert.All(loaded.Deliveries, d => Assert.Equal(DeliveryStatus.Pending, d.Status));
    }

    [MySqlFact]
    public async Task Enqueue_WithoutAnAddress_SkipsThatChannelOnly()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w, email: null, mobile: "0300-7654321");
        var n = await QueueAsync(Draft(c, "noaddr-" + Guid.NewGuid().ToString("N")));
        var email = (await LoadAsync(n!.Id)).Deliveries.Single(d => d.Channel == NotificationChannel.Email);
        Assert.Equal(DeliveryStatus.Skipped, email.Status);
        Assert.Contains("e-mail", email.LastError);
        Assert.Equal(DeliveryStatus.Pending, (await LoadAsync(n.Id)).Deliveries.Single(d => d.Channel == NotificationChannel.Sms).Status);
    }

    [MySqlFact]
    public async Task Enqueue_Concurrent_CreatesExactlyOneNotification()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var key = "race-" + Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => QueueAsync(Draft(c, key)))));
        Assert.Equal(1, results.Count(r => r is not null));
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        var rows = await db.Notifications.Where(n => n.DedupeKey == key).Include(n => n.Deliveries).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(5, rows[0].Deliveries.Count);
    }

    [MySqlFact]
    public async Task Preferences_SwitchChannelsOff_PerTypeOrForAll_AndInAppStaysOn()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        await k.Do(w.Director, async s =>
        {
            var prefs = Svc<NotificationPreferenceService>(s);
            var before = await prefs.GetForCustomerAsync(c);
            Assert.Contains(before, p => p is { Channel: NotificationChannel.InApp, Locked: true, IsEnabled: true });
            await prefs.SaveForCustomerAsync(c, new SavePreferencesRequest(
            [
                new SavePreferenceRequest(NotificationChannel.Sms, null, false),                                     // no SMS at all
                new SavePreferenceRequest(NotificationChannel.WhatsApp, NotificationType.InstallmentOverdue, false), // no WhatsApp for overdue
                new SavePreferenceRequest(NotificationChannel.Sms, NotificationType.InstallmentOverdue, true),       // ... but SMS for overdue is wanted
                new SavePreferenceRequest(NotificationChannel.InApp, null, false),                                   // ignored: in-app cannot be switched off
            ]));
            var after = await prefs.GetForCustomerAsync(c);
            Assert.True(after.Single(p => p.Channel == NotificationChannel.Sms && p.Type == NotificationType.InstallmentOverdue).IsEnabled);
            Assert.False(after.Single(p => p.Channel == NotificationChannel.Sms && p.Type == NotificationType.PaymentConfirmation).IsEnabled);
            Assert.False(after.Single(p => p.Channel == NotificationChannel.WhatsApp && p.Type == NotificationType.InstallmentOverdue).IsEnabled);
            Assert.True(after.Single(p => p.Channel == NotificationChannel.WhatsApp && p.Type == NotificationType.InstallmentUpcoming).IsEnabled);
        });
        var overdue = await LoadAsync((await QueueAsync(Draft(c, "pref1-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentOverdue)))!.Id);
        Assert.Contains(overdue.Deliveries, d => d.Channel == NotificationChannel.Sms);
        Assert.DoesNotContain(overdue.Deliveries, d => d.Channel == NotificationChannel.WhatsApp);
        Assert.Contains(overdue.Deliveries, d => d.Channel == NotificationChannel.InApp);
        var upcoming = await LoadAsync((await QueueAsync(Draft(c, "pref2-" + Guid.NewGuid().ToString("N"))))!.Id);
        Assert.DoesNotContain(upcoming.Deliveries, d => d.Channel == NotificationChannel.Sms);
        Assert.Contains(upcoming.Deliveries, d => d.Channel == NotificationChannel.WhatsApp);
        // someone without notifications.manage cannot change a customer's choices
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Sales, s => Svc<NotificationPreferenceService>(s).SaveForCustomerAsync(c, new SavePreferencesRequest([]))));
    }

    [MySqlFact]
    public async Task PaymentConfirmation_DoesNotDuplicateTheInAppNoticeOfPhase10()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await LoadAsync((await QueueAsync(Draft(c, "pay-" + Guid.NewGuid().ToString("N"), NotificationType.PaymentConfirmation)))!.Id);
        Assert.DoesNotContain(n.Deliveries, d => d.Channel == NotificationChannel.InApp);
        Assert.Contains(n.Deliveries, d => d.Channel == NotificationChannel.Email);
    }

    // ------------------------------------------------------------------------------------------------ dispatch
    [MySqlFact]
    public async Task Dispatch_InApp_WritesTheCustomerNoticeOnce_AndUnconfiguredChannelsAreSkipped()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "inapp-" + Guid.NewGuid().ToString("N")));
        for (var i = 0; i < 2; i++)
        {
            using var s = fx.Scope();
            await Svc<NotificationDispatchService>(s).DispatchDueAsync(500, n!.Id);
        }
        var loaded = await LoadAsync(n!.Id);
        Assert.Equal(DeliveryStatus.Sent, loaded.Deliveries.Single(d => d.Channel == NotificationChannel.InApp).Status);
        Assert.All(loaded.Deliveries.Where(d => d.Channel != NotificationChannel.InApp), d => { Assert.Equal(DeliveryStatus.Skipped, d.Status); Assert.NotNull(d.LastError); });
        Assert.Equal(NotificationStatus.Completed, loaded.Status);
        using var s2 = fx.Scope();
        var shown = await Svc<AppDbContext>(s2).CustomerNotifications.Where(x => x.CustomerId == c && x.RelatedType == "Notification" && x.RelatedId == n.Id).ToListAsync();
        Assert.Single(shown);
        Assert.Contains("Ali Khan", shown[0].Message);
    }

    [MySqlFact]
    public async Task Dispatch_TransientFailure_RetriesWithBackoff_ThenFails()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "retry-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email));
        var sender = new Scripted(NotificationChannel.Email, _ => SendResult.Transient("mailbox busy"));
        TimeSpan[] expected = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using (var s = fx.Scope())
            {
                var before = DateTime.UtcNow;
                var r = await Dispatcher(s, sender).DispatchDueAsync(100, n!.Id);
                Assert.Equal(1, r.Processed);
                var d = (await LoadAsync(n!.Id)).Deliveries.Single();
                Assert.Equal(attempt, d.Attempts);
                Assert.Equal("mailbox busy", d.LastError);
                if (attempt < 5)
                {
                    Assert.Equal(DeliveryStatus.Retrying, d.Status);
                    var wait = d.NextAttemptAtUtc - before;
                    Assert.InRange(wait, expected[attempt - 1] - TimeSpan.FromSeconds(30), expected[attempt - 1] + TimeSpan.FromSeconds(30));
                    // not due yet: another run does nothing
                    Assert.Equal(0, (await Dispatcher(s, sender).DispatchDueAsync(100, n!.Id)).Processed);
                    // make it due
                    await Svc<AppDbContext>(s).NotificationDeliveryLogs.Where(x => x.Id == d.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
                }
                else Assert.Equal(DeliveryStatus.Failed, d.Status);
            }
        }
        Assert.Equal(5, sender.Calls);
        Assert.Equal(NotificationStatus.Failed, (await LoadAsync(n!.Id)).Status);
        using var s3 = fx.Scope();
        Assert.Equal(0, (await Dispatcher(s3, sender).DispatchDueAsync(100, n!.Id)).Processed);   // failed deliveries are not retried automatically
    }

    [MySqlFact]
    public async Task Dispatch_PermanentFailure_StopsAtOnce_AndManualRetrySucceedsLater()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "perm-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email, NotificationChannel.Sms));
        var bad = new Scripted(NotificationChannel.Email, _ => SendResult.Permanently("invalid address"));
        var good = new Scripted(NotificationChannel.Sms, _ => SendResult.Sent("sms-1"));
        using (var s = fx.Scope()) await Dispatcher(s, bad, good).DispatchDueAsync(100, n!.Id);
        var l1 = await LoadAsync(n!.Id);
        var failed = l1.Deliveries.Single(d => d.Channel == NotificationChannel.Email);
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal(DeliveryStatus.Sent, l1.Deliveries.Single(d => d.Channel == NotificationChannel.Sms).Status);
        Assert.Equal("sms-1", l1.Deliveries.Single(d => d.Channel == NotificationChannel.Sms).ProviderReference);
        Assert.Equal(NotificationStatus.PartiallyFailed, l1.Status);   // e-mail failed for good, sms went out
    }

    [MySqlFact]
    public async Task ManualRetry_RequiresPermission_AndOnlyForFailedDeliveries()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "manual-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email));
        var bad = new Scripted(NotificationChannel.Email, _ => SendResult.Permanently("mailbox full"));
        using (var s = fx.Scope()) await Dispatcher(s, bad).DispatchDueAsync(100, n!.Id);
        var delivery = (await LoadAsync(n!.Id)).Deliveries.Single();
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);

        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Accountant, s => Svc<NotificationService>(s).RetryDeliveryAsync(delivery.Id)));   // view only
        var detail = await k.Run(w.Director, s => Svc<NotificationService>(s).RetryDeliveryAsync(delivery.Id));
        Assert.Equal(NotificationStatus.Pending, detail.Status);
        Assert.Equal(DeliveryStatus.Retrying, detail.Deliveries.Single().Status);
        Assert.Equal(0, detail.Deliveries.Single().Attempts);
        await Assert.ThrowsAsync<DomainException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).RetryDeliveryAsync(delivery.Id)));    // no longer failed

        var ok = new Scripted(NotificationChannel.Email, _ => SendResult.Sent("smtp-9"));
        using (var s = fx.Scope()) await Dispatcher(s, ok).DispatchDueAsync(100, n.Id);
        var done = await LoadAsync(n.Id);
        Assert.Equal(DeliveryStatus.Sent, done.Deliveries.Single().Status);
        Assert.Equal(NotificationStatus.Completed, done.Status);
        Assert.Contains(await AuditActionsAsync("NotificationDeliveryLog", delivery.Id.ToString()), a => a == "Notification.DeliveryRetried");
    }

    [MySqlFact]
    public async Task Dispatch_Concurrent_SendsEachDeliveryOnce()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "conc-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email));
        var deliveryId = (await LoadAsync(n!.Id)).Deliveries.Single().Id;
        var sender = new Scripted(NotificationChannel.Email, _ => SendResult.Sent("once"));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () => { using var s = fx.Scope(); return await Dispatcher(s, sender).DeliverAsync(deliveryId); })));
        Assert.Equal(1, sender.Calls);
        var d = (await LoadAsync(n.Id)).Deliveries.Single();
        Assert.Equal(DeliveryStatus.Sent, d.Status);
        Assert.Equal(1, d.Attempts);
    }

    [MySqlFact]
    public async Task Dispatch_IgnoresCancelledAndFutureNotifications()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var cancelled = await QueueAsync(Draft(c, "cancel-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email));
        var future = await QueueAsync(Draft(c, "future-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email) with { ScheduledForUtc = DateTime.UtcNow.AddHours(2) });

        await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).CancelAsync(cancelled!.Id, new CancelNotificationRequest("no", null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Accountant, s => Svc<NotificationService>(s).CancelAsync(cancelled!.Id, new CancelNotificationRequest("customer asked us to stop", null))));
        var detail = await k.Run(w.Director, s => Svc<NotificationService>(s).CancelAsync(cancelled!.Id, new CancelNotificationRequest("customer asked us to stop", null)));
        Assert.Equal(NotificationStatus.Cancelled, detail.Status);
        Assert.Equal(DeliveryStatus.Cancelled, detail.Deliveries.Single().Status);
        await Assert.ThrowsAsync<DomainException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).CancelAsync(cancelled.Id, new CancelNotificationRequest("cancel it again please", null))));

        var sender = new Scripted(NotificationChannel.Email, _ => SendResult.Sent());
        using (var s = fx.Scope())
        {
            await Dispatcher(s, sender).DispatchDueAsync(500, future!.Id);
            await Dispatcher(s, sender).DispatchDueAsync(500, cancelled!.Id);
            var db = Svc<AppDbContext>(s);
            Assert.Equal(DeliveryStatus.Pending, (await db.NotificationDeliveryLogs.AsNoTracking().FirstAsync(d => d.NotificationId == future!.Id)).Status);
        }
        Assert.Equal(0, sender.Calls);   // neither the cancelled nor the future-dated notification was sent
        using var s2 = fx.Scope();
        var db2 = Svc<AppDbContext>(s2);
        Assert.Equal(DeliveryStatus.Cancelled, (await db2.NotificationDeliveryLogs.AsNoTracking().FirstAsync(d => d.NotificationId == cancelled.Id)).Status);
        Assert.Contains(await AuditActionsAsync("Notification", cancelled.Id.ToString()), a => a == "Notification.Cancelled");
    }

    // ------------------------------------------------------------------------------------------------ manual sending
    [MySqlFact]
    public async Task ManualSend_ChecksPermissionAndScope_AndIsIdempotentPerKey()
    {
        var w = await k.WorldAsync();
        var other = await k.WorldAsync();              // a second project the support user is not assigned to
        var c1 = await k.CustomerAsync(w);
        await k.DealAsync(w, customerId: c1);
        var c2 = await k.CustomerAsync(w);
        await k.DealAsync(w, customerId: c2);
        var foreign = await k.DealAsync(other);

        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Sales, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, c1, null, null, "Hello", "Office closed", null))));
        var key = "send-" + Guid.NewGuid().ToString("N");
        var r = await k.Run(w.Support, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, w.ProjectId, null, "Office closed", "The office is closed on Friday.", key)));
        Assert.Equal(2, r.Recipients);
        Assert.Equal(2, r.Queued);
        var again = await k.Run(w.Support, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, w.ProjectId, null, "Office closed", "The office is closed on Friday.", key)));
        Assert.Equal(0, again.Queued);
        Assert.Equal(2, again.Duplicates);

        // scope: another project, a customer outside the user's projects, and company-wide all need more than project-level access
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Support, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, other.ProjectId, null, "x", "y", null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Support, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, foreign.CustomerId, null, null, "x", "y", null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Support, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, null, null, "x", "y", null))));
        var byProperty = await k.Run(w.Director, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.PossessionNotice, null, null, foreign.PropertyId, "Possession", "Please contact the office.", null)));
        Assert.Equal(1, byProperty.Recipients);
        Assert.Contains(await AuditActionsAsync("Notification", null), a => a == "Notification.Sent");
    }

    [MySqlFact]
    public async Task HistoryAndDetails_AreScopedToTheUsersProjects()
    {
        var a = await k.WorldAsync();
        var b = await k.WorldAsync();
        var ca = await k.CustomerAsync(a); await k.DealAsync(a, customerId: ca);
        var cb = await k.CustomerAsync(b); await k.DealAsync(b, customerId: cb);
        var key = "scope-" + Guid.NewGuid().ToString("N");
        await k.Run(a.Director, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, a.ProjectId, null, "Scope A", "For project A", key)));
        await k.Run(b.Director, s => Svc<NotificationService>(s).SendAsync(new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, b.ProjectId, null, "Scope B", "For project B", key)));

        var listA = await k.Run(a.Support, s => Svc<NotificationService>(s).ListAsync(new NotificationListRequest { Search = "Scope " }));
        Assert.Contains(listA.Items, x => x.Title == "Scope A");
        Assert.DoesNotContain(listA.Items, x => x.Title == "Scope B");
        var idB = (await k.Run(b.Director, s => Svc<NotificationService>(s).ListAsync(new NotificationListRequest { Search = "Scope B" }))).Items.First().Id;
        await Assert.ThrowsAsync<NotFoundException>(() => k.Run(a.Support, s => Svc<NotificationService>(s).GetAsync(idB)));
        var all = await k.Run(a.Director, s => Svc<NotificationService>(s).GetAsync(idB));    // all-project access sees everything
        Assert.Equal("Scope B", all.Title);
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(a.Cashier, s => Svc<NotificationService>(s).ListAsync(new NotificationListRequest())));
        var summary = await k.Run(a.Support, s => Svc<NotificationService>(s).SummaryAsync());
        Assert.True(summary.Pending >= 1);
    }

    // ------------------------------------------------------------------------------------------------ templates and settings
    [MySqlFact]
    public async Task Templates_AreEditedWithPermission_ValidatedAndDoNotChangeSentText()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var old = await QueueAsync(Draft(c, "tpl-old-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Sms));
        var tpl = (await k.Run(w.Director, s => Svc<NotificationService>(s).ListTemplatesAsync())).Single(t => t.Type == NotificationType.InstallmentUpcoming && t.Channel == NotificationChannel.Sms);

        var req = new SaveTemplateRequest(tpl.Type, tpl.Channel, "en", "Due soon", "NEW WORDING for {{CustomerName}}: {{Amount}} on {{DueDate}}", true, tpl.RowVersion);
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Accountant, s => Svc<NotificationService>(s).SaveTemplateAsync(req)));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Support, s => Svc<NotificationService>(s).SaveTemplateAsync(req)));        // company-wide: needs all-project access
        await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).SaveTemplateAsync(req with { Body = "Hello {{Secret}}" })));
        var saved = await k.Run(w.Director, s => Svc<NotificationService>(s).SaveTemplateAsync(req));
        Assert.StartsWith("NEW WORDING", saved.Body);
        await Assert.ThrowsAsync<ConcurrencyException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).SaveTemplateAsync(req)));      // stale row version
        Assert.Contains(await AuditActionsAsync("NotificationTemplate", tpl.Id.ToString()), a => a == "NotificationTemplate.Updated");

        var fresh = await QueueAsync(Draft(c, "tpl-new-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Sms));
        Assert.StartsWith("NEW WORDING for Ali Khan: Rs 10,000.00 on 2026-12-01", (await LoadAsync(fresh!.Id)).Deliveries.Single().Body);
        Assert.StartsWith("Dear Ali Khan", (await LoadAsync(old!.Id)).Deliveries.Single().Body);   // already queued text is unchanged

        // inactive template: the channel is not used any more
        await k.Run(w.Director, s => Svc<NotificationService>(s).SaveTemplateAsync(req with { IsActive = false, RowVersion = saved.RowVersion }));
        var third = await QueueAsync(Draft(c, "tpl-off-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming));
        Assert.DoesNotContain((await LoadAsync(third!.Id)).Deliveries, d => d.Channel == NotificationChannel.Sms);
        await k.Run(w.Director, s => Svc<NotificationService>(s).SaveTemplateAsync(req with { IsActive = true, RowVersion = null }));      // restore for the other tests
    }

    [MySqlFact]
    public async Task ReminderSettings_AreValidated_Permissioned_AndAudited()
    {
        var w = await k.WorldAsync();
        var original = await k.Run(w.Director, s => Svc<NotificationService>(s).GetSettingsAsync());
        Assert.Equal(new ReminderSettingsDto(3, 7, 7), original);
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Accountant, s => Svc<NotificationService>(s).SaveSettingsAsync(new ReminderSettingsDto(5, 7, 7))));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Support, s => Svc<NotificationService>(s).SaveSettingsAsync(new ReminderSettingsDto(5, 7, 7))));
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => k.Run(w.Director, s => Svc<NotificationService>(s).SaveSettingsAsync(new ReminderSettingsDto(99, 0, 0))));
        Assert.Equal(3, ex.Errors.Count);
        try
        {
            await k.Run(w.Director, s => Svc<NotificationService>(s).SaveSettingsAsync(new ReminderSettingsDto(10, 14, 30)));
            Assert.Equal(new ReminderSettingsDto(10, 14, 30), await k.Run(w.Accountant, s => Svc<NotificationService>(s).GetSettingsAsync()));
            Assert.Contains(await AuditActionsAsync("ApplicationSetting", "Notifications"), a => a == "Notification.SettingsChanged");
        }
        finally { await k.Run(w.Director, s => Svc<NotificationService>(s).SaveSettingsAsync(original)); }
    }

    // ------------------------------------------------------------------------------------------------ event scans
    [MySqlFact]
    public async Task EventScan_QueuesBookingPaymentAmendmentPossessionAndAnnouncement_Once()
    {
        var w = await k.WorldAsync();
        var other = await k.WorldAsync();
        var d = await k.DealAsync(w);
        var d2 = await k.DealAsync(other);
        await k.PostPaymentAsync(w, d, 100_000m);
        await k.Do(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            var ag = await db.CustomerAgreements.AsNoTracking().FirstAsync(a => a.Id == d.AgreementId);
            db.AgreementAmendments.Add(new AgreementAmendment { AgreementId = d.AgreementId, AmendmentType = AmendmentType.Reschedule, Status = AmendmentStatus.Approved, FromVersionNo = 1, ToVersionNo = 2,
                Reason = "Customer request", Details = "{}", DecidedAtUtc = DateTime.UtcNow });
            db.PropertyStatusHistories.Add(new PropertyStatusHistory { PropertyId = d.PropertyId, FromStatus = PropertyStatus.Sold, ToStatus = PropertyStatus.PossessionReady, Reason = "Ready", ChangedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        var ann = await k.Run(w.Director, s => Svc<PortalContentService>(s).SaveAnnouncementAsync(null, new SaveAnnouncementRequest("Eid greetings " + Guid.NewGuid().ToString("N")[..6], "Happy Eid", w.ProjectId, null, null, true, null)));

        int first;
        using (var s = fx.Scope()) first = await Svc<NotificationEventService>(s).QueueEventNotificationsAsync();
        Assert.True(first >= 5);

        using var s1 = fx.Scope();
        var db1 = Svc<AppDbContext>(s1);
        var mine = await db1.Notifications.AsNoTracking().Include(n => n.Deliveries).Where(n => n.CustomerId == d.CustomerId).ToListAsync();
        Assert.Contains(mine, n => n.Type == NotificationType.BookingConfirmation && n.DedupeKey == $"booking-confirmed:{d.BookingId}:c{d.CustomerId}");
        var pay = mine.Single(n => n.Type == NotificationType.PaymentConfirmation);
        Assert.DoesNotContain(pay.Deliveries, x => x.Channel == NotificationChannel.InApp);
        Assert.Contains("Receipt: ", pay.Deliveries.First(x => x.Channel == NotificationChannel.Email).Body);
        Assert.Single(mine, n => n.Type == NotificationType.AgreementAmendment);
        Assert.Single(mine, n => n.Type == NotificationType.PossessionNotice);
        Assert.Single(mine, n => n.Type == NotificationType.CompanyAnnouncement && n.RelatedId == ann.Id);
        // the announcement of project A reaches no customer of project B
        Assert.DoesNotContain(await db1.Notifications.AsNoTracking().Where(n => n.CustomerId == d2.CustomerId).ToListAsync(), n => n.Type == NotificationType.CompanyAnnouncement && n.RelatedId == ann.Id);

        // running again creates nothing new for these events
        using var s2 = fx.Scope();
        await Svc<NotificationEventService>(s2).QueueEventNotificationsAsync();
        Assert.Equal(mine.Count, await Svc<AppDbContext>(s2).Notifications.AsNoTracking().CountAsync(n => n.CustomerId == d.CustomerId));
    }

    [MySqlFact]
    public async Task EventScan_IgnoresEventsOlderThanTheLookbackWindow()
    {
        var w = await k.WorldAsync();
        var d = await k.DealAsync(w);
        await k.Do(w.Admin, async s => await Svc<AppDbContext>(s).Bookings.Where(b => b.Id == d.BookingId).ExecuteUpdateAsync(u => u.SetProperty(b => b.ConfirmedAtUtc, DateTime.UtcNow.AddDays(-30))));
        using var s1 = fx.Scope();
        await Svc<NotificationEventService>(s1).QueueEventNotificationsAsync();
        Assert.False(await Svc<AppDbContext>(s1).Notifications.AnyAsync(n => n.CustomerId == d.CustomerId && n.Type == NotificationType.BookingConfirmation));
    }

    [MySqlFact]
    public async Task InstallmentReminders_UpcomingAndOverdue_AreQueuedOnce_AndStopWhenPaid()
    {
        var w = await k.WorldAsync();
        var upcoming = await k.DealAsync(w);                      // down payment falls due today
        var overdue = await k.DealAsync(w, monthsBack: 2);        // several installments are past their grace period
        using (var s = fx.Scope()) await Svc<NotificationEventService>(s).QueueInstallmentRemindersAsync();

        using var s1 = fx.Scope();
        var db = Svc<AppDbContext>(s1);
        var up = await db.Notifications.AsNoTracking().Where(n => n.CustomerId == upcoming.CustomerId && n.AgreementId == upcoming.AgreementId).ToListAsync();
        Assert.Single(up);
        Assert.Equal(NotificationType.InstallmentUpcoming, up[0].Type);
        Assert.Contains("is due on", up[0].Body);

        var items = await (from i in db.PaymentScheduleItems.AsNoTracking() join sc in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals sc.Id
                           where sc.AgreementId == overdue.AgreementId && sc.Status == PaymentScheduleStatus.Active select i).ToListAsync();
        var today = await Svc<RealEstate.Application.Pricing.PricingSupport>(s1).TodayAsync();
        var expected = items.Count(i => i.GraceEndDate < today && i.Outstanding > 0 && (today.DayNumber - i.DueDate.DayNumber - 1) / 7 < 12);
        var od = await db.Notifications.AsNoTracking().Where(n => n.AgreementId == overdue.AgreementId).ToListAsync();
        Assert.True(expected >= 1);
        Assert.Equal(expected, od.Count(n => n.Type == NotificationType.InstallmentOverdue));
        Assert.All(od.Where(n => n.Type == NotificationType.InstallmentOverdue), n => Assert.Contains("overdue", n.Body));
        Assert.Equal(od.Count, od.Select(n => n.DedupeKey).Distinct().Count());

        // a second run (and a third) adds nothing
        var total = await db.Notifications.CountAsync();
        using (var s2 = fx.Scope()) { await Svc<NotificationEventService>(s2).QueueInstallmentRemindersAsync(); await Svc<NotificationEventService>(s2).QueueInstallmentRemindersAsync(); }
        Assert.Equal(total, await Svc<AppDbContext>(s1).Notifications.AsNoTracking().CountAsync());

        // settled installments are not reminded: pay everything outstanding of the upcoming deal's first line, then check no new reminder keys for it
        await k.PostPaymentAsync(w, upcoming, 500_000m);
        using var s3 = fx.Scope();
        await Svc<NotificationEventService>(s3).QueueInstallmentRemindersAsync();
        var after = await Svc<AppDbContext>(s3).Notifications.AsNoTracking().Where(n => n.AgreementId == upcoming.AgreementId && n.Type != NotificationType.PaymentConfirmation).ToListAsync();
        Assert.Single(after);   // still only the reminder queued before the payment
    }

    // ------------------------------------------------------------------------------------------------ staff digest
    [MySqlFact]
    public async Task StaffDigest_IsOptIn_OncePerDay_ForPermittedUsers_AndShownInTheInbox()
    {
        var w = await k.WorldAsync();
        await k.PostPaymentAsync(w, await k.DealAsync(w), 50_000m);
        var all = new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.InApp, NotificationType.StaffDigest, true), new SavePreferenceRequest(NotificationChannel.Email, NotificationType.StaffDigest, true)]);

        // a project-scoped user cannot subscribe to company-wide figures
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Sales, s => Svc<NotificationPreferenceService>(s).SaveMineAsStaffAsync(all)));
        Assert.All(await k.Run(w.Director, s => Svc<NotificationPreferenceService>(s).GetMineAsStaffAsync()), c => Assert.False(c.IsEnabled));

        await k.Run(w.Director, s => Svc<NotificationPreferenceService>(s).SaveMineAsStaffAsync(all));
        var mine = await k.Run(w.Director, s => Svc<NotificationPreferenceService>(s).GetMineAsStaffAsync());
        Assert.All(mine, c => Assert.True(c.IsEnabled));

        using (var s = fx.Scope()) Assert.True(await Svc<NotificationEventService>(s).QueueStaffDigestAsync() >= 1);
        using (var s = fx.Scope()) Assert.Equal(0, await Svc<NotificationEventService>(s).QueueStaffDigestAsync());     // same day: nothing more

        using (var s = fx.Scope()) await Svc<NotificationDispatchService>(s).DispatchDueAsync(500);
        var inbox = await k.Run(w.Director, s => Svc<NotificationService>(s).InboxAsync(new PagedRequest()));
        var item = inbox.Items.First(i => i.Type == NotificationType.StaffDigest);
        Assert.Contains("Daily summary", item.Title);
        Assert.Contains("overdue installment", item.Body);
        Assert.Null(item.ReadAtUtc);
        await Assert.ThrowsAsync<NotFoundException>(() => k.Do(w.Accountant, s => Svc<NotificationService>(s).MarkInboxReadAsync(item.Id)));     // someone else's
        await k.Do(w.Director, s => Svc<NotificationService>(s).MarkInboxReadAsync(item.Id));
        Assert.NotNull((await k.Run(w.Director, s => Svc<NotificationService>(s).InboxAsync(new PagedRequest()))).Items.First(i => i.Id == item.Id).ReadAtUtc);

        // switching it off stops the digest for the next day's key (nothing queued for a user without an enabled choice)
        await k.Run(w.Director, s => Svc<NotificationPreferenceService>(s).SaveMineAsStaffAsync(new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.InApp, NotificationType.StaffDigest, false), new SavePreferenceRequest(NotificationChannel.Email, NotificationType.StaffDigest, false)])));
        Assert.All(await k.Run(w.Director, s => Svc<NotificationPreferenceService>(s).GetMineAsStaffAsync()), c => Assert.False(c.IsEnabled));
        // history of staff digests is not visible to project-scoped users
        var history = await k.Run(w.Support, s => Svc<NotificationService>(s).ListAsync(new NotificationListRequest { Type = NotificationType.StaffDigest }));
        Assert.Empty(history.Items);
    }

    // ------------------------------------------------------------------------------------------------ database rules
    [MySqlFact]
    public async Task Database_EnforcesOneNotificationPerKey_OneDeliveryPerChannel_OneTemplatePerKey()
    {
        var w = await k.WorldAsync();
        var c = await k.CustomerAsync(w);
        var n = await QueueAsync(Draft(c, "db-" + Guid.NewGuid().ToString("N"), NotificationType.InstallmentUpcoming, NotificationChannel.Email));
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);

        db.Notifications.Add(new Notification { Type = NotificationType.InstallmentUpcoming, CustomerId = c, Title = "T", Body = "B", DedupeKey = n!.DedupeKey, ScheduledForUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.NotificationDeliveryLogs.Add(new NotificationDeliveryLog { NotificationId = n.Id, Channel = NotificationChannel.Email, Subject = "S", Body = "B", NextAttemptAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.NotificationTemplates.Add(new NotificationTemplate { Type = NotificationType.InstallmentUpcoming, Channel = NotificationChannel.Email, Language = "en", Subject = "S", Body = "B" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var key = NotificationRules.OwnerKey(c, null);
        db.NotificationPreferences.Add(new NotificationPreference { CustomerId = c, OwnerKey = key, Channel = NotificationChannel.Sms, TypeKey = 0, IsEnabled = false });
        await db.SaveChangesAsync();
        db.NotificationPreferences.Add(new NotificationPreference { CustomerId = c, OwnerKey = key, Channel = NotificationChannel.Sms, TypeKey = 0, IsEnabled = true });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task<List<string>> AuditActionsAsync(string entityType, string? entityId)
    {
        using var s = fx.Scope();
        return await Svc<AppDbContext>(s).AuditLogs.AsNoTracking().Where(a => a.EntityType == entityType && (entityId == null || a.EntityId == entityId)).Select(a => a.Action).ToListAsync();
    }
}
