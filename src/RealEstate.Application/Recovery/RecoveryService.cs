using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Installments;
using RealEstate.Application.Payments;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Recovery;

/// <summary>
/// Recovery (spec 6.7): upcoming and overdue dues, recovery assignments, follow-up records, defaulter report and recovery officer performance.
/// All figures are read from the active payment schedules of active agreements (outstanding = amount - paid - waived), restricted to the caller's projects.
/// A line is overdue from the day after its due date; late fees use the charge's late fee rule after the grace period.
/// </summary>
public class RecoveryService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support, InstallmentSupport installments, IUserQueryService users, IAuditService audit,
    IValidator<CreateAssignmentRequest> assignValidator, IValidator<CloseAssignmentRequest> closeValidator, IValidator<CreateFollowUpRequest> followUpValidator)
{
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private sealed class OpenLine
    {
        public long ItemId { get; init; }
        public long AgreementId { get; init; }
        public string AgreementNumber { get; init; } = default!;
        public long ProjectId { get; init; }
        public string ProjectCode { get; init; } = default!;
        public long BookingId { get; init; }
        public long ChargeId { get; init; }
        public int LineNo { get; init; }
        public string Description { get; init; } = default!;
        public DateOnly DueDate { get; init; }
        public DateOnly GraceEndDate { get; init; }
        public decimal Amount { get; init; }
        public decimal Paid { get; init; }
        public decimal Waived { get; init; }
        public decimal LateFeeWaived { get; init; }
        public decimal Outstanding { get; init; }
    }

    /// <summary>Open lines (outstanding &gt; 0) of active schedules of active agreements in the caller's projects.</summary>
    private async Task<IQueryable<OpenLine>> OpenLinesAsync(long? projectId, CancellationToken ct)
    {
        var projects = await support.ProjectsAsync(ct);
        var q = from i in db.PaymentScheduleItems.AsNoTracking()
                join s in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals s.Id
                join a in db.CustomerAgreements.AsNoTracking() on s.ActiveAgreementId equals a.Id
                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                join p in projects on b.ProjectId equals p.Id
                where a.Status == AgreementStatus.Active && i.Amount - i.AmountPaid - i.AmountWaived > 0m
                select new OpenLine
                {
                    ItemId = i.Id, AgreementId = a.Id, AgreementNumber = a.AgreementNumber, ProjectId = b.ProjectId, ProjectCode = p.ProjectCode, BookingId = b.Id, ChargeId = i.AgreementChargeId,
                    LineNo = i.LineNo, Description = i.Description, DueDate = i.DueDate, GraceEndDate = i.GraceEndDate, Amount = i.Amount, Paid = i.AmountPaid, Waived = i.AmountWaived,
                    LateFeeWaived = i.LateFeeWaived, Outstanding = i.Amount - i.AmountPaid - i.AmountWaived,
                };
        if (projectId is { } pid) q = q.Where(x => x.ProjectId == pid);
        return q;
    }

    private IQueryable<OpenLine> ForCustomer(IQueryable<OpenLine> q, long customerId) =>
        q.Where(x => db.BookingCustomers.Any(bc => bc.BookingId == x.BookingId && bc.CustomerId == customerId && bc.IsActive));

    private IQueryable<OpenLine> ForOfficer(IQueryable<OpenLine> q, Guid officer) =>
        q.Where(x => db.RecoveryAssignments.Any(r => r.ActiveAgreementId == x.AgreementId && r.OfficerUserId == officer));

    /// <summary>Recovery officers may only see agreements assigned to them unless they can manage assignments or see all projects' recovery.</summary>
    private async Task<Guid?> OwnScopeAsync(CancellationToken ct)
    {
        var id = RequireUser();
        if (await permissions.HasPermissionAsync(id, Permissions.RecoveryView, ct)) return null;
        await permissions.EnsureAsync(user, Permissions.RecoveryFollowUp, ct);
        return id;
    }

    // =====================================================================================================
    // dues
    // =====================================================================================================
    public async Task<DueListDto> UpcomingAsync(DueListRequest r, CancellationToken ct = default)
    {
        var own = await OwnScopeAsync(ct);
        var today = await support.TodayAsync(ct);
        var until = today.AddDays(Math.Clamp(r.Days ?? 30, 1, 366));
        var q = (await OpenLinesAsync(r.ProjectId, ct)).Where(x => x.DueDate >= today && x.DueDate <= until);
        q = Filter(q, r, own);
        var total = await q.CountAsync(ct);
        var sum = await q.SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        var rows = await q.OrderBy(x => x.DueDate).ThenBy(x => x.AgreementId).ThenBy(x => x.LineNo).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new DueListDto(new PagedResult<DueLineDto>(await MapAsync(rows, today, ct), r.Page, r.PageSize, total), sum, null);
    }

    public async Task<DueListDto> OverdueAsync(DueListRequest r, CancellationToken ct = default)
    {
        var own = await OwnScopeAsync(ct);
        var today = await support.TodayAsync(ct);
        var cutoff = today.AddDays(-Math.Clamp(r.Days ?? 1, 1, 3650));
        var q = Filter((await OpenLinesAsync(r.ProjectId, ct)).Where(x => x.DueDate <= cutoff), r, own);
        var total = await q.CountAsync(ct);
        var aging = await AgingAsync(q, today, ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("amount", _) => q.OrderByDescending(x => x.Outstanding).ThenBy(x => x.DueDate),
            ("agreement", _) => q.OrderBy(x => x.AgreementNumber).ThenBy(x => x.DueDate),
            (_, true) => q.OrderByDescending(x => x.DueDate).ThenBy(x => x.AgreementId),
            _ => q.OrderBy(x => x.DueDate).ThenBy(x => x.AgreementId).ThenBy(x => x.LineNo),
        };
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new DueListDto(new PagedResult<DueLineDto>(await MapAsync(rows, today, ct), r.Page, r.PageSize, total), aging.Total, aging);
    }

    private IQueryable<OpenLine> Filter(IQueryable<OpenLine> q, DueListRequest r, Guid? own)
    {
        if (r.CustomerId is { } cid) q = ForCustomer(q, cid);
        if (own is { } o) q = ForOfficer(q, o);
        else if (r.OfficerUserId is { } officer) q = ForOfficer(q, officer);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.AgreementNumber.Contains(t) || x.Description.Contains(t)); }
        return q;
    }

    private static async Task<AgingDto> AgingAsync(IQueryable<OpenLine> overdue, DateOnly today, CancellationToken ct)
    {
        var d30 = today.AddDays(-30); var d60 = today.AddDays(-60); var d90 = today.AddDays(-90);
        var a = await overdue.Where(x => x.DueDate < today && x.DueDate >= d30).SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        var b = await overdue.Where(x => x.DueDate < d30 && x.DueDate >= d60).SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        var c = await overdue.Where(x => x.DueDate < d60 && x.DueDate >= d90).SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        var d = await overdue.Where(x => x.DueDate < d90).SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        return new AgingDto(a, b, c, d, a + b + c + d);
    }

    private async Task<List<DueLineDto>> MapAsync(List<OpenLine> rows, DateOnly today, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var bookingIds = rows.Select(r => r.BookingId).Distinct().ToList();
        var holders = await (from bc in db.BookingCustomers.AsNoTracking()
                             join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                             where bookingIds.Contains(bc.BookingId) && bc.IsActive
                             orderby bc.IsPrimary descending, bc.SharePercent descending, bc.Id
                             select new { bc.BookingId, c.Id, c.FullName, c.Mobile }).ToListAsync(ct);
        var agreementIds = rows.Select(r => r.AgreementId).Distinct().ToList();
        var officers = await db.RecoveryAssignments.AsNoTracking().Where(a => a.ActiveAgreementId != null && agreementIds.Contains(a.ActiveAgreementId.Value))
            .ToDictionaryAsync(a => a.AgreementId, a => a.OfficerName, ct);
        var chargeIds = rows.Select(r => r.ChargeId).Distinct().ToList();
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => chargeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var ruleIds = charges.Values.Where(c => c.LateFeeRuleId != null).Select(c => c.LateFeeRuleId!.Value).Distinct().ToList();
        var rules = await db.LateFeeRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var policy = await installments.PolicyAsync(ct);
        return rows.Select(r =>
        {
            var h = holders.First(x => x.BookingId == r.BookingId);
            var fee = 0m;
            if (charges.TryGetValue(r.ChargeId, out var c) && c.LateFeeRuleId is { } rid && rules.TryGetValue(rid, out var rule))
                fee = Math.Max(Domain.Installments.LateFeeCalculator.Compute(InstallmentSupport.Terms(rule), r.Amount, r.Outstanding, r.GraceEndDate, today, policy) - r.LateFeeWaived, 0m);
            return new DueLineDto(r.ItemId, r.AgreementId, r.AgreementNumber, r.ProjectCode, h.Id, h.FullName, h.Mobile, r.LineNo, r.Description, r.DueDate, r.GraceEndDate, r.Amount, r.Paid,
                r.Waived, r.Outstanding, Math.Max(today.DayNumber - r.DueDate.DayNumber, 0), fee, officers.GetValueOrDefault(r.AgreementId));
        }).ToList();
    }

    // =====================================================================================================
    // defaulters and performance
    // =====================================================================================================
    public async Task<PagedResult<DefaulterDto>> DefaultersAsync(DefaulterListRequest r, CancellationToken ct = default)
    {
        var own = await OwnScopeAsync(ct);
        var today = await support.TodayAsync(ct);
        var q = (await OpenLinesAsync(r.ProjectId, ct)).Where(x => x.DueDate < today);
        if (own is { } o) q = ForOfficer(q, o);
        var cutoff = today.AddDays(-Math.Max(r.MinDaysOverdue, 1));
        var minCount = Math.Max(r.MinInstallments, 1);
        var grouped = q.GroupBy(x => new { x.AgreementId, x.AgreementNumber, x.ProjectCode, x.BookingId })
            .Select(g => new { g.Key.AgreementId, g.Key.AgreementNumber, g.Key.ProjectCode, g.Key.BookingId, Count = g.Count(), Amount = g.Sum(x => x.Outstanding), Oldest = g.Min(x => x.DueDate) })
            .Where(g => g.Oldest <= cutoff && g.Count >= minCount);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); grouped = grouped.Where(g => g.AgreementNumber.Contains(t)); }
        var total = await grouped.CountAsync(ct);
        var rows = await grouped.OrderByDescending(g => g.Amount).ThenBy(g => g.Oldest).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<DefaulterDto>(await MapDefaultersAsync(rows.Select(x => (x.AgreementId, x.AgreementNumber, x.ProjectCode, x.BookingId, x.Count, x.Amount, x.Oldest)).ToList(), today, ct),
            r.Page, r.PageSize, total);
    }

    private async Task<List<DefaulterDto>> MapDefaultersAsync(List<(long AgreementId, string AgreementNumber, string ProjectCode, long BookingId, int Count, decimal Amount, DateOnly Oldest)> rows,
        DateOnly today, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.AgreementId).ToList();
        var bookingIds = rows.Select(r => r.BookingId).ToList();
        var holders = await (from bc in db.BookingCustomers.AsNoTracking()
                             join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                             where bookingIds.Contains(bc.BookingId) && bc.IsActive
                             orderby bc.IsPrimary descending, bc.SharePercent descending, bc.Id
                             select new { bc.BookingId, c.Id, c.FullName, c.Mobile }).ToListAsync(ct);
        var lastPay = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.AgreementId) && p.Status == PaymentStatus.Posted)
            .GroupBy(p => p.AgreementId).Select(g => new { g.Key, Last = g.Max(p => p.PaymentDate) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var officers = await db.RecoveryAssignments.AsNoTracking().Where(a => a.ActiveAgreementId != null && ids.Contains(a.ActiveAgreementId.Value))
            .ToDictionaryAsync(a => a.AgreementId, a => a.OfficerName, ct);
        var follow = await db.CustomerFollowUps.AsNoTracking().Where(f => ids.Contains(f.AgreementId)).OrderByDescending(f => f.FollowUpDate).ThenByDescending(f => f.Id)
            .Select(f => new { f.AgreementId, f.FollowUpDate, f.Outcome }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var h = holders.First(x => x.BookingId == r.BookingId);
            var f = follow.FirstOrDefault(x => x.AgreementId == r.AgreementId);
            return new DefaulterDto(r.AgreementId, r.AgreementNumber, r.ProjectCode, h.Id, h.FullName, h.Mobile, r.Count, r.Amount, r.Oldest, today.DayNumber - r.Oldest.DayNumber,
                lastPay.TryGetValue(r.AgreementId, out var lp) ? lp : null, officers.GetValueOrDefault(r.AgreementId), f?.FollowUpDate, f?.Outcome);
        }).ToList();
    }

    /// <summary>
    /// Per officer, for the period (default: current month): assignments, follow-ups, promises, money collected on assigned agreements while assigned
    /// (posted, not reversed payments dated in the period and in the assignment window), overdue at assignment and overdue now.
    /// Recovery rate = collected / overdue at assignment of the assignments in the period.
    /// </summary>
    public async Task<IReadOnlyList<OfficerPerformanceDto>> OfficerPerformanceAsync(OfficerPerformanceRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryView, ct);
        var today = await support.TodayAsync(ct);
        var start = r.From ?? new DateOnly(today.Year, today.Month, 1);
        var end = r.To ?? today;
        if (end < start) throw Invalid("To", "The end date cannot be before the start date.");
        var projects = await support.ProjectsAsync(ct);
        var assignments = await (from a in db.RecoveryAssignments.AsNoTracking()
                                 join p in projects on a.ProjectId equals p.Id
                                 where a.AssignedOn <= end && (a.ClosedOn == null || a.ClosedOn >= start) && (r.ProjectId == null || a.ProjectId == r.ProjectId)
                                 select a).ToListAsync(ct);
        var agreementIds = assignments.Select(a => a.AgreementId).Distinct().ToList();
        var paymentsInPeriod = await db.Payments.AsNoTracking()
            .Where(p => agreementIds.Contains(p.AgreementId) && p.Status == PaymentStatus.Posted && p.PaymentDate >= start && p.PaymentDate <= end)
            .Select(p => new { p.AgreementId, p.PaymentDate, p.Amount }).ToListAsync(ct);
        var followUps = await (from f in db.CustomerFollowUps.AsNoTracking()
                               join p in projects on f.ProjectId equals p.Id
                               where f.FollowUpDate >= start && f.FollowUpDate <= end && (r.ProjectId == null || f.ProjectId == r.ProjectId)
                               select new { f.OfficerUserId, f.OfficerName, f.Outcome, f.PromisedAmount }).ToListAsync(ct);
        var activeIds = assignments.Where(a => a.Status == RecoveryAssignmentStatus.Active).Select(a => a.AgreementId).ToList();
        var overdueNow = await (await OpenLinesAsync(r.ProjectId, ct)).Where(x => activeIds.Contains(x.AgreementId) && x.DueDate < today)
            .GroupBy(x => x.AgreementId).Select(g => new { g.Key, Sum = g.Sum(x => x.Outstanding) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

        var officers = assignments.Select(a => (a.OfficerUserId, a.OfficerName)).Concat(followUps.Select(f => (f.OfficerUserId, f.OfficerName)))
            .GroupBy(x => x.OfficerUserId).Select(g => (Id: g.Key, Name: g.First().OfficerName)).ToList();
        return officers.Select(o =>
        {
            var mine = assignments.Where(a => a.OfficerUserId == o.Id).ToList();
            var collected = mine.Sum(a => paymentsInPeriod.Where(p => p.AgreementId == a.AgreementId && p.PaymentDate >= a.AssignedOn && (a.ClosedOn == null || p.PaymentDate <= a.ClosedOn))
                .Sum(p => p.Amount));
            var baseline = mine.Sum(a => a.OverdueAtAssignment);
            var fu = followUps.Where(f => f.OfficerUserId == o.Id).ToList();
            return new OfficerPerformanceDto(o.Id, o.Name, mine.Count(a => a.Status == RecoveryAssignmentStatus.Active), mine.Count(a => a.Status == RecoveryAssignmentStatus.Closed), fu.Count,
                fu.Count(f => f.Outcome == FollowUpOutcome.PromiseToPay), fu.Sum(f => f.PromisedAmount ?? 0m), collected, baseline,
                mine.Where(a => a.Status == RecoveryAssignmentStatus.Active).Sum(a => overdueNow.GetValueOrDefault(a.AgreementId)),
                baseline == 0m ? 0m : decimal.Round(collected * 100m / baseline, 2));
        }).OrderByDescending(x => x.Collected).ThenBy(x => x.OfficerName).ToList();
    }

    public async Task<RecoveryDashboardDto> DashboardAsync(long? projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryView, ct);
        var today = await support.TodayAsync(ct);
        var open = await OpenLinesAsync(projectId, ct);
        var overdue = open.Where(x => x.DueDate < today);
        var aging = await AgingAsync(overdue, today, ct);
        var overdueAgreements = await overdue.Select(x => x.AgreementId).Distinct().CountAsync(ct);
        var overdueLines = await overdue.CountAsync(ct);
        var until = today.AddDays(30);
        var upcoming = open.Where(x => x.DueDate >= today && x.DueDate <= until);
        var upcomingAmount = await upcoming.SumAsync(x => (decimal?)x.Outstanding, ct) ?? 0m;
        var upcomingCount = await upcoming.CountAsync(ct);
        var d90 = today.AddDays(-90);
        var defaulters90 = await overdue.GroupBy(x => x.AgreementId).Select(g => g.Min(x => x.DueDate)).CountAsync(d => d <= d90, ct);
        var unassigned = await overdue.Where(x => !db.RecoveryAssignments.Any(a => a.ActiveAgreementId == x.AgreementId)).Select(x => x.AgreementId).Distinct().CountAsync(ct);
        var projects = await support.ProjectsAsync(ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var payments = from p in db.Payments.AsNoTracking() join pr in projects on p.ProjectId equals pr.Id where projectId == null || p.ProjectId == projectId select p;
        var collected = await payments.Where(p => p.Status == PaymentStatus.Posted && p.PaymentDate >= monthStart && p.PaymentDate <= today).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var pendingVerification = await payments.CountAsync(p => p.Status == PaymentStatus.PendingVerification, ct);
        var pendingApproval = await payments.CountAsync(p => p.Status == PaymentStatus.PendingApproval, ct);
        // Follow-ups due: the latest follow-up of an active agreement asks for a next contact on or before today.
        var latest = from f in db.CustomerFollowUps.AsNoTracking()
                     join pr in projects on f.ProjectId equals pr.Id
                     where (projectId == null || f.ProjectId == projectId) && !db.CustomerFollowUps.Any(g => g.AgreementId == f.AgreementId && (g.FollowUpDate > f.FollowUpDate || (g.FollowUpDate == f.FollowUpDate && g.Id > f.Id)))
                     select f;
        var followUpsDue = await latest.CountAsync(f => f.NextFollowUpDate != null && f.NextFollowUpDate <= today
            && db.CustomerAgreements.Any(a => a.Id == f.AgreementId && a.Status == AgreementStatus.Active), ct);
        var top = await DefaultersAsync(new DefaulterListRequest { ProjectId = projectId, MinDaysOverdue = 1, PageSize = 5 }, ct);
        return new RecoveryDashboardDto(aging.Total, overdueAgreements, overdueLines, upcomingAmount, upcomingCount, defaulters90, collected, pendingVerification, pendingApproval, unassigned,
            followUpsDue, aging, await support.CurrencySymbolAsync(ct), top.Items);
    }

    // =====================================================================================================
    // assignments
    // =====================================================================================================
    public async Task<IReadOnlyList<OfficerOptionDto>> OfficersAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryAssign, ct);
        var projectId = await support.EnsureAgreementAccessAsync(agreementId, ct);
        var candidates = await users.SearchActiveAsync(null, 200, ct);
        var result = new List<OfficerOptionDto>();
        foreach (var u in candidates)
            if (await IsEligibleOfficerAsync(u.Id, projectId, ct)) result.Add(new OfficerOptionDto(u.Id, u.FullName));
        return result;
    }

    private async Task<bool> IsEligibleOfficerAsync(Guid userId, long projectId, CancellationToken ct) =>
        await permissions.HasPermissionAsync(userId, Permissions.RecoveryFollowUp, ct)
        && (await permissions.HasPermissionAsync(userId, Permissions.ProjectsAccessAll, ct)
            || await db.UserProjects.AnyAsync(up => up.UserId == userId && up.ProjectId == projectId && up.IsActive, ct));

    public async Task<PagedResult<AssignmentDto>> ListAssignmentsAsync(AssignmentListRequest r, CancellationToken ct = default)
    {
        var own = await OwnScopeAsync(ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from a in db.RecoveryAssignments.AsNoTracking()
                join p in projects on a.ProjectId equals p.Id
                join ag in db.CustomerAgreements.AsNoTracking() on a.AgreementId equals ag.Id
                join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                select new { a, p.ProjectCode, ag.AgreementNumber, c.FullName };
        if (own is { } o) q = q.Where(x => x.a.OfficerUserId == o);
        else if (r.OfficerUserId is { } officer) q = q.Where(x => x.a.OfficerUserId == officer);
        if (r.ProjectId is { } pid) q = q.Where(x => x.a.ProjectId == pid);
        if (r.Status is { } s) q = q.Where(x => x.a.Status == s);
        if (r.AgreementId is { } aid) q = q.Where(x => x.a.AgreementId == aid);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.AgreementNumber.Contains(t) || x.FullName.Contains(t) || x.a.OfficerName.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.a.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        var today = await support.TodayAsync(ct);
        var ids = rows.Select(x => x.a.AgreementId).ToList();
        var overdue = await (await OpenLinesAsync(null, ct)).Where(x => ids.Contains(x.AgreementId) && x.DueDate < today)
            .GroupBy(x => x.AgreementId).Select(g => new { g.Key, Sum = g.Sum(x => x.Outstanding) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return new PagedResult<AssignmentDto>(rows.Select(x => Map(x.a, x.ProjectCode, x.AgreementNumber, x.FullName, overdue.GetValueOrDefault(x.a.AgreementId))).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<AssignmentDto> AssignAsync(CreateAssignmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryAssign, ct);
        var userId = RequireUser();
        await assignValidator.ValidateOrThrowAsync(r, ct);
        var projectId = await support.EnsureAgreementAccessAsync(r.AgreementId, ct);
        var ag = await support.InfoAsync(r.AgreementId, ct);
        if (ag.Status != AgreementStatus.Active) throw new DomainException("Only active agreements can be assigned for recovery.");
        if (!await IsEligibleOfficerAsync(r.OfficerUserId, projectId, ct)) throw Invalid(nameof(r.OfficerUserId), "The user cannot record follow-ups or has no access to this project.");
        var officer = (await users.LookupAsync([r.OfficerUserId], ct)).FirstOrDefault(u => u.IsActive) ?? throw Invalid(nameof(r.OfficerUserId), "The user was not found or is inactive.");
        var today = await support.TodayAsync(ct);
        var overdue = await OverdueOfAsync(r.AgreementId, today, ct);
        var a = new RecoveryAssignment
        {
            ProjectId = projectId, AgreementId = r.AgreementId, CustomerId = ag.HolderCustomerId, OfficerUserId = officer.Id, OfficerName = officer.FullName, AssignedOn = today,
            AssignedBy = support.Actor, Status = RecoveryAssignmentStatus.Active, ActiveAgreementId = r.AgreementId, OverdueAtAssignment = overdue, Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
        };
        db.RecoveryAssignments.Add(a);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex))
        {
            db.Entry(a).State = EntityState.Detached;
            throw new DomainException("This agreement already has an active recovery assignment; close it first.");
        }
        await audit.RecordAsync(new AuditEntry("Recovery.Assigned", "RecoveryAssignment", a.Id.ToString(), NewValues: new { ag.AgreementNumber, Officer = officer.FullName, overdue }, UserId: userId), ct);
        return await GetAssignmentAsync(a.Id, ct);
    }

    public async Task<AssignmentDto> CloseAssignmentAsync(long id, CloseAssignmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryAssign, ct);
        var userId = RequireUser();
        await closeValidator.ValidateOrThrowAsync(r, ct);
        var a = await db.RecoveryAssignments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Assignment not found.");
        await support.EnsureAgreementAccessAsync(a.AgreementId, ct);
        if (a.Status != RecoveryAssignmentStatus.Active) throw new DomainException("The assignment is already closed.");
        db.UseRowVersion(a, r.RowVersion);
        a.Status = RecoveryAssignmentStatus.Closed; a.ActiveAgreementId = null; a.ClosedOn = await support.TodayAsync(ct); a.ClosedBy = support.Actor; a.CloseReason = r.Reason.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Recovery.AssignmentClosed", "RecoveryAssignment", id.ToString(), new { Status = "Active" }, new { Status = "Closed" }, r.Reason.Trim(), UserId: userId), ct);
        return await GetAssignmentAsync(id, ct);
    }

    private async Task<AssignmentDto> GetAssignmentAsync(long id, CancellationToken ct)
    {
        var x = await (from a in db.RecoveryAssignments.AsNoTracking()
                       join p in db.Projects.AsNoTracking() on a.ProjectId equals p.Id
                       join ag in db.CustomerAgreements.AsNoTracking() on a.AgreementId equals ag.Id
                       join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                       where a.Id == id
                       select new { a, p.ProjectCode, ag.AgreementNumber, c.FullName }).FirstAsync(ct);
        return Map(x.a, x.ProjectCode, x.AgreementNumber, x.FullName, await OverdueOfAsync(x.a.AgreementId, await support.TodayAsync(ct), ct));
    }

    private static AssignmentDto Map(RecoveryAssignment a, string projectCode, string agreementNumber, string customerName, decimal currentOverdue) =>
        new(a.Id, a.ProjectId, projectCode, a.AgreementId, agreementNumber, a.CustomerId, customerName, a.OfficerUserId, a.OfficerName, a.AssignedOn, a.AssignedBy, a.Status,
            a.OverdueAtAssignment, currentOverdue, a.Notes, a.ClosedOn, a.ClosedBy, a.CloseReason, a.RowVersion.ToToken());

    private async Task<decimal> OverdueOfAsync(long agreementId, DateOnly today, CancellationToken ct) =>
        (await support.ActiveItemsAsync(agreementId, tracked: false, ct)).Where(i => i.DueDate < today).Sum(i => i.Outstanding);

    // =====================================================================================================
    // follow-ups
    // =====================================================================================================
    public async Task<PagedResult<FollowUpDto>> ListFollowUpsAsync(FollowUpListRequest r, CancellationToken ct = default)
    {
        var own = await OwnScopeAsync(ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from f in db.CustomerFollowUps.AsNoTracking()
                join p in projects on f.ProjectId equals p.Id
                join ag in db.CustomerAgreements.AsNoTracking() on f.AgreementId equals ag.Id
                join c in db.Customers.AsNoTracking() on f.CustomerId equals c.Id
                select new { f, p.ProjectCode, ag.AgreementNumber, c.FullName };
        if (own is { } o) q = q.Where(x => x.f.OfficerUserId == o || db.RecoveryAssignments.Any(a => a.ActiveAgreementId == x.f.AgreementId && a.OfficerUserId == o));
        else if (r.OfficerUserId is { } officer) q = q.Where(x => x.f.OfficerUserId == officer);
        if (r.ProjectId is { } pid) q = q.Where(x => x.f.ProjectId == pid);
        if (r.AgreementId is { } aid) q = q.Where(x => x.f.AgreementId == aid);
        if (r.CustomerId is { } cid) q = q.Where(x => x.f.CustomerId == cid);
        if (r.Outcome is { } oc) q = q.Where(x => x.f.Outcome == oc);
        if (r.From is { } from) q = q.Where(x => x.f.FollowUpDate >= from);
        if (r.To is { } to) q = q.Where(x => x.f.FollowUpDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.AgreementNumber.Contains(t) || x.FullName.Contains(t) || x.f.Notes.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.f.FollowUpDate).ThenByDescending(x => x.f.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(x => new FollowUpDto(x.f.Id, x.f.ProjectId, x.ProjectCode, x.f.AgreementId, x.AgreementNumber, x.f.CustomerId, x.FullName, x.f.OfficerUserId, x.f.OfficerName,
                x.f.FollowUpDate, x.f.Method, x.f.Outcome, x.f.PromisedAmount, x.f.PromisedDate, x.f.NextFollowUpDate, x.f.OverdueAtFollowUp, x.f.Notes, x.f.CreatedAtUtc)).ToListAsync(ct);
        return new PagedResult<FollowUpDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<FollowUpDto> CreateFollowUpAsync(CreateFollowUpRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RecoveryFollowUp, ct);
        var userId = RequireUser();
        await followUpValidator.ValidateOrThrowAsync(r, ct);
        var projectId = await support.EnsureAgreementAccessAsync(r.AgreementId, ct);
        var ag = await support.InfoAsync(r.AgreementId, ct);
        var today = await support.TodayAsync(ct);
        if (r.FollowUpDate > today) throw Invalid(nameof(r.FollowUpDate), "A follow-up cannot be recorded for a future date.");
        var assignment = await db.RecoveryAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.ActiveAgreementId == r.AgreementId, ct);
        // Without recovery.view the user works only on agreements assigned to them.
        if (!await permissions.HasPermissionAsync(userId, Permissions.RecoveryView, ct) && assignment?.OfficerUserId != userId)
            throw new ForbiddenException("This agreement is not assigned to you.");
        var f = new CustomerFollowUp
        {
            ProjectId = projectId, AgreementId = r.AgreementId, CustomerId = ag.HolderCustomerId, AssignmentId = assignment?.Id, OfficerUserId = userId, OfficerName = support.Actor,
            FollowUpDate = r.FollowUpDate, Method = r.Method, Outcome = r.Outcome, PromisedAmount = r.Outcome == FollowUpOutcome.PromiseToPay ? r.PromisedAmount : null,
            PromisedDate = r.Outcome == FollowUpOutcome.PromiseToPay ? r.PromisedDate : null, NextFollowUpDate = r.NextFollowUpDate, OverdueAtFollowUp = await OverdueOfAsync(r.AgreementId, today, ct),
            Notes = r.Notes.Trim(),
        };
        db.CustomerFollowUps.Add(f);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Recovery.FollowUpRecorded", "CustomerFollowUp", f.Id.ToString(), NewValues: new { ag.AgreementNumber, f.Method, f.Outcome, f.PromisedAmount, f.PromisedDate },
            UserId: userId), ct);
        var customerName = await db.Customers.AsNoTracking().Where(c => c.Id == f.CustomerId).Select(c => c.FullName).FirstAsync(ct);
        return new FollowUpDto(f.Id, f.ProjectId, ag.ProjectCode, f.AgreementId, ag.AgreementNumber, f.CustomerId, customerName, f.OfficerUserId, f.OfficerName, f.FollowUpDate, f.Method,
            f.Outcome, f.PromisedAmount, f.PromisedDate, f.NextFollowUpDate, f.OverdueAtFollowUp, f.Notes, f.CreatedAtUtc);
    }
}
