using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>
/// Financial reports (spec 8.6), all read from the posted general ledger (except the receivable/payable sub-ledgers they reconcile against). A project filter
/// restricts every figure to lines carrying that project (project-wise statements, spec 8.5); without a filter the reports are company-wide and need access to
/// all projects. There are no closing entries: profit of earlier fiscal years appears as retained earnings, the current year's as current-year profit.
/// </summary>
public class AccountingReportService(IAppDbContext db, AccountingSupport support, JournalService journals)
{
    private sealed record AccountInfo(long Id, string Code, string Name, AccountNature Nature, long GroupId, string GroupName, AccountClassification Classification, CashFlowSection Section, int GroupOrder);

    private async Task<Dictionary<long, AccountInfo>> AccountsAsync(CancellationToken ct)
    {
        var groups = await db.AccountGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        var accounts = await db.ChartOfAccounts.AsNoTracking().ToListAsync(ct);
        return accounts.ToDictionary(a => a.Id, a =>
        {
            var g = groups[a.AccountGroupId];
            return new AccountInfo(a.Id, a.Code, a.Name, a.Nature, g.Id, g.Name, g.Classification, g.CashFlowSection, g.SortOrder);
        });
    }

    private IQueryable<GeneralLedgerEntry> Ledger(long? projectId, long? costCenterId)
    {
        var q = db.GeneralLedgerEntries.AsNoTracking();
        if (projectId is { } p) q = q.Where(e => e.ProjectId == p);
        if (costCenterId is { } c) q = q.Where(e => e.CostCenterId == c);
        return q;
    }

    private async Task<Dictionary<long, (decimal D, decimal C)>> SumsAsync(IQueryable<GeneralLedgerEntry> q, CancellationToken ct) =>
        await q.GroupBy(e => e.AccountId).Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToDictionaryAsync(x => x.Key, x => (x.D, x.C), ct);

    private async Task BeginAsync(long? projectId, CancellationToken ct)
    {
        await support.EnsureAsync(Permissions.AccountingReports, ct);
        await support.EnsureScopeAsync(projectId, ct);
    }

    // =====================================================================================================
    // trial balance
    // =====================================================================================================
    public async Task<TrialBalanceDto> TrialBalanceAsync(ReportFilter f, CancellationToken ct = default)
    {
        await BeginAsync(f.ProjectId, ct);
        var to = f.To ?? await support.TodayAsync(ct);
        var accounts = await AccountsAsync(ct);
        var opening = f.From is { } from ? await SumsAsync(Ledger(f.ProjectId, f.CostCenterId).Where(e => e.EntryDate < from), ct) : [];
        var period = await SumsAsync(Ledger(f.ProjectId, f.CostCenterId).Where(e => e.EntryDate <= to && (f.From == null || e.EntryDate >= f.From)), ct);
        var rows = new List<TrialBalanceRowDto>();
        foreach (var id in opening.Keys.Union(period.Keys))
        {
            var a = accounts[id];
            var (od, oc) = opening.GetValueOrDefault(id);
            var (pd, pc) = period.GetValueOrDefault(id);
            var on = od - oc; var cn = on + pd - pc;
            rows.Add(new TrialBalanceRowDto(id, a.Code, a.Name, a.GroupName, a.Nature, on > 0 ? on : 0m, on < 0 ? -on : 0m, pd, pc, cn > 0 ? cn : 0m, cn < 0 ? -cn : 0m));
        }
        rows = rows.OrderBy(r => r.Code).ToList();
        var tcd = rows.Sum(r => r.ClosingDebit); var tcc = rows.Sum(r => r.ClosingCredit);
        return new TrialBalanceDto(f.From, to, f.ProjectId, rows, rows.Sum(r => r.OpeningDebit), rows.Sum(r => r.OpeningCredit), rows.Sum(r => r.PeriodDebit), rows.Sum(r => r.PeriodCredit),
            tcd, tcc, tcd == tcc && rows.Sum(r => r.PeriodDebit) == rows.Sum(r => r.PeriodCredit), await support.CurrencyAsync(ct));
    }

