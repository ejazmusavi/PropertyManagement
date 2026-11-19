using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Pricing;

/// <summary>Everything the engine needs to know about the property being priced (resolved by the application layer).</summary>
public sealed record PricingPropertyInfo(long PropertyId, string PropertyCode, long ProjectId, long? BlockId, long? SectorId, long? StreetId, long TypeId, long CategoryId,
    bool IsCommercial, decimal Size, MeasurementUnit Unit, int? Floor, bool IsCorner, bool FacesMainRoad, bool FacesPark, DevelopmentStatus Development);

/// <summary>One line of the calculation: a rule, discount or adjustment that contributed to the price.</summary>
public sealed record PriceLine(string Source, long? SourceId, string Code, string Name, PricingScope? Scope, PricingRuleKind? Kind, PricingFactor? Factor,
    PricingMethod Method, decimal Value, decimal Amount);

public sealed record PriceResult(
    long PropertyId, string PropertyCode, long ProjectId, DateOnly AsOfDate, long? CustomerId, decimal Size, MeasurementUnit Unit,
    decimal BaseRate, MeasurementUnit? BaseRateUnit, PricingMethod BaseMethod, decimal BasePrice, decimal SizeBasedPrice,
    IReadOnlyList<PriceLine> Premiums, decimal TotalPremiums, IReadOnlyList<PriceLine> Discounts, decimal TotalDiscounts,
    IReadOnlyList<PriceLine> Charges, decimal TotalCharges, decimal ComputedPrice, bool IsOverridden, decimal? OverridePrice, decimal FinalPrice,
    IReadOnlyList<PriceLine> AppliedRules, int DecimalPlaces, RoundingMode Rounding);

