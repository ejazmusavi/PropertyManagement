using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Accounting;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Application.Reporting;

/// <summary>
/// Company and project dashboards (spec 9.1, 9.2). Operational figures are calculated live from the business tables (properties, bookings, payments, installment schedules,
/// expenses); financial figures (revenue, expenses, profit, cash and bank balances) come from the general ledger and are shown only to users who may read the financial reports.
/// Every dashboard carries reconciliation checks that compare the operational figures with the financial records they must agree with.
/// </summary>
public class DashboardService(IAppDbContext db, ReportingSupport s, AccountingReportService accounting)
{
    private sealed record Figures(Dictionary<long, (int Count, decimal Value)> Bookings, Dictionary<long, decimal> Collections, Dictionary<long, (decimal Outstanding, decimal Overdue, int OverdueLines, int OverdueAgreements)> Receivables,
        Dictionary<long, (decimal Approved, decimal Paid)> Expenses);

    private static decimal SqFt(decimal area, MeasurementUnit unit) => area * AreaConversion.SquareFeetPer(unit);

    private async Task<Figures> FiguresAsync(IQueryable<long> projects, DateOnly? from, DateOnly to, CancellationToken ct)
    {
        var bookings = await db.Bookings.AsNoTracking().Where(b => projects.Contains(b.ProjectId) && b.Status == BookingStatus.Confirmed && b.BookingDate <= to && (from == null || b.BookingDate >= from))
            .GroupBy(b => b.ProjectId).Select(g => new { g.Key, N = g.Count(), V = g.Sum(b => b.TotalPayable) }).ToListAsync(ct);
        var collections = await db.Payments.AsNoTracking().Where(p => projects.Contains(p.ProjectId) && p.Status == PaymentStatus.Posted && p.PaymentDate <= to && (from == null || p.PaymentDate >= from))
            .GroupBy(p => p.ProjectId).Select(g => new { g.Key, V = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var lines = await (from i in db.PaymentScheduleItems.AsNoTracking()
                           join sc in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals sc.Id
                           join a in db.CustomerAgreements.AsNoTracking() on sc.ActiveAgreementId equals a.Id
                           join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                           where a.Status == AgreementStatus.Active && projects.Contains(b.ProjectId) && i.Amount - i.AmountPaid - i.AmountWaived > 0m
                           group i by b.ProjectId into g
                           select new
                           {
                               g.Key,
                               Due = g.Sum(x => x.DueDate <= to ? x.Amount - x.AmountPaid - x.AmountWaived : 0m),
                               Over = g.Sum(x => x.DueDate < to ? x.Amount - x.AmountPaid - x.AmountWaived : 0m),
                               OverLines = g.Count(x => x.DueDate < to),
                           }).ToListAsync(ct);
        var overdueAgreements = await (from i in db.PaymentScheduleItems.AsNoTracking()
                                       join sc in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals sc.Id
                                       join a in db.CustomerAgreements.AsNoTracking() on sc.ActiveAgreementId equals a.Id
                                       join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                                       where a.Status == AgreementStatus.Active && projects.Contains(b.ProjectId) && i.DueDate < to && i.Amount - i.AmountPaid - i.AmountWaived > 0m
                                       select new { b.ProjectId, a.Id }).Distinct().GroupBy(x => x.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => projects.Contains(e.ProjectId) && e.Status == ExpenseStatus.Approved && e.ExpenseDate <= to && (from == null || e.ExpenseDate >= from))
            .GroupBy(e => e.ProjectId).Select(g => new { g.Key, A = g.Sum(e => e.Amount), P = g.Sum(e => e.AmountPaid) }).ToListAsync(ct);
        return new Figures(bookings.ToDictionary(x => x.Key, x => (x.N, x.V)), collections.ToDictionary(x => x.Key, x => x.V),
            lines.ToDictionary(x => x.Key, x => (x.Due, x.Over, x.OverLines, overdueAgreements.FirstOrDefault(o => o.Key == x.Key)?.N ?? 0)), expenses.ToDictionary(x => x.Key, x => (x.A, x.P)));
    }

    private async Task<bool> LedgerAllowedAsync(long? projectId, CancellationToken ct)
    {
        if (!await s.HasAsync(Permissions.AccountingReports, ct)) return false;
        return projectId is not null || await s.HasAllAccessAsync(ct);
    }

    private const string LedgerDenied = "Financial figures come from the general ledger and need the financial reports permission (company-wide figures also need access to all projects).";

    // =====================================================================================================
    // reconciliation checks
    // =====================================================================================================
    private async Task<List<DashboardCheckDto>> ChecksAsync(IQueryable<long> projects, DateOnly today, decimal? ledgerExpenses, decimal approvedExpenses, bool ledgerAvailable, CancellationToken ct)
    {
        var checks = new List<DashboardCheckDto>();

        // 1. collections: payments vs agreement ledger (all time)
        var posted = await db.Payments.AsNoTracking().Where(p => projects.Contains(p.ProjectId) && p.Status == PaymentStatus.Posted).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var received = await db.AgreementLedgerEntries.AsNoTracking().Where(e => projects.Contains(e.ProjectId) && e.EntryType == LedgerEntryType.PaymentReceived).SumAsync(e => (decimal?)e.Credit, ct) ?? 0m;
        var reversed = await db.AgreementLedgerEntries.AsNoTracking().Where(e => projects.Contains(e.ProjectId) && e.EntryType == LedgerEntryType.PaymentReversed).SumAsync(e => (decimal?)e.Debit, ct) ?? 0m;
        var ledgerCollections = received - reversed;
        checks.Add(new("Collections (all time)", "Posted payments", posted, "Customer ledger receipts less reversals", ledgerCollections, posted - ledgerCollections, posted == ledgerCollections,
            "Every posted payment must have a ledger receipt; reversed payments net to zero."));

        // 2. receivables: schedule-derived position vs agreement ledger balance (active agreements)
        var items = from i in db.PaymentScheduleItems.AsNoTracking()
                    join sc in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals sc.Id
                    join a in db.CustomerAgreements.AsNoTracking() on sc.ActiveAgreementId equals a.Id
                    join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                    where a.Status == AgreementStatus.Active && projects.Contains(b.ProjectId)
                    select new { i.DueDate, i.Amount, i.AmountPaid, i.AmountWaived };
        var outstandingDue = await items.Where(i => i.DueDate <= today).SumAsync(i => (decimal?)(i.Amount - i.AmountPaid - i.AmountWaived), ct) ?? 0m;
        var futurePaid = await items.Where(i => i.DueDate > today).SumAsync(i => (decimal?)i.AmountPaid, ct) ?? 0m;
        var activeAgreements = from a in db.CustomerAgreements.AsNoTracking() join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                               where a.Status == AgreementStatus.Active && projects.Contains(b.ProjectId) select a.Id;
        var advanceHeld = await (from al in db.PaymentAllocations.AsNoTracking() join p in db.Payments.AsNoTracking() on al.PaymentId equals p.Id
                                 where al.Kind == AllocationKind.Advance && al.Status == AllocationStatus.Active && p.Status == PaymentStatus.Posted && activeAgreements.Contains(al.AgreementId)
                                 select (decimal?)al.Amount).SumAsync(ct) ?? 0m;
        var postedActive = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Posted && activeAgreements.Contains(p.AgreementId)).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var allocated = await (from al in db.PaymentAllocations.AsNoTracking() join p in db.Payments.AsNoTracking() on al.PaymentId equals p.Id
                               where al.Status == AllocationStatus.Active && p.Status == PaymentStatus.Posted && activeAgreements.Contains(al.AgreementId)
                               select (decimal?)al.Amount).SumAsync(ct) ?? 0m;
        var expected = outstandingDue - futurePaid - advanceHeld - (postedActive - allocated);
        var ledgerBalance = await db.AgreementLedgerEntries.AsNoTracking().Where(e => projects.Contains(e.ProjectId) && activeAgreements.Contains(e.AgreementId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        checks.Add(new("Customer receivables (active agreements)", "Due installments less advance and unallocated money", expected, "Agreement ledger balance", ledgerBalance, expected - ledgerBalance, expected == ledgerBalance,
            "Installments post to the ledger when they fall due and the ledger is brought up to date when an agreement ledger or payment is opened; a difference means some agreements have not been brought up to date yet."));

        // 3. expenses: module vs general ledger
        if (ledgerAvailable && ledgerExpenses is { } le)
        {
            var pending = await db.AccountingPostingRequests.AsNoTracking().CountAsync(r => projects.Contains(r.ProjectId) && r.Status != AccountingPostingStatus.Posted && r.Status != AccountingPostingStatus.Skipped, ct);
            checks.Add(new("Expenses (selected period)", "Approved expenses (expense module)", approvedExpenses, "Expense accounts in the general ledger", le, approvedExpenses - le, approvedExpenses == le,
                pending > 0 ? $"{pending} accounting event(s) are not posted yet; manual journals and company-borne government fees also change the ledger figure."
                            : "Manual journals and company-borne government fees also change the ledger figure."));
        }
        return checks;
    }

    // =====================================================================================================
    // company dashboard
    // =====================================================================================================
    public async Task<CompanyDashboardDto> CompanyAsync(DashboardFilters filters, CancellationToken ct = default)
    {
        await s.EnsureAsync(Permissions.ReportsView, ct);
        var today = await s.TodayAsync(ct);
        var to = filters.To ?? today;
        if (filters.From is { } fr && fr > to) throw ReportingSupport.Invalid(nameof(filters.From), "The start date must not be after the end date.");
        var projects = await s.ProjectIdsAsync(null, ct);
        var list = await db.Projects.AsNoTracking().Where(p => projects.Contains(p.Id)).OrderBy(p => p.ProjectCode)
            .Select(p => new { p.Id, p.ProjectCode, p.ProjectName, p.CurrentArea, p.MeasurementUnit, p.Status }).ToListAsync(ct);
        var units = list.ToDictionary(p => p.Id, p => p.MeasurementUnit);
        var saleable = await db.LandDistributions.AsNoTracking().Where(d => projects.Contains(d.ProjectId) && (d.Category == LandCategory.Residential || d.Category == LandCategory.Commercial))
            .GroupBy(d => d.ProjectId).Select(g => new { g.Key, A = g.Sum(d => d.Area) }).ToListAsync(ct);
        var counts = await db.Properties.AsNoTracking().Where(p => projects.Contains(p.ProjectId)).GroupBy(p => new { p.ProjectId, p.Status }).Select(g => new { g.Key.ProjectId, g.Key.Status, N = g.Count() }).ToListAsync(ct);
        var fig = await FiguresAsync(projects, filters.From, to, ct);

        var rows = list.Select(p =>
        {
            var c = counts.Where(x => x.ProjectId == p.Id).ToList();
            var rc = fig.Receivables.GetValueOrDefault(p.Id);
            return new ProjectSummaryRowDto(p.Id, p.ProjectCode, p.ProjectName, SqFt(p.CurrentArea, p.MeasurementUnit), c.Sum(x => x.N), c.Where(x => x.Status == PropertyStatus.Sold).Sum(x => x.N),
                c.Where(x => x.Status == PropertyStatus.Available).Sum(x => x.N), fig.Bookings.GetValueOrDefault(p.Id).Value, fig.Collections.GetValueOrDefault(p.Id), rc.Outstanding, rc.Overdue,
                fig.Expenses.GetValueOrDefault(p.Id).Approved);
        }).ToList();

        int Count(PropertyStatus st) => counts.Where(x => x.Status == st).Sum(x => x.N);
        var approved = fig.Expenses.Values.Sum(v => v.Approved);

        var ledgerAllowed = await LedgerAllowedAsync(null, ct);
        decimal? cash = null, bank = null, ledgerExpenses = null, revenue = null, profit = null;
        if (ledgerAllowed)
        {
            var income = await accounting.IncomeStatementAsync(filters.From ?? new DateOnly(2000, 1, 1), to, null, ct);
            revenue = income.Revenue.Total; ledgerExpenses = income.Expenses.Total; profit = income.NetProfit;
            var cashIds = await db.CashAccounts.AsNoTracking().Select(c => c.GlAccountId).ToListAsync(ct);
            var bankIds = await db.BankAccounts.AsNoTracking().Select(b => b.GlAccountId).ToListAsync(ct);
            cash = await db.GeneralLedgerEntries.AsNoTracking().Where(e => cashIds.Contains(e.AccountId) && e.EntryDate <= to).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
            bank = await db.GeneralLedgerEntries.AsNoTracking().Where(e => bankIds.Contains(e.AccountId) && e.EntryDate <= to).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        }
        var checks = await ChecksAsync(projects, today, ledgerExpenses, approved, ledgerAllowed, ct);
        return new CompanyDashboardDto(filters.From, to, await s.CurrencyAsync(ct), list.Count, list.Count(p => p.Status == ProjectStatus.Active),
            list.Sum(p => SqFt(p.CurrentArea, p.MeasurementUnit)), saleable.Sum(x => SqFt(x.A, units.GetValueOrDefault(x.Key))),
            counts.Sum(x => x.N), Count(PropertyStatus.Available), Count(PropertyStatus.Reserved), Count(PropertyStatus.Booked), Count(PropertyStatus.Sold),
            fig.Bookings.Values.Sum(v => v.Count), fig.Bookings.Values.Sum(v => v.Value), fig.Collections.Values.Sum(), fig.Receivables.Values.Sum(v => v.Outstanding), fig.Receivables.Values.Sum(v => v.Overdue),
            approved, fig.Expenses.Values.Sum(v => v.Paid), ledgerAllowed, ledgerAllowed ? null : LedgerDenied, cash, bank, ledgerExpenses, revenue, profit, rows, checks);
    }

    // =====================================================================================================
    // project dashboard
    // =====================================================================================================
    public async Task<ProjectDashboardDto> ProjectAsync(long projectId, DashboardFilters filters, CancellationToken ct = default)
    {
        await s.EnsureAsync(Permissions.ReportsView, ct);
        var today = await s.TodayAsync(ct);
        var to = filters.To ?? today;
        if (filters.From is { } fr && fr > to) throw ReportingSupport.Invalid(nameof(filters.From), "The start date must not be after the end date.");
        var projects = await s.ProjectIdsAsync(projectId, ct);
        var p = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => new { x.Id, x.ProjectCode, x.ProjectName, x.Status, x.City, x.CurrentArea, x.MeasurementUnit }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Project not found.");
        var dist = await db.LandDistributions.AsNoTracking().Where(d => d.ProjectId == projectId && d.Area > 0m).OrderBy(d => d.Category).ToListAsync(ct);
        var totalDist = dist.Sum(d => d.Area);
        var land = dist.Select(d => new LandDistributionRowDto(d.Category.ToString(), d.Area, totalDist == 0m ? 0m : Math.Round(d.Area * 100m / totalDist, 2))).ToList();

        var props = await (from pr in db.Properties.AsNoTracking() join t in db.PropertyTypes.AsNoTracking() on pr.PropertyTypeId equals t.Id join c in db.PropertyCategories.AsNoTracking() on t.CategoryId equals c.Id
                           where pr.ProjectId == projectId select new { Category = c.Name, pr.Status, pr.Size, pr.MeasurementUnit }).ToListAsync(ct);
        var byCategory = props.GroupBy(x => x.Category).OrderBy(g => g.Key).Select(g => new InventoryCategoryRowDto(g.Key, g.Count(), g.Count(x => x.Status == PropertyStatus.Available),
            g.Count(x => x.Status == PropertyStatus.Reserved), g.Count(x => x.Status == PropertyStatus.Booked), g.Count(x => x.Status == PropertyStatus.Sold),
            g.Sum(x => AreaConversion.Convert(x.Size, x.MeasurementUnit, p.MeasurementUnit)))).ToList();
        var byStatus = props.GroupBy(x => x.Status).OrderBy(g => g.Key).Select(g => new InventoryStatusCountDto(g.Key.ToString(), g.Count())).ToList();

        var fig = await FiguresAsync(projects, filters.From, to, ct);
        var booking = fig.Bookings.GetValueOrDefault(projectId);
        var rc = fig.Receivables.GetValueOrDefault(projectId);
        var ex = fig.Expenses.GetValueOrDefault(projectId);
        var pending = await db.Bookings.AsNoTracking().CountAsync(b => b.ProjectId == projectId && b.Status == BookingStatus.PendingApproval, ct);
        var collections = fig.Collections.GetValueOrDefault(projectId);

        var ledgerAllowed = await LedgerAllowedAsync(projectId, ct);
        decimal? revenue = null, expenses = null, profit = null;
        if (ledgerAllowed)
        {
            var income = await accounting.IncomeStatementAsync(filters.From ?? new DateOnly(2000, 1, 1), to, projectId, ct);
            revenue = income.Revenue.Total; expenses = income.Expenses.Total; profit = income.NetProfit;
        }
        var checks = await ChecksAsync(projects, today, expenses, ex.Approved, ledgerAllowed, ct);
        return new ProjectDashboardDto(p.Id, p.ProjectCode, p.ProjectName, p.Status.ToString(), p.City, p.MeasurementUnit.ToString(), p.CurrentArea, SqFt(p.CurrentArea, p.MeasurementUnit), filters.From, to,
            await s.CurrencyAsync(ct), land, byCategory, byStatus, props.Count, props.Count(x => x.Status == PropertyStatus.Sold), booking.Count, booking.Value, booking.Count, booking.Value, pending,
            collections, rc.Outstanding, rc.Overdue, rc.OverdueLines, rc.OverdueAgreements, ex.Approved, ex.Paid, ex.Approved - ex.Paid,
            ledgerAllowed, ledgerAllowed ? null : LedgerDenied, revenue, expenses, profit, collections - ex.Approved, checks);
    }
}
