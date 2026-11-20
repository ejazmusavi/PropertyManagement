using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Installments;
using RealEstate.Domain.Sales;

namespace RealEstate.Domain.Tests;

public class Phase05ScheduleGenerationTests
{
    private static readonly MoneyPolicy P = MoneyPolicy.Default;
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static ChargeSpec Spec(decimal amount, ScheduleFrequency f, int? count, DateOnly first, long key = 1, int? dueDay = null, decimal? each = null,
        IReadOnlyList<ScheduleEntry>? entries = null, decimal? unit = null, TaxTreatment tax = TaxTreatment.NotApplicable, decimal rate = 0m, int grace = 0,
        ChargeCategory cat = ChargeCategory.SaleConsideration, int sort = 10, string name = "Installment") =>
        new(key, sort, name, cat, amount, f, count, each, first, dueDay, entries, grace, tax, rate, unit);

    [Theory]
    [InlineData(ScheduleFrequency.Monthly, 1)]
    [InlineData(ScheduleFrequency.Quarterly, 3)]
    [InlineData(ScheduleFrequency.HalfYearly, 6)]
    [InlineData(ScheduleFrequency.Annual, 12)]
    public void RegularFrequencies_UseTheirInterval_AndReconcile(ScheduleFrequency f, int months)
    {
        var r = ScheduleGenerationService.Generate([Spec(1_200_000m, f, 12, D(2025, 1, 10))], P);
        Assert.Equal(12, r.Items.Count);
        Assert.All(r.Items, i => Assert.Equal(100_000m, i.Amount));
        for (var k = 0; k < 12; k++) Assert.Equal(D(2025, 1, 10).AddMonths(k * months), r.Items[k].DueDate);
        Assert.True(r.IsReconciled);
        Assert.Equal(1_200_000m, r.TotalAmount);
        Assert.Equal("Installment 1/12", r.Items[0].Description);
    }

    [Fact]
    public void MonthEnd_DatesClampWithoutDrift()
    {
        var dates = ScheduleGenerationService.Generate([Spec(400m, ScheduleFrequency.Monthly, 5, D(2025, 1, 31))], P).Items.Select(i => i.DueDate).ToList();
        Assert.Equal([D(2025, 1, 31), D(2025, 2, 28), D(2025, 3, 31), D(2025, 4, 30), D(2025, 5, 31)], dates);
    }