    // =====================================================================================================
    // general ledger, cashbook, bankbook
    // =====================================================================================================
    public async Task<GeneralLedgerDto> GeneralLedgerAsync(GeneralLedgerRequest r, CancellationToken ct = default)
    {
        await BeginAsync(r.ProjectId, ct);
        var account = await db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AccountId, ct) ?? throw new NotFoundException("Account not found.");
        var to = r.To ?? await support.TodayAsync(ct);
        var page = Math.Max(1, r.Page); var size = Math.Clamp(r.PageSize, 1, 200);
        var baseQ = Ledger(r.ProjectId, r.CostCenterId).Where(e => e.AccountId == r.AccountId);
        var openingNet = r.From is { } from ? await baseQ.Where(e => e.EntryDate < from).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m : 0m;
        var inRange = baseQ.Where(e => e.EntryDate <= to && (r.From == null || e.EntryDate >= r.From)).OrderBy(e => e.EntryDate).ThenBy(e => e.Id);
        var total = await inRange.CountAsync(ct);
        var totals = await inRange.GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
        var before = (page - 1) * size == 0 ? 0m : await inRange.Take((page - 1) * size).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var rows = await (from e in inRange.Skip((page - 1) * size).Take(size)
                          join p in db.Projects.AsNoTracking() on e.ProjectId equals (long?)p.Id into ps
                          from p in ps.DefaultIfEmpty()
                          join c in db.CostCenters.AsNoTracking() on e.CostCenterId equals (long?)c.Id into cs
                          from c in cs.DefaultIfEmpty()
                          select new { e, ProjectCode = p == null ? null : p.ProjectCode, CostCenterCode = c == null ? null : c.Code }).ToListAsync(ct);
        rows = rows.OrderBy(x => x.e.EntryDate).ThenBy(x => x.e.Id).ToList();
        var sign = ChartOfAccount.IsDebitNormal(account.Nature) ? 1m : -1m;
        var running = openingNet + before;
        var lines = new List<LedgerLineDto>();
        foreach (var x in rows)
        {
            running += x.e.Debit - x.e.Credit;
            lines.Add(new LedgerLineDto(x.e.Id, x.e.EntryDate, x.e.VoucherNumber, x.e.JournalEntryId, x.e.Narration, x.e.Reference, x.ProjectCode, x.CostCenterCode, x.e.Debit, x.e.Credit, running * sign, x.e.Source));
        }
        var d = totals?.D ?? 0m; var cr = totals?.C ?? 0m;
        return new GeneralLedgerDto(account.Id, account.Code, account.Name, account.Nature, r.From, to, r.ProjectId, openingNet * sign, d, cr, (openingNet + d - cr) * sign,
            new PagedResult<LedgerLineDto>(lines, page, size, total), await support.CurrencyAsync(ct));
    }

    /// <summary>Cashbook: the general ledger of a cash account's ledger account.</summary>
    public async Task<GeneralLedgerDto> CashbookAsync(long cashAccountId, DateOnly? from, DateOnly? to, int page = 1, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingReports, ct);
        var gl = await db.CashAccounts.AsNoTracking().Where(c => c.Id == cashAccountId).Select(c => (long?)c.GlAccountId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Cash account not found.");
        return await GeneralLedgerAsync(new GeneralLedgerRequest(gl, from, to, null, null, page, 50), ct);
    }

    /// <summary>Bankbook: the general ledger of a bank account's ledger account.</summary>
    public async Task<GeneralLedgerDto> BankbookAsync(long bankAccountId, DateOnly? from, DateOnly? to, int page = 1, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingReports, ct);
        var gl = await db.BankAccounts.AsNoTracking().Where(c => c.Id == bankAccountId).Select(c => (long?)c.GlAccountId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Bank account not found.");
        return await GeneralLedgerAsync(new GeneralLedgerRequest(gl, from, to, null, null, page, 50), ct);
    }

    // =====================================================================================================
    // journal register
    // =====================================================================================================
    public async Task<JournalRegisterDto> JournalRegisterAsync(DateOnly? from, DateOnly? to, JournalSource? source, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingReports, ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 50);
        var q = (await support.ScopeJournalsAsync(db.JournalEntries.AsNoTracking(), ct)).Where(j => j.Status == JournalStatus.Posted);
        if (from is { } f) q = q.Where(j => j.EntryDate >= f);
        if (to is { } t) q = q.Where(j => j.EntryDate <= t);
        if (source is { } s) q = q.Where(j => j.Source == s);
        var total = await q.CountAsync(ct);
        var sums = await q.GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.TotalDebit), C = g.Sum(x => x.TotalCredit) }).FirstOrDefaultAsync(ct);
        var ids = await q.OrderBy(j => j.EntryDate).ThenBy(j => j.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(j => j.Id).ToListAsync(ct);
        var items = new List<JournalDetailDto>();
        foreach (var id in ids) items.Add(await journals.BuildAsync(id, ct));
        return new JournalRegisterDto(from, to, new PagedResult<JournalDetailDto>(items, page, pageSize, total), sums?.D ?? 0m, sums?.C ?? 0m);
    }

    // =====================================================================================================
    // statements
    // =====================================================================================================
    private static StatementSectionDto Section(string title, IEnumerable<AccountInfo> accounts, IReadOnlyDictionary<long, (decimal D, decimal C)> sums)
    {
        var lines = new List<StatementLineDto>();
        decimal total = 0m;
        foreach (var g in accounts.Where(a => sums.ContainsKey(a.Id)).GroupBy(a => (a.GroupId, a.GroupName, a.GroupOrder)).OrderBy(g => g.Key.GroupOrder).ThenBy(g => g.Key.GroupName))
        {
            var items = g.OrderBy(a => a.Code).Select(a => (a, Amount: AccountingMath.NormalBalance(a.Nature, sums[a.Id].D, sums[a.Id].C))).Where(x => x.Amount != 0m).ToList();
            if (items.Count == 0) continue;
            var sub = items.Sum(x => x.Amount);
            lines.Add(new StatementLineDto(null, "", g.Key.GroupName, sub, 0, true));
            lines.AddRange(items.Select(x => new StatementLineDto(x.a.Id, x.a.Code, x.a.Name, x.Amount, 1, false)));
            total += sub;
        }
        return new StatementSectionDto(title, lines, total);
    }

    public async Task<BalanceSheetDto> BalanceSheetAsync(DateOnly? asOf, long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var accounts = await AccountsAsync(ct);
        var sums = await SumsAsync(Ledger(projectId, null).Where(e => e.EntryDate <= date), ct);
        var assets = Section("Assets", accounts.Values.Where(a => a.Nature == AccountNature.Asset), sums);
        var liabilities = Section("Liabilities", accounts.Values.Where(a => a.Nature == AccountNature.Liability), sums);
        var equity = Section("Equity", accounts.Values.Where(a => a.Nature == AccountNature.Equity), sums);
        // Profit without closing entries: before the current fiscal year = retained earnings; within it = current-year profit.
        var yearStart = await db.FiscalYears.AsNoTracking().Where(y => y.StartDate <= date && y.EndDate >= date).Select(y => (DateOnly?)y.StartDate).FirstOrDefaultAsync(ct) ?? date;
        var pl = accounts.Values.Where(a => a.Nature is AccountNature.Revenue or AccountNature.Expense).Select(a => a.Id).ToHashSet();
        var earlier = await SumsAsync(Ledger(projectId, null).Where(e => e.EntryDate < yearStart && pl.Contains(e.AccountId)), ct);
        var retained = earlier.Sum(x => x.Value.C - x.Value.D);
        var current = sums.Where(x => pl.Contains(x.Key)).Sum(x => x.Value.C - x.Value.D) - retained;
        var totalLe = liabilities.Total + equity.Total + retained + current;
        return new BalanceSheetDto(date, projectId, assets, liabilities, equity, retained, current, totalLe, assets.Total == totalLe, await support.CurrencyAsync(ct));
    }

    public async Task<IncomeStatementDto> IncomeStatementAsync(DateOnly? from, DateOnly? to, long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var (f, t) = await RangeAsync(from, to, ct);
        var accounts = await AccountsAsync(ct);
        var sums = await SumsAsync(Ledger(projectId, null).Where(e => e.EntryDate >= f && e.EntryDate <= t), ct);
        var revenue = Section("Revenue", accounts.Values.Where(a => a.Nature == AccountNature.Revenue), sums);
        var expenses = Section("Expenses", accounts.Values.Where(a => a.Nature == AccountNature.Expense), sums);
        return new IncomeStatementDto(f, t, projectId, revenue, expenses, revenue.Total - expenses.Total, await support.CurrencyAsync(ct));
    }

    /// <summary>Default range: the fiscal year containing the end date (or the calendar year when no fiscal year exists).</summary>
    private async Task<(DateOnly From, DateOnly To)> RangeAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var t = to ?? await support.TodayAsync(ct);
        if (from is { } f) return (f, t);
        var start = await db.FiscalYears.AsNoTracking().Where(y => y.StartDate <= t && y.EndDate >= t).Select(y => (DateOnly?)y.StartDate).FirstOrDefaultAsync(ct);
        return (start ?? new DateOnly(t.Year, 1, 1), t);
    }

    /// <summary>
    /// Direct-method cash flow: movements of Cash and Bank accounts in the range, classified by the cash flow section of the counter-accounts' groups.
    /// Closing cash is checked against the ledger balance.
    /// </summary>
    public async Task<CashFlowDto> CashFlowAsync(DateOnly? from, DateOnly? to, long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var (f, t) = await RangeAsync(from, to, ct);
        var accounts = await AccountsAsync(ct);
        var cash = accounts.Values.Where(a => a.Classification is AccountClassification.Cash or AccountClassification.Bank).Select(a => a.Id).ToHashSet();
        var opening = await Ledger(projectId, null).Where(e => e.EntryDate < f && cash.Contains(e.AccountId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var closingLedger = await Ledger(projectId, null).Where(e => e.EntryDate <= t && cash.Contains(e.AccountId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var journalIds = Ledger(projectId, null).Where(e => e.EntryDate >= f && e.EntryDate <= t && cash.Contains(e.AccountId)).Select(e => e.JournalEntryId).Distinct();
        var entries = await Ledger(projectId, null).Where(e => journalIds.Contains(e.JournalEntryId)).Select(e => new { e.JournalEntryId, e.AccountId, e.Debit, e.Credit }).ToListAsync(ct);
        var bySection = new Dictionary<CashFlowSection, decimal>();
        var byGroup = new Dictionary<(CashFlowSection, string), decimal>();
        foreach (var j in entries.GroupBy(e => e.JournalEntryId))
        {
            var lines = j.Select(e => (IsCash: cash.Contains(e.AccountId), accounts[e.AccountId].Section, e.Debit, e.Credit)).ToList();
            foreach (var (section, amount) in AccountingMath.CashFlow(lines)) bySection[section] = bySection.GetValueOrDefault(section) + amount;
            if (!lines.Any(l => l.IsCash)) continue;
            foreach (var e in j.Where(e => !cash.Contains(e.AccountId)))
            {
                var key = (accounts[e.AccountId].Section, accounts[e.AccountId].GroupName);
                byGroup[key] = byGroup.GetValueOrDefault(key) + (e.Credit - e.Debit);
            }
        }
        var op = bySection.GetValueOrDefault(CashFlowSection.Operating); var inv = bySection.GetValueOrDefault(CashFlowSection.Investing); var fin = bySection.GetValueOrDefault(CashFlowSection.Financing);
        var details = new List<StatementLineDto>();
        foreach (var s in new[] { CashFlowSection.Operating, CashFlowSection.Investing, CashFlowSection.Financing })
        {
            details.Add(new StatementLineDto(null, "", $"{s} activities", bySection.GetValueOrDefault(s), 0, true));
            details.AddRange(byGroup.Where(x => x.Key.Item1 == s && x.Value != 0m).OrderBy(x => x.Key.Item2).Select(x => new StatementLineDto(null, "", x.Key.Item2, x.Value, 1, false)));
        }
        var net = op + inv + fin;
        return new CashFlowDto(f, t, projectId, opening, op, inv, fin, net, opening + net, closingLedger, opening + net == closingLedger, details, await support.CurrencyAsync(ct));
    }

    // =====================================================================================================
    // receivables and payables
    // =====================================================================================================
    private async Task<decimal> ControlBalanceAsync(AccountClassification[] classes, DateOnly asOf, long? projectId, CancellationToken ct)
    {
        var ids = await (from a in db.ChartOfAccounts join g in db.AccountGroups on a.AccountGroupId equals g.Id where classes.Contains(g.Classification) select a.Id).ToListAsync(ct);
        return await Ledger(projectId, null).Where(e => e.EntryDate <= asOf && ids.Contains(e.AccountId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
    }

    private static readonly AccountClassification[] CustomerControl = [AccountClassification.CustomerReceivables, AccountClassification.CustomerAdvances];

    /// <summary>Accounts receivable by agreement from the customer (agreement) ledger, compared with the customer control accounts (receivables and advances).</summary>
    public async Task<AccountsReceivableDto> AccountsReceivableAsync(DateOnly? asOf, long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var q = db.AgreementLedgerEntries.AsNoTracking().Where(e => e.EntryDate <= date);
        if (projectId is { } p) q = q.Where(e => e.ProjectId == p);
        var sums = await q.GroupBy(e => new { e.AgreementId, e.ProjectId }).Select(g => new { g.Key.AgreementId, g.Key.ProjectId, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var agIds = sums.Select(s => s.AgreementId).ToList();
        var info = await (from a in db.CustomerAgreements.AsNoTracking()
                          where agIds.Contains(a.Id)
                          join bc in db.BookingCustomers.AsNoTracking() on a.BookingId equals bc.BookingId
                          where bc.IsPrimary && bc.IsActive
                          join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                          select new { a.Id, a.AgreementNumber, CustomerId = c.Id, c.FullName }).ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ProjectCode, ct);
        var rows = sums.Select(s =>
        {
            var i = info.FirstOrDefault(x => x.Id == s.AgreementId);
            return new ReceivableRowDto(s.AgreementId, i?.AgreementNumber ?? $"#{s.AgreementId}", s.ProjectId, projects.GetValueOrDefault(s.ProjectId) ?? "", i?.CustomerId ?? 0, i?.FullName ?? "", s.D, s.C, s.D - s.C);
        }).Where(r => r.Balance != 0m).OrderBy(r => r.ProjectCode).ThenBy(r => r.AgreementNumber).ToList();
        var net = sums.Sum(s => s.D - s.C);
        var control = await ControlBalanceAsync(CustomerControl, date, projectId, ct);
        return new AccountsReceivableDto(date, projectId, rows, rows.Where(r => r.Balance > 0).Sum(r => r.Balance), -rows.Where(r => r.Balance < 0).Sum(r => r.Balance), net, control, net - control,
            await support.CurrencyAsync(ct));
    }

    /// <summary>Accounts payable by vendor from approved, unpaid expenses, compared with the vendor payable control accounts; government payables likewise.</summary>
    public async Task<AccountsPayableDto> AccountsPayableAsync(long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var today = await support.TodayAsync(ct);
        var q = db.Expenses.AsNoTracking().Where(e => e.Status == ExpenseStatus.Approved && e.AmountPaid < e.Amount);
        if (projectId is { } p) q = q.Where(e => e.ProjectId == p);
        var data = await (from e in q
                          join v in db.Vendors.AsNoTracking() on e.VendorId equals (long?)v.Id into vs
                          from v in vs.DefaultIfEmpty()
                          select new { e.VendorId, VendorName = v == null ? null : v.Name, e.Amount, e.AmountPaid, e.DueDate }).ToListAsync(ct);
        var rows = data.GroupBy(x => new { x.VendorId, x.VendorName }).Select(g => new PayableRowDto(g.Key.VendorId, g.Key.VendorName ?? "(no vendor)", g.Count(), g.Sum(x => x.Amount), g.Sum(x => x.AmountPaid),
            g.Sum(x => x.Amount - x.AmountPaid), g.Where(x => x.DueDate is { } d && d < today).Sum(x => x.Amount - x.AmountPaid))).OrderByDescending(r => r.Outstanding).ToList();
        var total = rows.Sum(r => r.Outstanding);
        var control = -await ControlBalanceAsync([AccountClassification.VendorPayables], today, projectId, ct);
        var gq = db.GovernmentFeeAssessments.AsNoTracking().Where(a => a.Status == GovernmentFeeAssessmentStatus.Assessed);
        if (projectId is { } gp) gq = gq.Where(a => a.ProjectId == gp);
        var govOutstanding = await gq.SumAsync(a => (decimal?)(a.Amount - a.PaidAmount), ct) ?? 0m;
        var govControl = -await ControlBalanceAsync([AccountClassification.GovernmentPayables], today, projectId, ct);
        return new AccountsPayableDto(today, projectId, rows, total, control, total - control, govOutstanding, govControl, await support.CurrencyAsync(ct));
    }

    // =====================================================================================================
    // project accounting
    // =====================================================================================================
    public async Task<ProjectProfitabilityDto> ProjectProfitabilityAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingReports, ct);
        var (f, t) = await RangeAsync(from, to, ct);
        var accounts = await AccountsAsync(ct);
        var all = await support.HasAllAccessAsync(ct);
        var visible = (await support.ProjectOptionsAsync(ct)).ToDictionary(p => p.Id);
        var rev = accounts.Values.Where(a => a.Nature == AccountNature.Revenue).Select(a => a.Id).ToHashSet();
        var exp = accounts.Values.Where(a => a.Nature == AccountNature.Expense).Select(a => a.Id).ToHashSet();
        var sums = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.EntryDate >= f && e.EntryDate <= t && (rev.Contains(e.AccountId) || exp.Contains(e.AccountId)))
            .GroupBy(e => new { e.ProjectId, IsRevenue = rev.Contains(e.AccountId) }).Select(g => new { g.Key.ProjectId, g.Key.IsRevenue, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var rows = new List<ProjectProfitRowDto>();
        foreach (var pid in sums.Select(s => s.ProjectId).Distinct())
        {
            if (pid is null ? !all : !visible.ContainsKey(pid.Value)) continue;
            var revenue = sums.Where(s => s.ProjectId == pid && s.IsRevenue).Sum(s => s.C - s.D);
            var expenses = sums.Where(s => s.ProjectId == pid && !s.IsRevenue).Sum(s => s.D - s.C);
            var p = pid is { } x ? visible[x] : null;
            rows.Add(new ProjectProfitRowDto(pid, p?.ProjectCode ?? "(company level)", p?.ProjectName ?? "Not assigned to a project", revenue, expenses, revenue - expenses,
                revenue == 0m ? null : decimal.Round((revenue - expenses) / revenue * 100m, 2)));
        }
        rows = rows.OrderBy(r => r.ProjectId is null).ThenBy(r => r.ProjectCode).ToList();
        return new ProjectProfitabilityDto(f, t, rows, rows.Sum(r => r.Revenue), rows.Sum(r => r.Expenses), rows.Sum(r => r.NetProfit), await support.CurrencyAsync(ct));
    }

    public async Task<ProjectExpenseStatementDto> ProjectExpenseStatementAsync(long projectId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var (f, t) = await RangeAsync(from, to, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var accounts = await AccountsAsync(ct);
        var exp = accounts.Values.Where(a => a.Nature == AccountNature.Expense).Select(a => a.Id).ToHashSet();
        var q = Ledger(projectId, null).Where(e => e.EntryDate >= f && e.EntryDate <= t && exp.Contains(e.AccountId));
        var sums = await SumsAsync(q, ct);
        var section = Section("Expenses", accounts.Values.Where(a => a.Nature == AccountNature.Expense), sums);
        var byCc = await (from e in q
                          join c in db.CostCenters.AsNoTracking() on e.CostCenterId equals (long?)c.Id into cs
                          from c in cs.DefaultIfEmpty()
                          group e by new { Code = c == null ? null : c.Code, Name = c == null ? null : c.Name } into g
                          select new { g.Key.Code, g.Key.Name, Amount = g.Sum(x => x.Debit - x.Credit) }).ToListAsync(ct);
        var module = await db.Expenses.AsNoTracking().Where(e => e.ProjectId == projectId && e.Status == ExpenseStatus.Approved && e.ExpenseDate >= f && e.ExpenseDate <= t).SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        return new ProjectExpenseStatementDto(projectId, project.ProjectCode, project.ProjectName, f, t, section,
            byCc.OrderBy(x => x.Code).Select(x => new StatementLineDto(null, x.Code ?? "", x.Name ?? "(no cost center)", x.Amount, 0, false)).ToList(), section.Total, module, await support.CurrencyAsync(ct));
    }

    // =====================================================================================================
    // reconciliations
    // =====================================================================================================
    /// <summary>
    /// Customer control account reconciliation per project: the agreement (customer) ledger balance against the ledger balance of the customer receivable and
    /// advance accounts. Differences are explained by customer events not yet posted (pending/failed) or by posting rules that do not use the control accounts.
    /// </summary>
    public async Task<ControlReconciliationDto> CustomerControlReconciliationAsync(DateOnly? asOf, long? projectId, CancellationToken ct = default)
    {
        await BeginAsync(projectId, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var projects = (await support.ProjectOptionsAsync(ct)).Where(p => projectId == null || p.Id == projectId).ToList();
        var ids = projects.Select(p => p.Id).ToList();
        var ledger = await db.AgreementLedgerEntries.AsNoTracking().Where(e => e.EntryDate <= date && ids.Contains(e.ProjectId))
            .GroupBy(e => e.ProjectId).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Key, x => x.B, ct);
        var controlIds = await (from a in db.ChartOfAccounts join g in db.AccountGroups on a.AccountGroupId equals g.Id where CustomerControl.Contains(g.Classification) select a.Id).ToListAsync(ct);
        var control = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.EntryDate <= date && e.ProjectId != null && ids.Contains(e.ProjectId.Value) && controlIds.Contains(e.AccountId))
            .GroupBy(e => e.ProjectId!.Value).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Key, x => x.B, ct);
        AccountingEventType[] customerEvents = [AccountingEventType.PaymentPosted, AccountingEventType.PaymentReversed, AccountingEventType.RefundPaid, AccountingEventType.ReceivableRecognized,
            AccountingEventType.ReceivableDerecognized, AccountingEventType.CustomerWaiver, AccountingEventType.CustomerWaiverReversed];
        var pending = await db.AccountingPostingRequests.AsNoTracking().Where(r => ids.Contains(r.ProjectId) && r.EventDate <= date && customerEvents.Contains(r.EventType)
                && (r.Status == AccountingPostingStatus.Pending || r.Status == AccountingPostingStatus.Failed))
            .GroupBy(r => r.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var unfed = await db.AgreementLedgerEntries.AsNoTracking()
            .Where(e => e.EntryDate <= date && ids.Contains(e.ProjectId) && (e.EntryType == LedgerEntryType.InstallmentDue || e.EntryType == LedgerEntryType.DueAdjustment || e.EntryType == LedgerEntryType.Waiver || e.EntryType == LedgerEntryType.WaiverAdjustment)
                && !db.AccountingPostingRequests.Any(r => r.SourceType == PostingEngineService.LedgerSource && r.SourceId == e.Id))
            .GroupBy(e => e.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var rows = projects.Where(p => ledger.ContainsKey(p.Id) || control.ContainsKey(p.Id)).Select(p =>
        {
            var l = ledger.GetValueOrDefault(p.Id); var c = control.GetValueOrDefault(p.Id);
            var n = pending.GetValueOrDefault(p.Id) + unfed.GetValueOrDefault(p.Id);
            return new ControlReconciliationRowDto(p.Id, p.ProjectCode, l, c, l - c, n, l == c);
        }).OrderBy(r => r.ProjectCode).ToList();
        return new ControlReconciliationDto(date, rows, rows.Sum(r => r.LedgerBalance), rows.Sum(r => r.ControlBalance), rows.Sum(r => r.Difference), rows.All(r => r.Reconciled), await support.CurrencyAsync(ct));
    }

    /// <summary>
    /// Project accounts reconciliation: per project, ledger debits and credits, customer ledger vs control accounts, vendor payables vs vendor payable accounts and
    /// approved expenses vs expense accounts; and the sum over projects (plus company-level lines) against the company totals.
    /// </summary>
    public async Task<ProjectReconciliationDto> ProjectReconciliationAsync(DateOnly? asOf, CancellationToken ct = default)
    {
        await BeginAsync(null, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var accounts = await AccountsAsync(ct);
        var projects = await support.ProjectOptionsAsync(ct);
        var customerIds = accounts.Values.Where(a => CustomerControl.Contains(a.Classification)).Select(a => a.Id).ToHashSet();
        var vendorIds = accounts.Values.Where(a => a.Classification == AccountClassification.VendorPayables).Select(a => a.Id).ToHashSet();
        var expenseIds = accounts.Values.Where(a => a.Nature == AccountNature.Expense).Select(a => a.Id).ToHashSet();
        var gl = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.EntryDate <= date)
            .GroupBy(e => new { e.ProjectId, e.AccountId }).Select(g => new { g.Key.ProjectId, g.Key.AccountId, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var ledger = await db.AgreementLedgerEntries.AsNoTracking().Where(e => e.EntryDate <= date).GroupBy(e => e.ProjectId).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Key, x => x.B, ct);
        var vendor = await db.Expenses.AsNoTracking().Where(e => e.Status == ExpenseStatus.Approved).GroupBy(e => e.ProjectId).Select(g => new { g.Key, Out = g.Sum(x => x.Amount - x.AmountPaid), Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Key, ct);
        var unprocessed = await db.AccountingPostingRequests.AsNoTracking().CountAsync(r => r.EventDate <= date && (r.Status == AccountingPostingStatus.Pending || r.Status == AccountingPostingStatus.Failed), ct);
        var rows = new List<ProjectReconciliationRowDto>();
        foreach (var p in projects.Select(p => (long?)p.Id).Append(null))
        {
            var lines = gl.Where(x => x.ProjectId == p).ToList();
            var code = p is { } id ? projects.First(x => x.Id == id).ProjectCode : "(company level)";
            var cl = p is { } a ? ledger.GetValueOrDefault(a) : 0m;
            var cc = lines.Where(x => customerIds.Contains(x.AccountId)).Sum(x => x.D - x.C);
            var vo = p is { } b && vendor.TryGetValue(b, out var v) ? v.Out : 0m;
            var vc = lines.Where(x => vendorIds.Contains(x.AccountId)).Sum(x => x.C - x.D);
            var me = p is { } c && vendor.TryGetValue(c, out var v2) ? v2.Total : 0m;
            var le = lines.Where(x => expenseIds.Contains(x.AccountId)).Sum(x => x.D - x.C);
            var d = lines.Sum(x => x.D); var cr = lines.Sum(x => x.C);
            if (d == 0m && cr == 0m && cl == 0m && vo == 0m && me == 0m) continue;
            rows.Add(new ProjectReconciliationRowDto(p, code, d, cr, cl, cc, vo, vc, me, le, d == cr && cl == cc && vo == vc));
        }
        var sumD = rows.Sum(r => r.Debit); var sumC = rows.Sum(r => r.Credit);
        var companyD = gl.Sum(x => x.D); var companyC = gl.Sum(x => x.C);
        return new ProjectReconciliationDto(date, rows, sumD, sumC, companyD, companyC, sumD == companyD && sumC == companyC && companyD == companyC, rows.All(r => r.Reconciled), unprocessed,
            await support.CurrencyAsync(ct));
    }

    // =====================================================================================================
    // dashboard
    // =====================================================================================================
    public async Task<AccountingDashboardDto> DashboardAsync(PostingEngineService engine, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var visible = await support.ScopeJournalsAsync(db.JournalEntries.AsNoTracking(), ct);
        var drafts = await visible.CountAsync(j => j.Status == JournalStatus.Draft || j.Status == JournalStatus.Rejected, ct);
        var pendingApprovals = await visible.CountAsync(j => j.Status == JournalStatus.PendingApproval, ct);
        var pendingRules = await db.PostingRules.AsNoTracking().CountAsync(r => r.Status == PostingRuleStatus.PendingApproval, ct);
        var today = await support.TodayAsync(ct);
        var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.StartDate <= today && p.EndDate >= today, ct);
        var open = await db.AccountingPeriods.AsNoTracking().CountAsync(p => p.Status == AccountingPeriodStatus.Open, ct);
        decimal cashBal = 0m, bankBal = 0m; var unmatched = 0;
        if (await support.HasAllAccessAsync(ct))
        {
            var accounts = await AccountsAsync(ct);
            var cashIds = accounts.Values.Where(a => a.Classification == AccountClassification.Cash).Select(a => a.Id).ToHashSet();
            var bankIds = accounts.Values.Where(a => a.Classification == AccountClassification.Bank).Select(a => a.Id).ToHashSet();
            cashBal = await db.GeneralLedgerEntries.AsNoTracking().Where(e => cashIds.Contains(e.AccountId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
            bankBal = await db.GeneralLedgerEntries.AsNoTracking().Where(e => bankIds.Contains(e.AccountId)).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
            unmatched = await db.BankTransactions.AsNoTracking().CountAsync(t => t.Status == BankTransactionStatus.Unmatched, ct);
        }
        return new AccountingDashboardDto(drafts, pendingApprovals, pendingRules, await engine.SummaryAsync(ct), open, period?.Name, period?.Status, cashBal, bankBal, unmatched, await support.CurrencyAsync(ct));
    }
}
