using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Expenses;

/// <summary>
/// Expense workflow (spec 7.3): create -> category -> project -> supporting documents -> approval (different user) -> payment (full or partial) -> accounting posting.
/// Accounting posting is an outbox row per event (Phase 8 consumes them). Payments run in a transaction that locks the expense row, so an expense can never be overpaid.
/// </summary>
public class ExpenseService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ExpenseSupport support, IAuditService audit, DocumentUploader uploader, IFileStorage storage,
    IValidator<CreateExpenseRequest> createValidator, IValidator<UpdateExpenseRequest> updateValidator, IValidator<RejectExpenseRequest> rejectValidator,
    IValidator<CancelExpenseRequest> cancelValidator, IValidator<PayExpenseRequest> payValidator, IValidator<ReverseVendorPaymentRequest> reverseValidator)
{
    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<ExpenseListItemDto>> ListAsync(ExpenseListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from e in db.Expenses.AsNoTracking()
                join p in projects on e.ProjectId equals p.Id
                select new { e, p.ProjectCode, CategoryName = e.Category!.Name, VendorName = e.Vendor != null ? e.Vendor.Name : null };
        if (r.ProjectId is { } pid) q = q.Where(x => x.e.ProjectId == pid);
        if (r.CategoryId is { } cid) q = q.Where(x => x.e.CategoryId == cid);
        if (r.VendorId is { } vid) q = q.Where(x => x.e.VendorId == vid);
        if (r.Status is { } s) q = q.Where(x => x.e.Status == s);
        if (r.PayableOnly) q = q.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid < x.e.Amount);
        if (r.PaymentStatus is { } ps)
            q = ps switch
            {
                ExpensePaymentStatus.Unpaid => q.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid <= 0m),
                ExpensePaymentStatus.PartiallyPaid => q.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid > 0m && x.e.AmountPaid < x.e.Amount),
                _ => q.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid >= x.e.Amount),
            };
        if (r.From is { } from) q = q.Where(x => x.e.ExpenseDate >= from);
        if (r.To is { } to) q = q.Where(x => x.e.ExpenseDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(x => x.e.ExpenseNumber.Contains(t) || x.e.Description.Contains(t) || (x.e.Reference != null && x.e.Reference.Contains(t)) || (x.VendorName != null && x.VendorName.Contains(t)));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.e.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new ExpenseListItemDto(x.e.Id, x.e.ExpenseNumber, x.e.ProjectId, x.ProjectCode,
            x.e.CategoryId, x.CategoryName, x.e.VendorId, x.VendorName, x.e.Description, x.e.ExpenseDate, x.e.DueDate, x.e.Amount, x.e.AmountPaid,
            x.e.Status == ExpenseStatus.Approved ? x.e.Amount - x.e.AmountPaid : 0m, x.e.Status,
            x.e.AmountPaid <= 0m ? ExpensePaymentStatus.Unpaid : x.e.AmountPaid >= x.e.Amount ? ExpensePaymentStatus.Paid : ExpensePaymentStatus.PartiallyPaid, x.e.CreatedBy)).ToListAsync(ct);
        return new PagedResult<ExpenseListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<ExpenseDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        await EnsureAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // create / edit / submit / cancel
    // =====================================================================================================
    public async Task<ExpenseDetailDto> CreateAsync(CreateExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesManage, ct);
        var userId = support.RequireUser();
        await createValidator.ValidateOrThrowAsync(r, ct);
        var key = ExpenseSupport.NullIfBlank(r.IdempotencyKey);
        if (key is not null && await db.Expenses.AsNoTracking().Where(x => x.IdempotencyKey == key)
                .Select(x => new { x.Id, x.ProjectId, x.CategoryId, x.VendorId, x.Amount, x.ExpenseDate, x.Description, x.CreatedBy }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.ProjectId != r.ProjectId || existing.CategoryId != r.CategoryId || existing.VendorId != r.VendorId || existing.Amount != r.Amount || existing.ExpenseDate != r.ExpenseDate
                || existing.Description != r.Description.Trim() || !string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
                throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different expense.");
            await EnsureAccessAsync(existing.Id, ct);
            return await BuildAsync(existing.Id, ct);
        }
        var e = await support.CreateExpenseAsync(r.ProjectId, r.CategoryId, r.VendorId, r.Description, r.ExpenseDate, r.DueDate, r.Amount, r.Reference, r.Remarks, key, ct);
        await audit.RecordAsync(new AuditEntry("Expense.Created", "Expense", e.Id.ToString(), NewValues: new { e.ExpenseNumber, e.ProjectId, e.CategoryId, e.VendorId, e.Amount, e.ExpenseDate }, UserId: userId), ct);
        return await BuildAsync(e.Id, ct);
    }

    public async Task<ExpenseDetailDto> UpdateAsync(long id, UpdateExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesManage, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(r, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        if (!Expense.CanEdit(e.Status)) throw new DomainException($"A {e.Status} expense cannot be edited.");
        await support.OpenProjectAsync(e.ProjectId, ct);
        if (r.CategoryId != e.CategoryId) await support.ActiveCategoryAsync(r.CategoryId, ct);
        var hasInvoice = await db.VendorInvoices.AnyAsync(i => i.ExpenseId == id, ct);
        if (hasInvoice && (r.VendorId != e.VendorId || r.Amount != e.Amount)) throw new DomainException("The vendor and amount of an invoiced expense are set by the vendor invoice and cannot be changed.");
        if (r.VendorId is { } v && v != e.VendorId) await support.ActiveVendorAsync(v, ct);
        db.UseRowVersion(e, r.RowVersion);
        var old = new { e.CategoryId, e.VendorId, e.Description, e.ExpenseDate, e.DueDate, e.Amount, e.Reference, Status = e.Status.ToString() };
        e.CategoryId = r.CategoryId; e.VendorId = r.VendorId; e.Description = ExpenseSupport.Trim(r.Description, 500); e.ExpenseDate = r.ExpenseDate; e.DueDate = r.DueDate; e.Amount = r.Amount;
        e.Reference = ExpenseSupport.TrimOrNull(r.Reference, 100); e.Remarks = ExpenseSupport.TrimOrNull(r.Remarks, 1000);
        if (e.Status == ExpenseStatus.Rejected) e.Status = ExpenseStatus.Draft;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Updated", "Expense", id.ToString(), old, new { e.CategoryId, e.VendorId, e.Description, e.ExpenseDate, e.DueDate, e.Amount, e.Reference, Status = e.Status.ToString() }, UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<ExpenseDetailDto> SubmitAsync(long id, SubmitExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesManage, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        if (!Expense.CanEdit(e.Status)) throw new DomainException($"A {e.Status} expense cannot be submitted.");
        if (!await db.ExpenseAttachments.AnyAsync(a => a.ExpenseId == id && !a.IsDeleted, ct)) throw new DomainException("Upload at least one supporting document (invoice, receipt, contract...) before submitting for approval.");
        await support.OpenProjectAsync(e.ProjectId, ct);
        db.UseRowVersion(e, r.RowVersion);
        var from = e.Status;
        e.Status = ExpenseStatus.PendingApproval; e.SubmittedByUserId = userId; e.SubmittedBy = support.Actor; e.SubmittedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Submitted", "Expense", id.ToString(), new { Status = from.ToString() }, new { Status = "PendingApproval", e.Amount }, UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<ExpenseDetailDto> CancelAsync(long id, CancelExpenseRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        var canManage = await permissions.HasPermissionAsync(userId, Permissions.ExpensesManage, ct);
        var canApprove = await permissions.HasPermissionAsync(userId, Permissions.ExpensesApprove, ct);
        if (!canManage && !canApprove) throw new ForbiddenException();
        await EnsureAccessAsync(id, ct);
        await cancelValidator.ValidateOrThrowAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<Expense>(id, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        await db.Entry(e).ReloadAsync(ct);
        if (e.Status is ExpenseStatus.Cancelled) throw new DomainException("The expense is already cancelled.");
        // an approved expense is the approvers' decision, so only they can undo it; drafts and pending expenses are the makers' to cancel
        if (e.Status == ExpenseStatus.Approved)
        {
            if (!canApprove) throw new ForbiddenException("Only users who can approve expenses can cancel an approved expense.");
            if (e.AmountPaid > 0m) throw new DomainException("An expense with payments cannot be cancelled; reverse its payments first.");
        }
        else if (!canManage) throw new ForbiddenException();
        db.UseRowVersion(e, r.RowVersion);
        var from = e.Status;
        e.Status = ExpenseStatus.Cancelled; e.CancelledBy = support.Actor; e.CancelledAtUtc = support.Now; e.CancelReason = r.Reason.Trim();
        var invoice = await db.VendorInvoices.FirstOrDefaultAsync(i => i.ExpenseId == id, ct);
        if (invoice is not null) invoice.ActiveKey = null;
        if (from == ExpenseStatus.Approved)
            support.Outbox(AccountingEventType.ExpenseCancelled, "Expense", id, e.ProjectId, null, e.Amount, await support.TodayAsync(ct), new { e.ExpenseNumber, e.CategoryId, e.VendorId, e.Amount, Reason = r.Reason.Trim() });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Cancelled", "Expense", id.ToString(), new { Status = from.ToString() }, new { Status = "Cancelled" }, r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // approval (maker-checker)
    // =====================================================================================================
    public async Task<ExpenseDetailDto> ApproveAsync(long id, ApproveExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesApprove, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<Expense>(id, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        await db.Entry(e).ReloadAsync(ct);
        if (e.Status != ExpenseStatus.PendingApproval) throw new DomainException($"The expense is {e.Status}; only expenses pending approval can be approved.");
        if (e.SubmittedByUserId == userId) throw new DomainException("An expense cannot be approved by the user who submitted it.");
        if (!await db.ExpenseAttachments.AnyAsync(a => a.ExpenseId == id && !a.IsDeleted, ct)) throw new DomainException("The expense has no supporting document.");
        await support.OpenProjectAsync(e.ProjectId, ct);
        db.UseRowVersion(e, r.RowVersion);
        var comments = ExpenseSupport.TrimOrNull(r.Comments, 1000);
        var now = support.Now;
        db.ExpenseApprovals.Add(new ExpenseApproval { ExpenseId = id, Decision = BookingDecision.Approved, ApproverUserId = userId, ApproverName = support.Actor, Comments = comments, DecidedAtUtc = now });
        e.Status = ExpenseStatus.Approved; e.ApprovedByUserId = userId; e.ApprovedBy = support.Actor; e.ApprovedAtUtc = now;
        var categoryName = await db.ExpenseCategories.AsNoTracking().Where(c => c.Id == e.CategoryId).Select(c => c.Name).FirstAsync(ct);
        var vendorName = e.VendorId is { } v ? await db.Vendors.AsNoTracking().Where(x => x.Id == v).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        support.Outbox(AccountingEventType.ExpenseApproved, "Expense", id, e.ProjectId, null, e.Amount, e.ExpenseDate,
            new { e.ExpenseNumber, e.CategoryId, Category = categoryName, e.VendorId, Vendor = vendorName, e.Amount, e.ExpenseDate, e.DueDate, Payable = true });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Approved", "Expense", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Approved", e.Amount }, comments, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    public async Task<ExpenseDetailDto> RejectAsync(long id, RejectExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesApprove, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        if (e.Status != ExpenseStatus.PendingApproval) throw new DomainException($"The expense is {e.Status}; only expenses pending approval can be rejected.");
        if (e.SubmittedByUserId == userId) throw new DomainException("An expense cannot be rejected by the user who submitted it; cancel it instead.");
        db.UseRowVersion(e, r.RowVersion);
        db.ExpenseApprovals.Add(new ExpenseApproval { ExpenseId = id, Decision = BookingDecision.Rejected, ApproverUserId = userId, ApproverName = support.Actor, Comments = r.Comments.Trim(), DecidedAtUtc = support.Now });
        e.Status = ExpenseStatus.Rejected;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Rejected", "Expense", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Rejected" }, r.Comments.Trim(), UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // payment
    // =====================================================================================================
    public async Task<ExpenseDetailDto> PayAsync(long id, PayExpenseRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesPay, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        await payValidator.ValidateOrThrowAsync(r, ct);
        var method = await support.MethodAsync(r.PaymentMethodId, r.Reference, ct);
        var today = await support.TodayAsync(ct);
        if (r.PaymentDate > today) throw ExpenseSupport.Invalid("PaymentDate", "The payment date cannot be in the future.");
        var key = r.IdempotencyKey.Trim();
        var hash = ExpenseSupport.Sha256($"{id}|{r.Amount}|{r.PaymentMethodId}|{r.PaymentDate:yyyy-MM-dd}|{r.Reference?.Trim()}");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<Expense>(id, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == id, ct);
        await db.Entry(e).ReloadAsync(ct);
        // idempotency is checked under the lock, so concurrent identical submissions resolve to the first one
        if (await db.VendorPayments.AsNoTracking().Where(p => p.IdempotencyKey == key).Select(p => new { p.ExpenseId, p.RequestHash, p.CreatedBy }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.ExpenseId != id || existing.RequestHash != hash || !string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
                throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different payment.");
            await tx.RollbackAsync(ct);
            return await BuildAsync(id, ct);
        }
        if (e.Status != ExpenseStatus.Approved) throw new DomainException("Only approved expenses can be paid.");
        await support.OpenProjectAsync(e.ProjectId, ct);
        var outstanding = e.Amount - e.AmountPaid;
        if (r.Amount > outstanding) throw ExpenseSupport.Invalid("Amount", $"The payment exceeds the outstanding balance ({outstanding:N2}).");
        var p = new VendorPayment
        {
            PaymentNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, RequestHash = hash, ExpenseId = id, ProjectId = e.ProjectId, VendorId = e.VendorId, PaymentMethodId = method.Id,
            Amount = r.Amount, PaymentDate = r.PaymentDate, Reference = ExpenseSupport.TrimOrNull(r.Reference, 100), Remarks = ExpenseSupport.TrimOrNull(r.Remarks, 1000), Status = VendorPaymentStatus.Posted,
        };
        db.VendorPayments.Add(p);
        e.AmountPaid += r.Amount;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        p.PaymentNumber = $"VPY-{p.Id:D6}";
        support.Outbox(AccountingEventType.ExpensePaid, "VendorPayment", p.Id, e.ProjectId, null, p.Amount, p.PaymentDate,
            new { p.PaymentNumber, e.ExpenseNumber, ExpenseId = id, e.VendorId, Method = method.Kind.ToString(), p.Reference, p.Amount, e.CategoryId, FullyPaid = e.AmountPaid >= e.Amount });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.Paid", "VendorPayment", p.Id.ToString(), new { AmountPaidBefore = e.AmountPaid - p.Amount },
            new { p.PaymentNumber, e.ExpenseNumber, p.Amount, Method = method.Name, p.Reference, p.PaymentDate }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    public async Task<ExpenseDetailDto> ReversePaymentAsync(long paymentId, ReverseVendorPaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesReverse, ct);
        var userId = support.RequireUser();
        await reverseValidator.ValidateOrThrowAsync(r, ct);
        var expenseId = await db.VendorPayments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (long?)p.ExpenseId).FirstOrDefaultAsync(ct);
        if (expenseId is null)
        {
            if (!await support.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this payment.");
            throw new NotFoundException("Payment not found.");
        }
        await EnsureAccessAsync(expenseId.Value, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<Expense>(expenseId.Value, ct);
        var e = await db.Expenses.FirstAsync(x => x.Id == expenseId.Value, ct);
        await db.Entry(e).ReloadAsync(ct);
        var p = await db.VendorPayments.FirstAsync(x => x.Id == paymentId, ct);
        await db.Entry(p).ReloadAsync(ct);
        if (p.Status != VendorPaymentStatus.Posted) throw new DomainException("The payment has already been reversed.");
        p.Status = VendorPaymentStatus.Reversed; p.ReversedBy = support.Actor; p.ReversedAtUtc = support.Now; p.ReversalReason = r.Reason.Trim();
        e.AmountPaid -= p.Amount;
        if (e.AmountPaid < 0m) throw new DomainException("The reversal would make the paid amount negative.");
        support.Outbox(AccountingEventType.ExpensePaymentReversed, "VendorPayment", p.Id, e.ProjectId, null, p.Amount, await support.TodayAsync(ct), new { p.PaymentNumber, e.ExpenseNumber, e.VendorId, p.Amount, Reason = r.Reason.Trim() });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Expense.PaymentReversed", "VendorPayment", p.Id.ToString(), new { Status = "Posted", p.Amount }, new { Status = "Reversed", e.AmountPaid }, r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(e.Id, ct);
    }

    public async Task<PagedResult<VendorPaymentDto>> ListPaymentsAsync(VendorPaymentListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from p in db.VendorPayments.AsNoTracking()
                join pr in projects on p.ProjectId equals pr.Id
                join e in db.Expenses.AsNoTracking() on p.ExpenseId equals e.Id
                join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                select new { p, pr.ProjectCode, e.ExpenseNumber, VendorName = e.Vendor != null ? e.Vendor.Name : null, MethodName = m.Name };
        if (r.ProjectId is { } pid) q = q.Where(x => x.p.ProjectId == pid);
        if (r.VendorId is { } vid) q = q.Where(x => x.p.VendorId == vid);
        if (r.ExpenseId is { } eid) q = q.Where(x => x.p.ExpenseId == eid);
        if (r.Status is { } s) q = q.Where(x => x.p.Status == s);
        if (r.From is { } from) q = q.Where(x => x.p.PaymentDate >= from);
        if (r.To is { } to) q = q.Where(x => x.p.PaymentDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.p.PaymentNumber.Contains(t) || x.ExpenseNumber.Contains(t) || (x.p.Reference != null && x.p.Reference.Contains(t)) || (x.VendorName != null && x.VendorName.Contains(t))); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.p.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new VendorPaymentDto(x.p.Id, x.p.PaymentNumber, x.p.ExpenseId, x.ExpenseNumber, x.p.ProjectId,
            x.ProjectCode, x.p.VendorId, x.VendorName, x.p.Amount, x.p.PaymentDate, x.p.PaymentMethodId, x.MethodName, x.p.Reference, x.p.Remarks, x.p.Status, x.p.CreatedBy, x.p.CreatedAtUtc,
            x.p.ReversedBy, x.p.ReversedAtUtc, x.p.ReversalReason)).ToListAsync(ct);
        return new PagedResult<VendorPaymentDto>(rows, r.Page, r.PageSize, total);
    }

    /// <summary>The projects the caller can access (for filters and forms); non-archived projects only when <paramref name="openOnly"/>.</summary>
    public async Task<IReadOnlyList<ProjectOptionDto>> ProjectOptionsAsync(bool openOnly = false, CancellationToken ct = default)
    {
        support.RequireUser();
        var projects = await support.ProjectsAsync(ct);
        if (openOnly) projects = projects.Where(p => p.Status != ProjectStatus.Archived);
        return await projects.OrderBy(p => p.ProjectCode).Take(200).Select(p => new ProjectOptionDto(p.Id, p.ProjectCode, p.ProjectName)).ToListAsync(ct);
    }

    /// <summary>Active payment methods (shared with Phase 6) for the pay forms.</summary>
    public async Task<IReadOnlyList<PaymentMethodDto>> MethodsAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } id || !(await permissions.HasPermissionAsync(id, Permissions.ExpensesPay, ct) || await permissions.HasPermissionAsync(id, Permissions.ExpensesView, ct)
            || await permissions.HasPermissionAsync(id, Permissions.GovFeesView, ct)))
            throw new ForbiddenException();
        return (await support.MethodsAsync(ct)).Select(m => new PaymentMethodDto(m.Id, m.Code, m.Name, m.Kind, m.RequiresVerification, m.RequiresReference, m.IsActive)).ToList();
    }

    // =====================================================================================================
    // attachments
    // =====================================================================================================
    public async Task<IReadOnlyList<ExpenseAttachmentDto>> ListAttachmentsAsync(long expenseId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        await EnsureAccessAsync(expenseId, ct);
        return await AttachmentsAsync(expenseId, ct);
    }

    public async Task<ExpenseAttachmentDto> UploadAttachmentAsync(long expenseId, ExpenseDocumentType type, string? title, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesManage, ct);
        support.RequireUser();
        await EnsureAccessAsync(expenseId, ct);
        if (!Enum.IsDefined(type)) throw ExpenseSupport.Invalid("DocumentType", "Invalid document type.");
        var e = await db.Expenses.AsNoTracking().FirstAsync(x => x.Id == expenseId, ct);
        if (e.Status == ExpenseStatus.Cancelled) throw new DomainException("Documents cannot be added to a cancelled expense.");
        var stored = await uploader.StoreAsync(file, $"expenses/{e.ProjectId}", ct);
        var t = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(stored.SafeFileName) : title.Trim();
        var doc = new ExpenseAttachment
        {
            ExpenseId = expenseId, DocumentType = type, Title = ExpenseSupport.Trim(t, 200), FileName = stored.SafeFileName, StorageKey = stored.File.Key, ContentType = stored.ContentType,
            SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.ExpenseAttachments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ExpenseAttachment.Uploaded", "ExpenseAttachment", doc.Id.ToString(), NewValues: new { expenseId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256, Type = type.ToString() }), ct);
        return new ExpenseAttachmentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType, doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy);
    }

    public async Task<DownloadedFile> DownloadAttachmentAsync(long expenseId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        await EnsureAccessAsync(expenseId, ct);
        var d = await db.ExpenseAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.ExpenseId == expenseId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteAttachmentAsync(long expenseId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesManage, ct);
        await EnsureAccessAsync(expenseId, ct);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw ExpenseSupport.Invalid("Reason", "Enter a reason of at least 5 characters.");
        var d = await db.ExpenseAttachments.FirstOrDefaultAsync(x => x.Id == documentId && x.ExpenseId == expenseId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        var status = await db.Expenses.AsNoTracking().Where(x => x.Id == expenseId).Select(x => x.Status).FirstAsync(ct);
        if (status is ExpenseStatus.Approved or ExpenseStatus.Cancelled) throw new DomainException("Documents of an approved or cancelled expense cannot be removed.");
        d.MarkDeleted(support.Now, support.Actor, reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ExpenseAttachment.Removed", "ExpenseAttachment", d.Id.ToString(), new { expenseId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // =====================================================================================================
    // dashboard
    // =====================================================================================================
    public async Task<ExpenseDashboardDto> DashboardAsync(long? projectId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesView, ct);
        if (projectId is { } pid) await support.EnsureProjectAccessAsync(pid, ct);
        var projects = await support.ProjectsAsync(ct);
        var today = await support.TodayAsync(ct);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-11);
        if (start > end) throw ExpenseSupport.Invalid("From", "The start date cannot be after the end date.");

        var all = from e in db.Expenses.AsNoTracking() join p in projects on e.ProjectId equals p.Id select new { e, p.ProjectCode, CategoryName = e.Category!.Name, VendorName = e.Vendor != null ? e.Vendor.Name : null };
        if (projectId is { } p0) all = all.Where(x => x.e.ProjectId == p0);
        var approvedInRange = all.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.ExpenseDate >= start && x.e.ExpenseDate <= end);
        var totalApproved = await approvedInRange.SumAsync(x => (decimal?)x.e.Amount, ct) ?? 0m;
        var totalPaid = await approvedInRange.SumAsync(x => (decimal?)x.e.AmountPaid, ct) ?? 0m;
        var payable = all.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid < x.e.Amount);
        var payables = await payable.SumAsync(x => (decimal?)(x.e.Amount - x.e.AmountPaid), ct) ?? 0m;
        var overdue = await payable.Where(x => x.e.DueDate != null && x.e.DueDate < today).SumAsync(x => (decimal?)(x.e.Amount - x.e.AmountPaid), ct) ?? 0m;
        var pending = all.Where(x => x.e.Status == ExpenseStatus.PendingApproval);
        var pendingCount = await pending.CountAsync(ct);
        var pendingAmount = await pending.SumAsync(x => (decimal?)x.e.Amount, ct) ?? 0m;
        var draftCount = await all.CountAsync(x => x.e.Status == ExpenseStatus.Draft || x.e.Status == ExpenseStatus.Rejected, ct);

        var byCategory = (await approvedInRange.GroupBy(x => x.CategoryName).Select(g => new ExpenseAmountRowDto(g.Key, g.Sum(x => x.e.Amount), g.Count())).ToListAsync(ct)).OrderByDescending(x => x.Amount).ToList();
        var byProject = (await approvedInRange.GroupBy(x => x.ProjectCode).Select(g => new ExpenseAmountRowDto(g.Key, g.Sum(x => x.e.Amount), g.Count())).ToListAsync(ct)).OrderByDescending(x => x.Amount).ToList();
        var monthRows = await approvedInRange.GroupBy(x => new { x.e.ExpenseDate.Year, x.e.ExpenseDate.Month }).Select(g => new { g.Key.Year, g.Key.Month, Amount = g.Sum(x => x.e.Amount), Count = g.Count() }).ToListAsync(ct);
        var byMonth = monthRows.OrderBy(m => m.Year).ThenBy(m => m.Month).Select(m => new ExpenseAmountRowDto($"{m.Year}-{m.Month:D2}", m.Amount, m.Count)).ToList();
        var topVendors = await payable.Where(x => x.VendorName != null).GroupBy(x => x.VendorName!).OrderByDescending(g => g.Sum(x => x.e.Amount - x.e.AmountPaid)).Take(5)
            .Select(g => new ExpenseAmountRowDto(g.Key, g.Sum(x => x.e.Amount - x.e.AmountPaid), g.Count())).ToListAsync(ct);

        var fees = from a in db.GovernmentFeeAssessments.AsNoTracking() join p in projects on a.ProjectId equals p.Id where a.Status == GovernmentFeeAssessmentStatus.Assessed select a;
        if (projectId is { } p1) fees = fees.Where(a => a.ProjectId == p1);
        var companyBorne = await fees.SumAsync(a => (decimal?)a.CompanyBorne, ct) ?? 0m;
        var govPayable = await fees.SumAsync(a => (decimal?)(a.Amount - a.PaidAmount), ct) ?? 0m;
        return new ExpenseDashboardDto(projectId, start, end, await support.CurrencySymbolAsync(ct), totalApproved, totalPaid, payables, overdue, pendingCount, pendingAmount, draftCount,
            companyBorne, govPayable, byCategory, byProject, byMonth, topVendors);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task EnsureAccessAsync(long expenseId, CancellationToken ct)
    {
        var projectId = await db.Expenses.AsNoTracking().Where(x => x.Id == expenseId).Select(x => (long?)x.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await support.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this expense.");
            throw new NotFoundException("Expense not found.");
        }
        await support.EnsureProjectAccessAsync(projectId.Value, ct);
    }

    private async Task<List<ExpenseAttachmentDto>> AttachmentsAsync(long expenseId, CancellationToken ct) =>
        await db.ExpenseAttachments.AsNoTracking().Where(d => d.ExpenseId == expenseId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new ExpenseAttachmentDto(d.Id, d.Title, d.FileName, d.DocumentType, d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);

    private async Task<ExpenseDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var x = await (from e in db.Expenses.AsNoTracking()
                       join p in db.Projects.AsNoTracking() on e.ProjectId equals p.Id
                       where e.Id == id
                       select new { e, p.ProjectCode, p.ProjectName, CategoryName = e.Category!.Name, VendorName = e.Vendor != null ? e.Vendor.Name : null }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Expense not found.");
        var e0 = x.e;
        var invoice = await db.VendorInvoices.AsNoTracking().Where(i => i.ExpenseId == id).Select(i => new VendorInvoiceSummaryDto(i.Id, i.InvoiceNumber, i.InvoiceDate, i.DueDate)).FirstOrDefaultAsync(ct);
        var approvals = await db.ExpenseApprovals.AsNoTracking().Where(a => a.ExpenseId == id).OrderBy(a => a.Id)
            .Select(a => new ExpenseApprovalDto(a.Id, a.Decision, a.ApproverName, a.Comments, a.DecidedAtUtc)).ToListAsync(ct);
        var payments = await (from p in db.VendorPayments.AsNoTracking()
                              join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                              where p.ExpenseId == id
                              orderby p.Id
                              select new VendorPaymentDto(p.Id, p.PaymentNumber, p.ExpenseId, e0.ExpenseNumber, p.ProjectId, x.ProjectCode, p.VendorId, x.VendorName, p.Amount, p.PaymentDate, p.PaymentMethodId, m.Name,
                                  p.Reference, p.Remarks, p.Status, p.CreatedBy, p.CreatedAtUtc, p.ReversedBy, p.ReversedAtUtc, p.ReversalReason)).ToListAsync(ct);
        return new ExpenseDetailDto(e0.Id, e0.ExpenseNumber, e0.ProjectId, x.ProjectCode, x.ProjectName, e0.CategoryId, x.CategoryName, e0.VendorId, x.VendorName, e0.Description, e0.ExpenseDate, e0.DueDate,
            e0.Amount, e0.AmountPaid, e0.Outstanding, e0.Status, e0.PaymentStatus, e0.Reference, e0.Remarks, e0.SubmittedBy, e0.SubmittedAtUtc, e0.ApprovedBy, e0.ApprovedAtUtc, e0.CancelledBy,
            e0.CancelledAtUtc, e0.CancelReason, e0.CreatedBy, e0.CreatedAtUtc, await support.CurrencySymbolAsync(ct), invoice, await AttachmentsAsync(id, ct), approvals, payments, e0.RowVersion.ToToken());
    }
}
