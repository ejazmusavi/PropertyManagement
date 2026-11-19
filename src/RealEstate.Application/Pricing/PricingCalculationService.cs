using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Application.Pricing;

/// <summary>Canonical serialization and hashing of a price calculation, so a stored snapshot can be verified and is never recalculated.</summary>
public static class PricingSnapshot
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(PriceCalculationDto calculation) => JsonSerializer.Serialize(calculation, Options);
    public static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    public static bool Verify(string json, string hash) => string.Equals(Hash(json), hash, StringComparison.OrdinalIgnoreCase);
    public static PriceCalculationDto Deserialize(string json) => JsonSerializer.Deserialize<PriceCalculationDto>(json, Options) ?? throw new DomainException("The pricing snapshot is empty.");
}

/// <summary>
/// The dedicated pricing calculation service. It reads the rule set in force on the calculation date and never touches bookings or agreements:
/// historical bookings keep the immutable snapshot taken when they were made.
/// </summary>
public class PricingCalculationService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, PricingSupport support)
{
    /// <summary>Calculates the price of a property for an optional customer on an optional date (default: today in the company time zone).</summary>
    public async Task<PriceCalculationDto> CalculateAsync(CalculatePriceRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        if (r.PropertyId <= 0) throw PricingSupport.Invalid("PropertyId", "Choose a property.");
        if (r.CustomerId is <= 0) throw PricingSupport.Invalid("CustomerId", "Invalid customer.");
        await support.EnsurePropertyAccessAsync(r.PropertyId, ct);
        return await ComputeAsync(r.PropertyId, r.CustomerId, r.AsOfDate, ct);
    }

    /// <summary>Calculation plus the canonical JSON and SHA-256 hash to store with a booking (Phase 4) or a published price.</summary>
    public async Task<PriceSnapshotDto> CreateSnapshotAsync(CalculatePriceRequest r, CancellationToken ct = default)
    {
        var calc = await CalculateAsync(r, ct);
        var json = PricingSnapshot.Serialize(calc);
        return new PriceSnapshotDto(json, PricingSnapshot.Hash(json), calc);
    }

    /// <summary>Internal calculation without permission checks; callers authorize first.</summary>
    internal async Task<PriceCalculationDto> ComputeAsync(long propertyId, long? customerId, DateOnly? asOf, CancellationToken ct)
    {
        var info = await support.LoadInfoAsync(propertyId, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var (decimals, mode) = await support.RoundingAsync(ct);
        var rules = await db.PricingRules.AsNoTracking().Include(x => x.Conditions)
            .Where(x => x.ProjectId == info.ProjectId && (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= date).ToListAsync(ct);
        var discounts = await db.DiscountRules.AsNoTracking()
            .Where(x => x.ProjectId == info.ProjectId && (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= date).ToListAsync(ct);
        var adjustments = await db.PropertyPriceAdjustments.AsNoTracking().Where(a => a.PropertyId == propertyId && a.EffectiveFrom <= date).ToListAsync(ct);
        var result = PricingEngine.Calculate(info, date, customerId, rules, discounts, adjustments, decimals, mode);
        var projectCode = await db.Projects.AsNoTracking().Where(p => p.Id == info.ProjectId).Select(p => p.ProjectCode).FirstAsync(ct);
        return Map(result, projectCode);
    }

    private static PriceLineDto L(PriceLine l) => new(l.Source, l.SourceId, l.Code, l.Name, l.Scope, l.Kind, l.Factor, l.Method, l.Value, l.Amount);

    private static PriceCalculationDto Map(PriceResult r, string projectCode) => new(PricingSnapshot.Version, r.PropertyId, r.PropertyCode, r.ProjectId, projectCode, r.AsOfDate, r.CustomerId, r.Size, r.Unit,
        r.BaseRate, r.BaseRateUnit, r.BaseMethod, r.BasePrice, r.SizeBasedPrice, r.Premiums.Select(L).ToList(), r.TotalPremiums, r.Discounts.Select(L).ToList(), r.TotalDiscounts,
        r.Charges.Select(L).ToList(), r.TotalCharges, r.ComputedPrice, r.IsOverridden, r.OverridePrice, r.FinalPrice, r.AppliedRules.Select(L).ToList(), r.DecimalPlaces, r.Rounding);
}