/// <summary>
/// Deterministic, side-effect-free pricing calculation. Precedence policy (explicit):
/// 1. Scope specificity: Customer &gt; Property &gt; PropertyCategory &gt; Street &gt; Block &gt; Project.
/// 2. Within the same scope: higher Priority, then later EffectiveFrom, then higher Id.
/// Base rates compete as one group. Premiums and additional charges compete per Code, so a more specific rule with the same code replaces the general one.
/// Non-stackable discounts compete (most specific wins); stackable discounts all apply. Approved adjustments are applied last; an approved override replaces the final price.
/// Price = size-based price + premiums - discounts + additional charges (each line rounded by the company rounding policy).
/// </summary>
public static class PricingEngine
{
    public static PriceResult Calculate(PricingPropertyInfo p, DateOnly asOf, long? customerId, IEnumerable<PricingRule> rules, IEnumerable<DiscountRule> discounts,
        IEnumerable<PropertyPriceAdjustment> adjustments, int decimals, RoundingMode rounding)
    {
        decimal R(decimal v) => MoneyRounding.Round(v, decimals, rounding);
        var applicable = rules.Where(r => r.ProjectId == p.ProjectId && r.IsEffectiveOn(asOf) && ScopeApplies(r.Scope, r.BlockId, r.StreetId, r.CategoryId, r.PropertyId, r.CustomerId, p, customerId) && ConditionsMet(r, p))
            .ToList();

        // ---- base rate ----
        var baseRule = Pick(applicable.Where(r => r.Kind == PricingRuleKind.BaseRate))
                       ?? throw new DomainException("No base rate applies to this property on the calculation date. Configure a base rate rule first.");
        decimal baseRate = baseRule.Value, basePrice, sizeBased;
        if (baseRule.Method == PricingMethod.PerUnit)
        {
            var unit = baseRule.Unit ?? throw new DomainException("The base rate rule has no measurement unit.");
            var ratePerPropertyUnit = baseRule.Value * AreaConversion.SquareFeetPer(p.Unit) / AreaConversion.SquareFeetPer(unit);
            basePrice = R(ratePerPropertyUnit);
            sizeBased = R(ratePerPropertyUnit * p.Size);
        }
        else { basePrice = sizeBased = R(baseRule.Value); }
        var lines = new List<PriceLine> { new("Rule", baseRule.Id, baseRule.Code, baseRule.Name, baseRule.Scope, PricingRuleKind.BaseRate, baseRule.Factor, baseRule.Method, baseRule.Value, sizeBased) };

        // ---- premiums and charges: one winner per code ----
        List<PriceLine> Group(PricingRuleKind kind) => applicable.Where(r => r.Kind == kind)
            .GroupBy(r => r.Code.ToUpperInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Pick(g)!).Select(r => new PriceLine("Rule", r.Id, r.Code, r.Name, r.Scope, r.Kind, r.Factor, r.Method, r.Value, R(RuleAmount(r, p, sizeBased)))).ToList();
        var premiums = Group(PricingRuleKind.Premium);
        var totalPremiums = premiums.Sum(l => l.Amount);
        var subtotal = sizeBased + totalPremiums;

        // ---- discounts (cannot exceed the price they discount) ----
        var remaining = subtotal;
        var discountLines = new List<PriceLine>();
        void AddDiscount(string source, long id, string code, string name, PricingScope? scope, PricingMethod method, decimal value)
        {
            var amount = Math.Min(R(method == PricingMethod.Percentage ? subtotal * value / 100m : value), remaining);
            if (amount < 0) amount = 0;
            remaining -= amount;
            discountLines.Add(new PriceLine(source, id, code, name, scope, null, null, method, value, amount));
        }
        var ds = discounts.Where(d => d.ProjectId == p.ProjectId && d.IsEffectiveOn(asOf) && ScopeApplies(d.Scope, d.BlockId, d.StreetId, d.CategoryId, d.PropertyId, null, p, customerId)).ToList();
        var exclusive = ds.Where(d => !d.IsStackable).OrderByDescending(d => d.Scope).ThenByDescending(d => d.Priority).ThenByDescending(d => d.EffectiveFrom).ThenByDescending(d => d.Id).FirstOrDefault();
        if (exclusive is not null) AddDiscount("Discount", exclusive.Id, exclusive.Code, exclusive.Name, exclusive.Scope, exclusive.Method, exclusive.Value);
        foreach (var d in ds.Where(d => d.IsStackable).OrderByDescending(d => d.Scope).ThenByDescending(d => d.Priority).ThenBy(d => d.Id))
            AddDiscount("Discount", d.Id, d.Code, d.Name, d.Scope, d.Method, d.Value);
        var adj = adjustments.Where(a => a.PropertyId == p.PropertyId && a.IsEffectiveOn(asOf) && (a.CustomerId is null || a.CustomerId == customerId)).ToList();
        foreach (var a in adj.Where(a => a.Kind == AdjustmentKind.Discount).OrderBy(a => a.Id))
            AddDiscount("Adjustment", a.Id, $"ADJ-{a.Id}", "Approved discount", null, a.Method, a.Value);
        var totalDiscounts = discountLines.Sum(l => l.Amount);

        // ---- additional charges (not discounted) ----
        var charges = Group(PricingRuleKind.AdditionalCharge);
        foreach (var a in adj.Where(a => a.Kind == AdjustmentKind.Surcharge).OrderBy(a => a.Id))
            charges.Add(new PriceLine("Adjustment", a.Id, $"ADJ-{a.Id}", "Approved surcharge", null, null, null, a.Method, a.Value,
                R(a.Method == PricingMethod.Percentage ? sizeBased * a.Value / 100m : a.Value)));
        var totalCharges = charges.Sum(l => l.Amount);

        var computed = subtotal - totalDiscounts + totalCharges;

        // ---- approved override replaces the final price (customer-specific first, then newest approval) ----
        var ov = adj.Where(a => a.Kind == AdjustmentKind.Override).OrderByDescending(a => a.CustomerId != null).ThenByDescending(a => a.ApprovedAtUtc).ThenByDescending(a => a.Id).FirstOrDefault();
        decimal? overridePrice = ov is null ? null : R(ov.Value);
        var final = overridePrice ?? computed;

        var applied = new List<PriceLine>(lines);
        applied.AddRange(premiums); applied.AddRange(discountLines); applied.AddRange(charges);
        if (ov is not null) applied.Add(new PriceLine("Adjustment", ov.Id, $"ADJ-{ov.Id}", "Approved price override", null, null, null, PricingMethod.FixedAmount, ov.Value, overridePrice!.Value - computed));

        return new PriceResult(p.PropertyId, p.PropertyCode, p.ProjectId, asOf, customerId, p.Size, p.Unit, baseRate, baseRule.Method == PricingMethod.PerUnit ? baseRule.Unit : null, baseRule.Method,
            basePrice, sizeBased, premiums, totalPremiums, discountLines, totalDiscounts, charges, totalCharges, computed, ov is not null, overridePrice, final, applied, decimals, rounding);
    }

