using RealEstate.Application.Installments;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase05ValidatorTests
{
    private static PlanComponentInput Component(AmountBasis basis = AmountBasis.Remainder, decimal? amount = null, decimal? percent = null, ScheduleFrequency f = ScheduleFrequency.Monthly,
        int? count = 24, decimal? each = null, IReadOnlyList<TemplateEntryInput>? entries = null, bool waiver = false, decimal maxWaiver = 0m, TaxTreatment tax = TaxTreatment.NotApplicable,
        decimal rate = 0m, ChargeCategory cat = ChargeCategory.SaleConsideration, int? dueDay = null) =>
        new(1, "Monthly", cat, basis, amount, percent, f, count, each, 1, 0, dueDay, entries, 5, null, waiver, maxWaiver, tax, rate, null, null);

    private static AgreementChargeInput Charge(decimal amount = 100_000m, ScheduleFrequency f = ScheduleFrequency.Monthly, int? count = 10, ChargeCategory cat = ChargeCategory.SaleConsideration,
        IReadOnlyList<ScheduleEntryInput>? entries = null) =>
        new(1, "Installments", cat, amount, f, count, null, new DateOnly(2025, 1, 1), null, entries, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null);

    [Fact]
    public void Component_Valid()
    {
        Assert.True(new PlanComponentInputValidator().Validate(Component()).IsValid);
        Assert.True(new PlanComponentInputValidator().Validate(Component(AmountBasis.PercentOfPrice, percent: 20m, f: ScheduleFrequency.OneTime, count: null)).IsValid);
        Assert.True(new PlanComponentInputValidator().Validate(Component(f: ScheduleFrequency.CustomDates, count: null, entries: [new(0, 40m), new(6, 60m)])).IsValid);
    }

    [Fact]
    public void Component_Invalid()
    {
        var v = new PlanComponentInputValidator();
        Assert.False(v.Validate(Component(AmountBasis.FixedAmount)).IsValid); // amount missing
        Assert.False(v.Validate(Component(AmountBasis.PercentOfPrice, percent: 120m)).IsValid);
        Assert.False(v.Validate(Component(amount: 5m)).IsValid); // amount on remainder
        Assert.False(v.Validate(Component(cat: ChargeCategory.UtilityCharges)).IsValid); // remainder must be sale consideration
        Assert.False(v.Validate(Component(count: 12, each: 1000m)).IsValid);
        Assert.False(v.Validate(Component(count: null)).IsValid);
        Assert.False(v.Validate(Component(f: ScheduleFrequency.OneTime, count: 3)).IsValid);
        Assert.False(v.Validate(Component(f: ScheduleFrequency.CustomDates, count: null, entries: [new(6, null), new(3, null)])).IsValid);
        Assert.False(v.Validate(Component(f: ScheduleFrequency.CustomDates, count: null, entries: [new(0, 40m), new(6, 50m)])).IsValid);
        Assert.False(v.Validate(Component(waiver: true, maxWaiver: 0m)).IsValid);
        Assert.False(v.Validate(Component(waiver: false, maxWaiver: 10m)).IsValid);
        Assert.False(v.Validate(Component(tax: TaxTreatment.Exclusive, rate: 0m)).IsValid);
        Assert.False(v.Validate(Component(tax: TaxTreatment.Exempt, rate: 5m)).IsValid);
        Assert.False(v.Validate(Component(dueDay: 32)).IsValid);
    }

    [Fact]
    public void Plan_TargetsMatchType()
    {
        var v = new CreateInstallmentPlanRequestValidator();
        Assert.True(v.Validate(new CreateInstallmentPlanRequest(1, "STD-24", "Standard", null, InstallmentPlanType.StandardProject, null, null, null, [Component()])).IsValid);
        Assert.False(v.Validate(new CreateInstallmentPlanRequest(1, "CAT", "Cat", null, InstallmentPlanType.PropertyCategory, null, null, null, null)).IsValid);
        Assert.False(v.Validate(new CreateInstallmentPlanRequest(1, "CUS", "Cus", null, InstallmentPlanType.CustomerSpecific, null, null, null, null)).IsValid);
        Assert.True(v.Validate(new CreateInstallmentPlanRequest(1, "CUS", "Cus", null, InstallmentPlanType.CustomerSpecific, null, 5, 7, null)).IsValid);
        Assert.False(v.Validate(new CreateInstallmentPlanRequest(1, "STD", "Std", null, InstallmentPlanType.StandardProject, null, null, 7, null)).IsValid);
        Assert.False(v.Validate(new CreateInstallmentPlanRequest(1, "bad code!", "Std", null, InstallmentPlanType.StandardProject, null, null, null, null)).IsValid);
    }

    [Fact]
    public void AgreementCharge_Rules()
    {
        var v = new AgreementChargeInputValidator();
        Assert.True(v.Validate(Charge()).IsValid);
        Assert.False(v.Validate(Charge(amount: 0m)).IsValid);
        Assert.False(v.Validate(Charge(amount: 10.555m)).IsValid);
        Assert.False(v.Validate(Charge(entries: [new(new DateOnly(2025, 1, 1), -5m)], f: ScheduleFrequency.CustomDates, count: null)).IsValid);
    }

    [Fact]
    public void Amendment_RequiresExactlyTheMatchingSection()
    {
        var v = new CreateAmendmentRequestValidator();
        Assert.True(v.Validate(new CreateAmendmentRequest(AmendmentType.Deferment, "Customer request", Deferment: new DefermentInput([5], 2, true))).IsValid);
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.Deferment, "Customer request")).IsValid);
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.Deferment, "Customer request", Deferment: new DefermentInput([5], 2, true), Waiver: new WaiverInput(5, WaiverKind.Principal, 10m))).IsValid);
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.Transfer, "Customer request")).IsValid);
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.Deferment, "abc", Deferment: new DefermentInput([5], 2, true))).IsValid); // reason too short
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.AdditionalCharge, "Customer request", AdditionalCharge: Charge())).IsValid); // sale consideration
        Assert.True(v.Validate(new CreateAmendmentRequest(AmendmentType.AdditionalCharge, "Customer request", AdditionalCharge: Charge(cat: ChargeCategory.UtilityCharges))).IsValid);
        Assert.False(v.Validate(new CreateAmendmentRequest(AmendmentType.Waiver, "Customer request", Waiver: new WaiverInput(5, WaiverKind.Principal, 0m))).IsValid);
    }

    [Fact]
    public void LateFeeRule_Rules()
    {
        var v = new SaveLateFeeRuleRequestValidator();
        Assert.True(v.Validate(new SaveLateFeeRuleRequest("LF-1", "Daily", null, LateFeeMethod.PercentPerDay, 0.05m, 5000m, 10m, null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveLateFeeRuleRequest("LF-1", "Daily", null, LateFeeMethod.PercentPerDay, 150m, null, null, null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveLateFeeRuleRequest("LF-1", "Fixed", null, LateFeeMethod.FixedOneTime, 0m, null, null, null, true, null)).IsValid);
        Assert.False(v.Validate(new SaveLateFeeRuleRequest("LF-1", "Fixed", null, LateFeeMethod.FixedOneTime, 10m, null, 120m, null, true, null)).IsValid);
    }
}
