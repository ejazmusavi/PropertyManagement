using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Payments;

/// <summary>
/// Payment workflow (spec 6.3): submission and validation -> verification where required -> approval (maker-checker) -> posting, allocation, receipt,
/// ledger update and accounting outbox in one transaction. Unverified customer-submitted proof never becomes a posted payment. Posted payments are
/// corrected only by reversal. Duplicate submissions are stopped by the required idempotency key, and a bank/cheque reference can be live only once.
/// </summary>
public class PaymentService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support, IAuditService audit,
    IValidator<CreatePaymentRequest> createValidator, IValidator<VerifyPaymentRequest> verifyValidator, IValidator<RejectPaymentRequest> rejectValidator,
    IValidator<AllocatePaymentRequest> allocateValidator, IValidator<ReversePaymentRequest> reverseValidator)
{
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    // =====================================================================================================
    // lookups
    // =====================================================================================================
    public async Task<IReadOnlyList<PaymentMethodDto>> MethodsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        return await db.PaymentMethods.AsNoTracking().Where(m => !activeOnly || m.IsActive).OrderBy(m => m.Kind).ThenBy(m => m.Name)
            .Select(m => new PaymentMethodDto(m.Id, m.Code, m.Name, m.Kind, m.RequiresVerification, m.RequiresReference, m.IsActive)).ToListAsync(ct);
    }

    /// <summary>Active agreements in the caller's projects matching an agreement/booking number or customer name/code (max 20).</summary>
    public async Task<IReadOnlyList<AgreementLookupDto>> AgreementLookupAsync(string? term, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from a in db.CustomerAgreements.AsNoTracking()
                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                join p in projects on b.ProjectId equals p.Id
                join bc in db.BookingCustomers.AsNoTracking() on b.Id equals bc.BookingId
                join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                where a.Status == AgreementStatus.Active && bc.IsActive && bc.IsPrimary
                select new { a.Id, a.AgreementNumber, b.BookingNumber, p.ProjectCode, CustomerId = c.Id, c.FullName, c.CustomerCode };
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            q = q.Where(x => x.AgreementNumber.Contains(t) || x.BookingNumber.Contains(t) || x.FullName.Contains(t) || x.CustomerCode.Contains(t));
        }
        return await q.OrderByDescending(x => x.Id).Take(20)
            .Select(x => new AgreementLookupDto(x.Id, x.AgreementNumber, x.BookingNumber, x.ProjectCode, x.CustomerId, x.FullName, x.CustomerCode)).ToListAsync(ct);
    }

    /// <summary>Parties, open lines and balances for the payment collection screen.</summary>
    public async Task<CollectionContextDto> CollectionContextAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        await support.EnsureLedgerCurrentAsync(agreementId, ct);
        var ag = await support.InfoAsync(agreementId, ct);
        var today = await support.TodayAsync(ct);
        var items = await support.ActiveItemsAsync(agreementId, tracked: false, ct);
        var parties = await (from bc in db.BookingCustomers.AsNoTracking()
                             join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                             where bc.BookingId == ag.BookingId && bc.IsActive
                             orderby bc.IsPrimary descending, bc.Id
                             select new PartyDto(c.Id, c.CustomerCode, c.FullName, bc.SharePercent, bc.IsPrimary)).ToListAsync(ct);
        var balance = await db.AgreementLedgerEntries.AsNoTracking().Where(e => e.AgreementId == agreementId).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        return new CollectionContextDto(ag.AgreementId, ag.AgreementNumber, ag.BookingNumber, ag.ProjectId, ag.ProjectCode, ag.Status, await support.CurrencySymbolAsync(ct),
            await support.CurrencyCodeAsync(ct), parties, items.Where(i => i.Outstanding > 0m).Select(i => PaymentSupport.Open(i, today)).ToList(),
            await support.ChargeOptionsAsync(agreementId, items, ct),
            items.Where(i => i.DueDate <= today).Sum(i => i.Outstanding), items.Where(i => i.DueDate < today).Sum(i => i.Outstanding),
            items.Where(i => i.DueDate > today).Sum(i => i.Outstanding), balance < 0m ? -balance : 0m, items.Count > 0);
    }

    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<PaymentListItemDto>> ListAsync(PaymentListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from pay in db.Payments.AsNoTracking()
                join p in projects on pay.ProjectId equals p.Id
                join a in db.CustomerAgreements.AsNoTracking() on pay.AgreementId equals a.Id
                join c in db.Customers.AsNoTracking() on pay.CustomerId equals c.Id
                join m in db.PaymentMethods.AsNoTracking() on pay.PaymentMethodId equals m.Id
                select new { pay, p.ProjectCode, a.AgreementNumber, c.FullName, c.CustomerCode, MethodName = m.Name };
        if (r.ProjectId is { } pid) q = q.Where(x => x.pay.ProjectId == pid);
        if (r.AgreementId is { } aid) q = q.Where(x => x.pay.AgreementId == aid);
        if (r.CustomerId is { } cid) q = q.Where(x => x.pay.CustomerId == cid);
        if (r.Status is { } s) q = q.Where(x => x.pay.Status == s);
        if (r.Method is { } mk) q = q.Where(x => x.pay.MethodKind == mk);
        if (r.From is { } from) q = q.Where(x => x.pay.PaymentDate >= from);
        if (r.To is { } to) q = q.Where(x => x.pay.PaymentDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(x => x.pay.PaymentNumber.Contains(t) || x.AgreementNumber.Contains(t) || x.FullName.Contains(t) || x.CustomerCode.Contains(t) || (x.pay.Reference != null && x.pay.Reference.Contains(t)));
        }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("amount", true) => q.OrderByDescending(x => x.pay.Amount).ThenByDescending(x => x.pay.Id),
            ("amount", false) => q.OrderBy(x => x.pay.Amount).ThenBy(x => x.pay.Id),
            ("date", false) => q.OrderBy(x => x.pay.PaymentDate).ThenBy(x => x.pay.Id),
            ("date", true) => q.OrderByDescending(x => x.pay.PaymentDate).ThenByDescending(x => x.pay.Id),
            _ => q.OrderByDescending(x => x.pay.Id),
        };
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new
        {
            x.pay.Id, x.pay.PaymentNumber, x.ProjectCode, x.pay.AgreementId, x.AgreementNumber, x.pay.CustomerId, x.FullName, x.MethodName, x.pay.MethodKind, x.pay.Source, x.pay.Amount,
            x.pay.PaymentDate, x.pay.Reference, x.pay.Status, x.pay.SubmittedBy, x.pay.SubmittedAtUtc,
            Installment = db.PaymentAllocations.Where(al => al.PaymentId == x.pay.Id && al.Status == AllocationStatus.Active && al.Kind == AllocationKind.Installment).Sum(al => (decimal?)al.Amount) ?? 0m,
            Advance = db.PaymentAllocations.Where(al => al.PaymentId == x.pay.Id && al.Status == AllocationStatus.Active && al.Kind == AllocationKind.Advance).Sum(al => (decimal?)al.Amount) ?? 0m,
            Refund = db.PaymentAllocations.Where(al => al.PaymentId == x.pay.Id && al.Status == AllocationStatus.Active && al.Kind == AllocationKind.Refund).Sum(al => (decimal?)al.Amount) ?? 0m,
            Receipt = db.PaymentReceipts.Where(rc => rc.PaymentId == x.pay.Id).Select(rc => rc.ReceiptNumber).FirstOrDefault(),
        }).ToListAsync(ct);
        return new PagedResult<PaymentListItemDto>(rows.Select(x => new PaymentListItemDto(x.Id, x.PaymentNumber, x.ProjectCode, x.AgreementId, x.AgreementNumber, x.CustomerId, x.FullName,
            x.MethodName, x.MethodKind, x.Source, x.Amount, x.PaymentDate, x.Reference, x.Status, x.Installment, x.Advance,
            x.Status == PaymentStatus.Posted ? x.Amount - x.Installment - x.Advance - x.Refund : 0m, x.Receipt, x.SubmittedBy, x.SubmittedAtUtc)).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<PaymentDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        await EnsureAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    public async Task<AllocationOptionsDto> AllocationOptionsAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        await EnsureAccessAsync(id, ct);
        var p = await db.Payments.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var (inst, adv, refund) = await support.AllocatedAsync(id, ct);
        var today = await support.TodayAsync(ct);
        var items = await support.ActiveItemsAsync(p.AgreementId, tracked: false, ct);
        return new AllocationOptionsDto(p.Id, p.PaymentNumber, p.AgreementId, p.Status == PaymentStatus.Posted ? p.Amount - inst - adv - refund : 0m, p.Status == PaymentStatus.Posted ? adv : 0m,
            await support.CurrencySymbolAsync(ct), p.RowVersion.ToToken(), items.Where(i => i.Outstanding > 0m).Select(i => PaymentSupport.Open(i, today)).ToList(),
            await support.ChargeOptionsAsync(p.AgreementId, items, ct));
    }

    // =====================================================================================================
    // 1-2. submission and validation
    // =====================================================================================================
    public async Task<PaymentDetailDto> CreateAsync(CreatePaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsCollect, ct);
        var userId = RequireUser();
        await createValidator.ValidateOrThrowAsync(r, ct);
        var key = r.IdempotencyKey.Trim();
        var hash = PaymentSupport.Sha256(string.Join('|', r.AgreementId, r.CustomerId, r.PaymentMethodId, r.Source, r.Amount.ToString("F2", CultureInfo.InvariantCulture),
            r.PaymentDate.ToString("O", CultureInfo.InvariantCulture), N(r.Reference)?.ToUpperInvariant(), N(r.BankName), r.InstrumentDate?.ToString("O", CultureInfo.InvariantCulture),
            r.AllocationMode, r.ChargeId, r.RemainderToAdvance, string.Join(';', (r.Lines ?? []).Select(l => $"{l.ItemId}:{l.Amount.ToString("F2", CultureInfo.InvariantCulture)}"))));
        if (await ReplayAsync(key, hash, userId, ct) is { } replay) return replay;

        await support.EnsureAgreementAccessAsync(r.AgreementId, ct);
        var ag = await support.InfoAsync(r.AgreementId, ct);
        if (ag.Status != AgreementStatus.Active) throw new DomainException("Payments can be recorded only against active agreements.");
        var method = await db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.PaymentMethodId && m.IsActive, ct)
            ?? throw Invalid(nameof(r.PaymentMethodId), "The payment method was not found or is inactive.");
        var reference = N(r.Reference);
        if (method.RequiresReference && reference is null) throw Invalid(nameof(r.Reference), $"A reference is required for {method.Name} payments.");
        if (method.Kind == PaymentMethodKind.Cheque)
        {
            if (N(r.BankName) is null) throw Invalid(nameof(r.BankName), "Enter the bank of the cheque.");
            if (r.InstrumentDate is null) throw Invalid(nameof(r.InstrumentDate), "Enter the cheque date.");
        }
        var today = await support.TodayAsync(ct);
        if (r.PaymentDate > today) throw Invalid(nameof(r.PaymentDate), "The payment date cannot be in the future.");
        var booking = await db.Bookings.AsNoTracking().Where(b => b.Id == ag.BookingId).Select(b => new { b.BookingDate }).FirstAsync(ct);
        if (r.PaymentDate < booking.BookingDate) throw Invalid(nameof(r.PaymentDate), "The payment date cannot be before the booking date.");
        // The payer is never trusted from the client: it must be a current party of this booking.
        var customerId = r.CustomerId ?? ag.HolderCustomerId;
        if (!await db.BookingCustomers.AnyAsync(c => c.BookingId == ag.BookingId && c.CustomerId == customerId && c.IsActive, ct))
            throw Invalid(nameof(r.CustomerId), "The customer is not a party to this agreement.");
        if (r.AllocationMode == AllocationMode.ChargeSpecific && !await db.AgreementCharges.AnyAsync(c => c.Id == r.ChargeId && c.AgreementId == r.AgreementId && c.Status == AgreementChargeStatus.Active, ct))
            throw Invalid(nameof(r.ChargeId), "The charge does not belong to this agreement.");
        if (r.Lines is { Count: > 0 })
        {
            var open = (await support.ActiveItemsAsync(r.AgreementId, tracked: false, ct)).ToDictionary(i => i.Id);
            foreach (var l in r.Lines)
                if (!open.TryGetValue(l.ItemId, out var item)) throw Invalid("Lines", "An installment does not belong to the active schedule of this agreement.");
                else if (l.Amount > item.Outstanding) throw Invalid("Lines", $"Line {item.LineNo}: the amount exceeds the outstanding balance ({item.Outstanding:N2}).");
        }

        var verify = method.RequiresVerification || r.Source == PaymentSource.CustomerSubmitted;
        var now = support.Now;
        var payment = new Payment
        {
            PaymentNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, RequestHash = hash, ProjectId = ag.ProjectId, AgreementId = ag.AgreementId, BookingId = ag.BookingId,
            CustomerId = customerId, PaymentMethodId = method.Id, MethodKind = method.Kind, Source = r.Source, Amount = r.Amount, CurrencyCode = await support.CurrencyCodeAsync(ct),
            PaymentDate = r.PaymentDate, Reference = reference, BankName = N(r.BankName), InstrumentDate = r.InstrumentDate, PayerName = N(r.PayerName), Remarks = N(r.Remarks),
            ActiveReferenceKey = reference is not null && method.Kind != PaymentMethodKind.Cash ? $"{method.Id}:{new string(reference.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()}" : null,
            RequestedAllocationMode = r.AllocationMode, RequestedChargeId = r.AllocationMode == AllocationMode.ChargeSpecific ? r.ChargeId : null,
            RequestedAllocationJson = r.AllocationMode is AllocationMode.Manual or AllocationMode.SpecificInstallment ? PaymentSupport.SerializeLines(r.Lines) : null,
            RequestedRemainderToAdvance = r.RemainderToAdvance, Status = verify ? PaymentStatus.PendingVerification : PaymentStatus.PendingApproval, VerificationRequired = verify,
            SubmittedByUserId = userId, SubmittedBy = support.Actor, SubmittedAtUtc = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Payments.Add(payment);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex) || PaymentSupport.IsDeadlock(ex))
        {
            await tx.RollbackAsync(ct);
            db.Entry(payment).State = EntityState.Detached;
            // A racing identical submission (same key) won: return it. MySQL may report the race as a deadlock instead of a duplicate.
            if (await ReplayAsync(key, hash, userId, ct) is { } raced) return raced;
            if (PaymentSupport.IsDeadlock(ex)) throw new ConcurrencyException();
            var existing = await db.Payments.AsNoTracking().Where(p => p.ActiveReferenceKey == payment.ActiveReferenceKey).Select(p => p.PaymentNumber).FirstOrDefaultAsync(ct);
            throw Invalid(nameof(r.Reference), existing is null ? "This payment was already submitted." : $"A payment with this reference is already recorded ({existing}).");
        }
        payment.PaymentNumber = $"PAY-{payment.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Payment.Submitted", "Payment", payment.Id.ToString(), NewValues: new
        {
            payment.PaymentNumber, ag.AgreementNumber, payment.Amount, Method = method.Name, payment.Source, payment.PaymentDate, payment.Reference, payment.Status, payment.RequestedAllocationMode,
        }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(payment.Id, ct);
    }

    private async Task<PaymentDetailDto?> ReplayAsync(string key, string hash, Guid userId, CancellationToken ct)
    {
        var existing = await db.Payments.AsNoTracking().Where(p => p.IdempotencyKey == key).Select(p => new { p.Id, p.RequestHash, p.SubmittedByUserId }).FirstOrDefaultAsync(ct);
        if (existing is null) return null;
        if (existing.SubmittedByUserId != userId || existing.RequestHash != hash)
            throw Invalid("IdempotencyKey", "This idempotency key was already used for a different payment.");
        await EnsureAccessAsync(existing.Id, ct);
        return await BuildAsync(existing.Id, ct);
    }

    // =====================================================================================================
    // 3. verification
    // =====================================================================================================
    public async Task<PaymentDetailDto> VerifyAsync(long id, VerifyPaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsVerify, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await verifyValidator.ValidateOrThrowAsync(r, ct);
        var p = await db.Payments.FirstAsync(x => x.Id == id, ct);
        if (p.Status != PaymentStatus.PendingVerification) throw new DomainException($"The payment is {p.Status}; only payments pending verification can be verified.");
        if (p.SubmittedByUserId == userId) throw new DomainException("A payment cannot be verified by the user who submitted it.");
        db.UseRowVersion(p, r.RowVersion);
        var now = support.Now;
        db.PaymentVerifications.Add(new PaymentVerification
        {
            PaymentId = id, Result = r.Verified ? VerificationResult.Verified : VerificationResult.Rejected, VerifiedByUserId = userId, VerifiedBy = support.Actor, VerifiedAtUtc = now,
            Notes = r.Notes.Trim(), BankReference = N(r.BankReference), ClearedOn = r.ClearedOn,
        });
        if (r.Verified) p.Move(PaymentStatus.PendingApproval);
        else { p.Move(PaymentStatus.Rejected); p.RejectedBy = support.Actor; p.RejectedAtUtc = now; p.RejectReason = r.Notes.Trim(); }
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(r.Verified ? "Payment.Verified" : "Payment.VerificationRejected", "Payment", id.ToString(), new { Status = "PendingVerification" },
            new { p.Status, r.BankReference, r.ClearedOn }, r.Notes.Trim(), UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // 4-9. approval, posting, allocation, receipt, ledger, accounting outbox
    // =====================================================================================================
    public async Task<PaymentDetailDto> ApproveAsync(long id, ApprovePaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsApprove, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        var pre = await db.Payments.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.AgreementId, x.Status, x.SubmittedByUserId }).FirstAsync(ct);
        if (pre.Status != PaymentStatus.PendingApproval) throw new DomainException($"The payment is {pre.Status}; only payments pending approval can be approved.");
        if (pre.SubmittedByUserId == userId) throw new DomainException("A payment cannot be approved by the user who submitted it.");
        if (r.AllocationMode is { } mode)
            await allocateValidator.ValidateOrThrowAsync(new AllocatePaymentRequest(mode == AllocationMode.Unallocated ? AllocationMode.Advance : mode, r.ChargeId, r.Lines, false, false, null), ct);
        var today = await support.TodayAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var agreement = await support.LockAgreementAsync(pre.AgreementId, ct);
        var p = await db.Payments.Include(x => x.PaymentMethod).FirstAsync(x => x.Id == id, ct);
        await db.Entry(p).ReloadAsync(ct);
        db.UseRowVersion(p, r.RowVersion);
        if (p.Status != PaymentStatus.PendingApproval) throw new DomainException($"The payment is already {p.Status}.");
        if (agreement.Status != AgreementStatus.Active) throw new DomainException("The agreement is no longer active; reject the payment instead.");
        var ag = await support.InfoAsync(p.AgreementId, ct);
        await support.SyncLedgerAsync(ag, today, ct);

        var now = support.Now;
        p.Move(PaymentStatus.Posted);
        p.ApprovedByUserId = userId; p.ApprovedBy = support.Actor; p.ApprovedAtUtc = now; p.PostedAtUtc = now;
        await db.SaveWithConcurrencyAsync(ct);

        var receipt = new PaymentReceipt { PaymentId = p.Id, ReceiptNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], Amount = p.Amount, IssuedAtUtc = now, IssuedBy = support.Actor };
        db.PaymentReceipts.Add(receipt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        receipt.ReceiptNumber = $"RCP-{receipt.Id:D6}";

        var refText = p.Reference is null ? "" : $", ref {p.Reference}";
        support.Post(ag, LedgerEntryType.PaymentReceived, 0m, p.Amount, p.PaymentDate, $"Payment {p.PaymentNumber} received ({p.PaymentMethod!.Name}{refText}), receipt {receipt.ReceiptNumber}",
            paymentId: p.Id, uniqueKey: $"PAY:{p.Id}:RECEIVED");
        await support.SaveLedgerAsync(ct);

        var instruction = r.AllocationMode is { } m
            ? new PaymentSupport.AllocationInstruction(m, r.ChargeId, r.Lines, r.RemainderToAdvance ?? false)
            : new PaymentSupport.AllocationInstruction(p.RequestedAllocationMode, p.RequestedChargeId, PaymentSupport.ParseLines(p.RequestedAllocationJson), p.RequestedRemainderToAdvance);
        var plan = await support.AllocateAsync(p, p.Amount, instruction, ct);

        support.Outbox(AccountingEventType.PaymentPosted, "Payment", p.Id, ag, p.Amount, p.PaymentDate, new
        {
            p.PaymentNumber, ReceiptNumber = receipt.ReceiptNumber, ag.AgreementNumber, p.CustomerId, Method = p.MethodKind.ToString(), p.Reference, p.Amount,
            Allocated = plan.Allocated, plan.Advance, plan.Unallocated,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry("Payment.Posted", "Payment", id.ToString(), new { Status = "PendingApproval" }, new
        {
            Status = "Posted", receipt.ReceiptNumber, p.Amount, AllocationMode = instruction.Mode, Allocated = plan.Allocated, plan.Advance, plan.Unallocated,
            Lines = plan.Lines.Select(l => new { l.ItemId, l.Amount }),
        }, N(r.Comments), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    public async Task<PaymentDetailDto> RejectAsync(long id, RejectPaymentRequest r, CancellationToken ct = default)
    {
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var p = await db.Payments.FirstAsync(x => x.Id == id, ct);
        var needed = p.Status switch
        {
            PaymentStatus.PendingVerification => Permissions.PaymentsVerify,
            PaymentStatus.PendingApproval => Permissions.PaymentsApprove,
            _ => throw new DomainException($"The payment is {p.Status}; only pending payments can be rejected."),
        };
        await permissions.EnsureAsync(user, needed, ct);
        if (p.SubmittedByUserId == userId) throw new DomainException("A payment cannot be rejected by the user who submitted it.");
        db.UseRowVersion(p, r.RowVersion);
        var from = p.Status;
        p.Move(PaymentStatus.Rejected);
        p.RejectedBy = support.Actor; p.RejectedAtUtc = support.Now; p.RejectReason = r.Reason.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Payment.Rejected", "Payment", id.ToString(), new { Status = from.ToString() }, new { Status = "Rejected" }, r.Reason.Trim(), UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Allocates the unallocated balance of a posted payment (and, if asked, the money it holds as advance). Over-allocation is impossible.</summary>
    public async Task<PaymentDetailDto> AllocateAsync(long id, AllocatePaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsAllocate, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await allocateValidator.ValidateOrThrowAsync(r, ct);
        var agreementId = await db.Payments.AsNoTracking().Where(x => x.Id == id).Select(x => x.AgreementId).FirstAsync(ct);
        var today = await support.TodayAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await support.LockAgreementAsync(agreementId, ct);
        var p = await db.Payments.FirstAsync(x => x.Id == id, ct);
        await db.Entry(p).ReloadAsync(ct);
        if (!string.IsNullOrWhiteSpace(r.RowVersion) && r.RowVersion != p.RowVersion.ToToken()) throw new ConcurrencyException();
        if (p.Status != PaymentStatus.Posted) throw new DomainException("Only posted payments can be allocated.");
        var ag = await support.InfoAsync(agreementId, ct);
        await support.SyncLedgerAsync(ag, today, ct);
        var (inst, adv, refund) = await support.AllocatedAsync(id, ct);
        var available = p.Amount - inst - adv - refund;
        var released = 0m;
        if (r.IncludeAdvance && adv > 0m)
        {
            foreach (var a in await db.PaymentAllocations.Where(a => a.PaymentId == id && a.Status == AllocationStatus.Active && a.Kind == AllocationKind.Advance).ToListAsync(ct))
            {
                a.Status = AllocationStatus.Released; a.ClosedAtUtc = support.Now; a.ClosedBy = support.Actor; a.CloseReason = "Advance re-applied";
                released += a.Amount;
            }
            available += released;
        }
        if (available <= 0m) throw new DomainException(adv > 0m && !r.IncludeAdvance
            ? "The payment has no unallocated balance. Tick 'include advance' to re-apply the money held as advance."
            : "The payment has no unallocated balance.");
        var plan = await support.AllocateAsync(p, available, new PaymentSupport.AllocationInstruction(r.Mode, r.ChargeId, r.Lines, r.RemainderToAdvance), ct);
        await audit.RecordAsync(new AuditEntry("Payment.Allocated", "Payment", id.ToString(), new { Unallocated = p.Amount - inst - adv - refund, AdvanceReleased = released },
            new { r.Mode, plan.Allocated, plan.Advance, plan.Unallocated, Lines = plan.Lines.Select(l => new { l.ItemId, l.Amount }) }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Reverses a posted payment (e.g. bounced cheque): allocations are reversed on the active schedule, offsetting ledger and outbox entries are added.</summary>
    public async Task<PaymentDetailDto> ReverseAsync(long id, ReversePaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsReverse, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await reverseValidator.ValidateOrThrowAsync(r, ct);
        var agreementId = await db.Payments.AsNoTracking().Where(x => x.Id == id).Select(x => x.AgreementId).FirstAsync(ct);
        var today = await support.TodayAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await support.LockAgreementAsync(agreementId, ct);
        var p = await db.Payments.FirstAsync(x => x.Id == id, ct);
        await db.Entry(p).ReloadAsync(ct);
        db.UseRowVersion(p, r.RowVersion);
        if (p.Status != PaymentStatus.Posted) throw new DomainException($"The payment is {p.Status}; only posted payments can be reversed.");
        var allocations = await db.PaymentAllocations.Where(a => a.PaymentId == id && a.Status == AllocationStatus.Active).ToListAsync(ct);
        if (allocations.Any(a => a.Kind == AllocationKind.Refund)) throw new DomainException("Money from this payment has been refunded; it cannot be reversed.");
        var ag = await support.InfoAsync(agreementId, ct);
        await support.SyncLedgerAsync(ag, today, ct);

        var now = support.Now;
        var reversal = new PaymentReversal { PaymentId = id, Amount = p.Amount, Reason = r.Reason.Trim(), ReversedByUserId = userId, ReversedBy = support.Actor, ReversedAtUtc = now };
        db.PaymentReversals.Add(reversal);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        var items = await support.ActiveItemsAsync(agreementId, tracked: true, ct);
        var roots = items.Count == 0 ? new Dictionary<long, long>() : await support.RootsAsync(agreementId, ct);
        foreach (var a in allocations)
        {
            if (a.Kind == AllocationKind.Installment && a.ScheduleItemId is { } itemId && PaymentSupport.Continuation(itemId, items, roots) is { } item)
            {
                if (item.AmountPaid < a.Amount) throw new DomainException($"Line {item.LineNo} shows less paid than this payment allocated; the schedule must be checked before reversing.");
                item.AmountPaid -= a.Amount;
                item.RefreshStatus();
            }
            a.Status = AllocationStatus.Reversed; a.ClosedAtUtc = now; a.ClosedBy = support.Actor; a.CloseReason = r.Reason.Trim(); a.ReversalId = reversal.Id;
        }
        reversal.AllocationsReversed = allocations.Count;
        p.Move(PaymentStatus.Reversed);
        p.ReversedAtUtc = now;
        await db.SaveWithConcurrencyAsync(ct);
        support.Post(ag, LedgerEntryType.PaymentReversed, p.Amount, 0m, today, $"Payment {p.PaymentNumber} reversed: {r.Reason.Trim()}", paymentId: p.Id, uniqueKey: $"PAY:{p.Id}:REVERSED");
        await support.SaveLedgerAsync(ct);
        support.Outbox(AccountingEventType.PaymentReversed, "Payment", p.Id, ag, p.Amount, today, new { p.PaymentNumber, ag.AgreementNumber, Reason = r.Reason.Trim(), ReversalId = reversal.Id });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry("Payment.Reversed", "Payment", id.ToString(), new { Status = "Posted" }, new { Status = "Reversed", AllocationsReversed = allocations.Count, p.Amount },
            r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task EnsureAccessAsync(long paymentId, CancellationToken ct)
    {
        var agreementId = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (long?)p.AgreementId).FirstOrDefaultAsync(ct);
        if (agreementId is null)
        {
            // Unknown ids look like inaccessible ones for users without all-project access.
            if (!await permissions.HasPermissionAsync(RequireUser(), Permissions.ProjectsAccessAll, ct)) throw new ForbiddenException("You do not have access to this payment.");
            throw new NotFoundException("Payment not found.");
        }
        await support.EnsureAgreementAccessAsync(agreementId.Value, ct);
    }

    internal async Task<PaymentDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var x = await (from p in db.Payments.AsNoTracking()
                       join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                       join a in db.CustomerAgreements.AsNoTracking() on p.AgreementId equals a.Id
                       join b in db.Bookings.AsNoTracking() on p.BookingId equals b.Id
                       join c in db.Customers.AsNoTracking() on p.CustomerId equals c.Id
                       join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                       where p.Id == id
                       select new { p, pr.ProjectCode, a.AgreementNumber, b.BookingNumber, c.CustomerCode, c.FullName, MethodName = m.Name }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Payment not found.");
        var p0 = x.p;
        var allocations = await AllocationsAsync(db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == id), ct);
        var verifications = await db.PaymentVerifications.AsNoTracking().Where(v => v.PaymentId == id).OrderBy(v => v.Id)
            .Select(v => new VerificationDto(v.Id, v.Result, v.VerifiedBy, v.VerifiedAtUtc, v.Notes, v.BankReference, v.ClearedOn)).ToListAsync(ct);
        var reversal = await db.PaymentReversals.AsNoTracking().Where(v => v.PaymentId == id)
            .Select(v => new ReversalDto(v.Id, v.Amount, v.Reason, v.ReversedBy, v.ReversedAtUtc, v.AllocationsReversed)).FirstOrDefaultAsync(ct);
        var receipt = await db.PaymentReceipts.AsNoTracking().Where(rc => rc.PaymentId == id)
            .Select(rc => new ReceiptDto(rc.Id, rc.ReceiptNumber, id, p0.PaymentNumber, rc.Amount, rc.IssuedAtUtc, rc.IssuedBy, rc.CopiesPrinted, rc.LastPrintedAtUtc, rc.LastPrintedBy,
                p0.CustomerId, x.FullName, x.AgreementNumber, x.ProjectCode, p0.Status, p0.PaymentDate, x.MethodName)).FirstOrDefaultAsync(ct);
        var outbox = await db.AccountingPostingRequests.AsNoTracking().Where(o => o.SourceType == "Payment" && o.SourceId == id).OrderBy(o => o.Id)
            .Select(o => new { o.EventType, o.Status }).ToListAsync(ct);
        var active = allocations.Where(a => a.Status == AllocationStatus.Active).ToList();
        var inst = active.Where(a => a.Kind == AllocationKind.Installment).Sum(a => a.Amount);
        var adv = active.Where(a => a.Kind == AllocationKind.Advance).Sum(a => a.Amount);
        var refunded = active.Where(a => a.Kind == AllocationKind.Refund).Sum(a => a.Amount);
        var accounting = outbox.Count == 0
            ? (p0.Status == PaymentStatus.Posted || p0.Status == PaymentStatus.Reversed ? "Not queued" : "Not applicable until posted")
            : string.Join("; ", outbox.Select(o => $"{o.EventType}: {o.Status} (awaiting accounting posting rules)"));
        return new PaymentDetailDto(p0.Id, p0.PaymentNumber, p0.ProjectId, x.ProjectCode, p0.AgreementId, x.AgreementNumber, p0.BookingId, x.BookingNumber, p0.CustomerId, x.CustomerCode,
            x.FullName, p0.PaymentMethodId, x.MethodName, p0.MethodKind, p0.Source, p0.Amount, p0.CurrencyCode, await support.CurrencySymbolAsync(ct), p0.PaymentDate, p0.Reference, p0.BankName,
            p0.InstrumentDate, p0.PayerName, p0.Remarks, p0.Status, p0.VerificationRequired, p0.RequestedAllocationMode, p0.RequestedChargeId, PaymentSupport.ParseLines(p0.RequestedAllocationJson),
            p0.RequestedRemainderToAdvance, p0.SubmittedBy, p0.SubmittedAtUtc, p0.ApprovedBy, p0.ApprovedAtUtc, p0.PostedAtUtc, p0.RejectedBy, p0.RejectedAtUtc, p0.RejectReason,
            inst, adv, p0.Status == PaymentStatus.Posted ? p0.Amount - inst - adv - refunded : 0m, refunded, allocations, verifications, reversal, receipt, accounting, p0.RowVersion.ToToken());
    }

    internal async Task<List<AllocationDto>> AllocationsAsync(IQueryable<PaymentAllocation> query, CancellationToken ct)
    {
        return await (from a in query
                      join i in db.PaymentScheduleItems.AsNoTracking() on a.ScheduleItemId equals i.Id into ij
                      from i in ij.DefaultIfEmpty()
                      join rf in db.Refunds.AsNoTracking() on a.RefundId equals rf.Id into rj
                      from rf in rj.DefaultIfEmpty()
                      orderby a.Id
                      select new AllocationDto(a.Id, a.Kind, a.Mode, a.ScheduleItemId, i == null ? null : i.LineNo, i == null ? null : i.Description, i == null ? null : i.DueDate,
                          a.AgreementChargeId, a.Amount, a.Status, a.AllocatedAtUtc, a.AllocatedBy, a.ClosedAtUtc, a.ClosedBy, a.CloseReason, rf == null ? null : rf.RefundNumber)).ToListAsync(ct);
    }
}