    [Fact]
    public void LeapYears_AreHandled()
    {
        var monthly = ScheduleGenerationService.RegularDates(D(2028, 1, 31), ScheduleFrequency.Monthly, 3, null);
        Assert.Equal([D(2028, 1, 31), D(2028, 2, 29), D(2028, 3, 31)], monthly);
        var annual = ScheduleGenerationService.RegularDates(D(2028, 2, 29), ScheduleFrequency.Annual, 5, null);
        Assert.Equal([D(2028, 2, 29), D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29)], annual);
    }

    [Fact]
    public void FixedDueDay_AppliesFromTheSecondInstallment()
    {
        var dates = ScheduleGenerationService.RegularDates(D(2025, 1, 15), ScheduleFrequency.Monthly, 4, 31);
        Assert.Equal([D(2025, 1, 15), D(2025, 2, 28), D(2025, 3, 31), D(2025, 4, 30)], dates);
        Assert.Equal([D(2025, 1, 15), D(2025, 4, 5)], ScheduleGenerationService.RegularDates(D(2025, 1, 15), ScheduleFrequency.Quarterly, 2, 5));
    }

    [Fact]
    public void Rounding_LastInstallmentAbsorbsTheDifference()
    {
        var r = ScheduleGenerationService.Generate([Spec(1_000_000m, ScheduleFrequency.Monthly, 3, D(2025, 1, 1))], P);
        Assert.Equal([333_333.33m, 333_333.33m, 333_333.34m], r.Items.Select(i => i.Amount));
        Assert.True(r.Items[2].IsPartial);
        Assert.False(r.Items[0].IsPartial);
        Assert.True(r.IsReconciled);
    }

    [Theory]
    [InlineData(RoundingMode.Nearest, 333_000, 334_000)]
    [InlineData(RoundingMode.Down, 333_000, 334_000)]
    [InlineData(RoundingMode.Up, 334_000, 332_000)]
    public void RoundingUnit_RoundsRegularInstallments_WithCompanyMode(RoundingMode mode, int regular, int last)
    {
        var r = ScheduleGenerationService.Generate([Spec(1_000_000m, ScheduleFrequency.Monthly, 3, D(2025, 1, 1), unit: 1000m)], new MoneyPolicy(2, mode));
        Assert.Equal([regular, regular, last], r.Items.Select(i => (int)i.Amount));
        Assert.Equal(1_000_000m, r.TotalAmount);
    }

    [Fact]
    public void ZeroDecimalCurrency_SplitsInWholeUnits()
    {
        var r = ScheduleGenerationService.Generate([Spec(100m, ScheduleFrequency.Monthly, 3, D(2025, 1, 1))], new MoneyPolicy(0, RoundingMode.Nearest));
        Assert.Equal([33m, 33m, 34m], r.Items.Select(i => i.Amount));
    }

    [Fact]
    public void InstallmentAmount_GivesPartialLastInstallment()
    {
        var r = ScheduleGenerationService.Generate([Spec(1_000_000m, ScheduleFrequency.Monthly, null, D(2025, 3, 5), each: 300_000m)], P);
        Assert.Equal([300_000m, 300_000m, 300_000m, 100_000m], r.Items.Select(i => i.Amount));
        Assert.True(r.Items[3].IsPartial);
        Assert.Equal(D(2025, 6, 5), r.Items[3].DueDate);
    }

    [Fact]
    public void OneTime_And_CustomDates_EqualSplit_And_Irregular()
    {
        var one = ScheduleGenerationService.Generate([Spec(50_000m, ScheduleFrequency.OneTime, null, D(2026, 6, 30))], P);
        Assert.Single(one.Items);
        Assert.Equal("Installment", one.Items[0].Description);

        var dates = new[] { D(2025, 2, 1), D(2025, 5, 17), D(2026, 1, 9) };
        var equal = ScheduleGenerationService.Generate([Spec(100m, ScheduleFrequency.CustomDates, null, dates[0], entries: dates.Select(d => new ScheduleEntry(d, null)).ToList())], P);
        Assert.Equal([33.33m, 33.33m, 33.34m], equal.Items.Select(i => i.Amount));
        Assert.Equal(dates, equal.Items.Select(i => i.DueDate));

        var irregular = ScheduleGenerationService.Generate([Spec(1_000m, ScheduleFrequency.CustomDates, null, dates[0],
            entries: [new(dates[0], 100m), new(dates[1], 650m), new(dates[2], 250m)])], P);
        Assert.Equal([100m, 650m, 250m], irregular.Items.Select(i => i.Amount));

        var ex = Assert.Throws<DomainException>(() => ScheduleGenerationService.Generate([Spec(1_000m, ScheduleFrequency.CustomDates, null, dates[0],
            entries: [new(dates[0], 100m), new(dates[1], 100m)])], P));
        Assert.Contains("add up to", ex.Message);
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Generate([Spec(1_000m, ScheduleFrequency.CustomDates, null, dates[1],
            entries: [new(dates[1], null), new(dates[0], null)])], P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Generate([Spec(1_000m, ScheduleFrequency.CustomDates, null, dates[0],
            entries: [new(dates[0], 100m), new(dates[1], null)])], P));
    }

    [Fact]
    public void Tax_IsCalculatedPerCharge_AndSplitSoItReconciles()
    {
        var r = ScheduleGenerationService.Generate([Spec(100_000m, ScheduleFrequency.Monthly, 3, D(2025, 1, 1), tax: TaxTreatment.Exclusive, rate: 16m)], P);
        Assert.Equal(16_000m, r.TotalTax);
        Assert.Equal([5_333.33m, 5_333.33m, 5_333.34m], r.Items.Select(i => i.TaxAmount));
        var inc = ScheduleGenerationService.Generate([Spec(116_000m, ScheduleFrequency.OneTime, null, D(2025, 1, 1), tax: TaxTreatment.Inclusive, rate: 16m)], P);
        Assert.Equal(16_000m, inc.TotalTax);
        Assert.Equal(116_000m, inc.TotalAmount);
    }

    [Fact]
    public void MultipleCharges_AreOrderedByDate_AndEachReconciles()
    {
        var r = ScheduleGenerationService.Generate([
            Spec(600_000m, ScheduleFrequency.Monthly, 6, D(2025, 2, 1), key: 2, sort: 20, name: "Monthly"),
            Spec(400_000m, ScheduleFrequency.OneTime, null, D(2025, 1, 1), key: 1, sort: 10, name: "Down payment"),
            Spec(50_000m, ScheduleFrequency.OneTime, null, D(2025, 2, 1), key: 3, sort: 5, name: "Development", cat: ChargeCategory.DevelopmentCharges, grace: 15),
        ], P);
        Assert.Equal(8, r.Items.Count);
        Assert.Equal("Down payment", r.Items[0].Description);
        Assert.Equal("Development", r.Items[1].Description); // same date as Monthly 1, lower sort order first
        Assert.Equal(D(2025, 2, 16), r.Items[1].GraceEndDate);
        Assert.Equal(Enumerable.Range(1, 8), r.Items.Select(i => i.LineNo));
        Assert.All(r.Reconciliation, x => Assert.Equal(0m, x.Difference));
        Assert.Equal(1_050_000m, r.TotalAmount);
    }

    [Fact]
    public void Generation_IsDeterministic_AndInputOrderIndependent()
    {
        ChargeSpec[] a = [Spec(999_999.99m, ScheduleFrequency.Quarterly, 7, D(2025, 1, 31), key: 1), Spec(12_345.67m, ScheduleFrequency.Monthly, 5, D(2025, 1, 31), key: 2, sort: 20)];
        var first = ScheduleGenerationService.Generate(a, P);
        var second = ScheduleGenerationService.Generate(a.Reverse(), P);
        string H(ScheduleResult r) => ScheduleGenerationService.Hash(r.Items.Select(i => (i.LineNo, i.ChargeKey, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount)));
        Assert.Equal(H(first), H(second));
        Assert.Equal(first.Items, second.Items);
        Assert.Equal(64, H(first).Length);
    }

    [Fact]
    public void Validation_RejectsInvalidCharges()
    {
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(0m, ScheduleFrequency.OneTime, null, D(2025, 1, 1)), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(10.005m, ScheduleFrequency.OneTime, null, D(2025, 1, 1)), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, 2, D(2025, 1, 1), each: 50m), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, null, D(2025, 1, 1)), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, 601, D(2025, 1, 1)), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.OneTime, 2, D(2025, 1, 1)), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.OneTime, null, D(2025, 1, 1), dueDay: 5), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, 2, D(2025, 1, 1), tax: TaxTreatment.Exclusive), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, 2, D(2025, 1, 1), unit: 0.005m), P));
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Validate(Spec(100m, ScheduleFrequency.Monthly, 2, D(2025, 1, 1), grace: 400), P));
        // rounding unit too large for the number of installments
        Assert.Throws<DomainException>(() => ScheduleGenerationService.Generate([Spec(1_000m, ScheduleFrequency.Monthly, 3, D(2025, 1, 1), unit: 1000m)], new MoneyPolicy(2, RoundingMode.Up)));
    }

    [Fact]
    public void LargeSchedules_Reconcile_ForManyAmounts()
    {
        foreach (var total in new[] { 0.07m, 1m, 7_777_777.77m, 12_500_000m, 99_999_999.99m })
            foreach (var n in new[] { 1, 2, 7, 60, 120, 600 })
            {
                if (total / n < 0.01m) continue;
                var r = ScheduleGenerationService.Generate([Spec(total, ScheduleFrequency.Monthly, n, D(2024, 2, 29))], P);
                Assert.Equal(total, r.TotalAmount);
                Assert.Equal(n, r.Items.Count);
                Assert.All(r.Items, i => Assert.True(i.Amount > 0m));
            }
    }
}

