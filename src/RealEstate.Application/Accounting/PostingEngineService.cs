using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>
/// Automatic journals (spec 8.3/8.4). Consumes the accounting outbox written by Phases 6 and 7, plus events this module feeds from the agreement ledger
/// (receivables recognized when installments fall due, due reductions, waivers) and from confirmed/cancelled bookings. Each event is posted with the most
/// specific approved posting rule into one balanced AJ journal; reversal events reverse the original event's journal. Events without an applicable rule,
/// or dated in a closed/locked period, are marked Failed with the reason and are retried on the next run; an accountant may mark an event as not posted
/// (Skipped) with a reason when the approved policy says it creates no journal. Each event is processed in its own transaction under a row lock, and a journal
/// is unique per event, so concurrent runs never post twice.
/// </summary>
public class PostingEngineService(IAppDbContext db, AccountingSupport support, JournalService journals, IAuditService audit, ProjectAccessService access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string LedgerSource = "AgreementLedgerEntry";
    public const string BookingSource = "Booking";
    private static readonly string[] ReferenceKeys = ["PaymentNumber", "RefundNumber", "ExpenseNumber", "AssessmentNumber", "CollectionNumber", "BookingNumber", "AgreementNumber"];

    // =====================================================================================================
    // queue
    // =====================================================================================================
    public async Task<PagedResult<PostingRequestDto>> ListAsync(PostingQueueRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = from x in db.AccountingPostingRequests.AsNoTracking()
                join p in projects on x.ProjectId equals p.Id
                select new { x, p.ProjectCode };
        if (r.Status is { } s) q = q.Where(z => z.x.Status == s);
        if (r.EventType is { } e) q = q.Where(z => z.x.EventType == e);
        if (r.ProjectId is { } pid) q = q.Where(z => z.x.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(z => z.x.PayloadJson.Contains(t) || z.x.SourceType.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(z => z.x.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        var jIds = rows.Where(z => z.x.JournalEntryId != null).Select(z => z.x.JournalEntryId!.Value).ToList();
        var numbers = await db.JournalEntries.AsNoTracking().Where(j => jIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.VoucherNumber ?? j.EntryNumber, ct);
        return new PagedResult<PostingRequestDto>(rows.Select(z => Map(z.x, z.ProjectCode, numbers)).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<PostingRequestDto> GetAsync(long id, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var x = await db.AccountingPostingRequests.AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, ct) ?? throw new NotFoundException("Posting event not found.");
        await access.EnsureAccessAsync(x.ProjectId, ct);
        var code = await db.Projects.AsNoTracking().Where(p => p.Id == x.ProjectId).Select(p => p.ProjectCode).FirstAsync(ct);
        var numbers = x.JournalEntryId is { } jid
            ? await db.JournalEntries.AsNoTracking().Where(j => j.Id == jid).ToDictionaryAsync(j => j.Id, j => j.VoucherNumber ?? j.EntryNumber, ct)
            : new Dictionary<long, string>();
        return Map(x, code, numbers);
    }

    private static PostingRequestDto Map(AccountingPostingRequest x, string projectCode, IReadOnlyDictionary<long, string> numbers) =>
        new(x.Id, x.EventType, PostingCatalog.Get(x.EventType).Label, x.SourceType, x.SourceId, x.ProjectId, projectCode, x.AgreementId, x.Amount, x.EventDate, x.Status, x.Attempts,
            x.LastError, x.JournalEntryId, x.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null, x.SkipReason, x.CreatedAtUtc, x.ProcessedAtUtc, x.PayloadJson);

    public async Task<PostingQueueSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var counts = await (from x in db.AccountingPostingRequests.AsNoTracking() join p in projects on x.ProjectId equals p.Id group x by x.Status into g select new { g.Key, N = g.Count() })
            .ToDictionaryAsync(z => z.Key, z => z.N, ct);
        return new PostingQueueSummaryDto(counts.GetValueOrDefault(AccountingPostingStatus.Pending), counts.GetValueOrDefault(AccountingPostingStatus.Failed),
            counts.GetValueOrDefault(AccountingPostingStatus.Posted), counts.GetValueOrDefault(AccountingPostingStatus.Skipped));
    }

    // =====================================================================================================
    // run
    // =====================================================================================================
    /// <summary>
    /// Feeds ledger/booking events, then posts pending events in order (and failed ones too when <paramref name="retryFailed"/> is set), optionally for one
    /// project only. Needs accounting.postings.process and access to all projects.
    /// </summary>
    public async Task<PostingRunResultDto> RunAsync(bool retryFailed = false, long? projectId = null, CancellationToken ct = default)
    {
        await EnsureProcessorAsync(ct);
        var fed = await FeedAsync(ct);
        var ids = await db.AccountingPostingRequests.AsNoTracking()
            .Where(x => x.Status == AccountingPostingStatus.Pending || (retryFailed && x.Status == AccountingPostingStatus.Failed))
            .Where(x => projectId == null || x.ProjectId == projectId)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(5000).ToListAsync(ct);
        var rules = await LoadRulesAsync(ct);
        int posted = 0, failed = 0, skipped = 0;
        foreach (var id in ids)
        {
            switch (await ProcessOneAsync(id, rules, ct))
            {
                case AccountingPostingStatus.Posted: posted++; break;
                case AccountingPostingStatus.Failed: failed++; break;
                case AccountingPostingStatus.Skipped: skipped++; break;
            }
        }
        await audit.RecordAsync(new AuditEntry("Accounting.PostingRun", "AccountingPostingRequest", null, NewValues: new { fed, Processed = ids.Count, posted, failed, skipped }), ct);
        return new PostingRunResultDto(fed, ids.Count, posted, failed, skipped);
    }

    /// <summary>Retries one pending or failed event (after a rule was approved or a period reopened).</summary>
    public async Task<PostingRequestDto> RetryAsync(long id, CancellationToken ct = default)
    {
        await EnsureProcessorAsync(ct);
        if (!await db.AccountingPostingRequests.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Posting event not found.");
        await ProcessOneAsync(id, await LoadRulesAsync(ct), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Marks a pending/failed event as not posted under the approved policy (e.g. sale not recognized on booking confirmation). Final; audited.</summary>
    public async Task<PostingRequestDto> SkipAsync(long id, SkipPostingRequest r, CancellationToken ct = default)
    {
        await EnsureProcessorAsync(ct);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw AccountingSupport.Invalid("Reason", "Give a reason of at least 5 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.AccountingPostingRequests.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Posting event not found.");
        await db.LockForUpdateAsync<AccountingPostingRequest>(id, ct);
        var x = await db.AccountingPostingRequests.FirstAsync(z => z.Id == id, ct);
        if (x.Status is not (AccountingPostingStatus.Pending or AccountingPostingStatus.Failed)) throw new DomainException("Only pending or failed events can be marked as not posted.");
        var from = x.Status;
        x.Status = AccountingPostingStatus.Skipped; x.SkipReason = r.Reason.Trim(); x.ProcessedAtUtc = support.Now; x.ProcessedBy = support.Actor;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Accounting.EventSkipped", "AccountingPostingRequest", id.ToString(), new { Status = from.ToString() }, new { Status = "Skipped", x.EventType }, x.SkipReason), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task EnsureProcessorAsync(CancellationToken ct)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingPostingsProcess, ct);
        if (!await support.HasAllAccessAsync(ct)) throw new ForbiddenException("Automatic posting covers every project and needs access to all projects.");
    }

    private async Task<List<PostingRule>> LoadRulesAsync(CancellationToken ct) =>
        await db.PostingRules.AsNoTracking().Include(r => r.Lines).Where(r => r.Status == PostingRuleStatus.Active).ToListAsync(ct);

    private async Task<AccountingPostingStatus?> ProcessOneAsync(long id, IReadOnlyList<PostingRule> rules, CancellationToken ct)
    {
        string error;
        try { return await TryPostAsync(id, rules, ct); }
        catch (DomainException ex) when (ex is not ForbiddenException) { error = ex.Message; }
        // The posting transaction was rolled back; forget whatever it had added and record the failure in a new transaction.
        db.Entry(new AccountingPostingRequest()).Context.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<AccountingPostingRequest>(id, ct);
        var req = await db.AccountingPostingRequests.FirstAsync(x => x.Id == id, ct);
        if (req.Status is not (AccountingPostingStatus.Pending or AccountingPostingStatus.Failed)) return null;
        req.Attempts++; req.ProcessedAtUtc = support.Now; req.ProcessedBy = support.Actor;
        req.Status = AccountingPostingStatus.Failed; req.LastError = AccountingSupport.Trim(error, 1000);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return req.Status;
    }

    private async Task<AccountingPostingStatus?> TryPostAsync(long id, IReadOnlyList<PostingRule> rules, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<AccountingPostingRequest>(id, ct);
        db.Entry(new AccountingPostingRequest()).Context.ChangeTracker.Clear();
        var req = await db.AccountingPostingRequests.FirstAsync(x => x.Id == id, ct);
        if (req.Status is not (AccountingPostingStatus.Pending or AccountingPostingStatus.Failed)) return null;
        req.Attempts++;
        req.ProcessedAtUtc = support.Now; req.ProcessedBy = support.Actor;
        {
            var def = PostingCatalog.Get(req.EventType);
            if (def.Reverses is { } originalType)
            {
                var original = await db.AccountingPostingRequests.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.EventType == originalType && x.SourceType == req.SourceType && x.SourceId == req.SourceId, ct)
                    ?? throw new DomainException("The original event of this reversal was not found.");
                if (original.Status == AccountingPostingStatus.Skipped)
                {
                    req.Status = AccountingPostingStatus.Skipped; req.SkipReason = "The original event was not posted (marked as not posted), so there is nothing to reverse."; req.LastError = null;
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return req.Status;
                }
                if (original.Status != AccountingPostingStatus.Posted || original.JournalEntryId is null) throw new DomainException("The original event has not been posted yet.");
                await db.LockForUpdateAsync<JournalEntry>(original.JournalEntryId.Value, ct);
                var journal = await db.JournalEntries.Include(j => j.Lines).FirstAsync(j => j.Id == original.JournalEntryId, ct);
                if (journal.ReversedByJournalId is not null) throw new DomainException("The original journal is already reversed.");
                var date = req.EventDate < journal.EntryDate ? journal.EntryDate : req.EventDate;
                var reason = $"{def.Label} ({Reference(Parse(req))})";
                var reversal = await journals.ReverseCoreAsync(journal, date, reason, req.Id, false, null, ct);
                req.JournalEntryId = reversal.Id;
            }
            else
            {
                var payload = Parse(req);
                var rule = PostingRuleEngine.SelectRule(rules, req.EventType, req.ProjectId, req.EventDate, payload);
                var lines = PostingRuleEngine.BuildLines(rule, payload);
                var reference = Reference(payload);
                var specs = lines.Select(l => new AccountingSupport.LineSpec(l.AccountId, req.ProjectId, l.CostCenterId, l.Side == EntrySide.Debit ? l.Amount : 0m,
                    l.Side == EntrySide.Credit ? l.Amount : 0m, l.Description ?? $"{def.Label} {reference}".Trim(), reference)).ToList();
                var check = await support.CheckAsync(req.EventDate, specs, JournalSource.Automatic, false, null, ct);
                var j = new JournalEntry
                {
                    EntryNumber = "NEW-" + Guid.NewGuid().ToString("N")[..24], Source = JournalSource.Automatic, EntryDate = req.EventDate,
                    Narration = AccountingSupport.Trim($"{def.Label} {reference}".Trim(), 500), Reference = AccountingSupport.TrimOrNull(reference, 100),
                    SourceType = req.SourceType, SourceId = req.SourceId, PostingRequestId = req.Id, PostingRuleId = rule.Id,
                    ApprovalComment = AccountingSupport.Trim($"Posted automatically under posting rule '{rule.Name}' approved by {rule.ApprovedBy}.", 500),
                    Lines = AccountingSupport.ToLines(specs),
                };
                await support.PostAsync(j, check, VoucherType.AJ, ct);
                req.JournalEntryId = j.Id;
            }
            req.Status = AccountingPostingStatus.Posted; req.LastError = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return req.Status;
        }
    }

    private static Dictionary<string, string?> Parse(AccountingPostingRequest req)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(req.PayloadJson))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number => p.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => null,
                        _ => p.Value.GetRawText(),
                    };
        }
        d.TryAdd("Amount", req.Amount.ToString(CultureInfo.InvariantCulture));
        d["ProjectId"] = req.ProjectId.ToString(CultureInfo.InvariantCulture);
        return d;
    }

    private static string Reference(IReadOnlyDictionary<string, string?> payload) =>
        ReferenceKeys.Select(k => payload.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // =====================================================================================================
    // feeders
    // =====================================================================================================
    /// <summary>
    /// Queues the events this module derives from existing records (serialized by a row lock on the company row): agreement ledger dues, due reductions and
    /// waivers, and confirmed and cancelled bookings. Payments, refunds, expenses and fees arrive through the Phase 6/7 outbox.
    /// </summary>
    public async Task<int> FeedAsync(CancellationToken ct = default)
    {
        var companyId = await db.Companies.AsNoTracking().Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (companyId is { } cid) await db.LockForUpdateAsync<Company>(cid, ct);
        var now = support.Now;
        var count = 0;

        LedgerEntryType[] types = [LedgerEntryType.InstallmentDue, LedgerEntryType.DueAdjustment, LedgerEntryType.Waiver, LedgerEntryType.WaiverAdjustment];
        var fedLedger = db.AccountingPostingRequests.Where(r => r.SourceType == LedgerSource).Select(r => r.SourceId);
        var entries = await (from e in db.AgreementLedgerEntries.AsNoTracking()
                             where types.Contains(e.EntryType) && !fedLedger.Contains(e.Id)
                             join a in db.CustomerAgreements.AsNoTracking() on e.AgreementId equals a.Id
                             join it in db.PaymentScheduleItems.AsNoTracking() on e.ScheduleItemId equals (long?)it.Id into its
                             from it in its.DefaultIfEmpty()
                             join ch in db.AgreementCharges.AsNoTracking() on it.AgreementChargeId equals ch.Id into chs
                             from ch in chs.DefaultIfEmpty()
                             orderby e.Id
                             select new { e, a.AgreementNumber, Category = (ChargeCategory?)ch.Category }).Take(2000).ToListAsync(ct);
        foreach (var x in entries)
        {
            var debit = x.e.Debit > 0m;
            var type = x.e.EntryType switch
            {
                LedgerEntryType.InstallmentDue or LedgerEntryType.DueAdjustment => debit ? AccountingEventType.ReceivableRecognized : AccountingEventType.ReceivableDerecognized,
                LedgerEntryType.Waiver => debit ? AccountingEventType.CustomerWaiverReversed : AccountingEventType.CustomerWaiver,
                _ => debit ? AccountingEventType.CustomerWaiverReversed : AccountingEventType.CustomerWaiver,
            };
            var amount = debit ? x.e.Debit : x.e.Credit;
            db.AccountingPostingRequests.Add(new AccountingPostingRequest
            {
                EventType = type, SourceType = LedgerSource, SourceId = x.e.Id, ProjectId = x.e.ProjectId, AgreementId = x.e.AgreementId, Amount = amount, EventDate = x.e.EntryDate,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    x.AgreementNumber, LedgerEntryId = x.e.Id, EntryType = x.e.EntryType.ToString(), ChargeCategory = x.Category?.ToString(), Amount = amount, x.e.CustomerId, x.e.Description,
                }, Json),
                Status = AccountingPostingStatus.Pending, CreatedAtUtc = now,
            });
            count++;
        }

        var fedConfirmed = db.AccountingPostingRequests.Where(r => r.SourceType == BookingSource && r.EventType == AccountingEventType.BookingConfirmed).Select(r => r.SourceId);
        var confirmed = await db.Bookings.AsNoTracking().Where(b => b.ConfirmedAtUtc != null && !fedConfirmed.Contains(b.Id)).OrderBy(b => b.Id).Take(2000)
            .Select(b => new { b.Id, b.BookingNumber, b.ProjectId, b.ConfirmedAtUtc, b.TotalPrice, b.TotalExtraDiscount, b.TotalCharges, b.TotalPayable, b.PaymentMode }).ToListAsync(ct);
        foreach (var b in confirmed)
        {
            db.AccountingPostingRequests.Add(new AccountingPostingRequest
            {
                EventType = AccountingEventType.BookingConfirmed, SourceType = BookingSource, SourceId = b.Id, ProjectId = b.ProjectId, Amount = b.TotalPayable,
                EventDate = await support.LocalDateAsync(b.ConfirmedAtUtc!.Value, ct),
                PayloadJson = JsonSerializer.Serialize(new { b.BookingNumber, b.TotalPrice, b.TotalExtraDiscount, b.TotalCharges, b.TotalPayable, PaymentMode = b.PaymentMode.ToString() }, Json),
                Status = AccountingPostingStatus.Pending, CreatedAtUtc = now,
            });
            count++;
        }
        await db.SaveChangesAsync(ct);

        var fedCancelled = db.AccountingPostingRequests.Where(r => r.SourceType == BookingSource && r.EventType == AccountingEventType.BookingCancelled).Select(r => r.SourceId);
        var cancelled = await db.Bookings.AsNoTracking().Where(b => b.ConfirmedAtUtc != null && b.CancelledAtUtc != null && b.Status == BookingStatus.Cancelled && !fedCancelled.Contains(b.Id))
            .OrderBy(b => b.Id).Take(2000).Select(b => new { b.Id, b.BookingNumber, b.ProjectId, b.CancelledAtUtc, b.TotalPayable, b.CancelReason }).ToListAsync(ct);
        foreach (var b in cancelled)
        {
            db.AccountingPostingRequests.Add(new AccountingPostingRequest
            {
                EventType = AccountingEventType.BookingCancelled, SourceType = BookingSource, SourceId = b.Id, ProjectId = b.ProjectId, Amount = b.TotalPayable,
                EventDate = await support.LocalDateAsync(b.CancelledAtUtc!.Value, ct),
                PayloadJson = JsonSerializer.Serialize(new { b.BookingNumber, b.TotalPayable, Reason = b.CancelReason }, Json),
                Status = AccountingPostingStatus.Pending, CreatedAtUtc = now,
            });
            count++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