    private static decimal RuleAmount(PricingRule r, PricingPropertyInfo p, decimal sizeBased) => r.Method switch
    {
        PricingMethod.FixedAmount => r.Value,
        PricingMethod.Percentage => sizeBased * r.Value / 100m,
        _ => r.Value * AreaConversion.Convert(p.Size, p.Unit, r.Unit ?? p.Unit),
    };

    private static PricingRule? Pick(IEnumerable<PricingRule> rules) =>
        rules.OrderByDescending(r => r.Scope).ThenByDescending(r => r.Priority).ThenByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).FirstOrDefault();

    public static bool ScopeApplies(PricingScope scope, long? block, long? street, long? category, long? property, long? customer, PricingPropertyInfo p, long? customerId) => scope switch
    {
        PricingScope.Project => true,
        PricingScope.Block => block is not null && block == p.BlockId,
        PricingScope.Street => street is not null && street == p.StreetId,
        PricingScope.PropertyCategory => category is not null && category == p.CategoryId,
        PricingScope.Property => property is not null && property == p.PropertyId,
        PricingScope.Customer => customer is not null && customer == customerId,
        _ => false,
    };

    /// <summary>Factor-implied conditions (corner, road, park, commercial) plus explicit conditions. Repeated type/sector/development conditions are alternatives (OR); all others must hold.</summary>
    public static bool ConditionsMet(PricingRule r, PricingPropertyInfo p)
    {
        if (r.Factor == PricingFactor.Corner && !p.IsCorner) return false;
        if (r.Factor == PricingFactor.RoadFacing && !p.FacesMainRoad) return false;
        if (r.Factor == PricingFactor.ParkFacing && !p.FacesPark) return false;
        if (r.Factor == PricingFactor.Commercial && !p.IsCommercial) return false;
        foreach (var group in r.Conditions.GroupBy(c => c.Field))
        {
            bool Test(PricingRuleCondition c) => c.Field switch
            {
                PricingConditionField.MinSize => AreaConversion.Convert(p.Size, p.Unit, r.Unit ?? p.Unit) >= Dec(c.Value),
                PricingConditionField.MaxSize => AreaConversion.Convert(p.Size, p.Unit, r.Unit ?? p.Unit) <= Dec(c.Value),
                PricingConditionField.PropertyType => long.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture) == p.TypeId,
                PricingConditionField.Sector => p.SectorId is not null && long.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture) == p.SectorId,
                PricingConditionField.DevelopmentStatus => Enum.Parse<DevelopmentStatus>(c.Value) == p.Development,
                PricingConditionField.Corner => bool.Parse(c.Value) == p.IsCorner,
                PricingConditionField.MainRoad => bool.Parse(c.Value) == p.FacesMainRoad,
                PricingConditionField.Park => bool.Parse(c.Value) == p.FacesPark,
                PricingConditionField.MinFloor => p.Floor is { } f && f >= int.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture),
                PricingConditionField.MaxFloor => p.Floor is { } f2 && f2 <= int.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture),
                _ => false,
            };
            var anyOf = group.Key is PricingConditionField.PropertyType or PricingConditionField.Sector or PricingConditionField.DevelopmentStatus;
            if (anyOf ? !group.Any(Test) : !group.All(Test)) return false;
        }
        return true;
    }

    private static decimal Dec(string v) => decimal.Parse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
}
