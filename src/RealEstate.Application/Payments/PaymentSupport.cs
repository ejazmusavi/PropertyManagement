using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Installments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Payments;

namespace RealEstate.Application.Payments;

/// <summary>
/// Shared money-movement core for payments, refunds and ledgers.
/// Concurrency model: every operation that moves money on an agreement runs in one database transaction that starts with <see cref="LockAgreementAsync"/>
/// (SELECT ... FOR UPDATE on the agreement row, then PaymentRevision + 1). Concurrent money operations on the same agreement therefore run one after the other
/// and always see each other's committed results; a Phase 5 amendment approval that read the schedule earlier fails on the agreement row version instead of
/// overwriting payment progress. Ledger and outbox rows for one-off events carry unique keys, so a retried operation cannot post twice.
/// </summary>
public class PaymentSupport(IAppDbContext db, ICurrentUser user, InstallmentSupport installments, ProjectAccessService access, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DateTime Now => clock.GetUtcNow().UtcDateTime;
    public string Actor => user.UserName ?? "system";
    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => installments.TodayAsync(ct);
    public Task<string> CurrencySymbolAsync(CancellationToken ct = default) => installments.CurrencyAsync(ct);
    public async Task<string> CurrencyCodeAsync(CancellationToken ct = default) => await db.Companies.AsNoTracking().Select(c => c.CurrencyCode).FirstOrDefaultAsync(ct) ?? "PKR";
    public Task<long> EnsureAgreementAccessAsync(long agreementId, CancellationToken ct = default) => installments.EnsureAgreementAccessAsync(agreementId, ct);

    // =====================================================================================================
    // agreement context and locking
    // =====================================================================================================
    public sealed record AgreementInfo(long AgreementId, string AgreementNumber, AgreementStatus Status, long BookingId, string BookingNumber, long ProjectId, string ProjectCode, long HolderCustomerId);

    public async Task<AgreementInfo> InfoAsync(long agreementId, CancellationToken ct = default)
    {
        var x = await (from a in db.CustomerAgreements.AsNoTracking()
                       join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                       join p in db.Projects.AsNoTracking() on b.ProjectId equals p.Id
                       where a.Id == agreementId
                       select new { a.Id, a.AgreementNumber, a.Status, b.BookingNumber, BookingId = b.Id, b.ProjectId, p.ProjectCode }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Agreement not found.");
        return new AgreementInfo(x.Id, x.AgreementNumber, x.Status, x.BookingId, x.BookingNumber, x.ProjectId, x.ProjectCode, await HolderAsync(x.BookingId, ct));
    }

    /// <summary>The account holder: the primary active party of the booking (falls back to the largest active share).</summary>
    public async Task<long> HolderAsync(long bookingId, CancellationToken ct = default) =>
        await db.BookingCustomers.AsNoTracking().Where(c => c.BookingId == bookingId && c.IsActive)
            .OrderByDescending(c => c.IsPrimary).ThenByDescending(c => c.SharePercent).ThenBy(c => c.Id).Select(c => (long?)c.CustomerId).FirstOrDefaultAsync(ct)
        ?? await db.BookingCustomers.AsNoTracking().Where(c => c.BookingId == bookingId).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Id).Select(c => c.CustomerId).FirstAsync(ct);

    /// <summary>
    /// Must be the first statement of the caller's transaction. Takes a row lock on the agreement (waits for other money operations on it) and increments
    /// PaymentRevision so that any concurrent writer holding an older copy of the agreement row fails its row-version check.
    /// </summary>
    public async Task<CustomerAgreement> LockAgreementAsync(long agreementId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("LockAgreementAsync requires an open transaction.");
        await db.LockForUpdateAsync<CustomerAgreement>(agreementId, ct);
        var ag = await db.CustomerAgreements.FirstOrDefaultAsync(a => a.Id == agreementId, ct) ?? throw new NotFoundException("Agreement not found.");
        // A copy tracked before the lock may be stale: refresh it from the locked row.
        await db.Entry(ag).ReloadAsync(ct);
        ag.PaymentRevision++;
        await db.SaveWithConcurrencyAsync(ct);
        return ag;
    }

    public static bool IsDeadlock(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("Deadlock found", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsDuplicate(DbUpdateException ex, string? contains = null)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) && (contains is null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    // =====================================================================================================
    // schedule lines
    // =====================================================================================================
    /// <summary>Lines of the agreement's active schedule (tracked when they will be updated). Empty when no schedule is active (none yet, or cancelled).</summary>
    public async Task<List<PaymentScheduleItem>> ActiveItemsAsync(long agreementId, bool tracked, CancellationToken ct = default)
    {
        var q = from i in db.PaymentScheduleItems
                join s in db.PaymentSchedules on i.ScheduleId equals s.Id
                where s.ActiveAgreementId == agreementId
                select i;
        if (!tracked) q = q.AsNoTracking();
        return await q.OrderBy(i => i.DueDate).ThenBy(i => i.LineNo).ToListAsync(ct);
    }

    /// <summary>Root (first version) of every schedule line of the agreement.</summary>
    public async Task<Dictionary<long, long>> RootsAsync(long agreementId, CancellationToken ct = default)
    {
        var all = await (from i in db.PaymentScheduleItems.AsNoTracking()
                         join s in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals s.Id
                         where s.AgreementId == agreementId
                         select new { i.Id, i.PreviousItemId }).ToListAsync(ct);
        return ReceivableEngine.Roots(all.ToDictionary(x => x.Id, x => x.PreviousItemId));
    }

    public static OpenLineDto Open(PaymentScheduleItem i, DateOnly today) =>
        new(i.Id, i.LineNo, i.AgreementChargeId, i.Description, i.DueDate, i.Amount, i.AmountPaid, i.AmountWaived, i.Outstanding, i.Outstanding > 0m && i.DueDate < today);

    public async Task<List<ChargeOptionDto>> ChargeOptionsAsync(long agreementId, IReadOnlyList<PaymentScheduleItem> items, CancellationToken ct = default)
    {
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        return charges.Select(c => new ChargeOptionDto(c.Id, c.Description, c.Category, items.Where(i => i.AgreementChargeId == c.Id).Sum(i => i.Outstanding)))
            .Where(c => c.Outstanding > 0m).ToList();
    }

    // =====================================================================================================
    // allocation
    // =====================================================================================================
    public sealed record AllocationInstruction(AllocationMode Mode, long? ChargeId, IReadOnlyList<AllocationLineInput>? Lines, bool RemainderToAdvance);

    /// <summary>Active (not reversed/released) allocations of a payment by kind.</summary>
    public async Task<(decimal Installment, decimal Advance, decimal Refund)> AllocatedAsync(long paymentId, CancellationToken ct = default)
    {
        var rows = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == paymentId && a.Status == AllocationStatus.Active)
            .GroupBy(a => a.Kind).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(ct);
        return (rows.Where(r => r.Key == AllocationKind.Installment).Sum(r => r.Sum), rows.Where(r => r.Key == AllocationKind.Advance).Sum(r => r.Sum),
            rows.Where(r => r.Key == AllocationKind.Refund).Sum(r => r.Sum));
    }

    /// <summary>
    /// Plans and stores an allocation of <paramref name="amount"/> from a posted payment inside the caller's locked transaction.
    /// Schedule lines are updated through <see cref="PaymentScheduleItem.RecordPayment"/>, which refuses to exceed a line's outstanding balance.
    /// </summary>
    public async Task<AllocationPlan> AllocateAsync(Payment payment, decimal amount, AllocationInstruction ins, CancellationToken ct = default)
    {
        var items = await ActiveItemsAsync(payment.AgreementId, tracked: true, ct);
        var mode = ins.Mode;
        if (items.Count == 0 && mode is not (AllocationMode.Advance or AllocationMode.Unallocated))
            mode = ins.RemainderToAdvance ? AllocationMode.Advance : AllocationMode.Unallocated; // no schedule yet: the money stays on account
        var plan = AllocationEngine.Plan(items.Select(i => new AllocItem(i.Id, i.AgreementChargeId, i.LineNo, i.DueDate, i.Outstanding)).ToList(), amount, mode,
            ins.Lines?.Select(l => new AllocLine(l.ItemId, l.Amount)).ToList(), ins.ChargeId, ins.RemainderToAdvance);
        var now = Now;
        foreach (var line in plan.Lines)
        {
            var item = items.First(i => i.Id == line.ItemId);
            item.RecordPayment(line.Amount, payment.PaymentDate);
            db.PaymentAllocations.Add(new PaymentAllocation
            {
                PaymentId = payment.Id, AgreementId = payment.AgreementId, Kind = AllocationKind.Installment, Mode = mode, ScheduleItemId = item.Id, AgreementChargeId = item.AgreementChargeId,
                Amount = line.Amount, AllocatedAtUtc = now, AllocatedBy = Actor,
            });
        }
        if (plan.Advance > 0m)
            db.PaymentAllocations.Add(new PaymentAllocation
            {
                PaymentId = payment.Id, AgreementId = payment.AgreementId, Kind = AllocationKind.Advance, Mode = mode, Amount = plan.Advance, AllocatedAtUtc = now, AllocatedBy = Actor,
            });
        await db.SaveWithConcurrencyAsync(ct);
        return plan;
    }

    /// <summary>The active-schedule line that continues <paramref name="itemId"/> (same version chain), or null when no schedule is active.</summary>
    public static PaymentScheduleItem? Continuation(long itemId, IReadOnlyList<PaymentScheduleItem> activeItems, IReadOnlyDictionary<long, long> roots)
    {
        var direct = activeItems.FirstOrDefault(i => i.Id == itemId);
        if (direct is not null) return direct;
        if (!roots.TryGetValue(itemId, out var root)) return null;
        return activeItems.FirstOrDefault(i => roots.TryGetValue(i.Id, out var r) && r == root);
    }

    // =====================================================================================================
    // ledgers
    // =====================================================================================================
    /// <summary>Adds an agreement ledger entry and its mirror on the holder's customer ledger. Call <see cref="SaveLedgerAsync"/> afterwards.</summary>
    public AgreementLedgerEntry Post(AgreementInfo ag, LedgerEntryType type, decimal debit, decimal credit, DateOnly date, string description,
        long? paymentId = null, long? refundId = null, long? rootId = null, long? itemId = null, string? uniqueKey = null)
    {
        if (debit < 0m || credit < 0m || (debit == 0m) == (credit == 0m)) throw new DomainException("A ledger entry needs exactly one positive side.");
        var entry = new AgreementLedgerEntry
        {
            AgreementId = ag.AgreementId, ProjectId = ag.ProjectId, CustomerId = ag.HolderCustomerId, EntryDate = date, PostedAtUtc = Now, PostedBy = Actor, EntryType = type,
            Debit = debit, Credit = credit, Description = Trim(description, 300), PaymentId = paymentId, RefundId = refundId, ScheduleItemRootId = rootId, ScheduleItemId = itemId,
            UniqueKey = uniqueKey,
        };
        db.AgreementLedgerEntries.Add(entry);
        pendingMirrors.Add(entry);
        return entry;
    }

    private readonly List<AgreementLedgerEntry> pendingMirrors = [];

    /// <summary>Saves pending agreement entries, then their customer-ledger mirrors (which reference them).</summary>
    public async Task SaveLedgerAsync(CancellationToken ct = default)
    {
        if (pendingMirrors.Count == 0) { await db.SaveWithConcurrencyAsync(ct); return; }
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw new ConcurrencyException(); }
        foreach (var e in pendingMirrors)
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                CustomerId = e.CustomerId, AgreementId = e.AgreementId, ProjectId = e.ProjectId, AgreementLedgerEntryId = e.Id, EntryDate = e.EntryDate, PostedAtUtc = e.PostedAtUtc,
                PostedBy = e.PostedBy, EntryType = e.EntryType, Debit = e.Debit, Credit = e.Credit, Description = e.Description, PaymentId = e.PaymentId, RefundId = e.RefundId,
            });
        pendingMirrors.Clear();
        await db.SaveWithConcurrencyAsync(ct);
    }

    /// <summary>
    /// Brings the ledgers up to date inside the caller's locked transaction: moves the agreement balance to the current account holder after a transfer, and posts
    /// receivables for installments that have fallen due (or reverses receivables that are no longer due, e.g. after a deferment or a cancellation).
    /// </summary>
    public async Task SyncLedgerAsync(AgreementInfo ag, DateOnly today, CancellationToken ct = default)
    {
        await TransferBalanceAsync(ag, today, ct);
        var deltas = await ReceivableDeltasAsync(ag, today, ct);
        foreach (var d in deltas)
        {
            if (d.DueDelta > 0m)
                Post(ag, d.FirstPosting ? LedgerEntryType.InstallmentDue : LedgerEntryType.DueAdjustment, d.DueDelta, 0m, d.EntryDate,
                    d.FirstPosting ? $"Installment due: {d.Description}" : $"Due adjustment: {d.Description}", rootId: d.RootId, itemId: d.ItemId);
            else if (d.DueDelta < 0m)
                Post(ag, LedgerEntryType.DueAdjustment, 0m, -d.DueDelta, d.EntryDate,
                    ag.Status == AgreementStatus.Active ? $"Receivable reversed (no longer due): {d.Description}" : $"Receivable reversed (agreement cancelled): {d.Description}",
                    rootId: d.RootId, itemId: d.ItemId);
            if (d.WaiverDelta > 0m)
                Post(ag, LedgerEntryType.Waiver, 0m, d.WaiverDelta, d.EntryDate, $"Waiver: {d.Description}", rootId: d.RootId, itemId: d.ItemId);
            else if (d.WaiverDelta < 0m)
                Post(ag, LedgerEntryType.WaiverAdjustment, -d.WaiverDelta, 0m, d.EntryDate, $"Waiver adjustment: {d.Description}", rootId: d.RootId, itemId: d.ItemId);
        }
        await SaveLedgerAsync(ct);
    }

    public async Task<IReadOnlyList<ReceivableDelta>> ReceivableDeltasAsync(AgreementInfo ag, DateOnly today, CancellationToken ct = default)
    {
        var active = await ActiveItemsAsync(ag.AgreementId, tracked: false, ct);
        var roots = active.Count == 0 ? new Dictionary<long, long>() : await RootsAsync(ag.AgreementId, ct);
        var posted = await db.AgreementLedgerEntries.AsNoTracking().Where(e => e.AgreementId == ag.AgreementId && e.ScheduleItemRootId != null)
            .GroupBy(e => e.ScheduleItemRootId!.Value).Select(g => new
            {
                Root = g.Key,
                Due = g.Where(e => e.EntryType == LedgerEntryType.InstallmentDue || e.EntryType == LedgerEntryType.DueAdjustment).Sum(e => e.Debit - e.Credit),
                Waived = g.Where(e => e.EntryType == LedgerEntryType.Waiver || e.EntryType == LedgerEntryType.WaiverAdjustment).Sum(e => e.Credit - e.Debit),
            }).ToListAsync(ct);
        var lines = active.Select(i => new DueLine(roots.GetValueOrDefault(i.Id, i.Id), i.Id, i.DueDate, i.Amount, i.AmountWaived, i.Description));
        return ReceivableEngine.Compute(lines, posted.ToDictionary(p => p.Root, p => (p.Due, p.Waived)), today, ag.Status == AgreementStatus.Active);
    }

    private async Task TransferBalanceAsync(AgreementInfo ag, DateOnly today, CancellationToken ct)
    {
        var balances = await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.AgreementId == ag.AgreementId)
            .GroupBy(e => e.CustomerId).Select(g => new { CustomerId = g.Key, Balance = g.Sum(e => e.Debit - e.Credit), Count = g.Count() }).ToListAsync(ct);
        foreach (var b in balances.Where(b => b.CustomerId != ag.HolderCustomerId && b.Balance != 0m))
        {
            var amount = Math.Abs(b.Balance);
            var owed = b.Balance > 0m;
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                CustomerId = b.CustomerId, AgreementId = ag.AgreementId, ProjectId = ag.ProjectId, EntryDate = today, PostedAtUtc = Now, PostedBy = Actor, EntryType = LedgerEntryType.BalanceTransferOut,
                Debit = owed ? 0m : amount, Credit = owed ? amount : 0m, Description = $"Agreement {ag.AgreementNumber} transferred to another account holder",
            });
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                CustomerId = ag.HolderCustomerId, AgreementId = ag.AgreementId, ProjectId = ag.ProjectId, EntryDate = today, PostedAtUtc = Now, PostedBy = Actor, EntryType = LedgerEntryType.BalanceTransferIn,
                Debit = owed ? amount : 0m, Credit = owed ? 0m : amount, Description = $"Agreement {ag.AgreementNumber} balance transferred in",
            });
        }
        await db.SaveWithConcurrencyAsync(ct);
    }

    /// <summary>
    /// Read paths call this before showing balances: when receivables or a holder transfer are pending it posts them in a short locked transaction
    /// (otherwise nothing is written).
    /// </summary>
    public async Task EnsureLedgerCurrentAsync(long agreementId, CancellationToken ct = default)
    {
        var ag = await InfoAsync(agreementId, ct);
        var today = await TodayAsync(ct);
        var needsTransfer = await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.AgreementId == agreementId && e.CustomerId != ag.HolderCustomerId)
            .GroupBy(e => e.CustomerId).Select(g => new { g.Key, Balance = g.Sum(e => e.Debit - e.Credit) }).ToListAsync(ct);
        if (!needsTransfer.Any(x => x.Balance != 0m) && (await ReceivableDeltasAsync(ag, today, ct)).Count == 0) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAgreementAsync(agreementId, ct);
        await SyncLedgerAsync(await InfoAsync(agreementId, ct), today, ct);
        await tx.CommitAsync(ct);
    }

    // =====================================================================================================
    // accounting outbox
    // =====================================================================================================
    public void Outbox(AccountingEventType type, string sourceType, long sourceId, AgreementInfo ag, decimal amount, DateOnly date, object payload) =>
        db.AccountingPostingRequests.Add(new AccountingPostingRequest
        {
            EventType = type, SourceType = sourceType, SourceId = sourceId, ProjectId = ag.ProjectId, AgreementId = ag.AgreementId, Amount = amount, EventDate = date,
            PayloadJson = JsonSerializer.Serialize(payload, Json), Status = AccountingPostingStatus.Pending, CreatedAtUtc = Now,
        });

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    public static string Trim(string v, int max) => v.Length > max ? v[..max] : v;

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string? SerializeLines(IReadOnlyList<AllocationLineInput>? lines) => lines is { Count: > 0 } ? JsonSerializer.Serialize(lines, Json) : null;
    public static IReadOnlyList<AllocationLineInput> ParseLines(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<AllocationLineInput>>(json, Json) ?? [];

    /// <summary>Restricts a payment-related query to agreements in projects the caller can access.</summary>
    public Task<IQueryable<Project>> ProjectsAsync(CancellationToken ct = default) => access.ScopeAsync(db.Projects.AsNoTracking(), ct);
}
