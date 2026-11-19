using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Pricing;

/// <summary>Published property prices, price history (append-only) and approved adjustments.</summary>
public class PropertyPricingService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, PricingSupport support,
    PricingCalculationService calculator, TimeProvider clock)
{
    // ---------- queries ----------
    public async Task<PropertyPriceViewDto> GetPriceAsync(long propertyId, long? customerId, DateOnly? asOf, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        if (customerId is <= 0) throw PricingSupport.Invalid("CustomerId", "Invalid customer.");
        await support.EnsurePropertyAccessAsync(propertyId, ct);
        var p = await db.Properties.AsNoTracking().FirstAsync(x => x.Id == propertyId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        var published = await db.PropertyPrices.AsNoTracking().FirstOrDefaultAsync(x => x.PropertyId == propertyId, ct);
        PriceCalculationDto? calc = null; string? error = null;
        try { calc = await calculator.ComputeAsync(propertyId, customerId, asOf, ct); }
        catch (DomainException ex) when (ex is not ForbiddenException and not NotFoundException) { error = ex.Message; }
        return new PropertyPriceViewDto(p.Id, p.PropertyCode, project.Id, project.ProjectCode, project.ProjectName, p.Status, !PricingSupport.IsPriceEditable(p.Status), published is null ? null : ToDto(published),
            calc, error, await AdjustmentsAsync(propertyId, ct));
    }

    public async Task<PagedResult<PropertyPriceListItemDto>> ListPropertyPricesAsync(PropertyPriceListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var props = db.Properties.AsNoTracking().Where(p => projects.Any(pr => pr.Id == p.ProjectId));
        if (r.ProjectId is { } pid) props = props.Where(p => p.ProjectId == pid);
        if (r.Status is { } s) props = props.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); props = props.Where(p => p.PropertyCode.Contains(t) || p.PropertyNumber.Contains(t)); }
        var q = from p in props
                join pp in db.PropertyPrices.AsNoTracking() on p.Id equals pp.PropertyId into g
                from pp in g.DefaultIfEmpty()
                select new { p, pp };
        if (r.Published is { } pub) q = pub ? q.Where(x => x.pp != null) : q.Where(x => x.pp == null);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.p.PropertyCode).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(x => new { x.p.Id, x.p.ProjectId, x.p.PropertyCode, x.p.PropertyNumber, Type = x.p.PropertyType!.Name, x.p.Size, x.p.MeasurementUnit, x.p.Status,
                Price = x.pp == null ? (decimal?)null : x.pp.FinalPrice, At = x.pp == null ? (DateTime?)null : x.pp.PublishedAtUtc, Over = x.pp != null && x.pp.IsOverridden }).ToListAsync(ct);
        var codes = await db.Projects.AsNoTracking().Where(x => rows.Select(y => y.ProjectId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ProjectCode, ct);
        return new PagedResult<PropertyPriceListItemDto>(rows.Select(x => new PropertyPriceListItemDto(x.Id, x.ProjectId, codes.GetValueOrDefault(x.ProjectId, ""), x.PropertyCode, x.PropertyNumber, x.Type,
            x.Size, x.MeasurementUnit, x.Status, x.Price, x.At, x.Over)).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<PagedResult<PriceHistoryDto>> ListHistoryAsync(PriceHistoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        if (r.PropertyId is { } one) await support.EnsurePropertyAccessAsync(one, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.PropertyPriceHistories.AsNoTracking().Where(h => projects.Any(p => p.Id == h.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(h => h.ProjectId == pid);
        if (r.PropertyId is { } prop) q = q.Where(h => h.PropertyId == prop);
        if (r.Source is { } src) q = q.Where(h => h.Source == src);
        if (r.From is { } from) { var f = from.ToDateTime(TimeOnly.MinValue); q = q.Where(h => h.ChangedAtUtc >= f); }
        if (r.To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(h => h.ChangedAtUtc < t); }
        if (!string.IsNullOrWhiteSpace(r.Search)) { var term = r.Search.Trim(); q = q.Where(h => h.Reason.Contains(term) || db.Properties.Any(p => p.Id == h.PropertyId && p.PropertyCode.Contains(term))); }
        var total = await q.CountAsync(ct);
        var page = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        var codes = await db.Properties.AsNoTracking().Where(p => page.Select(h => h.PropertyId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PropertyCode, ct);
        return new PagedResult<PriceHistoryDto>(page.Select(h => new PriceHistoryDto(h.Id, h.PropertyId, codes.GetValueOrDefault(h.PropertyId, ""), h.PreviousPrice, h.NewPrice, h.Source, h.Reason,
            h.ChangedAtUtc, h.ChangedBy, h.SnapshotHash, h.ApprovalId, h.AdjustmentId)).ToList(), r.Page, r.PageSize, total);
    }

    /// <summary>The stored snapshot of a history entry, verified against its hash (proves it was never altered or recalculated).</summary>
    public async Task<PriceSnapshotDto> GetHistorySnapshotAsync(long historyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var h = await db.PropertyPriceHistories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == historyId, ct) ?? throw new NotFoundException("History entry not found.");
        await access.EnsureAccessAsync(h.ProjectId, ct);
        if (!PricingSnapshot.Verify(h.SnapshotJson, h.SnapshotHash)) throw new DomainException("The stored pricing snapshot failed its integrity check.");
        return new PriceSnapshotDto(h.SnapshotJson, h.SnapshotHash, PricingSnapshot.Deserialize(h.SnapshotJson));
    }

    public async Task<IReadOnlyList<AdjustmentDto>> ListAdjustmentsAsync(long propertyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        await support.EnsurePropertyAccessAsync(propertyId, ct);
        return await AdjustmentsAsync(propertyId, ct);
    }

    private async Task<IReadOnlyList<AdjustmentDto>> AdjustmentsAsync(long propertyId, CancellationToken ct)
    {
        var code = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => p.PropertyCode).FirstAsync(ct);
        return (await db.PropertyPriceAdjustments.AsNoTracking().Where(a => a.PropertyId == propertyId).OrderByDescending(a => a.Id).ToListAsync(ct))
            .Select(a => new AdjustmentDto(a.Id, a.PropertyId, code, a.Kind, a.Method, a.Value, a.CustomerId, a.CustomerReference, a.EffectiveFrom, a.EffectiveTo, a.Status, a.ApprovalId, a.Reason,
                a.ApprovedAtUtc, a.RevokedAtUtc, a.RevokedBy, a.RevokeReason, a.RowVersion.ToToken())).ToList();
    }

    // ---------- publishing ----------
    /// <summary>Calculates the property's list price now and publishes it as the current price. Idempotent: an unchanged price creates no history.</summary>
    public async Task<PublishOutcomeDto> PublishAsync(long propertyId, PublishPriceRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        var projectId = await support.EnsurePropertyAccessAsync(propertyId, ct);
        await support.WritableProjectAsync(projectId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var outcome = await PublishCoreAsync(propertyId, PriceChangeSource.Published, PricingSupport.N(r.Reason) ?? "Price published", null, null, ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Publishes prices for every editable property of a project. Sold/booked properties are frozen; properties that cannot be priced are reported, not failed.</summary>
    public async Task<PublishProjectResultDto> PublishProjectAsync(long projectId, PublishPriceRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await support.WritableProjectAsync(projectId, ct);
        var props = await db.Properties.AsNoTracking().Where(p => p.ProjectId == projectId).OrderBy(p => p.PropertyCode).Select(p => new { p.Id, p.PropertyCode, p.Status }).ToListAsync(ct);
        int published = 0, unchanged = 0, frozen = 0; var skipped = new List<PublishSkippedDto>();
        foreach (var p in props)
        {
            if (!PricingSupport.IsPriceEditable(p.Status)) { frozen++; continue; }
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var o = await PublishCoreAsync(p.Id, PriceChangeSource.Published, PricingSupport.N(r.Reason) ?? "Project prices published", null, null, ct);
                await tx.CommitAsync(ct);
                if (o.Changed) published++; else unchanged++;
            }
            catch (DomainException ex) when (ex is not ForbiddenException) { skipped.Add(new PublishSkippedDto(p.PropertyCode, ex.Message)); }
        }
        return new PublishProjectResultDto(published, unchanged, frozen, skipped);
    }

    /// <summary>Calculates (no customer, today) and stores the price inside the caller's transaction; used by publishing and by approvals.</summary>
    internal async Task<PublishOutcomeDto> PublishCoreAsync(long propertyId, PriceChangeSource source, string reason, long? approvalId, long? adjustmentId, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().FirstAsync(p => p.Id == propertyId, ct);
        if (!PricingSupport.IsPriceEditable(property.Status))
            throw new DomainException($"The price of property {property.PropertyCode} is frozen because it is {property.Status}. Existing bookings keep their pricing snapshot.");
        var calc = await calculator.ComputeAsync(propertyId, null, null, ct);
        var json = PricingSnapshot.Serialize(calc);
        var hash = PricingSnapshot.Hash(json);
        var existing = await db.PropertyPrices.FirstOrDefaultAsync(x => x.PropertyId == propertyId, ct);
        if (existing is not null && ContentKey(PricingSnapshot.Deserialize(existing.SnapshotJson)) == ContentKey(calc)) return new PublishOutcomeDto(false, ToDto(existing));

        var now = clock.GetUtcNow().UtcDateTime;
        var previous = existing?.FinalPrice;
        var added = new List<object>();
        if (existing is null) { existing = new PropertyPrice { PropertyId = propertyId, ProjectId = property.ProjectId }; db.PropertyPrices.Add(existing); added.Add(existing); }
        existing.BaseRate = calc.BaseMethod == PricingMethod.PerUnit ? calc.BaseRate : null; existing.RateUnit = calc.BaseRateUnit; existing.BasePrice = calc.BasePrice; existing.SizeBasedPrice = calc.SizeBasedPrice;
        existing.TotalPremiums = calc.TotalPremiums; existing.TotalDiscounts = calc.TotalDiscounts; existing.TotalCharges = calc.TotalCharges; existing.FinalPrice = calc.FinalPrice;
        existing.IsOverridden = calc.IsOverridden; existing.AsOfDate = calc.AsOfDate; existing.SnapshotJson = json; existing.SnapshotHash = hash; existing.PublishedAtUtc = now; existing.PublishedBy = user.UserName;
        var history = new PropertyPriceHistory
        {
            PropertyId = propertyId, ProjectId = property.ProjectId, PreviousPrice = previous, NewPrice = calc.FinalPrice, Source = source, Reason = reason, ChangedAtUtc = now,
            ChangedByUserId = user.UserId, ChangedBy = user.UserName, SnapshotHash = hash, SnapshotJson = json, ApprovalId = approvalId, AdjustmentId = adjustmentId,
        };
        db.PropertyPriceHistories.Add(history); added.Add(history);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { Detach(existing, added); throw new ConcurrencyException(); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true) { Detach(existing, added); throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry("PropertyPrice.Published", "Property", propertyId.ToString(), new { FinalPrice = previous }, new { calc.FinalPrice, calc.IsOverridden, Hash = hash, Source = source.ToString() }, reason), ct);
        return new PublishOutcomeDto(true, ToDto(existing));
    }

    private void Detach(PropertyPrice price, List<object> added)
    {
        foreach (var o in added) db.Entry(o).State = EntityState.Detached;
        if (db.Entry(price).State != EntityState.Detached) db.Entry(price).State = EntityState.Detached;
    }

    /// <summary>Price content independent of the calculation date, used to avoid re-publishing an identical price every day.</summary>
    private static string ContentKey(PriceCalculationDto c) => PricingSnapshot.Hash(PricingSnapshot.Serialize(c with { AsOfDate = default }));

    // ---------- adjustments ----------
    public async Task<AdjustmentDto> RevokeAdjustmentAsync(long id, RetireRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingApprove, ct);
        var a = await db.PropertyPriceAdjustments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this adjustment.");
            throw new NotFoundException("Adjustment not found.");
        }
        await access.EnsureAccessAsync(a.ProjectId, ct);
        await support.WritableProjectAsync(a.ProjectId, ct);
        var status = await db.Properties.AsNoTracking().Where(p => p.Id == a.PropertyId).Select(p => p.Status).FirstAsync(ct);
        if (!PricingSupport.IsPriceEditable(status)) throw new DomainException($"The property is {status}; its price is frozen and the adjustment can no longer be revoked.");
        db.UseRowVersion(a, r.RowVersion);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = new { Status = a.Status.ToString(), a.EffectiveTo };
        a.Revoke(r.Reason ?? "", user.UserName ?? "unknown", await support.TodayAsync(ct), clock.GetUtcNow().UtcDateTime);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PropertyPriceAdjustment.Revoked", "PropertyPriceAdjustment", id.ToString(), before, new { Status = a.Status.ToString(), a.EffectiveTo }, a.RevokeReason), ct);
        await PublishCoreAsync(a.PropertyId, PriceChangeSource.AdjustmentRevoked, $"Adjustment {a.Id} revoked: {a.RevokeReason}", a.ApprovalId, a.Id, ct);
        await tx.CommitAsync(ct);
        return (await AdjustmentsAsync(a.PropertyId, ct)).First(x => x.Id == id);
    }

    internal static PropertyPriceDto ToDto(PropertyPrice p) => new(p.Id, p.PropertyId, p.BaseRate, p.RateUnit, p.BasePrice, p.SizeBasedPrice, p.TotalPremiums, p.TotalDiscounts, p.TotalCharges,
        p.FinalPrice, p.IsOverridden, p.AsOfDate, p.SnapshotHash, p.PublishedAtUtc, p.PublishedBy, p.RowVersion.ToToken());
}
