using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

/// <summary>
/// Turns business events and due dates into queued notifications (system level, called by the scheduled jobs). Every method is idempotent: each notification
/// carries a business key (payment id, installment and due date, announcement and customer...), so running a job twice, or after a crash, never sends twice.
/// Event scans look back a configurable number of days and never touch the earlier phases' services (they only read their tables).
/// </summary>
public class NotificationEventService(IAppDbContext db, NotificationQueue queue, PricingSupport pricing, IPermissionService permissions, TimeProvider clock)
{
    public const string UpcomingDaysKey = "Notifications:UpcomingDaysBefore";
    public const string OverdueRepeatKey = "Notifications:OverdueRepeatDays";
    public const string LookbackKey = "Notifications:EventLookbackDays";
    private const int MaxOverdueReminders = 12;
    private const int MaxPerRun = 2000;

    // ------------------------------------------------------------------------------------------------
    // settings
    // ------------------------------------------------------------------------------------------------
    public async Task<ReminderSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var rows = await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == UpcomingDaysKey || s.Key == OverdueRepeatKey || s.Key == LookbackKey).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new ReminderSettingsDto(Read(rows, UpcomingDaysKey, 3, 0, 60), Read(rows, OverdueRepeatKey, 7, 1, 60), Read(rows, LookbackKey, 7, 1, 60));
    }

    private static int Read(Dictionary<string, string?> rows, string key, int fallback, int min, int max) =>
        rows.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;

    // ------------------------------------------------------------------------------------------------
    // installment reminders
    // ------------------------------------------------------------------------------------------------
    private sealed record DueRow(long AgreementId, string AgreementNumber, long ProjectId, string ProjectName, long ChargeId, int InstallmentNo, DateOnly DueDate, DateOnly GraceEnd, decimal Outstanding, long BookingId, string Property);

    /// <summary>Queues upcoming-installment reminders (due within N days) and overdue reminders (repeated every M days after the grace period) for every current owner.</summary>
    public async Task<int> QueueInstallmentRemindersAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var today = await pricing.TodayAsync(ct);
        var horizon = today.AddDays(settings.UpcomingDaysBefore);
        var symbol = await db.Companies.AsNoTracking().Select(c => c.CurrencySymbol).FirstOrDefaultAsync(ct) ?? "Rs";

        var rows = await (from i in db.PaymentScheduleItems.AsNoTracking()
                          join s in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals s.Id
                          join a in db.CustomerAgreements.AsNoTracking() on s.AgreementId equals a.Id
                          join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                          join p in db.Projects.AsNoTracking() on b.ProjectId equals p.Id
                          where s.Status == PaymentScheduleStatus.Active && a.Status == AgreementStatus.Active && b.Status == BookingStatus.Confirmed
                                && i.Amount - i.AmountPaid - i.AmountWaived > 0m && i.DueDate <= horizon
                          orderby i.DueDate, i.Id
                          select new DueRow(a.Id, a.AgreementNumber, b.ProjectId, p.ProjectName, i.AgreementChargeId, i.InstallmentNo, i.DueDate, i.GraceEndDate,
                              i.Amount - i.AmountPaid - i.AmountWaived, b.Id,
                              string.Join(", ", db.BookingProperties.Where(bp => bp.BookingId == b.Id).Select(bp => bp.Property!.PropertyCode)))).Take(MaxPerRun).ToListAsync(ct);

        var ownersByBooking = await OwnersForAsync(rows.Select(x => x.BookingId).Distinct().ToList(), ct);
        var queued = 0;
        foreach (var r in rows)
        {
            var overdue = r.GraceEnd < today;
            if (!overdue && r.DueDate < today) continue;                 // inside the grace period: neither upcoming nor overdue yet
            var daysOverdue = overdue ? today.DayNumber - r.DueDate.DayNumber : 0;
            string key; NotificationType type;
            if (overdue)
            {
                var period = (daysOverdue - 1) / settings.OverdueRepeatDays;
                if (period >= MaxOverdueReminders) continue;
                type = NotificationType.InstallmentOverdue; key = $"installment-overdue:{r.AgreementId}:{r.ChargeId}:{r.InstallmentNo}:{r.DueDate:yyyyMMdd}:{period}";
            }
            else { type = NotificationType.InstallmentUpcoming; key = $"installment-upcoming:{r.AgreementId}:{r.ChargeId}:{r.InstallmentNo}:{r.DueDate:yyyyMMdd}"; }

            foreach (var c in ownersByBooking.GetValueOrDefault(r.BookingId) ?? [])
            {
                var data = Data(c.FullName, r.ProjectName, r.Property, r.AgreementNumber, $"{symbol} {r.Outstanding:N2}", r.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), daysOverdue.ToString(CultureInfo.InvariantCulture));
                var title = overdue ? $"Installment overdue - {r.AgreementNumber}" : $"Installment due on {r.DueDate:yyyy-MM-dd} - {r.AgreementNumber}";
                var text = overdue
                    ? $"Installment {r.InstallmentNo} of agreement {r.AgreementNumber} ({symbol} {r.Outstanding:N2}) was due on {r.DueDate:yyyy-MM-dd} and is {daysOverdue} day(s) overdue."
                    : $"Installment {r.InstallmentNo} of agreement {r.AgreementNumber} ({symbol} {r.Outstanding:N2}) is due on {r.DueDate:yyyy-MM-dd}.";
                if (await queue.EnqueueAsync(new NotificationDraft(type, $"{key}:c{c.Id}", c.Id, null, r.ProjectId, r.AgreementId, title, text, data, "Agreement", r.AgreementId), ct) is not null) queued++;
            }
        }
        return queued;
    }

    // ------------------------------------------------------------------------------------------------
    // business events
    // ------------------------------------------------------------------------------------------------
    /// <summary>Queues payment confirmations, booking confirmations, agreement amendments, possession notices and company announcements that happened in the look-back window.</summary>
    public async Task<int> QueueEventNotificationsAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-settings.EventLookbackDays);
        var symbol = await db.Companies.AsNoTracking().Select(c => c.CurrencySymbol).FirstOrDefaultAsync(ct) ?? "Rs";
        var queued = 0;
        queued += await PaymentsAsync(since, symbol, ct);
        queued += await BookingsAsync(since, symbol, ct);
        queued += await AmendmentsAsync(since, ct);
        queued += await PossessionAsync(since, ct);
        queued += await AnnouncementsAsync(now, since, ct);
        return queued;
    }

    private async Task<int> PaymentsAsync(DateTime since, string symbol, CancellationToken ct)
    {
        var rows = await (from p in db.Payments.AsNoTracking()
                          join c in db.Customers.AsNoTracking() on p.CustomerId equals c.Id
                          join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                          join a in db.CustomerAgreements.AsNoTracking() on p.AgreementId equals a.Id
                          join rc in db.PaymentReceipts.AsNoTracking() on p.Id equals rc.PaymentId into rcs
                          from rc in rcs.DefaultIfEmpty()
                          where p.Status == PaymentStatus.Posted && p.PostedAtUtc >= since
                          orderby p.Id
                          select new { p.Id, p.CustomerId, c.FullName, p.ProjectId, pr.ProjectName, p.AgreementId, a.AgreementNumber, p.Amount, p.PaymentNumber, p.PaymentDate, Receipt = rc == null ? null : rc.ReceiptNumber })
            .Take(MaxPerRun).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
        {
            var text = $"We received {symbol} {r.Amount:N2} on {r.PaymentDate:yyyy-MM-dd} for agreement {r.AgreementNumber}. Payment {r.PaymentNumber}" + (r.Receipt is null ? "." : $", receipt {r.Receipt}.");
            var data = Data(r.FullName, r.ProjectName, "", r.AgreementNumber, $"{symbol} {r.Amount:N2}", r.PaymentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "0", r.Receipt ?? r.PaymentNumber);
            if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.PaymentConfirmation, $"payment-confirmed:{r.Id}", r.CustomerId, null, r.ProjectId, r.AgreementId, "Payment received", text, data, "Payment", r.Id), ct) is not null) n++;
        }
        return n;
    }

    private async Task<int> BookingsAsync(DateTime since, string symbol, CancellationToken ct)
    {
        var rows = await (from b in db.Bookings.AsNoTracking()
                          join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id
                          where b.Status == BookingStatus.Confirmed && b.ConfirmedAtUtc >= since
                          orderby b.Id
                          select new { b.Id, b.BookingNumber, b.ProjectId, pr.ProjectName, b.TotalPayable,
                              Property = string.Join(", ", db.BookingProperties.Where(bp => bp.BookingId == b.Id).Select(bp => bp.Property!.PropertyCode)),
                              AgreementId = db.CustomerAgreements.Where(a => a.BookingId == b.Id).Select(a => (long?)a.Id).FirstOrDefault(),
                              AgreementNumber = db.CustomerAgreements.Where(a => a.BookingId == b.Id).Select(a => a.AgreementNumber).FirstOrDefault() }).Take(MaxPerRun).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
            foreach (var c in await OwnersAsync(r.Id, ct))
            {
                var text = $"Your booking {r.BookingNumber} for {r.Property} in {r.ProjectName} is confirmed" + (r.AgreementNumber is null ? "." : $" (agreement {r.AgreementNumber}).");
                var data = Data(c.FullName, r.ProjectName, r.Property, r.AgreementNumber ?? r.BookingNumber, $"{symbol} {r.TotalPayable:N2}", "", "0");
                if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.BookingConfirmation, $"booking-confirmed:{r.Id}:c{c.Id}", c.Id, null, r.ProjectId, r.AgreementId, "Booking confirmed", text, data, "Booking", r.Id), ct) is not null) n++;
            }
        return n;
    }

    private async Task<int> AmendmentsAsync(DateTime since, CancellationToken ct)
    {
        var rows = await (from m in db.AgreementAmendments.AsNoTracking()
                          join a in db.CustomerAgreements.AsNoTracking() on m.AgreementId equals a.Id
                          join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                          join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id
                          where m.Status == AmendmentStatus.Approved && m.DecidedAtUtc != null && m.DecidedAtUtc >= since
                          orderby m.Id
                          select new { m.Id, m.AmendmentType, m.Reason, m.ToVersionNo, AgreementId = a.Id, a.AgreementNumber, BookingId = b.Id, b.ProjectId, pr.ProjectName }).Take(MaxPerRun).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
            foreach (var c in await OwnersAsync(r.BookingId, ct))
            {
                var text = $"Agreement {r.AgreementNumber} was amended ({r.AmendmentType}); new version {(r.ToVersionNo?.ToString() ?? "-")}. Reason: {r.Reason}";
                var data = Data(c.FullName, r.ProjectName, "", r.AgreementNumber, "", "", "0");
                if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.AgreementAmendment, $"agreement-amended:{r.Id}:c{c.Id}", c.Id, null, r.ProjectId, r.AgreementId, "Agreement amended", text, data, "AgreementAmendment", r.Id), ct) is not null) n++;
            }
        return n;
    }

    private async Task<int> PossessionAsync(DateTime since, CancellationToken ct)
    {
        var rows = await (from h in db.PropertyStatusHistories.AsNoTracking()
                          join p in db.Properties.AsNoTracking() on h.PropertyId equals p.Id
                          join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                          where h.ToStatus == PropertyStatus.PossessionReady && h.ChangedAtUtc >= since
                          orderby h.Id
                          select new { h.Id, PropertyId = p.Id, p.PropertyCode, p.ProjectId, pr.ProjectName }).Take(MaxPerRun).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
        {
            var bookingId = await (from bp in db.BookingProperties.AsNoTracking()
                                   join b in db.Bookings.AsNoTracking() on bp.BookingId equals b.Id
                                   where bp.PropertyId == r.PropertyId && b.Status == BookingStatus.Confirmed select (long?)b.Id).FirstOrDefaultAsync(ct);
            if (bookingId is null) continue;
            foreach (var c in await OwnersAsync(bookingId.Value, ct))
            {
                var text = $"Possession of {r.PropertyCode} in {r.ProjectName} is ready. Please contact the office to arrange handover.";
                var data = Data(c.FullName, r.ProjectName, r.PropertyCode, "", "", "", "0");
                if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.PossessionNotice, $"possession-ready:{r.Id}:c{c.Id}", c.Id, null, r.ProjectId, null, "Possession ready", text, data, "Property", r.PropertyId), ct) is not null) n++;
            }
        }
        return n;
    }

    private async Task<int> AnnouncementsAsync(DateTime now, DateTime since, CancellationToken ct)
    {
        var anns = await db.Announcements.AsNoTracking().Where(a => a.IsActive && a.PublishFromUtc <= now && a.PublishFromUtc >= since && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now))
            .OrderBy(a => a.Id).ToListAsync(ct);
        var n = 0;
        foreach (var a in anns)
        {
            var q = from bc in db.BookingCustomers.AsNoTracking()
                    join b in db.Bookings.AsNoTracking() on bc.BookingId equals b.Id
                    join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                    where bc.IsActive && b.Status == BookingStatus.Confirmed && c.Status != CustomerStatus.Blocked && (a.ProjectId == null || b.ProjectId == a.ProjectId)
                    select new { c.Id, c.FullName };
            var customers = await q.Distinct().OrderBy(c => c.Id).Take(MaxPerRun).ToListAsync(ct);
            foreach (var c in customers)
                if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.CompanyAnnouncement, $"announcement:{a.Id}:c{c.Id}", c.Id, null, a.ProjectId, null, a.Title, a.Body,
                        Data(c.FullName, "", "", "", "", "", "0"), "Announcement", a.Id), ct) is not null) n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------------
    // staff digest (recurring report)
    // ------------------------------------------------------------------------------------------------
    /// <summary>Queues the daily management digest for staff who switched it on (and who still may see company-wide figures). One per user per day.</summary>
    public async Task<int> QueueStaffDigestAsync(CancellationToken ct = default)
    {
        var today = await pricing.TodayAsync(ct);
        var yesterday = today.AddDays(-1);
        var subscribers = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId != null && p.TypeKey == (int)NotificationType.StaffDigest && p.IsEnabled)
            .Select(p => p.UserId!.Value).Distinct().ToListAsync(ct);
        if (subscribers.Count == 0) return 0;

        var symbol = await db.Companies.AsNoTracking().Select(c => c.CurrencySymbol).FirstOrDefaultAsync(ct) ?? "Rs";
        var collected = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Posted && p.PaymentDate == yesterday).Select(p => (decimal?)p.Amount).SumAsync(ct) ?? 0m;
        var paymentCount = await db.Payments.AsNoTracking().CountAsync(p => p.Status == PaymentStatus.Posted && p.PaymentDate == yesterday, ct);
        var overdue = await (from i in db.PaymentScheduleItems.AsNoTracking()
                             join s in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals s.Id
                             join a in db.CustomerAgreements.AsNoTracking() on s.AgreementId equals a.Id
                             where s.Status == PaymentScheduleStatus.Active && a.Status == AgreementStatus.Active && i.GraceEndDate < today && i.Amount - i.AmountPaid - i.AmountWaived > 0m
                             group i by 1 into g select new { N = g.Count(), Total = g.Sum(x => x.Amount - x.AmountPaid - x.AmountWaived) }).FirstOrDefaultAsync(ct);
        var pendingPayments = await db.Payments.AsNoTracking().CountAsync(p => p.Status == PaymentStatus.PendingApproval || p.Status == PaymentStatus.PendingVerification, ct);
        var pendingBookings = await db.Bookings.AsNoTracking().CountAsync(b => b.Status == BookingStatus.PendingApproval, ct);

        var text = $"Daily summary for {yesterday:yyyy-MM-dd}: {paymentCount} payment(s) posted, {symbol} {collected:N2} collected. " +
                   $"{overdue?.N ?? 0} overdue installment(s), {symbol} {overdue?.Total ?? 0m:N2} outstanding. Waiting for action: {pendingPayments} payment(s), {pendingBookings} booking(s).";
        var n = 0;
        foreach (var userId in subscribers)
        {
            if (!await permissions.HasPermissionAsync(userId, Permissions.ReportsView, ct) || !await permissions.HasPermissionAsync(userId, Permissions.ProjectsAccessAll, ct)) continue;
            var data = Data("", "", "", "", $"{symbol} {collected:N2}", yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "0");
            if (await queue.EnqueueAsync(new NotificationDraft(NotificationType.StaffDigest, $"staff-digest:{userId:N}:{yesterday:yyyyMMdd}", null, userId, null, null, $"Daily summary {yesterday:yyyy-MM-dd}", text, data), ct) is not null) n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------------
    /// <summary>Current owners (non-blocked) of several bookings in one query.</summary>
    private async Task<Dictionary<long, List<(long Id, string FullName)>>> OwnersForAsync(IReadOnlyCollection<long> bookingIds, CancellationToken ct) =>
        (await (from bc in db.BookingCustomers.AsNoTracking() join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                where bookingIds.Contains(bc.BookingId) && bc.IsActive && c.Status != CustomerStatus.Blocked select new { bc.BookingId, c.Id, c.FullName }).ToListAsync(ct))
        .GroupBy(x => x.BookingId).ToDictionary(g => g.Key, g => g.Select(x => (x.Id, x.FullName)).ToList());

    private async Task<List<(long Id, string FullName)>> OwnersAsync(long bookingId, CancellationToken ct) =>
        (await (from bc in db.BookingCustomers.AsNoTracking() join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                where bc.BookingId == bookingId && bc.IsActive && c.Status != CustomerStatus.Blocked select new { c.Id, c.FullName }).ToListAsync(ct))
        .Select(x => (x.Id, x.FullName)).ToList();

    private static Dictionary<string, string> Data(string customer, string project, string property, string agreement, string amount, string due, string daysOverdue, string receipt = "") => new()
    {
        ["CustomerName"] = customer, ["ProjectName"] = project, ["PropertyCode"] = property, ["AgreementNumber"] = agreement, ["Amount"] = amount, ["DueDate"] = due,
        ["DaysOverdue"] = daysOverdue, ["ReceiptNumber"] = receipt,
    };
}