public class Phase05PlanResolverTests
{
    private static readonly MoneyPolicy P = MoneyPolicy.Default;
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static ComponentSpec C(long id, ChargeComponentKind kind, AmountBasis basis, decimal? amount, decimal? percent, ScheduleFrequency f, int? count = null,
        int offsetMonths = 0, ChargeCategory cat = ChargeCategory.SaleConsideration, IReadOnlyList<TemplateEntry>? entries = null) =>
        new(id, (int)id * 10, 100 + id, kind, kind.ToString(), cat, basis, amount, percent, f, count, null, offsetMonths, 0, null, entries, 10, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null);

    [Fact]
    public void Resolve_PercentRemainderAndAdditionalCharges()
    {
        var comps = new[]
        {
            C(1, ChargeComponentKind.DownPayment, AmountBasis.PercentOfPrice, null, 20m, ScheduleFrequency.OneTime),
            C(2, ChargeComponentKind.MonthlyInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, offsetMonths: 1),
            C(3, ChargeComponentKind.PossessionPayment, AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, offsetMonths: 30),
            C(4, ChargeComponentKind.DevelopmentCharges, AmountBasis.FixedAmount, 250_000m, null, ScheduleFrequency.OneTime, offsetMonths: 12, cat: ChargeCategory.DevelopmentCharges),
        };
        var r = PlanResolver.Resolve(comps, 10_000_000m, 0m, D(2025, 1, 15), D(2025, 1, 15), P, 999);
        Assert.Equal(2_000_000m, r[0].Amount);
        Assert.Equal(7_000_000m, r[1].Amount); // remainder after 20% and 10%
        Assert.Equal(D(2025, 2, 15), r[1].FirstDueDate);
        Assert.Equal(24, r[1].NumberOfInstallments);
        Assert.Equal(D(2027, 7, 15), r[2].FirstDueDate);
        Assert.Equal(250_000m, r[3].Amount);
        Assert.Equal(10_000_000m, r.Where(x => x.Category == ChargeCategory.SaleConsideration).Sum(x => x.Amount));
    }

