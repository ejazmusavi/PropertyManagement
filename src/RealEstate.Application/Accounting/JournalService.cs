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
/// Journal entry engine (spec 8.3): manual journals (draft, submit, maker-checker approval which posts, rejection, cancellation), reversal of posted journals,
/// fund transfers between bank/cash accounts (built from the approved transfer posting rule) and project cost allocations. Posted journals are never edited or
/// deleted.
/// </summary>
public class JournalService(IAppDbContext db, AccountingSupport support, IAuditService audit,
    IValidator<CreateJournalRequest> createValidator, IValidator<UpdateJournalRequest> updateValidator, IValidator<RejectJournalRequest> rejectValidator,
    IValidator<ReverseJournalRequest> reverseValidator, IValidator<CreateTransferRequest> transferValidator, IValidator<CreateCostAllocationRequest> allocationValidator)
{
    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<JournalListItemDto>> ListAsync(JournalListRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var q = await support.ScopeJournalsAsync(db.JournalEntries.AsNoTracking(), ct);
        if (r.Status is { } s) q = q.Where(j => j.Status == s);
        if (r.Source is { } src) q = q.Where(j => j.Source == src);
        if (r.ProjectId is { } p) { await support.EnsureScopeAsync(p, ct); q = q.Where(j => db.JournalEntryLines.Any(l => l.JournalEntryId == j.Id && l.ProjectId == p)); }
        if (r.AccountId is { } a) q = q.Where(j => db.JournalEntryLines.Any(l => l.JournalEntryId == j.Id && l.AccountId == a));
        if (r.From is { } f) q = q.Where(j => j.EntryDate >= f);
        if (r.To is { } t) q = q.Where(j => j.EntryDate <= t);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var term = r.Search.Trim();
            q = q.Where(j => j.EntryNumber.Contains(term) || (j.VoucherNumber != null && j.VoucherNumber.Contains(term)) || j.Narration.Contains(term) || (j.Reference != null && j.Reference.Contains(term)));
        }
        q = (r.SortBy?.ToLowerInvariant()) switch
        {
            "amount" => r.Descending ? q.OrderByDescending(j => j.TotalDebit) : q.OrderBy(j => j.TotalDebit),
            "number" => r.Descending ? q.OrderByDescending(j => j.Id) : q.OrderBy(j => j.Id),
            _ => r.Descending || r.SortBy is null ? q.OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.Id) : q.OrderBy(j => j.EntryDate).ThenBy(j => j.Id),
        };
        var total = await q.CountAsync(ct);
        var items = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(j => new JournalListItemDto(j.Id, j.EntryNumber, j.VoucherNumber, j.EntryDate, j.Source, j.Status, j.Narration, j.Reference,
                j.Status == JournalStatus.Posted ? j.TotalDebit : db.JournalEntryLines.Where(l => l.JournalEntryId == j.Id).Sum(l => l.Debit),
                j.Status == JournalStatus.Posted ? j.TotalCredit : db.JournalEntryLines.Where(l => l.JournalEntryId == j.Id).Sum(l => l.Credit),
                j.CreatedBy, j.SubmittedBy, j.SourceType, j.SourceId, j.ReversedByJournalId != null))
            .ToListAsync(ct);
        return new PagedResult<JournalListItemDto>(items, r.Page, r.PageSize, total);
    }

    public async Task<JournalDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    internal async Task<JournalDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var j = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Journal not found.");
        var accountIds = j.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var projectIds = j.Lines.Where(l => l.ProjectId != null).Select(l => l.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        var ccIds = j.Lines.Where(l => l.CostCenterId != null).Select(l => l.CostCenterId!.Value).Distinct().ToList();
        var centers = await db.CostCenters.AsNoTracking().Where(c => ccIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        string? Number(long? jid) => jid is { } x ? db.JournalEntries.AsNoTracking().Where(e => e.Id == x).Select(e => e.VoucherNumber ?? e.EntryNumber).FirstOrDefault() : null;
        var ruleName = j.PostingRuleId is { } rid ? await db.PostingRules.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Name).FirstOrDefaultAsync(ct) : null;
        var period = j.PeriodId is { } pid ? await db.AccountingPeriods.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var lines = j.Lines.OrderBy(l => l.LineNo).Select(l => new JournalLineDto(l.Id, l.LineNo, l.AccountId, accounts[l.AccountId].Code, accounts[l.AccountId].Name,
            l.ProjectId, l.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, l.CostCenterId, l.CostCenterId is { } c ? centers.GetValueOrDefault(c) : null,
            l.Debit, l.Credit, l.Description, l.Reference)).ToList();
        return new JournalDetailDto(j.Id, j.EntryNumber, j.VoucherNumber, j.EntryDate, j.Source, j.Status, j.Narration, j.Reference, j.SourceType, j.SourceId, j.PostingRequestId,
            j.PostingRuleId, ruleName, j.ReversalOfJournalId, Number(j.ReversalOfJournalId), j.ReversedByJournalId, Number(j.ReversedByJournalId), j.ReversalReason,
            lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), period, j.CreatedBy, j.CreatedAtUtc, j.SubmittedBy, j.SubmittedAtUtc, j.ApprovedBy, j.ApprovedAtUtc, j.ApprovalComment,
            j.PostedAtUtc, j.RejectedBy, j.RejectionComment, j.CancelledBy, j.CancelReason, j.ClosedPeriodReason, lines, await support.CurrencyAsync(ct), j.RowVersion.ToToken());
    }

    // =====================================================================================================
    // manual journals
    // =====================================================================================================
    public async Task<JournalDetailDto> CreateAsync(CreateJournalRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var key = AccountingSupport.TrimOrNull(r.IdempotencyKey, 100);
        if (key is not null && await db.JournalEntries.AsNoTracking().Where(j => j.IdempotencyKey == key).Select(j => new { j.Id, j.CreatedBy }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (!string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase)) throw AccountingSupport.Invalid("IdempotencyKey", "This idempotency key was used by another user.");
            return await BuildAsync(existing.Id, ct);
        }
        var specs = Specs(r.Lines);
        await CheckDraftAsync(r.EntryDate, specs, ct);
        var j = new JournalEntry
        {
            EntryNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], IdempotencyKey = key, Source = JournalSource.Manual, Status = JournalStatus.Draft, EntryDate = r.EntryDate,
            Narration = AccountingSupport.Trim(r.Narration, 500), Reference = AccountingSupport.TrimOrNull(r.Reference, 100), Lines = AccountingSupport.ToLines(specs),
        };
        if (r.Submit) { j.Status = JournalStatus.PendingApproval; j.SubmittedByUserId = userId; j.SubmittedBy = support.Actor; j.SubmittedAtUtc = support.Now; }
        db.JournalEntries.Add(j);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("IdempotencyKey", "This journal was already submitted."); }
        j.EntryNumber = $"JE-{j.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Created", "JournalEntry", j.Id.ToString(), NewValues: new { j.EntryNumber, j.EntryDate, j.Narration, j.Status, Total = specs.Sum(s => s.Debit), Lines = specs.Count }), ct);
        return await BuildAsync(j.Id, ct);
    }

    public async Task<JournalDetailDto> UpdateAsync(long id, UpdateJournalRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(r, ct);
        var j = await db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(j, r.RowVersion);
        if (j.Source != JournalSource.Manual) throw new DomainException("Only manual journals can be edited; correct transfers and allocations by cancelling them.");
        if (!JournalEntry.CanEdit(j.Status)) throw new DomainException($"A {j.Status.ToString().ToLowerInvariant()} journal cannot be edited.");
        var specs = Specs(r.Lines);
        await CheckDraftAsync(r.EntryDate, specs, ct);
        var old = new { j.EntryDate, j.Narration, j.Status, Total = j.Lines.Sum(l => l.Debit), Lines = j.Lines.Count };
        db.JournalEntryLines.RemoveRange(j.Lines); // draft lines only: never posted
        j.Lines = AccountingSupport.ToLines(specs);
        j.EntryDate = r.EntryDate; j.Narration = AccountingSupport.Trim(r.Narration, 500); j.Reference = AccountingSupport.TrimOrNull(r.Reference, 100);
        j.Status = JournalStatus.Draft; j.RejectedBy = null; j.RejectedAtUtc = null; j.RejectionComment = null;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Updated", "JournalEntry", id.ToString(), old, new { j.EntryDate, j.Narration, j.Status, Total = specs.Sum(s => s.Debit), Lines = specs.Count }), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<JournalDetailDto> SubmitAsync(long id, SubmitJournalRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        var j = await db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(j, r.RowVersion);
        if (!JournalEntry.CanEdit(j.Status)) throw new DomainException($"A {j.Status.ToString().ToLowerInvariant()} journal cannot be submitted.");
        await CheckDraftAsync(j.EntryDate, AccountingSupport.Specs(j.Lines), ct);
        j.Status = JournalStatus.PendingApproval; j.SubmittedByUserId = userId; j.SubmittedBy = support.Actor; j.SubmittedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Submitted", "JournalEntry", id.ToString(), new { Status = "Draft" }, new { Status = "PendingApproval" }), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>
    /// Maker-checker approval: a different user than the creator and submitter approves, which posts the journal (voucher, general ledger). The journal row is
    /// locked first so concurrent approvals post once.
    /// </summary>
    public async Task<JournalDetailDto> ApproveAsync(long id, ApproveJournalRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsApprove, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<JournalEntry>(id, ct);
        var j = await db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(j, r.RowVersion);
        if (j.Status == JournalStatus.Posted) throw new DomainException("The journal is already posted.");
        if (j.Status != JournalStatus.PendingApproval) throw new DomainException("Only journals pending approval can be approved.");
        if (j.SubmittedByUserId == userId || string.Equals(j.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("A journal must be approved by a different user than the one who created or submitted it.");
        var check = await support.CheckAsync(j.EntryDate, AccountingSupport.Specs(j.Lines), j.Source == JournalSource.Manual ? JournalSource.Manual : j.Source, r.PostToClosedPeriod, r.ClosedPeriodReason, ct);
        j.ApprovedByUserId = userId; j.ApprovedBy = support.Actor; j.ApprovedAtUtc = support.Now; j.ApprovalComment = AccountingSupport.TrimOrNull(r.Comments, 500);
        await support.PostAsync(j, check, VoucherFor(j.Source), ct);
        if (j.Source == JournalSource.CostAllocation)
        {
            var alloc = await db.ProjectCostAllocations.FirstAsync(a => a.JournalEntryId == id, ct);
            alloc.Status = CostAllocationStatus.Posted;
            await db.SaveWithConcurrencyAsync(ct);
        }
        await audit.RecordAsync(new AuditEntry("Journal.Posted", "JournalEntry", id.ToString(), new { Status = "PendingApproval" },
            new { Status = "Posted", j.VoucherNumber, j.TotalDebit, check.ClosedPeriodReason }, j.ApprovalComment), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    public async Task<JournalDetailDto> RejectAsync(long id, RejectJournalRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsApprove, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var j = await db.JournalEntries.FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(j, r.RowVersion);
        if (j.Status != JournalStatus.PendingApproval) throw new DomainException("Only journals pending approval can be rejected.");
        if (j.SubmittedByUserId == userId) throw new DomainException("You cannot reject your own journal; edit or cancel it instead.");
        j.Status = JournalStatus.Rejected; j.RejectedBy = support.Actor; j.RejectedAtUtc = support.Now; j.RejectionComment = r.Comments.Trim();
        if (j.Source == JournalSource.CostAllocation)
            (await db.ProjectCostAllocations.FirstAsync(a => a.JournalEntryId == id, ct)).Status = CostAllocationStatus.Rejected;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Rejected", "JournalEntry", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Rejected" }, j.RejectionComment), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<JournalDetailDto> CancelAsync(long id, CancelJournalRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw AccountingSupport.Invalid("Reason", "Give a reason of at least 5 characters.");
        var j = await db.JournalEntries.FirstAsync(x => x.Id == id, ct);
        db.UseRowVersion(j, r.RowVersion);
        if (j.Status is not (JournalStatus.Draft or JournalStatus.Rejected or JournalStatus.PendingApproval)) throw new DomainException("Only unposted journals can be cancelled; reverse a posted journal instead.");
        var from = j.Status;
        j.Status = JournalStatus.Cancelled; j.CancelledBy = support.Actor; j.CancelledAtUtc = support.Now; j.CancelReason = r.Reason.Trim();
        if (j.Source == JournalSource.CostAllocation)
            (await db.ProjectCostAllocations.FirstAsync(a => a.JournalEntryId == id, ct)).Status = CostAllocationStatus.Cancelled;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Cancelled", "JournalEntry", id.ToString(), new { Status = from.ToString() }, new { Status = "Cancelled" }, j.CancelReason), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>
    /// Reverses a posted manual, transfer or allocation journal with a new RV journal (debits and credits swapped) dated on the reversal date. Automatic journals are
    /// reversed by reversing their business transaction (payment, expense, fee), which posts the reversal automatically.
    /// </summary>
    public async Task<JournalDetailDto> ReverseAsync(long id, ReverseJournalRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsReverse, ct);
        await support.EnsureJournalAccessAsync(id, ct);
        await reverseValidator.ValidateOrThrowAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<JournalEntry>(id, ct);
        var original = await db.JournalEntries.Include(x => x.Lines).FirstAsync(x => x.Id == id, ct);
        if (original.Status != JournalStatus.Posted) throw new DomainException("Only posted journals can be reversed.");
        if (original.ReversedByJournalId is not null) throw new DomainException("The journal has already been reversed.");
        if (original.Source == JournalSource.Reversal) throw new DomainException("A reversal journal cannot be reversed; post a new journal instead.");
        if (original.Source == JournalSource.Automatic)
            throw new DomainException("Automatic journals are reversed by reversing their source transaction (payment, refund, expense or fee), or corrected with a manual journal.");
        var date = r.ReversalDate ?? await support.TodayAsync(ct);
        if (date < original.EntryDate) throw AccountingSupport.Invalid("ReversalDate", "The reversal date cannot be before the original journal date.");
        var reversal = await ReverseCoreAsync(original, date, r.Reason.Trim(), null, r.PostToClosedPeriod, r.ClosedPeriodReason, ct);
        reversal.ApprovedByUserId = userId; reversal.ApprovedBy = support.Actor; reversal.ApprovedAtUtc = support.Now;
        if (original.Source == JournalSource.CostAllocation)
            (await db.ProjectCostAllocations.FirstAsync(a => a.JournalEntryId == id, ct)).Status = CostAllocationStatus.Cancelled;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.Reversed", "JournalEntry", id.ToString(), new { Status = "Posted" },
            new { ReversedBy = reversal.VoucherNumber, ReversalDate = date }, r.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(reversal.Id, ct);
    }

    /// <summary>Creates and posts the mirror journal of <paramref name="original"/> (caller holds the transaction and the original's row lock).</summary>
    internal async Task<JournalEntry> ReverseCoreAsync(JournalEntry original, DateOnly date, string reason, long? postingRequestId, bool postToClosed, string? closedReason, CancellationToken ct)
    {
        var specs = AccountingSupport.Reverse(original.Lines);
        var check = await support.CheckAsync(date, specs, JournalSource.Reversal, postToClosed, closedReason, ct);
        var reversal = new JournalEntry
        {
            EntryNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], Source = JournalSource.Reversal, EntryDate = date,
            Narration = AccountingSupport.Trim($"Reversal of {original.VoucherNumber}: {reason}", 500), Reference = original.VoucherNumber,
            SourceType = original.SourceType, SourceId = original.SourceId, PostingRequestId = postingRequestId, ReversalOfJournalId = original.Id, ReversalReason = reason,
            Lines = AccountingSupport.ToLines(specs),
        };
        await support.PostAsync(reversal, check, VoucherType.RV, ct);
        original.ReversedByJournalId = reversal.Id; original.ReversalReason = reason;
        await db.SaveWithConcurrencyAsync(ct);
        return reversal;
    }

    private static VoucherType VoucherFor(JournalSource s) => s switch
    {
        JournalSource.Automatic => VoucherType.AJ, JournalSource.Reversal => VoucherType.RV, JournalSource.Transfer => VoucherType.TR, JournalSource.CostAllocation => VoucherType.CA, _ => VoucherType.JV,
    };

    private static IReadOnlyList<AccountingSupport.LineSpec> Specs(IReadOnlyList<JournalLineInput> lines) =>
        lines.Select(l => new AccountingSupport.LineSpec(l.AccountId, l.ProjectId, l.CostCenterId, l.Debit, l.Credit, l.Description, l.Reference)).ToList();

    /// <summary>Draft checks: balance, accounts, cost centers and the caller's access to every line's project (company-level lines need all-project access).</summary>
    private async Task CheckDraftAsync(DateOnly date, IReadOnlyList<AccountingSupport.LineSpec> specs, CancellationToken ct)
    {
        var errors = JournalBalancer.Validate(specs.Select(s => (s.Debit, s.Credit)).ToList());
        if (errors.Count > 0) throw AccountingSupport.Invalid("Lines", string.Join(" ", errors));
        foreach (var p in specs.Select(s => s.ProjectId).Distinct()) await support.EnsureScopeAsync(p, ct);
        var ids = specs.Select(s => s.AccountId).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != ids.Count) throw AccountingSupport.Invalid("Lines", "A line refers to an unknown account.");
        if (accounts.FirstOrDefault(a => !a.IsActive) is { } inactive) throw AccountingSupport.Invalid("Lines", $"Account {inactive.Code} is inactive.");
        if (await db.ChartOfAccounts.AnyAsync(a => a.ParentAccountId != null && ids.Contains(a.ParentAccountId.Value), ct)) throw AccountingSupport.Invalid("Lines", "Post to sub-accounts, not to parent accounts.");
        if (accounts.FirstOrDefault(a => !a.AllowManualEntries) is { } control && !await support.HasAsync(Permissions.AccountingJournalsControl, ct))
            throw AccountingSupport.Invalid("Lines", $"Account {control.Code} is a control account; manual journals to it need the control-account permission.");
        _ = await support.PeriodForAsync(date, ct); // a period must exist (status is checked at posting)
    }

    // =====================================================================================================
    // fund transfers (spec 8.4 bank transfers / cash transfers)
    // =====================================================================================================
    /// <summary>
    /// Builds a transfer journal between two bank/cash accounts from the active posting rule for BankTransfer (any bank side) or CashTransfer (cash to cash), using
    /// the event accounts FromAccount/ToAccount. The journal then follows the normal maker-checker approval.
    /// </summary>
    public async Task<JournalDetailDto> CreateTransferAsync(CreateTransferRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await transferValidator.ValidateOrThrowAsync(r, ct);
        await support.EnsureScopeAsync(r.ProjectId, ct);
        var key = AccountingSupport.TrimOrNull(r.IdempotencyKey, 100);
        if (key is not null && await db.JournalEntries.AsNoTracking().Where(j => j.IdempotencyKey == key).Select(j => (long?)j.Id).FirstOrDefaultAsync(ct) is { } done) return await BuildAsync(done, ct);
        var from = await MoneyAccountAsync(r.FromAccountId, ct);
        var to = await MoneyAccountAsync(r.ToAccountId, ct);
        var type = from.IsBank || to.IsBank ? AccountingEventType.BankTransfer : AccountingEventType.CashTransfer;
        var rules = await db.PostingRules.AsNoTracking().Include(x => x.Lines).Where(x => x.EventType == type && x.Status == PostingRuleStatus.Active).ToListAsync(ct);
        var payload = new Dictionary<string, string?>
        {
            ["Amount"] = r.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), ["FromAccount"] = from.GlAccountId.ToString(), ["ToAccount"] = to.GlAccountId.ToString(),
        };
        PostingRule rule;
        IReadOnlyList<ResolvedLine> lines;
        try { rule = PostingRuleEngine.SelectRule(rules, type, r.ProjectId, r.TransferDate, payload); lines = PostingRuleEngine.BuildLines(rule, payload); }
        catch (DomainException ex) { throw new DomainException(ex.Message + " Configure and approve a posting rule for " + PostingCatalog.Get(type).Label.ToLowerInvariant() + "s first."); }
        var specs = lines.Select(l => new AccountingSupport.LineSpec(l.AccountId, r.ProjectId, l.CostCenterId, l.Side == EntrySide.Debit ? l.Amount : 0m, l.Side == EntrySide.Credit ? l.Amount : 0m,
            l.Description ?? $"{from.Name} to {to.Name}", r.Reference)).ToList();
        await CheckDraftAsync(r.TransferDate, specs, ct);
        var j = new JournalEntry
        {
            EntryNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], IdempotencyKey = key, Source = JournalSource.Transfer, EntryDate = r.TransferDate,
            Narration = AccountingSupport.Trim(r.Narration, 500), Reference = AccountingSupport.TrimOrNull(r.Reference, 100), PostingRuleId = rule.Id,
            Status = r.Submit ? JournalStatus.PendingApproval : JournalStatus.Draft, Lines = AccountingSupport.ToLines(specs),
        };
        if (r.Submit) { j.SubmittedByUserId = userId; j.SubmittedBy = support.Actor; j.SubmittedAtUtc = support.Now; }
        db.JournalEntries.Add(j);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("IdempotencyKey", "This transfer was already submitted."); }
        j.EntryNumber = $"JE-{j.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Journal.TransferCreated", "JournalEntry", j.Id.ToString(), NewValues: new { j.EntryNumber, From = from.Name, To = to.Name, r.Amount, r.TransferDate, Rule = rule.Name }), ct);
        return await BuildAsync(j.Id, ct);
    }

    private sealed record MoneyAccount(long GlAccountId, string Name, bool IsBank);

    private async Task<MoneyAccount> MoneyAccountAsync(long glAccountId, CancellationToken ct)
    {
        if (await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.GlAccountId == glAccountId, ct) is { } b)
            return b.IsActive ? new MoneyAccount(b.GlAccountId, b.Name, true) : throw AccountingSupport.Invalid("FromAccountId", $"Bank account {b.Name} is inactive.");
        if (await db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(c => c.GlAccountId == glAccountId, ct) is { } c)
            return c.IsActive ? new MoneyAccount(c.GlAccountId, c.Name, false) : throw AccountingSupport.Invalid("FromAccountId", $"Cash account {c.Name} is inactive.");
        throw AccountingSupport.Invalid("FromAccountId", "Transfers are made between registered bank and cash accounts.");
    }

    // =====================================================================================================
    // project cost allocation
    // =====================================================================================================
    public async Task<CostAllocationDto> CreateAllocationAsync(CreateCostAllocationRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingJournalsManage, ct);
        await allocationValidator.ValidateOrThrowAsync(r, ct);
        await support.EnsureScopeAsync(r.SourceProjectId, ct);
        foreach (var l in r.Lines) await support.EnsureScopeAsync(l.ProjectId, ct);
        if (r.SourceProjectId is { } sp && r.Lines.Any(l => l.ProjectId == sp)) throw AccountingSupport.Invalid("Lines", "The source project cannot also be a target.");
        var account = await db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AccountId, ct) ?? throw AccountingSupport.Invalid("AccountId", "The account was not found.");
        if (account.Nature != AccountNature.Expense) throw AccountingSupport.Invalid("AccountId", "Costs are allocated on expense accounts.");
        var amounts = AccountingMath.Split(r.Amount, r.Lines.Select(l => l.Percentage).ToList());
        var specs = new List<AccountingSupport.LineSpec>();
        for (var i = 0; i < r.Lines.Count; i++)
            specs.Add(new AccountingSupport.LineSpec(r.AccountId, r.Lines[i].ProjectId, r.Lines[i].CostCenterId, amounts[i], 0m, $"Allocated {r.Lines[i].Percentage:0.####}%", null));
        specs.Add(new AccountingSupport.LineSpec(r.AccountId, r.SourceProjectId, r.SourceCostCenterId, 0m, r.Amount, "Cost allocated out", null));
        await CheckDraftAsync(r.AllocationDate, specs, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var j = new JournalEntry
        {
            EntryNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], Source = JournalSource.CostAllocation, EntryDate = r.AllocationDate, Status = JournalStatus.PendingApproval,
            Narration = AccountingSupport.Trim($"Cost allocation: {r.Basis}", 500), Lines = AccountingSupport.ToLines(specs),
            SubmittedByUserId = userId, SubmittedBy = support.Actor, SubmittedAtUtc = support.Now,
        };
        db.JournalEntries.Add(j);
        await db.SaveChangesAsync(ct);
        j.EntryNumber = $"JE-{j.Id:D6}";
        var alloc = new ProjectCostAllocation
        {
            AllocationNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], AllocationDate = r.AllocationDate, AccountId = r.AccountId, SourceProjectId = r.SourceProjectId,
            SourceCostCenterId = r.SourceCostCenterId, Amount = r.Amount, Basis = r.Basis.Trim(), Status = CostAllocationStatus.PendingApproval, JournalEntryId = j.Id,
            Lines = r.Lines.Select((l, i) => new ProjectCostAllocationLine { ProjectId = l.ProjectId, CostCenterId = l.CostCenterId, Percentage = l.Percentage, Amount = amounts[i] }).ToList(),
        };
        db.ProjectCostAllocations.Add(alloc);
        await db.SaveChangesAsync(ct);
        alloc.AllocationNumber = $"PCA-{alloc.Id:D6}";
        j.Reference = alloc.AllocationNumber; j.SourceType = "ProjectCostAllocation"; j.SourceId = alloc.Id;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("CostAllocation.Created", "ProjectCostAllocation", alloc.Id.ToString(),
            NewValues: new { alloc.AllocationNumber, alloc.Amount, alloc.AccountId, alloc.SourceProjectId, Lines = alloc.Lines.Select(l => new { l.ProjectId, l.Percentage, l.Amount }) }), ct);
        await tx.CommitAsync(ct);
        return await GetAllocationAsync(alloc.Id, ct);
    }

    public async Task<CostAllocationDto> GetAllocationAsync(long id, CancellationToken ct = default) =>
        (await ListAllocationsAsync(id, ct)).FirstOrDefault() ?? throw new NotFoundException("Cost allocation not found.");

    public async Task<IReadOnlyList<CostAllocationDto>> ListAllocationsAsync(long? id = null, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var visible = (await support.ScopeJournalsAsync(db.JournalEntries.AsNoTracking(), ct)).Select(j => j.Id);
        var list = await db.ProjectCostAllocations.AsNoTracking().Include(a => a.Lines).Where(a => (id == null || a.Id == id) && visible.Contains(a.JournalEntryId))
            .OrderByDescending(a => a.Id).Take(200).ToListAsync(ct);
        var projectIds = list.SelectMany(a => a.Lines.Select(l => l.ProjectId).Append(a.SourceProjectId ?? 0)).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        var accountIds = list.Select(a => a.AccountId).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);
        var jIds = list.Select(a => a.JournalEntryId).ToList();
        var journals = await db.JournalEntries.AsNoTracking().Where(j => jIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.VoucherNumber ?? j.EntryNumber, ct);
        return list.Select(a => new CostAllocationDto(a.Id, a.AllocationNumber, a.AllocationDate, a.AccountId, accounts[a.AccountId], a.SourceProjectId,
            a.SourceProjectId is { } s ? projects.GetValueOrDefault(s) : null, a.Amount, a.Basis, a.Status, a.JournalEntryId, journals[a.JournalEntryId], a.CreatedBy,
            a.Lines.Select(l => new CostAllocationLineDto(l.ProjectId, projects.GetValueOrDefault(l.ProjectId) ?? "", l.CostCenterId, l.Percentage, l.Amount)).ToList())).ToList();
    }
}
