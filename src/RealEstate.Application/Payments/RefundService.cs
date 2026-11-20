using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Payments;

/// <summary>
/// Refund processing: request -> approval by a different user (RefundApproval) -> payout. A refund can only return money the agreement holds as credit:
/// unallocated and advance money of posted payments, plus - once the agreement is cancelled - the money applied to its (cancelled) installments.
/// The payout consumes that money from the payments (Refund allocations), posts a ledger debit and an accounting outbox event in one locked transaction.
/// </summary>
public class RefundService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support, PaymentService payments, IAuditService audit,
    IValidator<CreateRefundRequest> createValidator, IValidator<RejectRefundRequest> rejectValidator, IValidator<CancelRefundRequest> cancelValidator,
    IValidator<PayRefundRequest> payValidator)
{
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static readonly PaymentRefundStatus[] Open = [PaymentRefundStatus.Requested, PaymentRefundStatus.Approved];

    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<RefundListItemDto>> ListAsync(RefundListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from rf in db.Refunds.AsNoTracking()
                join p in projects on rf.ProjectId equals p.Id
                join a in db.CustomerAgreements.AsNoTracking() on rf.AgreementId equals a.Id
                join c in db.Customers.AsNoTracking() on rf.CustomerId equals c.Id
                select new { rf, p.ProjectCode, a.AgreementNumber, c.FullName };
        if (r.ProjectId is { } pid) q = q.Where(x => x.rf.ProjectId == pid);
        if (r.AgreementId is { } aid) q = q.Where(x => x.rf.AgreementId == aid);
        if (r.Status is { } s) q = q.Where(x => x.rf.Status == s);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.rf.RefundNumber.Contains(t) || x.AgreementNumber.Contains(t) || x.FullName.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.rf.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(x => new RefundListItemDto(x.rf.Id, x.rf.RefundNumber, x.ProjectCode, x.rf.AgreementId, x.AgreementNumber, x.rf.CustomerId, x.FullName, x.rf.Amount, x.rf.Status,
                x.rf.RequestedBy, x.rf.RequestedAtUtc, x.rf.PaidOn)).ToListAsync(ct);
        return new PagedResult<RefundListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<RefundDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        await EnsureAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    public async Task<RefundableDto> RefundableAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var ag = await support.InfoAsync(agreementId, ct);
        var refundable = await RefundableAmountAsync(agreementId, ag.Status, ct);
        var reserved = await ReservedAsync(agreementId, null, ct);
        var bookingRefunds = await db.BookingRefunds.AsNoTracking().Where(b => b.BookingId == ag.BookingId).OrderBy(b => b.Id)
            .Select(b => new BookingRefundOptionDto(b.Id, b.Amount, b.Reason, b.Status,
                db.Refunds.Where(x => x.BookingRefundId == b.Id && x.Status != PaymentRefundStatus.Rejected && x.Status != PaymentRefundStatus.Cancelled).Sum(x => (decimal?)x.Amount) ?? 0m))
            .ToListAsync(ct);
        return new RefundableDto(agreementId, ag.AgreementNumber, ag.Status, refundable, reserved, Math.Max(refundable - reserved, 0m), await support.CurrencySymbolAsync(ct), bookingRefunds);
    }

    // =====================================================================================================
    // workflow
    // =====================================================================================================
    public async Task<RefundDetailDto> CreateAsync(CreateRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RefundsRequest, ct);
        var userId = RequireUser();
        await createValidator.ValidateOrThrowAsync(r, ct);
        var key = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
        if (key is not null && await db.Refunds.AsNoTracking().Where(x => x.IdempotencyKey == key).Select(x => new { x.Id, x.RequestedByUserId, x.AgreementId, x.Amount }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.RequestedByUserId != userId || existing.AgreementId != r.AgreementId || existing.Amount != r.Amount) throw Invalid("IdempotencyKey", "This idempotency key was already used for a different refund.");
            await EnsureAccessAsync(existing.Id, ct);
            return await BuildAsync(existing.Id, ct);
        }
        await support.EnsureAgreementAccessAsync(r.AgreementId, ct);
        await support.EnsureLedgerCurrentAsync(r.AgreementId, ct);
        var ag = await support.InfoAsync(r.AgreementId, ct);
        await CheckBookingRefundAsync(r.BookingRefundId, ag.BookingId, r.Amount, null, ct);
        var available = await RefundableAmountAsync(ag.AgreementId, ag.Status, ct) - await ReservedAsync(ag.AgreementId, null, ct);
        if (r.Amount > available) throw Invalid(nameof(r.Amount), $"The refund exceeds the money available for refund ({Math.Max(available, 0m):N2}).");

        var refund = new Refund
        {
            RefundNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, ProjectId = ag.ProjectId, AgreementId = ag.AgreementId, CustomerId = ag.HolderCustomerId,
            BookingRefundId = r.BookingRefundId, Amount = r.Amount, Reason = r.Reason.Trim(), Status = PaymentRefundStatus.Requested, RequestedByUserId = userId,
            RequestedBy = support.Actor, RequestedAtUtc = support.Now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Refunds.Add(refund);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw Invalid("IdempotencyKey", "This refund was already submitted."); }
        refund.RefundNumber = $"RFD-{refund.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Refund.Requested", "Refund", refund.Id.ToString(), NewValues: new { refund.RefundNumber, ag.AgreementNumber, refund.Amount, refund.BookingRefundId },
            Reason: refund.Reason, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(refund.Id, ct);
    }

    public async Task<RefundDetailDto> ApproveAsync(long id, ApproveRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RefundsApprove, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        var refund = await db.Refunds.FirstAsync(x => x.Id == id, ct);
        if (refund.Status != PaymentRefundStatus.Requested) throw new DomainException($"The refund is already {refund.Status}.");
        if (refund.RequestedByUserId == userId) throw new DomainException("A refund cannot be approved by the user who requested it.");
        db.UseRowVersion(refund, r.RowVersion);
        await support.EnsureLedgerCurrentAsync(refund.AgreementId, ct);
        var ag = await support.InfoAsync(refund.AgreementId, ct);
        var available = await RefundableAmountAsync(ag.AgreementId, ag.Status, ct) - await ReservedAsync(ag.AgreementId, id, ct);
        if (refund.Amount > available) throw new DomainException($"The refund exceeds the money now available for refund ({Math.Max(available, 0m):N2}); reject it or wait.");
        var comments = string.IsNullOrWhiteSpace(r.Comments) ? null : r.Comments.Trim();
        db.RefundApprovals.Add(new RefundApproval { RefundId = id, Decision = BookingDecision.Approved, ApproverUserId = userId, ApproverName = support.Actor, Comments = comments, DecidedAtUtc = support.Now });
        refund.Status = PaymentRefundStatus.Approved;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Refund.Approved", "Refund", id.ToString(), new { Status = "Requested" }, new { Status = "Approved", refund.Amount }, comments, UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<RefundDetailDto> RejectAsync(long id, RejectRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RefundsApprove, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var refund = await db.Refunds.FirstAsync(x => x.Id == id, ct);
        if (refund.Status != PaymentRefundStatus.Requested) throw new DomainException($"The refund is already {refund.Status}.");
        if (refund.RequestedByUserId == userId) throw new DomainException("A refund cannot be rejected by the user who requested it; cancel it instead.");
        db.UseRowVersion(refund, r.RowVersion);
        db.RefundApprovals.Add(new RefundApproval { RefundId = id, Decision = BookingDecision.Rejected, ApproverUserId = userId, ApproverName = support.Actor, Comments = r.Comments.Trim(), DecidedAtUtc = support.Now });
        refund.Status = PaymentRefundStatus.Rejected;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Refund.Rejected", "Refund", id.ToString(), new { Status = "Requested" }, new { Status = "Rejected" }, r.Comments.Trim(), UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<RefundDetailDto> CancelAsync(long id, CancelRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RefundsRequest, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await cancelValidator.ValidateOrThrowAsync(r, ct);
        var refund = await db.Refunds.FirstAsync(x => x.Id == id, ct);
        if (!Open.Contains(refund.Status)) throw new DomainException($"The refund is already {refund.Status}.");
        db.UseRowVersion(refund, r.RowVersion);
        var from = refund.Status;
        refund.Status = PaymentRefundStatus.Cancelled; refund.CancelledBy = support.Actor; refund.CancelledAtUtc = support.Now; refund.CancelReason = r.Reason.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Refund.Cancelled", "Refund", id.ToString(), new { Status = from.ToString() }, new { Status = "Cancelled" }, r.Reason.Trim(), UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Records the payout of an approved refund.</summary>
    public async Task<RefundDetailDto> PayAsync(long id, PayRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RefundsPay, ct);
        var userId = RequireUser();
        await EnsureAccessAsync(id, ct);
        await payValidator.ValidateOrThrowAsync(r, ct);
        var method = await db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.PaymentMethodId && m.IsActive, ct)
            ?? throw Invalid(nameof(r.PaymentMethodId), "The payment method was not found or is inactive.");
        if (method.RequiresReference && string.IsNullOrWhiteSpace(r.Reference)) throw Invalid(nameof(r.Reference), $"A reference is required for {method.Name} payouts.");
        var today = await support.TodayAsync(ct);
        if (r.PaidOn > today) throw Invalid(nameof(r.PaidOn), "The payout date cannot be in the future.");
        var agreementId = await db.Refunds.AsNoTracking().Where(x => x.Id == id).Select(x => x.AgreementId).FirstAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var agreement = await support.LockAgreementAsync(agreementId, ct);
        var refund = await db.Refunds.FirstAsync(x => x.Id == id, ct);
        await db.Entry(refund).ReloadAsync(ct);
        db.UseRowVersion(refund, r.RowVersion);
        if (refund.Status != PaymentRefundStatus.Approved) throw new DomainException(refund.Status == PaymentRefundStatus.Paid ? "The refund has already been paid." : "Only approved refunds can be paid.");
        var ag = await support.InfoAsync(agreementId, ct);
        await support.SyncLedgerAsync(ag, today, ct);
        var refundable = await RefundableAmountAsync(agreementId, agreement.Status, ct);
        if (refund.Amount > refundable) throw new DomainException($"The refund exceeds the money available for refund ({refundable:N2}).");

        var consumed = await ConsumeAsync(refund, agreement.Status, ct);
        var now = support.Now;
        refund.Status = PaymentRefundStatus.Paid; refund.PaymentMethodId = method.Id; refund.PaidOn = r.PaidOn;
        refund.PaymentReference = string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim(); refund.PaidBy = support.Actor; refund.PaidAtUtc = now;
        await db.SaveWithConcurrencyAsync(ct);
        support.Post(ag, LedgerEntryType.RefundPaid, refund.Amount, 0m, r.PaidOn, $"Refund {refund.RefundNumber} paid ({method.Name}{(refund.PaymentReference is null ? "" : $", ref {refund.PaymentReference}")})",
            refundId: refund.Id, uniqueKey: $"RFD:{refund.Id}:PAID");
        await support.SaveLedgerAsync(ct);
        support.Outbox(AccountingEventType.RefundPaid, "Refund", refund.Id, ag, refund.Amount, r.PaidOn, new
        {
            refund.RefundNumber, ag.AgreementNumber, refund.CustomerId, Method = method.Kind.ToString(), refund.PaymentReference, refund.Amount, AgreementStatus = agreement.Status.ToString(),
            Sources = consumed.Select(c => new { c.PaymentId, c.Kind, c.Amount }),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry("Refund.Paid", "Refund", id.ToString(), new { Status = "Approved" }, new { Status = "Paid", refund.Amount, Method = method.Name, refund.PaymentReference, r.PaidOn },
            UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // money available for refund
    // =====================================================================================================
    /// <summary>Unallocated + advance money of posted payments; for a cancelled agreement also the money applied to its installments.</summary>
    internal async Task<decimal> RefundableAmountAsync(long agreementId, AgreementStatus status, CancellationToken ct)
    {
        var posted = await db.Payments.AsNoTracking().Where(p => p.AgreementId == agreementId && p.Status == PaymentStatus.Posted).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var allocated = await (from a in db.PaymentAllocations.AsNoTracking()
                               join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                               where a.AgreementId == agreementId && a.Status == AllocationStatus.Active && p.Status == PaymentStatus.Posted
                               group a by a.Kind into g
                               select new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(ct);
        var unallocated = posted - allocated.Sum(a => a.Sum);
        var advance = allocated.Where(a => a.Key == AllocationKind.Advance).Sum(a => a.Sum);
        var installments = status == AgreementStatus.Cancelled ? allocated.Where(a => a.Key == AllocationKind.Installment).Sum(a => a.Sum) : 0m;
        return unallocated + advance + installments;
    }

    private async Task<decimal> ReservedAsync(long agreementId, long? exceptRefundId, CancellationToken ct) =>
        await db.Refunds.AsNoTracking().Where(x => x.AgreementId == agreementId && Open.Contains(x.Status) && x.Id != (exceptRefundId ?? 0)).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

    private async Task CheckBookingRefundAsync(long? bookingRefundId, long bookingId, decimal amount, long? exceptRefundId, CancellationToken ct)
    {
        if (bookingRefundId is not { } brId) return;
        var br = await db.BookingRefunds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == brId && b.BookingId == bookingId, ct)
            ?? throw Invalid("BookingRefundId", "The booking refund does not belong to this agreement's booking.");
        if (br.Status != RefundStatus.Approved) throw Invalid("BookingRefundId", "The booking refund request has not been approved.");
        var used = await db.Refunds.AsNoTracking().Where(x => x.BookingRefundId == brId && x.Status != PaymentRefundStatus.Rejected && x.Status != PaymentRefundStatus.Cancelled
            && x.Id != (exceptRefundId ?? 0)).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        if (used + amount > br.Amount) throw Invalid("Amount", $"The approved booking refund allows {br.Amount - used:N2} more.");
    }

    private sealed record Consumed(long PaymentId, AllocationKind Kind, decimal Amount);

    /// <summary>Takes the refund amount from the agreement's payments (oldest first): unallocated money, then advance, then (cancelled agreements) installment allocations.</summary>
    private async Task<List<Consumed>> ConsumeAsync(Refund refund, AgreementStatus status, CancellationToken ct)
    {
        var left = refund.Amount;
        var consumed = new List<Consumed>();
        var now = support.Now;
        var posted = await db.Payments.Where(p => p.AgreementId == refund.AgreementId && p.Status == PaymentStatus.Posted).OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).ToListAsync(ct);
        var ids = posted.Select(p => p.Id).ToList();
        var active = await db.PaymentAllocations.Where(a => ids.Contains(a.PaymentId) && a.Status == AllocationStatus.Active).ToListAsync(ct);

        void Take(Payment p, decimal amount)
        {
            db.PaymentAllocations.Add(new PaymentAllocation
            {
                PaymentId = p.Id, AgreementId = p.AgreementId, Kind = AllocationKind.Refund, Mode = AllocationMode.Unallocated, RefundId = refund.Id, Amount = amount,
                AllocatedAtUtc = now, AllocatedBy = support.Actor,
            });
            consumed.Add(new Consumed(p.Id, AllocationKind.Refund, amount));
            left -= amount;
        }

        foreach (var p in posted)
        {
            if (left == 0m) break;
            var free = p.Amount - active.Where(a => a.PaymentId == p.Id).Sum(a => a.Amount);
            if (free > 0m) Take(p, Math.Min(free, left));
        }
        var releasable = new List<AllocationKind> { AllocationKind.Advance };
        if (status == AgreementStatus.Cancelled) releasable.Add(AllocationKind.Installment);
        foreach (var kind in releasable)
            foreach (var p in posted)
                foreach (var a in active.Where(a => a.PaymentId == p.Id && a.Kind == kind).OrderBy(a => a.Id).ToList())
                {
                    if (left == 0m) break;
                    var part = Math.Min(a.Amount, left);
                    a.Status = AllocationStatus.Released; a.ClosedAtUtc = now; a.ClosedBy = support.Actor; a.CloseReason = $"Refunded ({refund.RefundNumber})";
                    if (part < a.Amount)
                        db.PaymentAllocations.Add(new PaymentAllocation
                        {
                            PaymentId = a.PaymentId, AgreementId = a.AgreementId, Kind = a.Kind, Mode = a.Mode, ScheduleItemId = a.ScheduleItemId, AgreementChargeId = a.AgreementChargeId,
                            Amount = a.Amount - part, AllocatedAtUtc = now, AllocatedBy = support.Actor,
                        });
                    Take(p, part);
                }
        if (left != 0m) throw new DomainException("The refund exceeds the money available for refund.");
        await db.SaveWithConcurrencyAsync(ct);
        return consumed;
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task EnsureAccessAsync(long refundId, CancellationToken ct)
    {
        var agreementId = await db.Refunds.AsNoTracking().Where(x => x.Id == refundId).Select(x => (long?)x.AgreementId).FirstOrDefaultAsync(ct);
        if (agreementId is null)
        {
            if (!await permissions.HasPermissionAsync(RequireUser(), Permissions.ProjectsAccessAll, ct)) throw new ForbiddenException("You do not have access to this refund.");
            throw new NotFoundException("Refund not found.");
        }
        await support.EnsureAgreementAccessAsync(agreementId.Value, ct);
    }

    private async Task<RefundDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var x = await (from rf in db.Refunds.AsNoTracking()
                       join p in db.Projects.AsNoTracking() on rf.ProjectId equals p.Id
                       join a in db.CustomerAgreements.AsNoTracking() on rf.AgreementId equals a.Id
                       join c in db.Customers.AsNoTracking() on rf.CustomerId equals c.Id
                       where rf.Id == id
                       select new { rf, p.ProjectCode, a.AgreementNumber, AgreementStatus = a.Status, c.FullName }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Refund not found.");
        var rf0 = x.rf;
        var methodName = rf0.PaymentMethodId is { } mid ? await db.PaymentMethods.AsNoTracking().Where(m => m.Id == mid).Select(m => m.Name).FirstOrDefaultAsync(ct) : null;
        var brAmount = rf0.BookingRefundId is { } brId ? await db.BookingRefunds.AsNoTracking().Where(b => b.Id == brId).Select(b => (decimal?)b.Amount).FirstOrDefaultAsync(ct) : null;
        var approvals = await db.RefundApprovals.AsNoTracking().Where(a => a.RefundId == id).OrderBy(a => a.Id)
            .Select(a => new RefundApprovalDto(a.Id, a.Decision, a.ApproverName, a.Comments, a.DecidedAtUtc)).ToListAsync(ct);
        var sources = await payments.AllocationsAsync(db.PaymentAllocations.AsNoTracking().Where(a => a.RefundId == id), ct);
        var refundable = Open.Contains(rf0.Status)
            ? Math.Max(await RefundableAmountAsync(rf0.AgreementId, x.AgreementStatus, ct) - await ReservedAsync(rf0.AgreementId, id, ct), 0m)
            : 0m;
        return new RefundDetailDto(rf0.Id, rf0.RefundNumber, rf0.ProjectId, x.ProjectCode, rf0.AgreementId, x.AgreementNumber, x.AgreementStatus, rf0.CustomerId, x.FullName, rf0.BookingRefundId,
            brAmount, rf0.Amount, rf0.Reason, rf0.Status, rf0.RequestedBy, rf0.RequestedAtUtc, methodName, rf0.PaidOn, rf0.PaymentReference, rf0.PaidBy, rf0.PaidAtUtc, rf0.CancelledBy,
            rf0.CancelledAtUtc, rf0.CancelReason, refundable, await support.CurrencySymbolAsync(ct), approvals, sources, rf0.RowVersion.ToToken());
    }
}