    [Fact]
    public void Resolve_BookingAmountBecomesTheDownPayment()
    {
        var withComponent = PlanResolver.Resolve([
            C(1, ChargeComponentKind.DownPayment, AmountBasis.PercentOfPrice, null, 20m, ScheduleFrequency.OneTime, offsetMonths: 2),
            C(2, ChargeComponentKind.MonthlyInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 10, offsetMonths: 1)], 5_000_000m, 750_000m, D(2025, 3, 1), D(2025, 4, 1), P, 999);
        Assert.Equal(750_000m, withComponent[0].Amount);
        Assert.Equal(D(2025, 3, 1), withComponent[0].FirstDueDate);
        Assert.Equal(4_250_000m, withComponent[1].Amount);

        var without = PlanResolver.Resolve([C(2, ChargeComponentKind.MonthlyInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 10)], 5_000_000m, 500_000m,
            D(2025, 3, 1), D(2025, 3, 1), P, 999);
        Assert.Equal(2, without.Count);
        Assert.Equal(999, without[0].ChargeTypeId);
        Assert.Equal(500_000m, without[0].Amount);
        Assert.Equal(4_500_000m, without[1].Amount);
    }

    [Fact]
    public void Resolve_CustomShares_AndErrors()
    {
        var r = PlanResolver.Resolve([C(1, ChargeComponentKind.CustomCharge, AmountBasis.Remainder, null, null, ScheduleFrequency.CustomDates,
            entries: [new(0, 25m), new(6, 25m), new(18, 50m)])], 1_000_001m, 0m, D(2025, 1, 31), D(2025, 1, 31), P, 1);
        Assert.Equal([250_000.25m, 250_000.25m, 500_000.50m], r[0].CustomEntries!.Select(e => e.Amount!.Value));
        Assert.Equal([D(2025, 1, 31), D(2025, 7, 31), D(2026, 7, 31)], r[0].CustomEntries!.Select(e => e.DueDate));
        Assert.True(ScheduleGenerationService.Generate([new ChargeSpec(1, 1, "x", ChargeCategory.SaleConsideration, r[0].Amount, r[0].Frequency, null, null, r[0].FirstDueDate, null, r[0].CustomEntries, 0,
            TaxTreatment.NotApplicable, 0m, null)], P).IsReconciled);

        Assert.Throws<DomainException>(() => PlanResolver.Resolve([
            C(1, ChargeComponentKind.ConfirmationPayment, AmountBasis.FixedAmount, 150m, null, ScheduleFrequency.OneTime),
            C(2, ChargeComponentKind.MonthlyInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 3)], 100m, 0m, D(2025, 1, 1), D(2025, 1, 1), P, 1));
        Assert.Throws<DomainException>(() => PlanResolver.Resolve([
            C(1, ChargeComponentKind.MonthlyInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 3),
            C(2, ChargeComponentKind.AnnualInstallment, AmountBasis.Remainder, null, null, ScheduleFrequency.Annual, 3)], 100m, 0m, D(2025, 1, 1), D(2025, 1, 1), P, 1));
    }
}

public class Phase05LateFeeTests
{
    private static readonly MoneyPolicy P = MoneyPolicy.Default;
    private static readonly DateOnly Grace = new(2025, 1, 31);

    [Theory]
    [InlineData(LateFeeMethod.FixedOneTime, 500, 2025, 2, 1, 500)]
    [InlineData(LateFeeMethod.PercentOneTime, 2, 2025, 2, 1, 2000)]
    [InlineData(LateFeeMethod.FixedPerDay, 100, 2025, 2, 10, 1000)]
    [InlineData(LateFeeMethod.PercentPerDay, 0.1, 2025, 2, 10, 1000)]
    [InlineData(LateFeeMethod.FixedPerMonth, 1000, 2025, 3, 1, 2000)]
    [InlineData(LateFeeMethod.PercentPerMonth, 1, 2025, 2, 28, 1000)]
    public void Methods(LateFeeMethod method, double value, int y, int m, int d, int expected)
    {
        var fee = LateFeeCalculator.Compute(new LateFeeTerms(method, (decimal)value, null, null), 100_000m, 100_000m, Grace, new DateOnly(y, m, d), P);
        Assert.Equal(expected, (int)fee);
    }

