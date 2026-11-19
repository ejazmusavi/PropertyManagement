using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Pricing;

/// <summary>
/// Maker-checker approvals for price overrides, customer-specific discounts, special (customer-negotiated) pricing and pricing exceptions.
/// Nothing takes effect until a different user holding pricing.approve approves; approval creates the immutable adjustment (or activates the customer-scope rule).
/// </summary>
public class PricingApprovalService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, PricingSupport support,
    PricingCalculationService calculator, PropertyPricingService pricing, TimeProvider clock,
    IValidator<RequestPricingApprovalRequest> requestValidator, IValidator<DecidePricingApprovalRequest> decideValidator)
{
    // ---------- queries ----------
    public async Task<PagedResult<ApprovalDto>> ListAsync(ApprovalListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.PricingApprovals.AsNoTracking().Where(a => projects.Any(p => p.Id == a.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(a => a.ProjectId == pid);
        if (r.PropertyId is { } prop) q = q.Where(a => a.PropertyId == prop);
        if (r.Status is { } s) q = q.Where(a => a.Status == s);
        if (r.Type is { } t) q = q.Where(a => a.Type == t);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var term = r.Search.Trim(); q = q.Where(a => a.Reason.Contains(term) || (a.RequestedByName != null && a.RequestedByName.Contains(term))); }
        var total = await q.CountAsync(ct);
        var page = await q.OrderByDescending(a => a.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<ApprovalDto>(await ToDtosAsync(page, ct), r.Page, r.PageSize, total);
    }

    public async Task<ApprovalDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var a = await LoadAsync(id, false, ct);
        await access.EnsureAccessAsync(a.ProjectId, ct);
        return (await ToDtosAsync([a], ct))[0];
    }

    // ---------- commands ----------
    public async Task<ApprovalDto> RequestAsync(RequestPricingApprovalRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingRequest, ct);
        var projectId = await support.EnsurePropertyAccessAsync(r.PropertyId, ct);
        await requestValidator.ValidateOrThrowAsync(r, ct);
        await support.WritableProjectAsync(projectId, ct);
        var status = await db.Properties.AsNoTracking().Where(p => p.Id == r.PropertyId).Select(p => p.Status).FirstAsync(ct);
        if (!PricingSupport.IsPriceEditable(status)) throw new DomainException($"The property is {status}; its price is frozen and cannot be changed. Existing bookings keep their pricing snapshot.");
        var today = await support.TodayAsync(ct);
        var from = r.EffectiveFrom ?? today;
        if (from < today) throw PricingSupport.Invalid("EffectiveFrom", "The effective date cannot be in the past.");
        if (await db.PricingApprovals.AnyAsync(a => a.PropertyId == r.PropertyId && a.Type == r.Type && a.CustomerId == r.CustomerId && a.Status == PricingApprovalStatus.Pending, ct))
            throw new DomainException("A pending request of this type already exists for this property and customer. Decide or cancel it first.");

        var original = (await calculator.ComputeAsync(r.PropertyId, r.CustomerId, null, ct)).FinalPrice;
        var kind = r.Type switch { PricingApprovalType.CustomerDiscount => AdjustmentKind.Discount, PricingApprovalType.PricingException => r.AdjustmentKind!.Value, _ => AdjustmentKind.Override };
        var method = kind == AdjustmentKind.Override ? PricingMethod.FixedAmount : r.Method!.Value;
        decimal proposed = kind switch
        {
            AdjustmentKind.Override => r.Value,
            AdjustmentKind.Discount => Math.Max(0, original - (method == PricingMethod.Percentage ? original * r.Value / 100m : r.Value)),
            _ => original + (method == PricingMethod.Percentage ? original * r.Value / 100m : r.Value),
        };
        var a = new PricingApproval
        {
            Type = r.Type, ProjectId = projectId, PropertyId = r.PropertyId, CustomerId = r.CustomerId, CustomerReference = PricingSupport.N(r.CustomerReference), AdjustmentKind = kind, Method = method,
            Value = r.Value, EffectiveFrom = from, EffectiveTo = r.EffectiveTo, OriginalPrice = original, ProposedPrice = Math.Round(proposed, 2), Reason = r.Reason.Trim(),
            RequestedByUserId = user.UserId!.Value, RequestedByName = user.UserName, RequestedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PricingApprovals.Add(a);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("PricingApproval.Requested", "PricingApproval", a.Id.ToString(),
            NewValues: new { Type = a.Type.ToString(), a.PropertyId, a.CustomerId, Kind = kind.ToString(), Method = method.ToString(), a.Value, a.OriginalPrice, a.ProposedPrice }, Reason: a.Reason), ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync([a], ct))[0];
    }

    public async Task<ApprovalDto> DecideAsync(long id, DecidePricingApprovalRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingApprove, ct);
        await decideValidator.ValidateOrThrowAsync(r, ct);
        var a = await LoadAsync(id, true, ct);
        await access.EnsureAccessAsync(a.ProjectId, ct);
        await support.WritableProjectAsync(a.ProjectId, ct);
        var approver = user.UserId!.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = await support.TodayAsync(ct);
        db.UseRowVersion(a, r.RowVersion);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!r.Approve)
        {
            a.Reject(approver, user.UserName, r.Comment!, now);
            if (a.PricingRuleId is { } rid)
                (await db.PricingRules.FirstAsync(x => x.Id == rid, ct)).Reject(r.Comment!, now);
            await SaveAsync(ct);
            await audit.RecordAsync(new AuditEntry("PricingApproval.Rejected", "PricingApproval", id.ToString(), new { Status = "Pending" }, new { Status = "Rejected" }, r.Comment), ct);
            await tx.CommitAsync(ct);
            return (await ToDtosAsync([a], ct))[0];
        }

        a.Approve(approver, user.UserName, r.Comment, now);
        if (a.PricingRuleId is { } ruleId)
        {
            var rule = await db.PricingRules.FirstAsync(x => x.Id == ruleId, ct);
            if (rule.EffectiveFrom < today) rule.EffectiveFrom = today;
            if (rule.EffectiveTo is { } to && to < rule.EffectiveFrom) throw new DomainException("The rule's effective period has already ended; it cannot be approved.");
            rule.Activate(now);
            await SaveAsync(ct);
        }
        else
        {
            var propertyId = a.PropertyId!.Value;
            var status = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => p.Status).FirstAsync(ct);
            if (!PricingSupport.IsPriceEditable(status)) throw new DomainException($"The property is now {status}; its price is frozen and the request can no longer be approved.");
            var from = a.EffectiveFrom < today ? today : a.EffectiveFrom;
            if (a.EffectiveTo is { } to && to < from) throw new DomainException("The requested period has already ended; the request cannot be approved.");
            var adj = new PropertyPriceAdjustment
            {
                PropertyId = propertyId, ProjectId = a.ProjectId, Kind = a.AdjustmentKind!.Value, Method = a.Method!.Value, Value = a.Value, CustomerId = a.CustomerId, CustomerReference = a.CustomerReference,
                EffectiveFrom = from, EffectiveTo = a.EffectiveTo, Status = AdjustmentStatus.Active, ApprovalId = a.Id, ApprovedAtUtc = now, Reason = a.Reason,
            };
            db.PropertyPriceAdjustments.Add(adj);
            await SaveAsync(ct);
            a.AdjustmentId = adj.Id;
            await SaveAsync(ct);
            await audit.RecordAsync(new AuditEntry("PropertyPriceAdjustment.Created", "PropertyPriceAdjustment", adj.Id.ToString(),
                NewValues: new { adj.PropertyId, Kind = adj.Kind.ToString(), Method = adj.Method.ToString(), adj.Value, adj.CustomerId, adj.EffectiveFrom, adj.EffectiveTo, adj.ApprovalId }, Reason: a.Reason), ct);
            // A property-wide adjustment changes the published price; a customer-specific one only applies when pricing for that customer.
            if (adj.CustomerId is null) await pricing.PublishCoreAsync(propertyId, PriceChangeSource.ApprovalApplied, $"Approval {a.Id} ({a.Type}) applied", a.Id, adj.Id, ct);
        }
        await audit.RecordAsync(new AuditEntry("PricingApproval.Approved", "PricingApproval", id.ToString(), new { Status = "Pending" }, new { Status = "Approved", a.AdjustmentId, a.PricingRuleId }, r.Comment), ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync([a], ct))[0];
    }

    public async Task<ApprovalDto> CancelAsync(long id, CancelPricingApprovalRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) throw new ForbiddenException();
        var a = await LoadAsync(id, true, ct);
        await access.EnsureAccessAsync(a.ProjectId, ct);
        if (a.RequestedByUserId == me) await permissions.EnsureAsync(user, Permissions.PricingRequest, ct);
        else await permissions.EnsureAsync(user, Permissions.PricingApprove, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        db.UseRowVersion(a, r.RowVersion);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        a.Cancel(me, user.UserName, r.Comment, now);
        if (a.PricingRuleId is { } rid) (await db.PricingRules.FirstAsync(x => x.Id == rid, ct)).Reject("Request cancelled", now);
        await SaveAsync(ct);
        await audit.RecordAsync(new AuditEntry("PricingApproval.Cancelled", "PricingApproval", id.ToString(), new { Status = "Pending" }, new { Status = "Cancelled" }, r.Comment), ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync([a], ct))[0];
    }

    // ---------- helpers ----------
    /// <summary>Saves with optimistic concurrency; a racing duplicate decision (unique adjustment per approval) is also a concurrency conflict.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw new ConcurrencyException(); }
    }

    private async Task<PricingApproval> LoadAsync(long id, bool tracked, CancellationToken ct)
    {
        var a = await (tracked ? db.PricingApprovals : db.PricingApprovals.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this request.");
            throw new NotFoundException("Pricing request not found.");
        }
        return a;
    }

    private async Task<IReadOnlyList<ApprovalDto>> ToDtosAsync(IReadOnlyList<PricingApproval> list, CancellationToken ct)
    {
        var pids = list.Select(a => a.ProjectId).Distinct().ToList(); var props = list.Where(a => a.PropertyId != null).Select(a => a.PropertyId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        var codes = await db.Properties.AsNoTracking().Where(p => props.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PropertyCode, ct);
        return list.Select(a => new ApprovalDto(a.Id, a.Type, a.Status, a.ProjectId, projects.GetValueOrDefault(a.ProjectId, ""), a.PropertyId, a.PropertyId is { } p ? codes.GetValueOrDefault(p) : null,
            a.CustomerId, a.CustomerReference, a.PricingRuleId, a.AdjustmentKind, a.Method, a.Value, a.EffectiveFrom, a.EffectiveTo, a.OriginalPrice, a.ProposedPrice, a.Reason, a.RequestedByUserId,
            a.RequestedByName, a.RequestedAtUtc, a.DecidedByName, a.DecidedAtUtc, a.DecisionComment, a.AdjustmentId, a.RowVersion.ToToken())).ToList();
    }
}
