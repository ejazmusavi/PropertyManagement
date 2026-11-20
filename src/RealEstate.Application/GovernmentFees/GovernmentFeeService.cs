using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Expenses;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.GovernmentFees;

/// <summary>
/// Government fees (spec 7.4). Five positions are kept apart on every assessment: customer liability, customer collection, government payable, actual payment to government and
/// the company-borne expense (Amount = CustomerLiability + CompanyBorne). They never touch the Phase 6 customer/agreement ledgers. Collections and payments run in a transaction
/// that locks the assessment row, so neither can exceed its cap (collections: customer liability; payments: the assessed amount). Each event writes an accounting outbox row.
/// </summary>
public class GovernmentFeeService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ExpenseSupport support, PaymentSupport paymentSupport, IAuditService audit, DocumentUploader uploader, IFileStorage storage,
    IValidator<SaveGovernmentFeeTypeRequest> typeValidator, IValidator<CreateGovernmentFeeAssessmentRequest> assessValidator, IValidator<CancelGovernmentFeeAssessmentRequest> cancelValidator,
    IValidator<FeeCollectionRequest> collectValidator, IValidator<GovernmentPaymentRequest> payValidator, IValidator<ReverseFeeEntryRequest> reverseValidator)
{
    // =====================================================================================================
    // fee types
    // =====================================================================================================
    public async Task<IReadOnlyList<GovernmentFeeTypeDto>> ListTypesAsync(bool activeOnly = false, long? projectId = null, CancellationToken ct = default)
    {
        if (user.UserId is not { } uid || !(await permissions.HasPermissionAsync(uid, Permissions.GovFeesView, ct) || await permissions.HasPermissionAsync(uid, Permissions.GovFeesAssess, ct)
            || await permissions.HasPermissionAsync(uid, Permissions.GovFeesSetup, ct)))
            throw new ForbiddenException();
        var projects = await support.ProjectsAsync(ct);
        var q = from t in db.GovernmentFeeTypes.AsNoTracking()
                where t.ProjectId == null || projects.Any(p => p.Id == t.ProjectId)
                select t;
        if (activeOnly) q = q.Where(t => t.IsActive);
        if (projectId is { } pid) q = q.Where(t => t.ProjectId == null || t.ProjectId == pid);
        var rows = await q.OrderBy(t => t.Kind).ThenBy(t => t.Name).ToListAsync(ct);
        var codes = await db.Projects.AsNoTracking().Where(p => rows.Select(r => r.ProjectId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        return rows.Select(t => MapType(t, t.ProjectId is { } p && codes.TryGetValue(p, out var c) ? c : null)).ToList();
    }

    public async Task<GovernmentFeeTypeDto> GetTypeAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        var t = await db.GovernmentFeeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Government fee type not found.");
        if (t.ProjectId is { } p) await support.EnsureProjectAccessAsync(p, ct);
        return MapType(t, await ProjectCodeAsync(t.ProjectId, ct));
    }

    public async Task<GovernmentFeeTypeDto> CreateTypeAsync(SaveGovernmentFeeTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesSetup, ct);
        await typeValidator.ValidateOrThrowAsync(r, ct);
        await EnsureScopeAllowedAsync(r.ProjectId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.GovernmentFeeTypes.AnyAsync(t => t.Code == code, ct)) throw ExpenseSupport.Invalid("Code", "This code is already used.");
        var t = new GovernmentFeeType { Code = code };
        Apply(t, r);
        t.ProjectId = r.ProjectId;
        db.GovernmentFeeTypes.Add(t);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw ExpenseSupport.Invalid("Code", "This code is already used."); }
        await audit.RecordAsync(new AuditEntry("GovernmentFeeType.Created", "GovernmentFeeType", t.Id.ToString(), NewValues: new { t.Code, t.Name, t.Kind, t.Method, t.FixedAmount, t.Rate, t.DefaultBearer, t.ProjectId }), ct);
        return MapType(t, await ProjectCodeAsync(t.ProjectId, ct));
    }

    public async Task<GovernmentFeeTypeDto> UpdateTypeAsync(long id, SaveGovernmentFeeTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesSetup, ct);
        await typeValidator.ValidateOrThrowAsync(r, ct);
        var t = await db.GovernmentFeeTypes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Government fee type not found.");
        await EnsureScopeAllowedAsync(t.ProjectId, ct);
        if (t.IsSystem && (!string.Equals(t.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase) || t.Kind != r.Kind)) throw ExpenseSupport.Invalid("Code", "The code and kind of a standard fee type cannot be changed.");
        if (!string.Equals(t.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase)) throw ExpenseSupport.Invalid("Code", "The code of a fee type cannot be changed.");
        if (r.ProjectId != t.ProjectId) throw ExpenseSupport.Invalid("ProjectId", "The scope (company-wide or project) of a fee type cannot be changed.");
        if (t.IsSystem && !r.IsActive) throw new DomainException("Standard fee types cannot be deactivated.");
        db.UseRowVersion(t, r.RowVersion);
        var old = new { t.Name, t.Method, t.FixedAmount, t.Rate, t.DefaultBearer, t.IsActive };
        Apply(t, r);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFeeType.Updated", "GovernmentFeeType", id.ToString(), old, new { t.Name, t.Method, t.FixedAmount, t.Rate, t.DefaultBearer, t.IsActive }, "Terms apply to new assessments only; existing assessments keep their amounts."), ct);
        return MapType(t, await ProjectCodeAsync(t.ProjectId, ct));
    }

    /// <summary>Company-wide types need all-project access (like company-wide late fee rules); project types need access to that project.</summary>
    private async Task EnsureScopeAllowedAsync(long? projectId, CancellationToken ct)
    {
        if (projectId is { } p) await support.EnsureProjectAccessAsync(p, ct);
        else if (!await support.HasAllAccessAsync(ct)) throw new ForbiddenException("Only users with access to all projects can manage company-wide fee types.");
    }

    private static void Apply(GovernmentFeeType t, SaveGovernmentFeeTypeRequest r)
    {
        t.Name = ExpenseSupport.Trim(r.Name, 120); t.Kind = r.Kind; t.Method = r.Method; t.DefaultBearer = r.DefaultBearer; t.Description = ExpenseSupport.TrimOrNull(r.Description, 500); t.IsActive = r.IsActive;
        t.FixedAmount = r.Method == FeeCalculationMethod.Fixed ? r.FixedAmount : null;
        t.Rate = r.Method == FeeCalculationMethod.PercentOfBasis ? r.Rate : null;
    }

    private async Task<string?> ProjectCodeAsync(long? projectId, CancellationToken ct) =>
        projectId is { } p ? await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.ProjectCode).FirstOrDefaultAsync(ct) : null;

    private static GovernmentFeeTypeDto MapType(GovernmentFeeType t, string? projectCode) => new(t.Id, t.ProjectId, projectCode, t.Code, t.Name, t.Kind, t.Method, t.FixedAmount, t.Rate, t.DefaultBearer,
        t.Description, t.IsSystem, t.IsActive, t.RowVersion.ToToken());

    // =====================================================================================================
    // assessments: queries
    // =====================================================================================================
    public async Task<PagedResult<GovernmentFeeListItemDto>> ListAsync(GovernmentFeeListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from a in db.GovernmentFeeAssessments.AsNoTracking()
                join p in projects on a.ProjectId equals p.Id
                join ag in db.CustomerAgreements.AsNoTracking() on a.AgreementId equals ag.Id
                join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                select new { a, p.ProjectCode, ag.AgreementNumber, c.FullName, TypeName = a.FeeType!.Name, a.FeeType.Kind };
        if (r.ProjectId is { } pid) q = q.Where(x => x.a.ProjectId == pid);
        if (r.AgreementId is { } aid) q = q.Where(x => x.a.AgreementId == aid);
        if (r.CustomerId is { } cid) q = q.Where(x => x.a.CustomerId == cid);
        if (r.FeeTypeId is { } fid) q = q.Where(x => x.a.FeeTypeId == fid);
        if (r.Status is { } s) q = q.Where(x => x.a.Status == s);
        if (r.CollectionPending) q = q.Where(x => x.a.Status == GovernmentFeeAssessmentStatus.Assessed && x.a.CollectedAmount < x.a.CustomerLiability);
        if (r.PaymentPending) q = q.Where(x => x.a.Status == GovernmentFeeAssessmentStatus.Assessed && x.a.PaidAmount < x.a.Amount);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.a.AssessmentNumber.Contains(t) || x.AgreementNumber.Contains(t) || x.FullName.Contains(t) || x.TypeName.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.a.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new GovernmentFeeListItemDto(x.a.Id, x.a.AssessmentNumber, x.a.ProjectId, x.ProjectCode,
            x.a.AgreementId, x.AgreementNumber, x.a.CustomerId, x.FullName, x.a.FeeTypeId, x.TypeName, x.Kind, x.a.Amount, x.a.CustomerLiability, x.a.CompanyBorne, x.a.CollectedAmount,
            x.a.Status == GovernmentFeeAssessmentStatus.Assessed ? x.a.CustomerLiability - x.a.CollectedAmount : 0m, x.a.PaidAmount,
            x.a.Status == GovernmentFeeAssessmentStatus.Assessed ? x.a.Amount - x.a.PaidAmount : 0m, x.a.Status, x.a.DueDate)).ToListAsync(ct);
        return new PagedResult<GovernmentFeeListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<GovernmentFeeDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        await EnsureAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    public async Task<GovernmentFeeSummaryDto> SummaryAsync(long? projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        if (projectId is { } pid) await support.EnsureProjectAccessAsync(pid, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from a in db.GovernmentFeeAssessments.AsNoTracking() join p in projects on a.ProjectId equals p.Id where a.Status == GovernmentFeeAssessmentStatus.Assessed select new { a, TypeName = a.FeeType!.Name };
        if (projectId is { } p0) q = q.Where(x => x.a.ProjectId == p0);
        var totals = await q.GroupBy(_ => 1).Select(g => new
        {
            Count = g.Count(), Assessed = g.Sum(x => x.a.Amount), Liability = g.Sum(x => x.a.CustomerLiability), Collected = g.Sum(x => x.a.CollectedAmount),
            Borne = g.Sum(x => x.a.CompanyBorne), Paid = g.Sum(x => x.a.PaidAmount),
        }).FirstOrDefaultAsync(ct);
        var byType = (await q.GroupBy(x => x.TypeName).Select(g => new ExpenseAmountRowDto(g.Key, g.Sum(x => x.a.Amount), g.Count())).ToListAsync(ct)).OrderByDescending(x => x.Amount).ToList();
        var t = totals;
        return new GovernmentFeeSummaryDto(projectId, await support.CurrencySymbolAsync(ct), t?.Count ?? 0, t?.Assessed ?? 0m, t?.Liability ?? 0m, t?.Collected ?? 0m, (t?.Liability ?? 0m) - (t?.Collected ?? 0m),
            t?.Borne ?? 0m, t?.Paid ?? 0m, (t?.Assessed ?? 0m) - (t?.Paid ?? 0m), byType);
    }

    public async Task<PagedResult<GovernmentFeePaymentDto>> ListPaymentsAsync(GovernmentPaymentListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from p in db.GovernmentFeePayments.AsNoTracking()
                join pr in projects on p.ProjectId equals pr.Id
                join a in db.GovernmentFeeAssessments.AsNoTracking() on p.AssessmentId equals a.Id
                join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                select new { p, pr.ProjectCode, a.AssessmentNumber, MethodName = m.Name };
        if (r.ProjectId is { } pid) q = q.Where(x => x.p.ProjectId == pid);
        if (r.AssessmentId is { } aid) q = q.Where(x => x.p.AssessmentId == aid);
        if (r.Status is { } s) q = q.Where(x => x.p.Status == s);
        if (r.From is { } from) q = q.Where(x => x.p.PaymentDate >= from);
        if (r.To is { } to) q = q.Where(x => x.p.PaymentDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.p.PaymentNumber.Contains(t) || x.AssessmentNumber.Contains(t) || (x.p.Reference != null && x.p.Reference.Contains(t))); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.p.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new GovernmentFeePaymentDto(x.p.Id, x.p.PaymentNumber, x.p.AssessmentId, x.AssessmentNumber,
            x.p.ProjectId, x.ProjectCode, x.p.Amount, x.p.PaymentDate, x.MethodName, x.p.Reference, x.p.Remarks, x.p.Status, x.p.CreatedBy, x.p.CreatedAtUtc, x.p.ReversedBy, x.p.ReversedAtUtc,
            x.p.ReversalReason)).ToListAsync(ct);
        return new PagedResult<GovernmentFeePaymentDto>(rows, r.Page, r.PageSize, total);
    }

    /// <summary>Active agreements (up to 20 matches) in the caller's projects, for the assessment picker.</summary>
    public async Task<IReadOnlyList<AgreementLookupDto>> AgreementLookupAsync(string? term, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
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

    /// <summary>One agreement (with its account holder) for the assessment form.</summary>
    public async Task<AgreementLookupDto> GetAgreementAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        await paymentSupport.EnsureAgreementAccessAsync(agreementId, ct);
        var ag = await paymentSupport.InfoAsync(agreementId, ct);
        var c = await db.Customers.AsNoTracking().FirstAsync(x => x.Id == ag.HolderCustomerId, ct);
        return new AgreementLookupDto(ag.AgreementId, ag.AgreementNumber, ag.BookingNumber, ag.ProjectCode, c.Id, c.FullName, c.CustomerCode);
    }

    // =====================================================================================================
    // assessments: workflow
    // =====================================================================================================
    public async Task<GovernmentFeeDetailDto> AssessAsync(CreateGovernmentFeeAssessmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        var userId = support.RequireUser();
        await assessValidator.ValidateOrThrowAsync(r, ct);
        var key = r.IdempotencyKey.Trim();
        if (await db.GovernmentFeeAssessments.AsNoTracking().Where(a => a.IdempotencyKey == key)
                .Select(a => new { a.Id, a.AgreementId, a.FeeTypeId, a.CreatedBy, a.BasisAmount, a.Amount, a.CompanyBorne, a.DueDate, Method = a.FeeType!.Method }).FirstOrDefaultAsync(ct) is { } existing)
        {
            var different = existing.AgreementId != r.AgreementId || existing.FeeTypeId != r.FeeTypeId || !string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase)
                || r.BasisAmount != existing.BasisAmount || r.DueDate != existing.DueDate
                || (r.CompanyBorneAmount is { } cb && cb != existing.CompanyBorne)
                || (existing.Method == FeeCalculationMethod.Manual && r.Amount != existing.Amount);
            if (different) throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different assessment.");
            await EnsureAccessAsync(existing.Id, ct);
            return await BuildAsync(existing.Id, ct);
        }
        await paymentSupport.EnsureAgreementAccessAsync(r.AgreementId, ct);
        var ag = await paymentSupport.InfoAsync(r.AgreementId, ct);
        if (ag.Status != AgreementStatus.Active) throw new DomainException("Fees can only be assessed for an active agreement.");
        await support.OpenProjectAsync(ag.ProjectId, ct);
        var type = await db.GovernmentFeeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.FeeTypeId, ct) ?? throw ExpenseSupport.Invalid("FeeTypeId", "The government fee type was not found.");
        if (!type.IsActive) throw ExpenseSupport.Invalid("FeeTypeId", "The government fee type is inactive.");
        if (type.ProjectId is { } tp && tp != ag.ProjectId) throw ExpenseSupport.Invalid("FeeTypeId", "This fee type belongs to another project.");

        var policy = await support.PolicyAsync(ct);
        var amount = type.Calculate(r.BasisAmount, policy.Decimals, policy.Mode);
        switch (type.Method)
        {
            case FeeCalculationMethod.Manual:
                if (r.Amount is not { } manual) throw ExpenseSupport.Invalid("Amount", "Enter the fee amount.");
                amount = manual;
                break;
            case FeeCalculationMethod.PercentOfBasis:
                if (r.BasisAmount is null) throw ExpenseSupport.Invalid("BasisAmount", "Enter the amount the percentage applies to.");
                break;
        }
        if (amount is not { } fee || fee <= 0m) throw ExpenseSupport.Invalid("Amount", "The calculated fee must be above zero.");
        var borne = r.CompanyBorneAmount ?? (type.DefaultBearer == FeeBearer.Company ? fee : 0m);
        if (borne < 0m || borne > fee) throw ExpenseSupport.Invalid("CompanyBorneAmount", "The company-borne part must be between zero and the fee amount.");
        var today = await support.TodayAsync(ct);
        if (r.DueDate is { } due && due < today) throw ExpenseSupport.Invalid("DueDate", "The due date cannot be in the past.");

        var a = new GovernmentFeeAssessment
        {
            AssessmentNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, ProjectId = ag.ProjectId, AgreementId = ag.AgreementId, BookingId = ag.BookingId, CustomerId = ag.HolderCustomerId,
            FeeTypeId = type.Id, BasisAmount = r.BasisAmount, Amount = fee, CustomerLiability = fee - borne, CompanyBorne = borne, DueDate = r.DueDate,
            Reference = ExpenseSupport.TrimOrNull(r.Reference, 100), Remarks = ExpenseSupport.TrimOrNull(r.Remarks, 1000), Status = GovernmentFeeAssessmentStatus.Assessed,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.GovernmentFeeAssessments.Add(a);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw ExpenseSupport.Invalid("IdempotencyKey", "This assessment was already submitted."); }
        a.AssessmentNumber = $"GFA-{a.Id:D6}";
        support.Outbox(AccountingEventType.GovFeeAssessed, "GovernmentFeeAssessment", a.Id, a.ProjectId, a.AgreementId, a.Amount, today,
            new { a.AssessmentNumber, FeeType = type.Name, Kind = type.Kind.ToString(), ag.AgreementNumber, a.CustomerId, a.Amount, a.CustomerLiability, a.CompanyBorne });
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.Assessed", "GovernmentFeeAssessment", a.Id.ToString(),
            NewValues: new { a.AssessmentNumber, FeeType = type.Name, ag.AgreementNumber, a.Amount, a.CustomerLiability, a.CompanyBorne, a.BasisAmount }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(a.Id, ct);
    }

    public async Task<GovernmentFeeDetailDto> CancelAsync(long id, CancelGovernmentFeeAssessmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(id, ct);
        await cancelValidator.ValidateOrThrowAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GovernmentFeeAssessment>(id, ct);
        var a = await db.GovernmentFeeAssessments.FirstAsync(x => x.Id == id, ct);
        await db.Entry(a).ReloadAsync(ct);
        if (a.Status == GovernmentFeeAssessmentStatus.Cancelled) throw new DomainException("The assessment is already cancelled.");
        if (a.CollectedAmount > 0m || a.PaidAmount > 0m) throw new DomainException("An assessment with collections or payments cannot be cancelled; reverse them first.");
        db.UseRowVersion(a, r.RowVersion);
        a.Status = GovernmentFeeAssessmentStatus.Cancelled; a.CancelledBy = support.Actor; a.CancelledAtUtc = support.Now; a.CancelReason = r.Reason.Trim();
        support.Outbox(AccountingEventType.GovFeeAssessmentCancelled, "GovernmentFeeAssessment", a.Id, a.ProjectId, a.AgreementId, a.Amount, await support.TodayAsync(ct),
            new { a.AssessmentNumber, a.Amount, a.CustomerLiability, a.CompanyBorne, Reason = r.Reason.Trim() });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.Cancelled", "GovernmentFeeAssessment", id.ToString(), new { Status = "Assessed" }, new { Status = "Cancelled" }, r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(id, ct);
    }

    // =====================================================================================================
    // customer collections
    // =====================================================================================================
    public async Task<GovernmentFeeDetailDto> CollectAsync(long assessmentId, FeeCollectionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(assessmentId, ct);
        await collectValidator.ValidateOrThrowAsync(r, ct);
        var method = await support.MethodAsync(r.PaymentMethodId, r.Reference, ct);
        var today = await support.TodayAsync(ct);
        if (r.CollectionDate > today) throw ExpenseSupport.Invalid("CollectionDate", "The collection date cannot be in the future.");
        var key = r.IdempotencyKey.Trim();
        var hash = ExpenseSupport.Sha256($"{assessmentId}|{r.Amount}|{r.PaymentMethodId}|{r.CollectionDate:yyyy-MM-dd}|{r.Reference?.Trim()}");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GovernmentFeeAssessment>(assessmentId, ct);
        var a = await db.GovernmentFeeAssessments.FirstAsync(x => x.Id == assessmentId, ct);
        await db.Entry(a).ReloadAsync(ct);
        if (await db.GovernmentFeeCollections.AsNoTracking().Where(c => c.IdempotencyKey == key).Select(c => new { c.AssessmentId, c.RequestHash, c.CreatedBy }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.AssessmentId != assessmentId || existing.RequestHash != hash || !string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
                throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different collection.");
            await tx.RollbackAsync(ct);
            return await BuildAsync(assessmentId, ct);
        }
        if (a.Status != GovernmentFeeAssessmentStatus.Assessed) throw new DomainException("Collections can only be recorded on an active assessment.");
        var outstanding = a.CustomerLiability - a.CollectedAmount;
        if (outstanding <= 0m) throw new DomainException("The customer's share of this fee is already fully collected.");
        if (r.Amount > outstanding) throw ExpenseSupport.Invalid("Amount", $"The collection exceeds the customer's outstanding liability ({outstanding:N2}).");
        var c = new GovernmentFeeCollection
        {
            CollectionNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, RequestHash = hash, AssessmentId = assessmentId, ProjectId = a.ProjectId, CustomerId = a.CustomerId,
            PaymentMethodId = method.Id, Amount = r.Amount, CollectionDate = r.CollectionDate, Reference = ExpenseSupport.TrimOrNull(r.Reference, 100), Remarks = ExpenseSupport.TrimOrNull(r.Remarks, 1000),
            Status = FeeEntryStatus.Posted,
        };
        db.GovernmentFeeCollections.Add(c);
        a.CollectedAmount += r.Amount;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        c.CollectionNumber = $"GFC-{c.Id:D6}";
        support.Outbox(AccountingEventType.GovFeeCollected, "GovernmentFeeCollection", c.Id, a.ProjectId, a.AgreementId, c.Amount, c.CollectionDate,
            new { c.CollectionNumber, a.AssessmentNumber, a.CustomerId, Method = method.Kind.ToString(), c.Reference, c.Amount });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.Collected", "GovernmentFeeCollection", c.Id.ToString(), new { CollectedBefore = a.CollectedAmount - c.Amount },
            new { c.CollectionNumber, a.AssessmentNumber, c.Amount, Method = method.Name, c.Reference, c.CollectionDate }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(assessmentId, ct);
    }

    public async Task<GovernmentFeeDetailDto> ReverseCollectionAsync(long collectionId, ReverseFeeEntryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesPay, ct);
        var userId = support.RequireUser();
        await reverseValidator.ValidateOrThrowAsync(r, ct);
        var assessmentId = await db.GovernmentFeeCollections.AsNoTracking().Where(c => c.Id == collectionId).Select(c => (long?)c.AssessmentId).FirstOrDefaultAsync(ct);
        if (assessmentId is null) throw await MissingAsync("collection", ct);
        await EnsureAccessAsync(assessmentId.Value, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GovernmentFeeAssessment>(assessmentId.Value, ct);
        var a = await db.GovernmentFeeAssessments.FirstAsync(x => x.Id == assessmentId.Value, ct);
        await db.Entry(a).ReloadAsync(ct);
        var c = await db.GovernmentFeeCollections.FirstAsync(x => x.Id == collectionId, ct);
        await db.Entry(c).ReloadAsync(ct);
        if (c.Status != FeeEntryStatus.Posted) throw new DomainException("The collection has already been reversed.");
        c.Status = FeeEntryStatus.Reversed; c.ReversedBy = support.Actor; c.ReversedAtUtc = support.Now; c.ReversalReason = r.Reason.Trim();
        a.CollectedAmount -= c.Amount;
        if (a.CollectedAmount < 0m) throw new DomainException("The reversal would make the collected amount negative.");
        support.Outbox(AccountingEventType.GovFeeCollectionReversed, "GovernmentFeeCollection", c.Id, a.ProjectId, a.AgreementId, c.Amount, await support.TodayAsync(ct),
            new { c.CollectionNumber, a.AssessmentNumber, c.Amount, Reason = r.Reason.Trim() });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.CollectionReversed", "GovernmentFeeCollection", c.Id.ToString(), new { Status = "Posted", c.Amount }, new { Status = "Reversed", a.CollectedAmount }, r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(a.Id, ct);
    }

    // =====================================================================================================
    // payments to government
    // =====================================================================================================
    public async Task<GovernmentFeeDetailDto> PayAsync(long assessmentId, GovernmentPaymentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesPay, ct);
        var userId = support.RequireUser();
        await EnsureAccessAsync(assessmentId, ct);
        await payValidator.ValidateOrThrowAsync(r, ct);
        var method = await support.MethodAsync(r.PaymentMethodId, r.Reference, ct);
        var today = await support.TodayAsync(ct);
        if (r.PaymentDate > today) throw ExpenseSupport.Invalid("PaymentDate", "The payment date cannot be in the future.");
        var key = r.IdempotencyKey.Trim();
        var hash = ExpenseSupport.Sha256($"{assessmentId}|{r.Amount}|{r.PaymentMethodId}|{r.PaymentDate:yyyy-MM-dd}|{r.Reference?.Trim()}");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GovernmentFeeAssessment>(assessmentId, ct);
        var a = await db.GovernmentFeeAssessments.FirstAsync(x => x.Id == assessmentId, ct);
        await db.Entry(a).ReloadAsync(ct);
        if (await db.GovernmentFeePayments.AsNoTracking().Where(p => p.IdempotencyKey == key).Select(p => new { p.AssessmentId, p.RequestHash, p.CreatedBy }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.AssessmentId != assessmentId || existing.RequestHash != hash || !string.Equals(existing.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
                throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different payment.");
            await tx.RollbackAsync(ct);
            return await BuildAsync(assessmentId, ct);
        }
        if (a.Status != GovernmentFeeAssessmentStatus.Assessed) throw new DomainException("Payments can only be recorded on an active assessment.");
        var payable = a.Amount - a.PaidAmount;
        if (payable <= 0m) throw new DomainException("This fee has already been paid to government in full.");
        if (r.Amount > payable) throw ExpenseSupport.Invalid("Amount", $"The payment exceeds the government payable ({payable:N2}).");
        var p = new GovernmentFeePayment
        {
            PaymentNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, RequestHash = hash, AssessmentId = assessmentId, ProjectId = a.ProjectId, PaymentMethodId = method.Id,
            Amount = r.Amount, PaymentDate = r.PaymentDate, Reference = ExpenseSupport.TrimOrNull(r.Reference, 100), Remarks = ExpenseSupport.TrimOrNull(r.Remarks, 1000), Status = FeeEntryStatus.Posted,
        };
        db.GovernmentFeePayments.Add(p);
        a.PaidAmount += r.Amount;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        p.PaymentNumber = $"GFP-{p.Id:D6}";
        support.Outbox(AccountingEventType.GovFeePaid, "GovernmentFeePayment", p.Id, a.ProjectId, a.AgreementId, p.Amount, p.PaymentDate,
            new { p.PaymentNumber, a.AssessmentNumber, Method = method.Kind.ToString(), p.Reference, p.Amount, a.CustomerLiability, a.CompanyBorne });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.Paid", "GovernmentFeePayment", p.Id.ToString(), new { PaidBefore = a.PaidAmount - p.Amount },
            new { p.PaymentNumber, a.AssessmentNumber, p.Amount, Method = method.Name, p.Reference, p.PaymentDate }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(assessmentId, ct);
    }

    public async Task<GovernmentFeeDetailDto> ReversePaymentAsync(long paymentId, ReverseFeeEntryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesPay, ct);
        var userId = support.RequireUser();
        await reverseValidator.ValidateOrThrowAsync(r, ct);
        var assessmentId = await db.GovernmentFeePayments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => (long?)p.AssessmentId).FirstOrDefaultAsync(ct);
        if (assessmentId is null) throw await MissingAsync("payment", ct);
        await EnsureAccessAsync(assessmentId.Value, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GovernmentFeeAssessment>(assessmentId.Value, ct);
        var a = await db.GovernmentFeeAssessments.FirstAsync(x => x.Id == assessmentId.Value, ct);
        await db.Entry(a).ReloadAsync(ct);
        var p = await db.GovernmentFeePayments.FirstAsync(x => x.Id == paymentId, ct);
        await db.Entry(p).ReloadAsync(ct);
        if (p.Status != FeeEntryStatus.Posted) throw new DomainException("The payment has already been reversed.");
        p.Status = FeeEntryStatus.Reversed; p.ReversedBy = support.Actor; p.ReversedAtUtc = support.Now; p.ReversalReason = r.Reason.Trim();
        a.PaidAmount -= p.Amount;
        if (a.PaidAmount < 0m) throw new DomainException("The reversal would make the paid amount negative.");
        support.Outbox(AccountingEventType.GovFeePaymentReversed, "GovernmentFeePayment", p.Id, a.ProjectId, a.AgreementId, p.Amount, await support.TodayAsync(ct),
            new { p.PaymentNumber, a.AssessmentNumber, p.Amount, Reason = r.Reason.Trim() });
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFee.PaymentReversed", "GovernmentFeePayment", p.Id.ToString(), new { Status = "Posted", p.Amount }, new { Status = "Reversed", a.PaidAmount }, r.Reason.Trim(), UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(a.Id, ct);
    }

    // =====================================================================================================
    // documents (government receipts, challans)
    // =====================================================================================================
    public async Task<GovernmentFeeDocumentDto> UploadDocumentAsync(long assessmentId, GovernmentFeeDocumentType type, string? title, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        await EnsureAccessAsync(assessmentId, ct);
        if (!Enum.IsDefined(type)) throw ExpenseSupport.Invalid("DocumentType", "Invalid document type.");
        var projectId = await db.GovernmentFeeAssessments.AsNoTracking().Where(a => a.Id == assessmentId).Select(a => a.ProjectId).FirstAsync(ct);
        var stored = await uploader.StoreAsync(file, $"govfees/{projectId}", ct);
        var t = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(stored.SafeFileName) : title.Trim();
        var doc = new GovernmentFeeDocument
        {
            AssessmentId = assessmentId, DocumentType = type, Title = ExpenseSupport.Trim(t, 200), FileName = stored.SafeFileName, StorageKey = stored.File.Key, ContentType = stored.ContentType,
            SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.GovernmentFeeDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFeeDocument.Uploaded", "GovernmentFeeDocument", doc.Id.ToString(), NewValues: new { assessmentId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256, Type = type.ToString() }), ct);
        return new GovernmentFeeDocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType, doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy);
    }

    public async Task<IReadOnlyList<GovernmentFeeDocumentDto>> ListDocumentsAsync(long assessmentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        await EnsureAccessAsync(assessmentId, ct);
        return await DocumentsAsync(assessmentId, ct);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long assessmentId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesView, ct);
        await EnsureAccessAsync(assessmentId, ct);
        var d = await db.GovernmentFeeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.AssessmentId == assessmentId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long assessmentId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GovFeesAssess, ct);
        await EnsureAccessAsync(assessmentId, ct);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw ExpenseSupport.Invalid("Reason", "Enter a reason of at least 5 characters.");
        var d = await db.GovernmentFeeDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.AssessmentId == assessmentId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(support.Now, support.Actor, reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("GovernmentFeeDocument.Removed", "GovernmentFeeDocument", d.Id.ToString(), new { assessmentId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task<Exception> MissingAsync(string what, CancellationToken ct) =>
        !await support.HasAllAccessAsync(ct) ? new ForbiddenException($"You do not have access to this {what}.") : new NotFoundException($"The {what} was not found.");

    private async Task EnsureAccessAsync(long assessmentId, CancellationToken ct)
    {
        var projectId = await db.GovernmentFeeAssessments.AsNoTracking().Where(a => a.Id == assessmentId).Select(a => (long?)a.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null) throw await MissingAsync("fee assessment", ct);
        await support.EnsureProjectAccessAsync(projectId.Value, ct);
    }

    private async Task<List<GovernmentFeeDocumentDto>> DocumentsAsync(long assessmentId, CancellationToken ct) =>
        await db.GovernmentFeeDocuments.AsNoTracking().Where(d => d.AssessmentId == assessmentId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new GovernmentFeeDocumentDto(d.Id, d.Title, d.FileName, d.DocumentType, d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);

    private async Task<GovernmentFeeDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var x = await (from a in db.GovernmentFeeAssessments.AsNoTracking()
                       join p in db.Projects.AsNoTracking() on a.ProjectId equals p.Id
                       join ag in db.CustomerAgreements.AsNoTracking() on a.AgreementId equals ag.Id
                       join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                       where a.Id == id
                       select new { a, p.ProjectCode, ag.AgreementNumber, c.FullName, TypeName = a.FeeType!.Name, a.FeeType.Kind, a.FeeType.Method }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Fee assessment not found.");
        var a0 = x.a;
        var collections = await (from c in db.GovernmentFeeCollections.AsNoTracking()
                                 join m in db.PaymentMethods.AsNoTracking() on c.PaymentMethodId equals m.Id
                                 where c.AssessmentId == id
                                 orderby c.Id
                                 select new GovernmentFeeCollectionDto(c.Id, c.CollectionNumber, c.Amount, c.CollectionDate, m.Name, c.Reference, c.Remarks, c.Status, c.CreatedBy, c.CreatedAtUtc,
                                     c.ReversedBy, c.ReversedAtUtc, c.ReversalReason)).ToListAsync(ct);
        var payments = await (from p in db.GovernmentFeePayments.AsNoTracking()
                              join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                              where p.AssessmentId == id
                              orderby p.Id
                              select new GovernmentFeePaymentDto(p.Id, p.PaymentNumber, p.AssessmentId, a0.AssessmentNumber, p.ProjectId, x.ProjectCode, p.Amount, p.PaymentDate, m.Name, p.Reference, p.Remarks,
                                  p.Status, p.CreatedBy, p.CreatedAtUtc, p.ReversedBy, p.ReversedAtUtc, p.ReversalReason)).ToListAsync(ct);
        return new GovernmentFeeDetailDto(a0.Id, a0.AssessmentNumber, a0.ProjectId, x.ProjectCode, a0.AgreementId, x.AgreementNumber, a0.CustomerId, x.FullName, a0.FeeTypeId, x.TypeName, x.Kind, x.Method,
            a0.BasisAmount, a0.Amount, a0.CustomerLiability, a0.CompanyBorne, a0.CollectedAmount, a0.CustomerOutstanding, a0.PaidAmount, a0.GovernmentPayable, a0.Status, a0.DueDate, a0.Reference, a0.Remarks,
            a0.CreatedBy, a0.CreatedAtUtc, a0.CancelledBy, a0.CancelledAtUtc, a0.CancelReason, await support.CurrencySymbolAsync(ct), collections, payments, await DocumentsAsync(id, ct), a0.RowVersion.ToToken());
    }
}