    [Fact]
    public void NothingWithinGrace_OrWhenSettled_AndCapsApply()
    {
        var t = new LateFeeTerms(LateFeeMethod.FixedPerDay, 100m, 1_500m, null);
        Assert.Equal(0m, LateFeeCalculator.Compute(t, 10_000m, 10_000m, Grace, Grace, P));
        Assert.Equal(0m, LateFeeCalculator.Compute(t, 10_000m, 0m, Grace, Grace.AddDays(30), P));
        Assert.Equal(1_500m, LateFeeCalculator.Compute(t, 10_000m, 10_000m, Grace, Grace.AddDays(30), P));
        var pct = new LateFeeTerms(LateFeeMethod.FixedPerDay, 100m, null, 5m);
        Assert.Equal(500m, LateFeeCalculator.Compute(pct, 10_000m, 10_000m, Grace, Grace.AddDays(30), P));
    }

    [Fact]
    public void StartedMonths_CountsCalendarMonthsAfterGrace()
    {
        Assert.Equal(0, LateFeeCalculator.StartedMonths(Grace, Grace));
        Assert.Equal(1, LateFeeCalculator.StartedMonths(Grace, new DateOnly(2025, 2, 1)));
        Assert.Equal(1, LateFeeCalculator.StartedMonths(Grace, new DateOnly(2025, 2, 28)));
        Assert.Equal(2, LateFeeCalculator.StartedMonths(Grace, new DateOnly(2025, 3, 1)));
        Assert.Equal(12, LateFeeCalculator.StartedMonths(new DateOnly(2024, 2, 29), new DateOnly(2025, 2, 28)));
        Assert.Throws<DomainException>(() => LateFeeCalculator.Validate(new LateFeeTerms(LateFeeMethod.PercentPerDay, 150m, null, null)));
        Assert.Throws<DomainException>(() => LateFeeCalculator.Validate(new LateFeeTerms(LateFeeMethod.FixedPerDay, 0m, null, null)));
    }
}

public class Phase05AmendmentEngineTests
{
    private static readonly MoneyPolicy P = MoneyPolicy.Default;
    private static readonly DateOnly Today = new(2025, 1, 1);
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>Charge 1: down payment 200,000 (one-time). Charge 2: 1,200,000 over 12 monthly installments from 2025-02-28. Items get ids 101...</summary>
    private static (List<ChargeState> Charges, List<ItemState> Items) Setup(bool waivers = true, int grace = 5)
    {
        var specs = new[]
        {
            new ChargeSpec(1, 10, "Down payment", ChargeCategory.SaleConsideration, 200_000m, ScheduleFrequency.OneTime, null, null, D(2025, 1, 15), null, null, 0, TaxTreatment.NotApplicable, 0m, null),
            new ChargeSpec(2, 20, "Monthly", ChargeCategory.SaleConsideration, 1_200_000m, ScheduleFrequency.Monthly, 12, null, D(2025, 2, 28), 31, null, grace, TaxTreatment.NotApplicable, 0m, null),
        };
        var r = ScheduleGenerationService.Generate(specs, P);
        var items = r.Items.Select(i => new ItemState(100 + i.LineNo, i.ChargeKey, i.LineNo, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount, i.IsPartial, 0m, 0m, 0m, null)).ToList();
        var charges = specs.Select(s => new ChargeState(s.Key, s.SortOrder, s.Description, s.Category, s.Amount, s.Frequency, s.FirstDueDate, s.DueDayOfMonth, s.GracePeriodDays, null,
            s.TaxTreatment, s.TaxRatePercent, s.RoundingUnit, waivers, waivers ? 50m : 0m)).ToList();
        return (charges, items);
    }

    private static AmendmentOutcome Apply(AmendmentCommand c, (List<ChargeState> Charges, List<ItemState> Items) s) => ScheduleAmendmentEngine.Apply(s.Charges, s.Items, c, P, Today);

    [Fact]
    public void Deferment_CascadesLaterInstallments_AndKeepsTotals()
    {
        var s = Setup();
        var o = Apply(new DefermentCommand([104], 2, CascadeSubsequent: true), s); // 104 = monthly installment 3 (2025-04-30)
        Assert.True(o.IsReconciled);
        Assert.Equal(1_400_000m, o.TotalAmount);
        var monthly = o.Items.Where(i => i.ChargeKey == 2).ToList();
        Assert.Equal(D(2025, 2, 28), monthly[0].DueDate);
        Assert.Equal(D(2025, 3, 31), monthly[1].DueDate);
        Assert.Equal(D(2025, 6, 30), monthly[2].DueDate);
        Assert.Equal(D(2025, 7, 5), monthly[2].GraceEndDate);
        Assert.Equal(10, o.Changes.Count);
        Assert.All(o.Changes, c => Assert.Equal(ScheduleChangeOperation.DueDateChanged, c.Operation));
        Assert.All(o.Changes, c => Assert.NotNull(c.ToItemIndex));
        var single = Apply(new DefermentCommand([104], 1, CascadeSubsequent: false), s);
        Assert.Single(single.Changes);
    }

