using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Application.Pricing;

/// <summary>Shared lookups for the pricing services: company-time "today", rounding policy, project/property access and pricing inputs.</summary>
public class PricingSupport(IAppDbContext db, ProjectAccessService access, TimeProvider clock)
{
    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tz = await db.Companies.AsNoTracking().Select(c => c.TimeZoneId).FirstOrDefaultAsync(ct);
        try { if (!string.IsNullOrWhiteSpace(tz)) now = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(tz)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return DateOnly.FromDateTime(now);
    }

    public async Task<(int Decimals, RoundingMode Mode)> RoundingAsync(CancellationToken ct = default)
    {
        var c = await db.Companies.AsNoTracking().Select(x => new { x.CurrencyDecimalPlaces, x.RoundingMode }).FirstOrDefaultAsync(ct);
        return c is null ? (2, RoundingMode.Nearest) : (Math.Clamp(c.CurrencyDecimalPlaces, 0, 4), c.RoundingMode);
    }

    /// <summary>Checks access to the property's project. Unknown ids look like inaccessible ones for non-privileged users.</summary>
    public async Task<long> EnsurePropertyAccessAsync(long propertyId, CancellationToken ct = default)
    {
        var projectId = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => (long?)p.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this property.");
            throw new NotFoundException("Property not found.");
        }
        await access.EnsureAccessAsync(projectId.Value, ct);
        return projectId.Value;
    }

    public async Task<Project> WritableProjectAsync(long projectId, CancellationToken ct = default)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        if (p.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
        return p;
    }

    public static bool IsPriceEditable(PropertyStatus s) => s is PropertyStatus.Available or PropertyStatus.Reserved or PropertyStatus.Cancelled;

    public async Task<PricingPropertyInfo> LoadInfoAsync(long propertyId, CancellationToken ct = default)
    {
        var p = await db.Properties.AsNoTracking().Include(x => x.PropertyType).ThenInclude(t => t!.Category).FirstOrDefaultAsync(x => x.Id == propertyId, ct)
                ?? throw new NotFoundException("Property not found.");
        var t = p.PropertyType!;
        return new PricingPropertyInfo(p.Id, p.PropertyCode, p.ProjectId, p.BlockId, p.SectorId, p.StreetId, t.Id, t.CategoryId,
            t.Form == PropertyForm.Commercial || string.Equals(t.Category!.Code, "COMMERCIAL", StringComparison.OrdinalIgnoreCase),
            p.Size, p.MeasurementUnit, p.FloorNumber, p.IsCorner, p.FacesMainRoad, p.FacesPark, p.DevelopmentStatus);
    }

    public static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    public static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Validation of rule targets and conditions against project data.</summary>
public static class PricingTargets
{
    /// <summary>Checks that exactly the target matching the scope is supplied and that it belongs to the project.</summary>
    public static async Task ValidateAsync(IAppDbContext db, long projectId, PricingScope scope, long? blockId, long? streetId, long? categoryId, long? propertyId, long? customerId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Need(bool ok, string field, string msg) { if (!ok) errors[field] = [msg]; }
        Need(scope == PricingScope.Block ? blockId is not null : blockId is null, "BlockId", scope == PricingScope.Block ? "Choose the block." : "A block applies only to block-scope rules.");
        Need(scope == PricingScope.Street ? streetId is not null : streetId is null, "StreetId", scope == PricingScope.Street ? "Choose the street." : "A street applies only to street-scope rules.");
        Need(scope == PricingScope.PropertyCategory ? categoryId is not null : categoryId is null, "CategoryId", scope == PricingScope.PropertyCategory ? "Choose the property category." : "A category applies only to category-scope rules.");
        Need(scope == PricingScope.Property ? propertyId is not null : propertyId is null, "PropertyId", scope == PricingScope.Property ? "Choose the property." : "A property applies only to property-scope rules.");
        Need(scope == PricingScope.Customer ? customerId is > 0 : customerId is null, "CustomerId", scope == PricingScope.Customer ? "A customer is required for customer-negotiated pricing." : "A customer applies only to customer-scope rules.");
        if (errors.Count > 0) throw new RequestValidationException(errors);
        if (blockId is { } b && !await db.ProjectBlocks.AnyAsync(z => z.Id == b && z.ProjectId == projectId, ct)) throw PricingSupport.Invalid("BlockId", "The block does not belong to this project.");
        if (streetId is { } s && !await db.ProjectStreets.AnyAsync(z => z.Id == s && z.ProjectId == projectId, ct)) throw PricingSupport.Invalid("StreetId", "The street does not belong to this project.");
        if (categoryId is { } c && !await db.PropertyCategories.AnyAsync(z => z.Id == c, ct)) throw PricingSupport.Invalid("CategoryId", "Property category not found.");
        if (propertyId is { } p && !await db.Properties.AnyAsync(z => z.Id == p && z.ProjectId == projectId, ct)) throw PricingSupport.Invalid("PropertyId", "The property does not belong to this project.");
    }

