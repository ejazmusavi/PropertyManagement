using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Expenses;
using RealEstate.Application.Installments;
using RealEstate.Application.Payments;
using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>
/// Shared accounting core: caller, clock, project access, period resolution and the single posting path into the general ledger. Every journal - manual,
/// automatic, reversal, transfer or allocation - is posted through <see cref="PostAsync"/>, which re-validates balance, accounts and the period, issues the voucher
/// number under a row lock on the fiscal year (gapless, no duplicates) and writes one immutable <see cref="GeneralLedgerEntry"/> per line.
/// </summary>
public class AccountingSupport(IAppDbContext db, ICurrentUser user, IPermissionService permissions, ProjectAccessService access, InstallmentSupport installments, TimeProvider clock)
{
    public DateTime Now => clock.GetUtcNow().UtcDateTime;
    public string Actor => user.UserName ?? "system";
    public Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => installments.TodayAsync(ct);
    public Task<string> CurrencyAsync(CancellationToken ct = default) => installments.CurrencyAsync(ct);
    public static RequestValidationException Invalid(string field, string message) => ExpenseSupport.Invalid(field, message);
    public static string Trim(string? v, int max) => ExpenseSupport.Trim(v, max);
    public static string? TrimOrNull(string? v, int max) => ExpenseSupport.TrimOrNull(v, max);
    public static bool IsDuplicate(DbUpdateException ex, string? contains = null) => PaymentSupport.IsDuplicate(ex, contains);

    public Task EnsureAsync(string permission, CancellationToken ct = default) => permissions.EnsureAsync(user, permission, ct);
    public async Task<bool> HasAsync(string permission, CancellationToken ct = default) => user.UserId is { } id && await permissions.HasPermissionAsync(id, permission, ct);

    // =====================================================================================================
    // project access
    // =====================================================================================================
    public Task<bool> HasAllAccessAsync(CancellationToken ct = default) => access.HasAllAccessAsync(ct);

    /// <summary>Company-wide accounting data (no project filter, company-level lines) needs access to all projects; a project filter needs that project.</summary>
    public async Task EnsureScopeAsync(long? projectId, CancellationToken ct = default)
    {
        if (projectId is { } p) { await access.EnsureAccessAsync(p, ct); return; }
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Company-wide accounting data needs access to all projects; choose one of your projects.");
    }

    public async Task<IReadOnlyList<ProjectOptionDto>> ProjectOptionsAsync(CancellationToken ct = default) =>
        await (await access.ScopeAsync(db.Projects.AsNoTracking(), ct)).OrderBy(p => p.ProjectCode).Select(p => new ProjectOptionDto(p.Id, p.ProjectCode, p.ProjectName)).ToListAsync(ct);

    /// <summary>Journals visible to the caller: all for all-project users, otherwise only journals whose every line is on one of the caller's projects.</summary>
    public async Task<IQueryable<JournalEntry>> ScopeJournalsAsync(IQueryable<JournalEntry> q, CancellationToken ct = default)
    {
        if (await access.HasAllAccessAsync(ct)) return q;
        var id = RequireUser();
        var mine = db.UserProjects.Where(up => up.UserId == id && up.IsActive).Select(up => up.ProjectId);
        return q.Where(j => db.JournalEntryLines.Any(l => l.JournalEntryId == j.Id)
            && !db.JournalEntryLines.Any(l => l.JournalEntryId == j.Id && (l.ProjectId == null || !mine.Contains(l.ProjectId.Value))));
    }

    public async Task EnsureJournalAccessAsync(long journalId, CancellationToken ct = default)
    {
        var q = await ScopeJournalsAsync(db.JournalEntries.AsNoTracking().Where(j => j.Id == journalId), ct);
        if (await q.AnyAsync(ct)) return;
        if (await access.HasAllAccessAsync(ct)) throw new NotFoundException("Journal not found.");
        throw new ForbiddenException("You do not have access to this journal.");
    }