    [Fact]
    public void Reduction_RedistributesInsideTheCharge()
    {
        var s = Setup();
        var spread = Apply(new ReductionCommand(103, 40_000m, ReductionRedistribution.SpreadOverRemaining, null), s); // installment 2 of 12
        Assert.True(spread.IsReconciled);
        var monthly = spread.Items.Where(i => i.ChargeKey == 2).ToList();
        Assert.Equal(40_000m, monthly[1].Amount);
        Assert.Equal(60_000m, monthly.Skip(2).Sum(i => i.Amount) - 1_000_000m);
        Assert.Equal(1_200_000m, monthly.Sum(i => i.Amount));

        var last = Apply(new ReductionCommand(103, 40_000m, ReductionRedistribution.AddToLast, null), s);
        Assert.Equal(160_000m, last.Items.Where(i => i.ChargeKey == 2).Last().Amount);

        var added = Apply(new ReductionCommand(103, 40_000m, ReductionRedistribution.NewInstallment, D(2026, 6, 1)), s);
        Assert.Equal(13, added.Items.Count(i => i.ChargeKey == 2));
        Assert.Contains(added.Items, i => i.DueDate == D(2026, 6, 1) && i.Amount == 60_000m && i.Description == "Monthly 13/13");
        Assert.Throws<DomainException>(() => Apply(new ReductionCommand(103, 100_000m, ReductionRedistribution.AddToLast, null), s));
        Assert.Throws<DomainException>(() => Apply(new ReductionCommand(113, 50_000m, ReductionRedistribution.SpreadOverRemaining, null), s)); // last one: nothing after it
    }

    [Fact]
    public void AdditionalInstallments_ReSpreadTheOutstandingBalance_KeepingTheDueDay()
    {
        var o = Apply(new AdditionalInstallmentsCommand(2, 3), Setup());
        var monthly = o.Items.Where(i => i.ChargeKey == 2).ToList();
        Assert.Equal(15, monthly.Count);
        Assert.Equal(80_000m, monthly[0].Amount);
        Assert.Equal(D(2025, 2, 28), monthly[0].DueDate);
        Assert.Equal(D(2025, 3, 31), monthly[1].DueDate); // due day 31 preserved
        Assert.Equal(1_200_000m, monthly.Sum(i => i.Amount));
        Assert.Equal(12, o.Changes.Count(c => c.Operation == ScheduleChangeOperation.Removed));
        Assert.Equal(15, o.Changes.Count(c => c.Operation == ScheduleChangeOperation.Added));
    }

    [Fact]
    public void Reschedule_KeepsSettledLines_AndClosesAPartlyPaidOne()
    {
        var s = Setup();
        // first monthly installment fully paid, second partly paid (30,000 of 100,000)
        s.Items[1] = s.Items[1] with { Paid = 100_000m, LastPaymentDate = D(2025, 2, 20) };
        s.Items[2] = s.Items[2] with { Paid = 30_000m, LastPaymentDate = D(2025, 3, 25) };
        var o = Apply(new RescheduleCommand(2, ScheduleFrequency.Quarterly, 5, null, D(2025, 6, 30), null, null), s);
        var monthly = o.Items.Where(i => i.ChargeKey == 2).ToList();
        Assert.Equal(7, monthly.Count); // 1 paid + 1 closed partly paid + 5 quarterly
        Assert.Equal(100_000m, monthly[0].Amount);
        Assert.Equal(30_000m, monthly[1].Amount);
        Assert.Equal(ScheduleItemStatus.Paid, monthly[1].Status);
        Assert.All(monthly.Skip(2), i => Assert.Equal(214_000m, i.Amount)); // (1,200,000 - 130,000) / 5
        Assert.Equal(D(2025, 9, 30), monthly[3].DueDate);
        Assert.Contains(o.Changes, c => c.Operation == ScheduleChangeOperation.Settled && c.OldAmount == 100_000m && c.NewAmount == 30_000m);
        Assert.True(o.IsReconciled);
        Assert.Equal(PreviousIds(o, 102), 102);
        Assert.Throws<DomainException>(() => Apply(new RescheduleCommand(2, ScheduleFrequency.Monthly, 3, null, D(2024, 12, 1), null, null), s)); // past date
    }

    private static long? PreviousIds(AmendmentOutcome o, long id) => o.Items.FirstOrDefault(i => i.PreviousItemId == id)?.PreviousItemId;