    public static async Task ValidateConditionsAsync(IAppDbContext db, CreatePricingRuleRequest r, CancellationToken ct)
    {
        var conds = r.Conditions ?? [];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Key(int i) => $"Conditions[{i}]";
        if (conds.GroupBy(c => c.Field).Any(g => g.Key is PricingConditionField.MinSize or PricingConditionField.MaxSize or PricingConditionField.Corner or PricingConditionField.MainRoad
                                                  or PricingConditionField.Park or PricingConditionField.MinFloor or PricingConditionField.MaxFloor && g.Count() > 1))
            throw PricingSupport.Invalid("Conditions", "Each of minimum/maximum size, floor and corner/road/park may be given only once.");
        for (var i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            if (!Enum.IsDefined(c.Field)) throw PricingSupport.Invalid(Key(i), "Unknown condition.");
            var v = c.Value?.Trim() ?? "";
            switch (c.Field)
            {
                case PricingConditionField.MinSize or PricingConditionField.MaxSize:
                    if (!decimal.TryParse(v, System.Globalization.NumberStyles.Number, inv, out var size) || size <= 0 || decimal.Round(size, 4) != size) throw PricingSupport.Invalid(Key(i), "Size must be a positive number with at most 4 decimals.");
                    if (r.Unit is null) throw PricingSupport.Invalid("Unit", "Size conditions need a measurement unit.");
                    break;
                case PricingConditionField.PropertyType:
                    if (!long.TryParse(v, System.Globalization.NumberStyles.None, inv, out var t) || !await db.PropertyTypes.AnyAsync(x => x.Id == t, ct)) throw PricingSupport.Invalid(Key(i), "Property type not found.");
                    break;
                case PricingConditionField.Sector:
                    if (!long.TryParse(v, System.Globalization.NumberStyles.None, inv, out var sec) || !await db.ProjectSectors.AnyAsync(x => x.Id == sec && x.ProjectId == r.ProjectId, ct)) throw PricingSupport.Invalid(Key(i), "The sector does not belong to this project.");
                    break;
                case PricingConditionField.DevelopmentStatus:
                    if (!Enum.TryParse<DevelopmentStatus>(v, out var ds) || !Enum.IsDefined(ds)) throw PricingSupport.Invalid(Key(i), "Unknown development status.");
                    break;
                case PricingConditionField.Corner or PricingConditionField.MainRoad or PricingConditionField.Park:
                    if (!bool.TryParse(v, out _)) throw PricingSupport.Invalid(Key(i), "Use true or false.");
                    break;
                default:
                    if (!int.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, inv, out var fl) || fl is < -5 or > 200) throw PricingSupport.Invalid(Key(i), "Enter a valid floor number.");
                    break;
            }
        }
        var min = conds.FirstOrDefault(c => c.Field == PricingConditionField.MinSize); var max = conds.FirstOrDefault(c => c.Field == PricingConditionField.MaxSize);
        if (min is not null && max is not null && decimal.Parse(min.Value, inv) > decimal.Parse(max.Value, inv)) throw PricingSupport.Invalid("Conditions", "Minimum size cannot exceed maximum size.");
        if (r.Factor == PricingFactor.DevelopmentStatus && !conds.Any(c => c.Field == PricingConditionField.DevelopmentStatus))
            throw PricingSupport.Invalid("Conditions", "A development-status premium needs at least one development status condition.");
    }
}
