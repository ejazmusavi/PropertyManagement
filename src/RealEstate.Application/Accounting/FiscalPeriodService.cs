using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>
/// Fiscal years with monthly accounting periods, and period closing/locking/reopening with an append-only history (spec 8.1 AccountingPeriodLock, acceptance
/// "closed periods cannot receive unauthorized postings"). Status changes lock the fiscal year row, the same lock posting takes, so a period cannot close
/// halfway through a posting.
/// </summary>
public class FiscalPeriodService(IAppDbContext db, AccountingSupport support, IAuditService audit,
    IValidator<CreateFiscalYearRequest> yearValidator, IValidator<PeriodActionRequest> actionValidator)
{
    public async Task<IReadOnlyList<FiscalYearDto>> ListAsync(CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var years = await db.FiscalYears.AsNoTracking().Include(y => y.Periods).OrderByDescending(y => y.StartDate).ToListAsync(ct);
        var posted = await db.JournalEntries.AsNoTracking().Where(j => j.Status == JournalStatus.Posted && j.PeriodId != null)
            .GroupBy(j => j.PeriodId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return years.Select(y => Map(y, posted)).ToList();
    }

    public async Task<FiscalYearDto> GetAsync(long id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(y => y.Id == id) ?? throw new NotFoundException("Fiscal year not found.");

    private static FiscalYearDto Map(FiscalYear y, IReadOnlyDictionary<long, int> posted) =>
        new(y.Id, y.Name, y.StartDate, y.EndDate, y.Status, y.ClosedBy, y.ClosedAtUtc,
            y.Periods.OrderBy(p => p.PeriodNo).Select(p => new AccountingPeriodDto(p.Id, p.FiscalYearId, p.PeriodNo, p.Name, p.StartDate, p.EndDate, p.Status, posted.GetValueOrDefault(p.Id), p.RowVersion.ToToken())).ToList(),
            y.RowVersion.ToToken());

    /// <summary>Creates a fiscal year and its monthly periods. Years may not overlap. The company's fiscal start month is suggested in the UI, not enforced.</summary>
    public async Task<FiscalYearDto> CreateAsync(CreateFiscalYearRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingPeriodsManage, ct);
        await yearValidator.ValidateOrThrowAsync(r, ct);
        var periods = PeriodCalendar.MonthlyPeriods(r.StartDate, r.EndDate);
        if (await db.FiscalYears.AnyAsync(y => y.StartDate <= r.EndDate && y.EndDate >= r.StartDate, ct)) throw new DomainException("The fiscal year overlaps an existing fiscal year.");
        var year = new FiscalYear { Name = r.Name.Trim(), StartDate = r.StartDate, EndDate = r.EndDate, Status = FiscalYearStatus.Open };
        foreach (var p in periods) year.Periods.Add(new AccountingPeriod { PeriodNo = p.No, Name = p.Name, StartDate = p.Start, EndDate = p.End, Status = AccountingPeriodStatus.Open });
        db.FiscalYears.Add(year);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Name", "A fiscal year with this name or start date already exists."); }
        await audit.RecordAsync(new AuditEntry("FiscalYear.Created", "FiscalYear", year.Id.ToString(), NewValues: new { year.Name, year.StartDate, year.EndDate, Periods = periods.Count }), ct);
        return await GetAsync(year.Id, ct);
    }

    /// <summary>Close (only override holders may still post), lock (no postings) or reopen a period, with a reason; every change is kept in the lock history.</summary>
    public async Task<AccountingPeriodDto> ChangePeriodAsync(long periodId, PeriodActionRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingPeriodsManage, ct);
        await actionValidator.ValidateOrThrowAsync(r, ct);
        var yearId = await db.AccountingPeriods.AsNoTracking().Where(p => p.Id == periodId).Select(p => (long?)p.FiscalYearId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Accounting period not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<FiscalYear>(yearId, ct);
        var year = await db.FiscalYears.AsNoTracking().FirstAsync(y => y.Id == yearId, ct);
        if (year.Status == FiscalYearStatus.Closed) throw new DomainException($"Fiscal year {year.Name} is closed; its periods cannot change.");
        var p = await db.AccountingPeriods.FirstAsync(x => x.Id == periodId, ct);
        db.UseRowVersion(p, r.RowVersion);
        var from = p.Status;
        var to = PeriodCalendar.Apply(from, r.Action);
        if (to != AccountingPeriodStatus.Open && await db.JournalEntries.AnyAsync(j => j.PeriodId == null && j.EntryDate >= p.StartDate && j.EntryDate <= p.EndDate && j.Status == JournalStatus.PendingApproval, ct)
            && r.Action == PeriodLockAction.Lock)
            throw new DomainException("Journals dated in this period are waiting for approval; approve, reject or cancel them before locking.");
        p.Status = to;
        db.AccountingPeriodLocks.Add(new AccountingPeriodLock { PeriodId = p.Id, Action = r.Action, FromStatus = from, ToStatus = to, Reason = r.Reason.Trim(), ByUserId = userId, By = support.Actor, AtUtc = support.Now });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry($"AccountingPeriod.{r.Action}", "AccountingPeriod", p.Id.ToString(), new { Status = from.ToString() }, new { Status = to.ToString(), p.Name }, r.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(yearId, ct)).Periods.First(x => x.Id == periodId);
    }

    /// <summary>
    /// Closes a fiscal year for good: every period must be closed or locked, no journal may wait for approval and no automatic posting event dated in the year
    /// may be pending or failed. Afterwards nothing can be posted into the year. Profit is carried in the statements as retained earnings (no closing entry).
    /// </summary>
    public async Task<FiscalYearDto> CloseYearAsync(long id, CloseFiscalYearRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingPeriodsManage, ct);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw AccountingSupport.Invalid("Reason", "Give a reason of at least 5 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.FiscalYears.AnyAsync(y => y.Id == id, ct)) throw new NotFoundException("Fiscal year not found.");
        await db.LockForUpdateAsync<FiscalYear>(id, ct);
        var y = await db.FiscalYears.Include(x => x.Periods).FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(y, r.RowVersion);
        if (y.Status == FiscalYearStatus.Closed) throw new DomainException("The fiscal year is already closed.");
        if (y.Periods.Any(p => p.Status == AccountingPeriodStatus.Open)) throw new DomainException("Close or lock every period of the year first.");
        if (await db.JournalEntries.AnyAsync(j => j.EntryDate >= y.StartDate && j.EntryDate <= y.EndDate && (j.Status == JournalStatus.PendingApproval || j.Status == JournalStatus.Draft), ct))
            throw new DomainException("Draft or pending journals are dated in this year; post, reject or cancel them first.");
        if (await db.AccountingPostingRequests.AnyAsync(q => q.EventDate >= y.StartDate && q.EventDate <= y.EndDate && (q.Status == AccountingPostingStatus.Pending || q.Status == AccountingPostingStatus.Failed), ct))
            throw new DomainException("Automatic posting events dated in this year are still pending or failed; post or skip them first.");
        y.Status = FiscalYearStatus.Closed; y.ClosedBy = support.Actor; y.ClosedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("FiscalYear.Closed", "FiscalYear", id.ToString(), new { Status = "Open" }, new { Status = "Closed", y.Name }, r.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<PagedResult<PeriodLockDto>> LockHistoryAsync(long? fiscalYearId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = from l in db.AccountingPeriodLocks.AsNoTracking()
                join p in db.AccountingPeriods.AsNoTracking() on l.PeriodId equals p.Id
                where fiscalYearId == null || p.FiscalYearId == fiscalYearId
                orderby l.AtUtc descending, l.Id descending
                select new PeriodLockDto(l.Id, l.PeriodId, p.Name, l.Action, l.FromStatus, l.ToStatus, l.Reason, l.By, l.AtUtc);
        var total = await q.CountAsync(ct);
        return new PagedResult<PeriodLockDto>(await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct), page, pageSize, total);
    }
}