    [Fact]
    public void SettledInstallments_CannotBeMovedOrReduced()
    {
        var s = Setup();
        s.Items[1] = s.Items[1] with { Paid = 100_000m };
        s.Items[2] = s.Items[2] with { Paid = 10_000m };
        Assert.Throws<DomainException>(() => Apply(new DefermentCommand([103], 1, false), s));
        Assert.Throws<DomainException>(() => Apply(new ReductionCommand(103, 50_000m, ReductionRedistribution.AddToLast, null), s));
        Assert.Throws<DomainException>(() => Apply(new DueDateChangeCommand([new(102, D(2025, 5, 1))]), s));
        // a partly paid line can still be moved (its balance is still due)
        Assert.True(Apply(new DueDateChangeCommand([new(103, D(2025, 5, 1))]), s).IsReconciled);
    }

    [Fact]
    public void DueDateChange_ReordersLines_AndRenumbers()
    {
        var o = Apply(new DueDateChangeCommand([new(102, D(2025, 1, 10))]), Setup()); // monthly 1 moved before the down payment
        Assert.Equal("Monthly 1/12", o.Items[0].Description);
        Assert.Equal(D(2025, 1, 15), o.Items[0].GraceEndDate);
        Assert.Equal(1, o.Items[0].LineNo);
        Assert.Throws<DomainException>(() => Apply(new DueDateChangeCommand([new(102, D(2025, 2, 28))]), Setup()));
    }

    [Fact]
    public void PenaltyChange_UpdatesGraceOfOpenLines_AndChargeTerms()
    {
        var s = Setup();
        s.Items[1] = s.Items[1] with { Paid = 100_000m };
        var o = Apply(new PenaltyChangeCommand(2, 77, 15), s);
        Assert.Equal(11, o.Changes.Count(c => c.Operation == ScheduleChangeOperation.GraceChanged));
        Assert.Equal(15, o.Charges.Single(c => c.Key == 2).GracePeriodDays);
        Assert.Equal(77, o.Charges.Single(c => c.Key == 2).LateFeeRuleId);
        Assert.Equal(D(2025, 3, 5), o.Items.Single(i => i.PreviousItemId == 102).GraceEndDate); // settled line untouched
        Assert.Throws<DomainException>(() => Apply(new PenaltyChangeCommand(2, null, 5), Setup()));
    }

    [Fact]
    public void Waivers_RespectChargeRules()
    {
        var o = Apply(new WaiverCommand(102, WaiverKind.Principal, 20_000m, 0m), Setup());
        var item = o.Items.Single(i => i.PreviousItemId == 102);
        Assert.Equal(20_000m, item.Waived);
        Assert.Equal(80_000m, item.Outstanding);
        Assert.Equal(ScheduleItemStatus.PartiallyPaid, item.Status);
        Assert.Equal(1_400_000m, o.TotalAmount); // schedule total unchanged; the waiver reduces what is owed
        Assert.Throws<DomainException>(() => Apply(new WaiverCommand(102, WaiverKind.Principal, 60_000m, 0m), Setup())); // above 50%
        Assert.Throws<DomainException>(() => Apply(new WaiverCommand(102, WaiverKind.Principal, 1_000m, 0m), Setup(waivers: false)));
        var fee = Apply(new WaiverCommand(102, WaiverKind.LateFee, 300m, 500m), Setup());
        Assert.Equal(300m, fee.Items.Single(i => i.PreviousItemId == 102).LateFeeWaived);
        Assert.Throws<DomainException>(() => Apply(new WaiverCommand(102, WaiverKind.LateFee, 600m, 500m), Setup()));
        Assert.Throws<DomainException>(() => Apply(new WaiverCommand(102, WaiverKind.LateFee, 100m, 0m), Setup()));
    }

    [Fact]
    public void AdditionalCharge_AddsLines_AndCannotBeSaleConsideration()
    {
        var spec = new ChargeSpec(-1, 30, "Utility connection", ChargeCategory.UtilityCharges, 90_000m, ScheduleFrequency.Quarterly, 3, null, D(2025, 3, 1), null, null, 0, TaxTreatment.Exclusive, 10m, null);
        var o = Apply(new AdditionalChargeCommand(spec, null, false, 0m), Setup());
        Assert.Equal(16, o.Items.Count);
        Assert.Equal(1_490_000m, o.TotalAmount);
        Assert.Equal(9_000m, o.Items.Where(i => i.ChargeKey == -1).Sum(i => i.TaxAmount));
        Assert.Equal(3, o.Changes.Count(c => c.Operation == ScheduleChangeOperation.Added));
        Assert.Throws<DomainException>(() => Apply(new AdditionalChargeCommand(spec with { Category = ChargeCategory.SaleConsideration }, null, false, 0m), Setup()));
        Assert.Throws<DomainException>(() => Apply(new AdditionalChargeCommand(spec with { FirstDueDate = D(2024, 1, 1) }, null, false, 0m), Setup()));
    }

