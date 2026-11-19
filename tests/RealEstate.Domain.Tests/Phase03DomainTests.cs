using System.Text.Json;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Domain.Tests;

public class Phase03DomainTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static long nextId = 1;

    private static PricingPropertyInfo Prop(decimal size = 5m, MeasurementUnit unit = MeasurementUnit.Marla, bool corner = false, bool road = false, bool park = false, bool commercial = false,
        long? block = 10, long? street = 20, long category = 30, long type = 40, long? sector = null, int? floor = null, DevelopmentStatus dev = DevelopmentStatus.Developed) =>
        new(100, "P-100", 1, block, sector, street, type, category, commercial, size, unit, floor, corner, road, park, dev);

    private static PricingRule Rule(PricingRuleKind kind, PricingMethod method, decimal value, PricingScope scope = PricingScope.Project, string code = "BASE", PricingFactor factor = PricingFactor.Size,
        MeasurementUnit? unit = MeasurementUnit.Marla, int priority = 0, DateOnly? from = null, DateOnly? to = null, PricingRuleStatus status = PricingRuleStatus.Active,
        long? block = null, long? street = null, long? category = null, long? property = null, long? customer = null, (PricingConditionField, string)[]? conditions = null)
    {
        var r = new PricingRule
        {
            Id = nextId++, ProjectId = 1, Code = code, Name = code, Kind = kind, Factor = factor, Scope = scope, BlockId = block, StreetId = street, CategoryId = category, PropertyId = property,
            CustomerId = customer, Method = method, Value = value, Unit = unit, Priority = priority, EffectiveFrom = from ?? Today.AddDays(-10), EffectiveTo = to, Status = status,
        };
        foreach (var (f, v) in conditions ?? []) r.Conditions.Add(new PricingRuleCondition { Field = f, Value = v });
        return r;
    }

    private static PricingRule Base(decimal rate, PricingScope scope = PricingScope.Project, int priority = 0, DateOnly? from = null, long? block = null, long? street = null, long? category = null, long? property = null, long? customer = null) =>
        Rule(PricingRuleKind.BaseRate, PricingMethod.PerUnit, rate, scope, "BASE", priority: priority, from: from, block: block, street: street, category: category, property: property, customer: customer);

    private static DiscountRule Disc(PricingMethod method, decimal value, PricingScope scope = PricingScope.Project, bool stackable = false, int priority = 0, long? block = null) => new()
    {
        Id = nextId++, ProjectId = 1, Code = "D" + nextId, Name = "Disc", Scope = scope, BlockId = block, Method = method, Value = value, IsStackable = stackable, Priority = priority,
        EffectiveFrom = Today.AddDays(-10), Status = PricingRuleStatus.Active,
    };

    private static PropertyPriceAdjustment Adj(AdjustmentKind kind, PricingMethod method, decimal value, long? customer = null, DateOnly? from = null, DateOnly? to = null, DateTime? approved = null) => new()
    {
        Id = nextId++, PropertyId = 100, ProjectId = 1, Kind = kind, Method = method, Value = value, CustomerId = customer, EffectiveFrom = from ?? Today.AddDays(-1), EffectiveTo = to,
        ApprovedAtUtc = approved ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Status = AdjustmentStatus.Active, ApprovalId = nextId++, Reason = "test",
    };

    private static PriceResult Calc(PricingPropertyInfo p, IEnumerable<PricingRule> rules, IEnumerable<DiscountRule>? discounts = null, IEnumerable<PropertyPriceAdjustment>? adj = null, long? customer = null, DateOnly? asOf = null) =>
        PricingEngine.Calculate(p, asOf ?? Today, customer, rules, discounts ?? [], adj ?? [], 2, RoundingMode.Nearest);

    // ---------- rounding and units ----------
    [Theory]
    [InlineData(RoundingMode.Nearest, 1.005, 1.01)] [InlineData(RoundingMode.Bankers, 1.005, 1.00)] [InlineData(RoundingMode.Bankers, 1.015, 1.02)]
    [InlineData(RoundingMode.Up, 1.001, 1.01)] [InlineData(RoundingMode.Down, 1.999, 1.99)]
    public void Rounding_FollowsConfiguredMode(RoundingMode mode, double value, double expected) =>
        Assert.Equal((decimal)expected, MoneyRounding.Round((decimal)value, 2, mode));

    [Fact] public void Kanal_Is20Marla() => Assert.Equal(20m, AreaConversion.Convert(1m, MeasurementUnit.Kanal, MeasurementUnit.Marla));
    [Fact] public void Marla_Is272_25SquareFeet() => Assert.Equal(272.25m, AreaConversion.Convert(1m, MeasurementUnit.Marla, MeasurementUnit.SquareFeet));
    [Fact] public void SameUnit_IsIdentity() => Assert.Equal(7.5m, AreaConversion.Convert(7.5m, MeasurementUnit.Acre, MeasurementUnit.Acre));

    // ---------- base price ----------
    [Fact]
    public void BaseRate_PerUnit_MultipliesBySize()
    {
        var r = Calc(Prop(5m), [Base(1_000_000m)]);
        Assert.Equal(1_000_000m, r.BasePrice);
        Assert.Equal(5_000_000m, r.SizeBasedPrice);
        Assert.Equal(5_000_000m, r.FinalPrice);
        Assert.Single(r.AppliedRules);
    }

    [Fact]
    public void BaseRate_ConvertsBetweenUnits()
    {
        // 1 kanal property priced by a per-marla rate = 20 marla.
        var r = Calc(Prop(1m, MeasurementUnit.Kanal), [Base(1_000_000m)]);
        Assert.Equal(20_000_000m, r.SizeBasedPrice);
        Assert.Equal(20_000_000m, r.BasePrice);
    }

    [Fact]
    public void BaseRate_FixedAmount_IsTheTotalPrice()
    {
        var r = Calc(Prop(5m), [Rule(PricingRuleKind.BaseRate, PricingMethod.FixedAmount, 7_250_000m, unit: null)]);
        Assert.Equal(7_250_000m, r.SizeBasedPrice);
        Assert.Equal(7_250_000m, r.FinalPrice);
    }

    [Fact]
    public void NoBaseRate_Throws() => Assert.Throws<DomainException>(() => Calc(Prop(), []));

    [Fact]
    public void SizeBandRates_PickTheMatchingBand()
    {
        var small = Rule(PricingRuleKind.BaseRate, PricingMethod.PerUnit, 1_000_000m, priority: 0, conditions: [(PricingConditionField.MaxSize, "5")]);
        var large = Rule(PricingRuleKind.BaseRate, PricingMethod.PerUnit, 900_000m, priority: 0, conditions: [(PricingConditionField.MinSize, "5.0001")]);
        Assert.Equal(5_000_000m, Calc(Prop(5m), [small, large]).FinalPrice);
        Assert.Equal(9_000_000m, Calc(Prop(10m), [small, large]).FinalPrice);
    }

    // ---------- hierarchy / precedence ----------
    [Fact]
    public void MoreSpecificScope_OverridesGeneral()
    {
        var p = Prop();
        var rules = new List<PricingRule> { Base(1_000_000m) };
        Assert.Equal(1_000_000m, Calc(p, rules).BasePrice);
        rules.Add(Base(1_100_000m, PricingScope.Block, block: 10)); Assert.Equal(1_100_000m, Calc(p, rules).BasePrice);
        rules.Add(Base(1_200_000m, PricingScope.Street, street: 20)); Assert.Equal(1_200_000m, Calc(p, rules).BasePrice);
        rules.Add(Base(1_300_000m, PricingScope.PropertyCategory, category: 30)); Assert.Equal(1_300_000m, Calc(p, rules).BasePrice);
        rules.Add(Base(1_400_000m, PricingScope.Property, property: 100)); Assert.Equal(1_400_000m, Calc(p, rules).BasePrice);
        rules.Add(Base(1_500_000m, PricingScope.Customer, customer: 7));
        Assert.Equal(1_400_000m, Calc(p, rules).BasePrice);                    // no customer supplied: not applicable
        Assert.Equal(1_400_000m, Calc(p, rules, customer: 8).BasePrice);       // another customer: not applicable
        Assert.Equal(1_500_000m, Calc(p, rules, customer: 7).BasePrice);       // negotiated customer rate wins
    }

    [Fact]
    public void RulesForOtherBlocksStreetsCategoriesAndProperties_DoNotApply()
    {
        var rules = new[] { Base(1m), Base(9m, PricingScope.Block, block: 99), Base(9m, PricingScope.Street, street: 99), Base(9m, PricingScope.PropertyCategory, category: 99), Base(9m, PricingScope.Property, property: 99) };
        Assert.Equal(1m, Calc(Prop(1m), rules).BasePrice);
    }

    [Fact]
    public void SameScope_TieBreak_Priority_ThenNewestStart_ThenHighestId()
    {
        var a = Base(1m, priority: 1); var b = Base(2m, priority: 5); Assert.Equal(2m, Calc(Prop(1m), [a, b]).BasePrice);
        var older = Base(3m, from: Today.AddDays(-9)); var newer = Base(4m, from: Today.AddDays(-2)); Assert.Equal(4m, Calc(Prop(1m), [older, newer]).BasePrice);
        var x = Base(5m); var y = Base(6m); Assert.Equal(6m, Calc(Prop(1m), [x, y]).BasePrice);
        Assert.Equal(6m, Calc(Prop(1m), [y, x]).BasePrice); // input order does not matter
    }

    // ---------- effective dates and status ----------
    [Fact]
    public void RuleOutsideItsWindow_DoesNotApply()
    {
        var future = Base(9m, from: Today.AddDays(1)); var expired = Rule(PricingRuleKind.BaseRate, PricingMethod.PerUnit, 8m, from: Today.AddDays(-20), to: Today.AddDays(-1));
        Assert.Equal(1m, Calc(Prop(1m), [Base(1m), future, expired]).BasePrice);
        Assert.Equal(8m, Calc(Prop(1m), [Base(1m, from: Today.AddDays(-30)), expired], asOf: Today.AddDays(-5)).BasePrice); // inside the old rule window it is still the newest
    }

    [Fact]
    public void RetiredRule_StaysEffectiveInsideItsClosedWindow_SoPastPricesAreReproducible()
    {
        var rule = Base(5m, from: Today.AddDays(-30));
        rule.Retire("replaced", Today, DateTime.UtcNow);
        Assert.Equal(Today.AddDays(-1), rule.EffectiveTo);
        Assert.Equal(5m, Calc(Prop(1m), [rule], asOf: Today.AddDays(-3)).BasePrice);
        Assert.Throws<DomainException>(() => Calc(Prop(1m), [rule], asOf: Today));
    }

    [Theory]
    [InlineData(PricingRuleStatus.PendingApproval)] [InlineData(PricingRuleStatus.Rejected)]
    public void PendingOrRejectedRules_NeverApply(PricingRuleStatus status) =>
        Assert.Throws<DomainException>(() => Calc(Prop(), [Rule(PricingRuleKind.BaseRate, PricingMethod.PerUnit, 1m, status: status)]));

    // ---------- premiums ----------
    [Fact]
    public void CornerPremium_AppliesOnlyToCornerProperties_AsPercentOfSizeBasedPrice()
    {
        var corner = Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 10m, code: "CORNER", factor: PricingFactor.Corner);
        Assert.Equal(5_000_000m, Calc(Prop(5m, corner: false), [Base(1_000_000m), corner]).FinalPrice);
        var r = Calc(Prop(5m, corner: true), [Base(1_000_000m), corner]);
        Assert.Equal(500_000m, r.TotalPremiums);
        Assert.Equal(5_500_000m, r.FinalPrice);
        Assert.Single(r.Premiums);
    }

    [Fact]
    public void RoadParkAndCommercialPremiums_FollowTheirFactor()
    {
        var rules = new[]
        {
            Base(1_000_000m),
            Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 100_000m, code: "ROAD", factor: PricingFactor.RoadFacing, unit: null),
            Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 50_000m, code: "PARK", factor: PricingFactor.ParkFacing, unit: null),
            Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 20m, code: "COMM", factor: PricingFactor.Commercial),
        };
        Assert.Equal(5_000_000m, Calc(Prop(5m), rules).FinalPrice);
        Assert.Equal(5_100_000m, Calc(Prop(5m, road: true), rules).FinalPrice);
        Assert.Equal(5_150_000m, Calc(Prop(5m, road: true, park: true), rules).FinalPrice);
        Assert.Equal(6_000_000m, Calc(Prop(5m, commercial: true), rules).FinalPrice);
    }

    [Fact]
    public void PerUnitPremium_ScalesWithSize() =>
        Assert.Equal(5_000_000m + 5 * 20_000m, Calc(Prop(5m, corner: true), [Base(1_000_000m), Rule(PricingRuleKind.Premium, PricingMethod.PerUnit, 20_000m, code: "C", factor: PricingFactor.Corner)]).FinalPrice);

    [Fact]
    public void SameCodePremium_MoreSpecificScopeReplacesTheGeneralOne_DifferentCodesStack()
    {
        var general = Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 10m, code: "CORNER", factor: PricingFactor.Corner);
        var block = Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 15m, PricingScope.Block, "CORNER", PricingFactor.Corner, block: 10);
        var other = Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 25_000m, code: "VIEW", factor: PricingFactor.Other, unit: null);
        var r = Calc(Prop(5m, corner: true), [Base(1_000_000m), general, block, other]);
        Assert.Equal(2, r.Premiums.Count);
        Assert.Equal(750_000m + 25_000m, r.TotalPremiums);
        Assert.Contains(r.Premiums, l => l.SourceId == block.Id);
        Assert.DoesNotContain(r.Premiums, l => l.SourceId == general.Id);
    }

    [Fact]
    public void DevelopmentStatusPremium_UsesItsCondition()
    {
        var dev = Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 5m, code: "DEV", factor: PricingFactor.DevelopmentStatus, conditions: [(PricingConditionField.DevelopmentStatus, "Developed")]);
        Assert.Equal(5_250_000m, Calc(Prop(5m, dev: DevelopmentStatus.Developed), [Base(1_000_000m), dev]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m, dev: DevelopmentStatus.Undeveloped), [Base(1_000_000m), dev]).FinalPrice);
    }

    [Fact]
    public void Conditions_TypeAlternatives_Floor_AndSector()
    {
        var typeRule = Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 1000m, code: "T", factor: PricingFactor.Other, unit: null,
            conditions: [(PricingConditionField.PropertyType, "40"), (PricingConditionField.PropertyType, "41")]);
        Assert.Equal(5_001_000m, Calc(Prop(5m, type: 41), [Base(1_000_000m), typeRule]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m, type: 42), [Base(1_000_000m), typeRule]).FinalPrice);
        var floor = Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 500m, code: "F", factor: PricingFactor.Other, unit: null, conditions: [(PricingConditionField.MinFloor, "2"), (PricingConditionField.MaxFloor, "4")]);
        Assert.Equal(5_000_500m, Calc(Prop(5m, floor: 3), [Base(1_000_000m), floor]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m, floor: 5), [Base(1_000_000m), floor]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m, floor: null), [Base(1_000_000m), floor]).FinalPrice);
        var sector = Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 700m, code: "S", factor: PricingFactor.Location, unit: null, conditions: [(PricingConditionField.Sector, "9")]);
        Assert.Equal(5_000_700m, Calc(Prop(5m, sector: 9), [Base(1_000_000m), sector]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m, sector: 8), [Base(1_000_000m), sector]).FinalPrice);
    }

    // ---------- discounts ----------
    [Fact]
    public void ExclusiveDiscounts_OnlyTheMostSpecificApplies()
    {
        var project = Disc(PricingMethod.Percentage, 5m); var block = Disc(PricingMethod.Percentage, 8m, PricingScope.Block, block: 10);
        var r = Calc(Prop(5m), [Base(1_000_000m)], [project, block]);
        Assert.Single(r.Discounts);
        Assert.Equal(400_000m, r.TotalDiscounts);
        Assert.Equal(4_600_000m, r.FinalPrice);
    }

    [Fact]
    public void StackableDiscounts_AllApply_AsPercentOfPriceBeforeDiscount()
    {
        var r = Calc(Prop(5m), [Base(1_000_000m)], [Disc(PricingMethod.Percentage, 5m, stackable: true), Disc(PricingMethod.FixedAmount, 100_000m, stackable: true)]);
        Assert.Equal(2, r.Discounts.Count);
        Assert.Equal(250_000m + 100_000m, r.TotalDiscounts);
        Assert.Equal(4_650_000m, r.FinalPrice);
    }

    [Fact]
    public void TotalDiscount_NeverExceedsThePrice()
    {
        var r = Calc(Prop(1m), [Base(1000m)], [Disc(PricingMethod.FixedAmount, 800m, stackable: true), Disc(PricingMethod.FixedAmount, 800m, stackable: true)]);
        Assert.Equal(1000m, r.TotalDiscounts);
        Assert.Equal(0m, r.FinalPrice);
    }

    // ---------- charges ----------
    [Fact]
    public void AdditionalCharges_AreAddedAfterDiscounts_AndNotDiscounted()
    {
        var charge = Rule(PricingRuleKind.AdditionalCharge, PricingMethod.Percentage, 2m, code: "DEVCHG", factor: PricingFactor.Other);
        var fixedCharge = Rule(PricingRuleKind.AdditionalCharge, PricingMethod.FixedAmount, 10_000m, code: "TRANSFER", factor: PricingFactor.Other, unit: null);
        var r = Calc(Prop(5m), [Base(1_000_000m), charge, fixedCharge], [Disc(PricingMethod.Percentage, 10m)]);
        Assert.Equal(100_000m + 10_000m, r.TotalCharges);
        Assert.Equal(500_000m, r.TotalDiscounts);
        Assert.Equal(4_610_000m, r.FinalPrice);
        Assert.Equal(r.ComputedPrice, r.FinalPrice);
    }

    // ---------- approved adjustments ----------
    [Fact]
    public void CustomerSpecificDiscount_AppliesOnlyForThatCustomer()
    {
        var adj = Adj(AdjustmentKind.Discount, PricingMethod.Percentage, 10m, customer: 7);
        Assert.Equal(5_000_000m, Calc(Prop(5m), [Base(1_000_000m)], adj: [adj]).FinalPrice);
        Assert.Equal(5_000_000m, Calc(Prop(5m), [Base(1_000_000m)], adj: [adj], customer: 8).FinalPrice);
        Assert.Equal(4_500_000m, Calc(Prop(5m), [Base(1_000_000m)], adj: [adj], customer: 7).FinalPrice);
    }

    [Fact]
    public void PropertyWideAdjustments_ApplyToEveryone_SurchargeIncreases()
    {
        var r = Calc(Prop(5m), [Base(1_000_000m)], adj: [Adj(AdjustmentKind.Discount, PricingMethod.FixedAmount, 50_000m), Adj(AdjustmentKind.Surcharge, PricingMethod.Percentage, 1m)]);
        Assert.Equal(50_000m, r.TotalDiscounts);
        Assert.Equal(50_000m, r.TotalCharges);
        Assert.Equal(5_000_000m, r.FinalPrice);
    }

    [Fact]
    public void ApprovedOverride_ReplacesFinalPrice_CustomerSpecificBeatsGeneral_AndIsListed()
    {
        var general = Adj(AdjustmentKind.Override, PricingMethod.FixedAmount, 4_800_000m);
        var forCustomer = Adj(AdjustmentKind.Override, PricingMethod.FixedAmount, 4_500_000m, customer: 7);
        var r = Calc(Prop(5m), [Base(1_000_000m)], adj: [general, forCustomer]);
        Assert.True(r.IsOverridden); Assert.Equal(4_800_000m, r.FinalPrice); Assert.Equal(5_000_000m, r.ComputedPrice);
        Assert.Equal(4_500_000m, Calc(Prop(5m), [Base(1_000_000m)], adj: [general, forCustomer], customer: 7).FinalPrice);
        Assert.Equal(-200_000m, r.AppliedRules.Last().Amount);
    }

    [Fact]
    public void Adjustment_OutsideWindowOrRevoked_DoesNotApplyAfterItsEnd()
    {
        var a = Adj(AdjustmentKind.Override, PricingMethod.FixedAmount, 1_000m, from: Today.AddDays(-5));
        a.Revoke("mistake", "sys", Today, DateTime.UtcNow);
        Assert.Equal(AdjustmentStatus.Revoked, a.Status);
        Assert.Equal(1_000m, Calc(Prop(1m), [Base(5_000m)], adj: [a], asOf: Today.AddDays(-2)).FinalPrice); // historic calc keeps it
        Assert.Equal(5_000m, Calc(Prop(1m), [Base(5_000m)], adj: [a]).FinalPrice);                          // today it is gone
        Assert.Equal(5_000m, Calc(Prop(1m), [Base(5_000m)], adj: [Adj(AdjustmentKind.Override, PricingMethod.FixedAmount, 1m, from: Today.AddDays(3))]).FinalPrice);
    }

    // ---------- reproducibility ----------
    [Fact]
    public void Calculation_IsDeterministic_RegardlessOfInputOrder()
    {
        var rules = new List<PricingRule>
        {
            Base(1_234_567.8912m), Base(1_300_000m, PricingScope.Block, block: 10),
            Rule(PricingRuleKind.Premium, PricingMethod.Percentage, 7.5m, code: "CORNER", factor: PricingFactor.Corner),
            Rule(PricingRuleKind.Premium, PricingMethod.FixedAmount, 12_345.67m, code: "VIEW", factor: PricingFactor.Other, unit: null),
            Rule(PricingRuleKind.AdditionalCharge, PricingMethod.Percentage, 1.25m, code: "DEV", factor: PricingFactor.Other),
        };
        var discounts = new[] { Disc(PricingMethod.Percentage, 3.3m, stackable: true), Disc(PricingMethod.FixedAmount, 1000.5m, stackable: true) };
        var a = Calc(Prop(7.25m, corner: true), rules, discounts);
        rules.Reverse();
        var b = Calc(Prop(7.25m, corner: true), rules, discounts.Reverse());
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(Calc(Prop(7.25m, corner: true), rules, discounts)));
        Assert.Equal(a.FinalPrice, a.SizeBasedPrice + a.TotalPremiums - a.TotalDiscounts + a.TotalCharges);
        Assert.Equal(decimal.Round(a.FinalPrice, 2), a.FinalPrice);
    }

    // ---------- entity rules ----------
    [Fact]
    public void Approval_CannotBeDecidedByItsRequester_Twice_OrRejectedWithoutComment()
    {
        var requester = Guid.NewGuid(); var approver = Guid.NewGuid();
        PricingApproval New() => new() { RequestedByUserId = requester, Reason = "x" };
        Assert.Throws<DomainException>(() => New().Approve(requester, "me", null, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => New().Reject(approver, "a", " ", DateTime.UtcNow));
        var a = New(); a.Approve(approver, "a", "ok", DateTime.UtcNow);
        Assert.Equal(PricingApprovalStatus.Approved, a.Status); Assert.Equal(approver, a.DecidedByUserId);
        Assert.Throws<DomainException>(() => a.Approve(Guid.NewGuid(), "b", null, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => a.Cancel(requester, "me", null, DateTime.UtcNow));
        var c = New(); c.Cancel(requester, "me", "changed my mind", DateTime.UtcNow); Assert.Equal(PricingApprovalStatus.Cancelled, c.Status);
    }

    [Fact]
    public void Rule_StateTransitions_AreGuarded()
    {
        var pending = Base(1m); pending.Status = PricingRuleStatus.PendingApproval;
        Assert.Throws<DomainException>(() => pending.Retire("r", Today, DateTime.UtcNow));
        pending.Activate(DateTime.UtcNow); Assert.Equal(PricingRuleStatus.Active, pending.Status);
        Assert.Throws<DomainException>(() => pending.Activate(DateTime.UtcNow));
        Assert.Throws<DomainException>(() => pending.Retire(" ", Today, DateTime.UtcNow));
        pending.Retire("replaced", Today, DateTime.UtcNow);
        Assert.Throws<DomainException>(() => pending.Retire("again", Today, DateTime.UtcNow));
        var rej = Base(1m); rej.Status = PricingRuleStatus.PendingApproval; rej.Reject("no", DateTime.UtcNow); Assert.Equal(PricingRuleStatus.Rejected, rej.Status);
    }
}