    // =====================================================================================================
    // periods
    // =====================================================================================================
    public async Task<(FiscalYear Year, AccountingPeriod Period)> PeriodForAsync(DateOnly date, CancellationToken ct = default)
    {
        var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date, ct)
            ?? throw new DomainException($"No accounting period covers {date:yyyy-MM-dd}. Create the fiscal year first.");
        var year = await db.FiscalYears.AsNoTracking().FirstAsync(y => y.Id == period.FiscalYearId, ct);
        return (year, period);
    }

    // =====================================================================================================
    // posting
    // =====================================================================================================
    public sealed record LineSpec(long AccountId, long? ProjectId, long? CostCenterId, decimal Debit, decimal Credit, string? Description, string? Reference);
    public sealed record PostingCheck(FiscalYear Year, AccountingPeriod Period, string? ClosedPeriodReason);

    /// <summary>
    /// Validates a journal for posting without changing anything: balance, accounts (exist, active, no sub-accounts, manual entries on control accounts need
    /// accounting.journals.control), cost centers, projects, and the period (locked or closed year: rejected; closed period: only with accounting.periods.override
    /// and a reason).
    /// </summary>
    public async Task<PostingCheck> CheckAsync(DateOnly date, IReadOnlyList<LineSpec> lines, JournalSource source, bool postToClosedPeriod, string? closedPeriodReason, CancellationToken ct = default)
    {
        var errors = JournalBalancer.Validate(lines.Select(l => (l.Debit, l.Credit)).ToList());
        if (errors.Count > 0) throw new DomainException(string.Join(" ", errors));

        var accountIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var parents = (await db.ChartOfAccounts.AsNoTracking().Where(a => a.ParentAccountId != null && accountIds.Contains(a.ParentAccountId.Value)).Select(a => a.ParentAccountId!.Value).ToListAsync(ct)).ToHashSet();
        foreach (var id in accountIds)
        {
            if (!accounts.TryGetValue(id, out var a)) throw new DomainException($"Account #{id} was not found.");
            if (!a.IsActive) throw new DomainException($"Account {a.Code} {a.Name} is inactive.");
            if (parents.Contains(id)) throw new DomainException($"Account {a.Code} {a.Name} has sub-accounts; post to a sub-account.");
            if (!a.AllowManualEntries && source is JournalSource.Manual && !await HasAsync(Permissions.AccountingJournalsControl, ct))
                throw new DomainException($"Account {a.Code} {a.Name} is a control account; manual journals to it need the control-account permission.");
        }
        var ccIds = lines.Where(l => l.CostCenterId is not null).Select(l => l.CostCenterId!.Value).Distinct().ToList();
        if (ccIds.Count > 0)
        {
            var centers = await db.CostCenters.AsNoTracking().Where(c => ccIds.Contains(c.Id)).ToListAsync(ct);
            foreach (var id in ccIds)
            {
                var c = centers.FirstOrDefault(x => x.Id == id) ?? throw new DomainException($"Cost center #{id} was not found.");
                if (!c.IsActive) throw new DomainException($"Cost center {c.Code} is inactive.");
                if (c.ProjectId is { } cp && lines.Where(l => l.CostCenterId == id).Any(l => l.ProjectId != cp)) throw new DomainException($"Cost center {c.Code} belongs to another project.");
            }
        }
        var projectIds = lines.Where(l => l.ProjectId is not null).Select(l => l.ProjectId!.Value).Distinct().ToList();
        if (projectIds.Count > 0 && await db.Projects.CountAsync(p => projectIds.Contains(p.Id), ct) != projectIds.Count) throw new DomainException("A journal line refers to an unknown project.");

        var (year, period) = await PeriodForAsync(date, ct);
        string? reason = null;
        switch (PeriodCalendar.CanPost(year.Status, period.Status))
        {
            case PeriodCalendar.PostingDecision.Rejected:
                throw new DomainException(year.Status == FiscalYearStatus.Closed
                    ? $"Fiscal year {year.Name} is closed; no postings are allowed."
                    : $"Accounting period {period.Name} is locked; no postings are allowed.");
            case PeriodCalendar.PostingDecision.RequiresOverride:
                if (!postToClosedPeriod) throw new DomainException($"Accounting period {period.Name} is closed. Only an authorized user can post into it, with a reason.");
                if (!await HasAsync(Permissions.AccountingPeriodsOverride, ct)) throw new ForbiddenException($"Accounting period {period.Name} is closed and you are not authorized to post into it.");
                reason = TrimOrNull(closedPeriodReason, 500);
                if (reason is null || reason.Length < 5) throw Invalid("ClosedPeriodReason", "Give a reason (at least 5 characters) for posting into a closed period.");
                break;
        }
        return new PostingCheck(year, period, reason);
    }

    /// <summary>
    /// Posts a journal inside the caller's transaction: locks the fiscal year row (voucher sequence), issues the voucher, marks the journal Posted and writes the
    /// general ledger. The journal may be new (automatic) or tracked (manual approval); its lines must already be set.
    /// </summary>
    public async Task PostAsync(JournalEntry j, PostingCheck check, VoucherType type, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Posting requires an open transaction.");
        await db.LockForUpdateAsync<FiscalYear>(check.Year.Id, ct);
        // Period actions lock the same row, so the status read here is final for this transaction.
        var yearStatus = await db.FiscalYears.AsNoTracking().Where(y => y.Id == check.Year.Id).Select(y => y.Status).FirstAsync(ct);
        var periodStatus = await db.AccountingPeriods.AsNoTracking().Where(p => p.Id == check.Period.Id).Select(p => p.Status).FirstAsync(ct);
        var decision = PeriodCalendar.CanPost(yearStatus, periodStatus);
        if (decision == PeriodCalendar.PostingDecision.Rejected || (decision == PeriodCalendar.PostingDecision.RequiresOverride && check.ClosedPeriodReason is null))
            throw new DomainException($"Accounting period {check.Period.Name} was closed or locked while posting; no posting was made.");
        var last =await db.AccountingVouchers.Where(v => v.VoucherType == type && v.FiscalYearId == check.Year.Id).MaxAsync(v => (int?)v.SequenceNo, ct) ?? 0;
        var number = PeriodCalendar.VoucherNumber(type, check.Year.StartDate, check.Year.EndDate, last + 1);
        var now = Now;
        j.Status = JournalStatus.Posted;
        j.FiscalYearId = check.Year.Id;
        j.PeriodId = check.Period.Id;
        j.TotalDebit = j.Lines.Sum(l => l.Debit);
        j.TotalCredit = j.Lines.Sum(l => l.Credit);
        j.PostedAtUtc = now;
        j.VoucherNumber = number;
        j.ClosedPeriodReason = check.ClosedPeriodReason;
        if (db.Entry(j).State == EntityState.Detached) db.JournalEntries.Add(j);
        if (j.EntryNumber.StartsWith("NEW-", StringComparison.Ordinal))
        {
            await db.SaveWithConcurrencyAsync(ct);
            j.EntryNumber = $"JE-{j.Id:D6}";
        }
        await db.SaveWithConcurrencyAsync(ct);
        db.AccountingVouchers.Add(new AccountingVoucher { VoucherType = type, FiscalYearId = check.Year.Id, SequenceNo = last + 1, VoucherNumber = number, JournalEntryId = j.Id, IssuedAtUtc = now, IssuedBy = Actor });
        foreach (var l in j.Lines.OrderBy(l => l.LineNo))
            db.GeneralLedgerEntries.Add(new GeneralLedgerEntry
            {
                JournalEntryId = j.Id, JournalEntryLineId = l.Id, AccountId = l.AccountId, ProjectId = l.ProjectId, CostCenterId = l.CostCenterId, EntryDate = j.EntryDate,
                FiscalYearId = check.Year.Id, PeriodId = check.Period.Id, Debit = l.Debit, Credit = l.Credit, VoucherNumber = number, Source = j.Source,
                Narration = Trim(l.Description ?? j.Narration, 500), Reference = l.Reference ?? j.Reference, SourceType = j.SourceType, SourceId = j.SourceId, PostedAtUtc = now,
            });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw new ConcurrencyException(); }
    }

    public static List<JournalEntryLine> ToLines(IEnumerable<LineSpec> specs) =>
        specs.Select((l, i) => new JournalEntryLine
        {
            LineNo = i + 1, AccountId = l.AccountId, ProjectId = l.ProjectId, CostCenterId = l.CostCenterId, Debit = l.Debit, Credit = l.Credit,
            Description = TrimOrNull(l.Description, 500), Reference = TrimOrNull(l.Reference, 100),
        }).ToList();

    public static IReadOnlyList<LineSpec> Specs(IEnumerable<JournalEntryLine> lines) =>
        lines.OrderBy(l => l.LineNo).Select(l => new LineSpec(l.AccountId, l.ProjectId, l.CostCenterId, l.Debit, l.Credit, l.Description, l.Reference)).ToList();

    /// <summary>The mirror image of a posted journal: debits become credits and vice versa.</summary>
    public static IReadOnlyList<LineSpec> Reverse(IEnumerable<JournalEntryLine> lines) =>
        lines.OrderBy(l => l.LineNo).Select(l => new LineSpec(l.AccountId, l.ProjectId, l.CostCenterId, l.Credit, l.Debit, l.Description, l.Reference)).ToList();

    /// <summary>The company's fiscal year start month (company settings), used to suggest new fiscal years.</summary>
    public async Task<int> FiscalStartMonthAsync(CancellationToken ct = default) =>
        await db.Companies.AsNoTracking().Select(c => (int?)c.FiscalYearStartMonth).FirstOrDefaultAsync(ct) ?? 7;

    /// <summary>Converts a UTC instant to the company's local date (company time zone).</summary>
    public async Task<DateOnly> LocalDateAsync(DateTime utc, CancellationToken ct = default)
    {
        var tzId = await db.Companies.AsNoTracking().Select(c => c.TimeZoneId).FirstOrDefaultAsync(ct);
        TimeZoneInfo tz;
        try { tz = string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.Utc; }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));
    }
}