    [Fact]
    public void UnknownItems_AreRejected()
    {
        Assert.Throws<DomainException>(() => Apply(new DefermentCommand([999], 1, false), Setup()));
        Assert.Throws<DomainException>(() => Apply(new AdditionalInstallmentsCommand(1, 2), Setup())); // one-time charge
    }
}

public class Phase05SettlementTests
{
    private static List<PaymentScheduleItem> Items() =>
    [
        new() { Id = 1, LineNo = 1, DueDate = new(2025, 1, 31), Amount = 100m, Description = "1" },
        new() { Id = 2, LineNo = 2, DueDate = new(2025, 2, 28), Amount = 100m, Description = "2" },
        new() { Id = 3, LineNo = 3, DueDate = new(2025, 3, 31), Amount = 100m, Description = "3" },
    ];

    [Fact]
    public void PartialPayment_LeavesInstallmentPartiallyPaid()
    {
        var items = Items();
        Assert.Equal(0m, ScheduleSettlement.Allocate(items, 60m, new DateOnly(2025, 2, 5)));
        Assert.Equal(ScheduleItemStatus.PartiallyPaid, items[0].Status);
        Assert.Equal(40m, items[0].Outstanding);
        Assert.False(items[0].IsPaidInAdvance);
    }

    [Fact]
    public void AdvancePayment_SettlesFutureInstallments_AndReturnsSurplus()
    {
        var items = Items();
        var credit = ScheduleSettlement.Allocate(items, 350m, new DateOnly(2025, 1, 10));
        Assert.Equal(50m, credit);
        Assert.All(items, i => Assert.Equal(ScheduleItemStatus.Paid, i.Status));
        Assert.All(items, i => Assert.True(i.IsPaidInAdvance));
        Assert.Throws<DomainException>(() => items[0].RecordPayment(1m, new DateOnly(2025, 1, 10)));
    }

    [Fact]
    public void Waiver_And_Payment_Combine()
    {
        var i = Items()[0];
        i.ApplyWaiver(30m);
        Assert.Equal(ScheduleItemStatus.PartiallyPaid, i.Status);
        i.RecordPayment(70m, new DateOnly(2025, 2, 1));
        Assert.Equal(ScheduleItemStatus.Paid, i.Status);
        var w = Items()[1];
        w.ApplyWaiver(100m);
        Assert.Equal(ScheduleItemStatus.Waived, w.Status);
        Assert.Throws<DomainException>(() => Items()[2].ApplyWaiver(101m));
    }

    [Fact]
    public void AgreementText_PrintsTheSchedule_OnlyWhenOneExists()
    {
        var data = new AgreementData("Co", "AGR-000001", 2, "Schedule", new DateTime(2025, 1, 1), "BK-000001", new DateOnly(2025, 1, 1), "P1", "Project", PaymentMode.Installment, "Rs",
            [new AgreementOwner("CUS-1", "A", "1", 100m, true)], [new AgreementLine("PLT-1", "1", 5m, MeasurementUnit.Marla, 1_000m, 0m, 1_000m, "h")], [], 1_000m, 0m, 0m, 1_000m, 0m, null);
        var without = AgreementText.BuildAgreement(data);
        Assert.Contains("attached to this agreement when it is generated", without);
        var with = AgreementText.BuildAgreement(data with { ScheduleVersionNo = 1, Schedule = [new AgreementScheduleLine(1, "Down payment", new DateOnly(2025, 1, 1), 1_000m, 0m)] });
        Assert.Contains("PAYMENT SCHEDULE (version 1)", with);
        Assert.Contains("2025-01-01  Down payment", with);
        Assert.DoesNotContain("when it is generated", with);
    }
}

public class Phase05PlanResolverEdgeTests
{
    [Fact]
    public void ZeroRemainder_IsDropped_WhenTheBookingAmountCoversThePrice()
    {
        var comps = new[]
        {
            new ComponentSpec(1, 10, 5, ChargeComponentKind.DownPayment, "Full payment", ChargeCategory.SaleConsideration, AmountBasis.Remainder, null, null, ScheduleFrequency.OneTime,
                null, null, 0, 0, null, null, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null),
        };
        var r = PlanResolver.Resolve(comps, 5_000_000m, 5_000_000m, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), MoneyPolicy.Default, 99);
        Assert.Single(r);
        Assert.Equal(5_000_000m, r[0].Amount);
        Assert.Equal(new DateOnly(2025, 1, 1), r[0].FirstDueDate);
    }
}
