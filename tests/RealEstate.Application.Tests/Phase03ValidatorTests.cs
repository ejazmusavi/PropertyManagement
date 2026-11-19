using RealEstate.Application.Pricing;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase03ValidatorTests
{
    private static readonly DateOnly D = new(2026, 11, 19);

    private static CreatePricingRuleRequest Rule() => new(1, "BASE-1", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project, null, null, null, null, null, null,
        PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, D, null, null);

    private static CreateDiscountRuleRequest Discount() => new(1, "EARLY", "Early bird", null, PricingScope.Project, null, null, null, null, PricingMethod.Percentage, 5m, false, 0, D, null);

    private static RequestPricingApprovalRequest Override() => new(PricingApprovalType.PriceOverride, 1, null, null, null, null, 4_000_000m, null, null, "Negotiated with management");

    [Fact] public void ValidRule_Passes() => Assert.True(new CreatePricingRuleValidator().Validate(Rule()).IsValid);

    [Theory]
    [InlineData("")] [InlineData("bad code")] [InlineData("a;b")]
    public void Rule_Code_Invalid(string code) => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Code = code }).IsValid);

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void Rule_Value_MustBePositive(double v) => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Value = (decimal)v }).IsValid);

    [Fact] public void Rule_Value_MaxFourDecimals() => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Value = 1.00001m }).IsValid);

    [Fact] public void Rule_FixedAmount_MaxTwoDecimals() =>
        Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Method = PricingMethod.FixedAmount, Value = 10.123m, Unit = null }).IsValid);

    [Fact] public void Rule_Percentage_CannotExceed100() =>
        Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Kind = PricingRuleKind.Premium, Method = PricingMethod.Percentage, Value = 101m }).IsValid);

    [Fact] public void Rule_BaseRate_CannotBePercentage() =>
        Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Method = PricingMethod.Percentage, Value = 10m }).IsValid);

    [Fact] public void Rule_PerUnit_NeedsUnit() => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Unit = null }).IsValid);

    [Fact] public void Rule_EndBeforeStart_Rejected() => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { EffectiveTo = D.AddDays(-1) }).IsValid);

    [Fact] public void Rule_PriorityRange() => Assert.False(new CreatePricingRuleValidator().Validate(Rule() with { Priority = 1001 }).IsValid);

    [Fact] public void ValidDiscount_Passes() => Assert.True(new CreateDiscountRuleValidator().Validate(Discount()).IsValid);

    [Fact] public void Discount_CannotBeCustomerScoped() => Assert.False(new CreateDiscountRuleValidator().Validate(Discount() with { Scope = PricingScope.Customer }).IsValid);

    [Fact] public void Discount_PerUnitIsNotADiscountMethod() => Assert.False(new CreateDiscountRuleValidator().Validate(Discount() with { Method = PricingMethod.PerUnit }).IsValid);

    [Theory]
    [InlineData(0)] [InlineData(100.5)]
    public void Discount_Percentage_Range(double v) => Assert.False(new CreateDiscountRuleValidator().Validate(Discount() with { Value = (decimal)v }).IsValid);

    [Fact] public void ValidOverrideRequest_Passes() => Assert.True(new RequestPricingApprovalValidator().Validate(Override()).IsValid);

    [Fact] public void Request_ReasonTooShort_Rejected() => Assert.False(new RequestPricingApprovalValidator().Validate(Override() with { Reason = "ok" }).IsValid);

    [Fact] public void CustomerDiscount_NeedsCustomerAndMethod()
    {
        var v = new RequestPricingApprovalValidator();
        var r = Override() with { Type = PricingApprovalType.CustomerDiscount, Method = PricingMethod.Percentage, Value = 5m };
        Assert.False(v.Validate(r).IsValid);                                   // no customer
        Assert.True(v.Validate(r with { CustomerId = 7 }).IsValid);
        Assert.False(v.Validate(r with { CustomerId = 7, Method = null }).IsValid);
        Assert.False(v.Validate(r with { CustomerId = 7, Method = PricingMethod.Percentage, Value = 150m }).IsValid);
    }

    [Fact] public void SpecialPricing_NeedsCustomer() =>
        Assert.False(new RequestPricingApprovalValidator().Validate(Override() with { Type = PricingApprovalType.SpecialPricing }).IsValid);

    [Fact] public void PricingException_NeedsKind_AndForbidsCustomer()
    {
        var v = new RequestPricingApprovalValidator();
        var r = Override() with { Type = PricingApprovalType.PricingException, Method = PricingMethod.FixedAmount, Value = 1000m };
        Assert.False(v.Validate(r).IsValid);
        Assert.True(v.Validate(r with { AdjustmentKind = AdjustmentKind.Discount }).IsValid);
        Assert.False(v.Validate(r with { AdjustmentKind = AdjustmentKind.Override }).IsValid);
        Assert.False(v.Validate(r with { AdjustmentKind = AdjustmentKind.Discount, CustomerId = 3 }).IsValid);
    }

    [Fact] public void Decision_Reject_NeedsComment()
    {
        var v = new DecidePricingApprovalValidator();
        Assert.False(v.Validate(new DecidePricingApprovalRequest(false, " ", null)).IsValid);
        Assert.True(v.Validate(new DecidePricingApprovalRequest(false, "too low", null)).IsValid);
        Assert.True(v.Validate(new DecidePricingApprovalRequest(true, null, null)).IsValid);
    }

    [Fact] public void Snapshot_HashIsStable_AndDetectsTampering()
    {
        var calc = new PriceCalculationDto(1, 1, "P-1", 1, "PRJ", D, null, 5m, MeasurementUnit.Marla, 1m, MeasurementUnit.Marla, PricingMethod.PerUnit, 1m, 5m, [], 0m, [], 0m, [], 0m, 5m, false, null, 5m, [], 2, RoundingMode.Nearest);
        var json = PricingSnapshot.Serialize(calc);
        var hash = PricingSnapshot.Hash(json);
        Assert.Equal(hash, PricingSnapshot.Hash(PricingSnapshot.Serialize(calc)));
        Assert.True(PricingSnapshot.Verify(json, hash));
        Assert.False(PricingSnapshot.Verify(json + " ", hash));
        Assert.Equal(calc.FinalPrice, PricingSnapshot.Deserialize(json).FinalPrice);
    }
}
