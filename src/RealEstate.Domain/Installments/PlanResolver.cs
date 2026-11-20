using System.Globalization;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Installments;

/// <summary>A custom-schedule entry of a plan template: months after the schedule start and, optionally, the share of the component (percent).</summary>
public sealed record TemplateEntry(int OffsetMonths, decimal? SharePercent);

/// <summary>Plan component as needed to resolve it into an agreement charge.</summary>
public sealed record ComponentSpec(
    long ComponentId, int SortOrder, long ChargeTypeId, ChargeComponentKind Kind, string Name, ChargeCategory Category, AmountBasis Basis, decimal? Amount, decimal? Percent,
    ScheduleFrequency Frequency, int? NumberOfInstallments, decimal? InstallmentAmount, int FirstDueOffsetMonths, int FirstDueOffsetDays, int? DueDayOfMonth,
    IReadOnlyList<TemplateEntry>? CustomEntries, int GracePeriodDays, long? LateFeeRuleId, bool WaiverAllowed, decimal MaxWaiverPercent,
    TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, string? Remarks);

/// <summary>A resolved agreement charge (absolute amount and dates) produced from a plan component.</summary>
public sealed record ResolvedCharge(
    long? SourceComponentId, int SortOrder, long ChargeTypeId, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency,
    int? NumberOfInstallments, decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth, IReadOnlyList<ScheduleEntry>? CustomEntries,
    int GracePeriodDays, long? LateFeeRuleId, bool WaiverAllowed, decimal MaxWaiverPercent, TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, string? Remarks);

/// <summary>
/// Turns a plan template into agreement charges for one agreement.
/// Price basis = the booking's total payable. Sale-consideration components split that basis: fixed amounts, percentages of it, and at most one
/// Remainder component that takes whatever is left so the agreement reconciles exactly. The booking amount (due at booking) becomes the down payment.
/// Other categories (development, utility, government fees, other) are additional charges with fixed amounts or percentages of the basis.
/// </summary>
public static class PlanResolver
{
    public static IReadOnlyList<ResolvedCharge> Resolve(IReadOnlyList<ComponentSpec> components, decimal priceBasis, decimal bookingAmount, DateOnly bookingDate, DateOnly startDate,
        MoneyPolicy policy, long downPaymentChargeTypeId)
    {
        if (components.Count == 0) throw new DomainException("The plan has no components.");
        if (components.Count(c => c.Basis == AmountBasis.Remainder) > 1) throw new DomainException("A plan can have only one remainder component.");
        var result = new List<(ComponentSpec? Src, ResolvedCharge Charge)>();
        var downPaymentUsed = false;
        foreach (var c in components.OrderBy(c => c.SortOrder).ThenBy(c => c.ComponentId))
        {
            if (c.Basis == AmountBasis.Remainder) { result.Add((c, null!)); continue; }
            decimal amount;
            DateOnly first;
            if (c.Kind == ChargeComponentKind.DownPayment && c.Category == ChargeCategory.SaleConsideration && bookingAmount > 0m && !downPaymentUsed)
            {
                // The down payment agreed at booking wins over the template amount and falls due on the booking date.
                amount = bookingAmount; first = bookingDate; downPaymentUsed = true;
                result.Add((c, Build(c, amount, first, policy, startDate, oneTime: true)));
                continue;
            }
            amount = c.Basis == AmountBasis.FixedAmount ? c.Amount ?? 0m : policy.Round(priceBasis * (c.Percent ?? 0m) / 100m);
            first = FirstDue(c, startDate);
            if (amount <= 0m) throw new DomainException($"{c.Name}: the resolved amount is zero.");
            result.Add((c, Build(c, amount, first, policy, startDate, oneTime: false)));
        }
        if (bookingAmount > 0m && !downPaymentUsed)
            result.Insert(0, (null, new ResolvedCharge(null, 0, downPaymentChargeTypeId, "Down payment (due at booking)", ChargeCategory.SaleConsideration, bookingAmount,
                ScheduleFrequency.OneTime, null, null, bookingDate, null, null, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, "Booking amount agreed at booking")));

        var remainderIndex = result.FindIndex(r => r.Src?.Basis == AmountBasis.Remainder);
        if (remainderIndex >= 0)
        {
            var c = result[remainderIndex].Src!;
            if (c.Category != ChargeCategory.SaleConsideration) throw new DomainException($"{c.Name}: only a sale-consideration component can take the remainder.");
            var allocated = result.Where((r, i) => i != remainderIndex && r.Charge.Category == ChargeCategory.SaleConsideration).Sum(r => r.Charge.Amount);
            var rest = priceBasis - allocated;
            if (rest < 0m) throw new DomainException($"The other components already cover {allocated.ToString("N2", CultureInfo.InvariantCulture)}, more than the price {priceBasis.ToString("N2", CultureInfo.InvariantCulture)}; '{c.Name}' cannot take a remainder.");
            if (rest == 0m) result.RemoveAt(remainderIndex); // fully covered (e.g. a cash sale paid in full at booking)
            else result[remainderIndex] = (c, Build(c, rest, FirstDue(c, startDate), policy, startDate, oneTime: false));
        }
        return result.Select(r => r.Charge).ToList();
    }

    public static DateOnly FirstDue(ComponentSpec c, DateOnly start)
    {
        if (c.Frequency == ScheduleFrequency.CustomDates && c.CustomEntries is { Count: > 0 } e) return start.AddMonths(e[0].OffsetMonths);
        return start.AddMonths(c.FirstDueOffsetMonths).AddDays(c.FirstDueOffsetDays);
    }

    private static ResolvedCharge Build(ComponentSpec c, decimal amount, DateOnly first, MoneyPolicy policy, DateOnly start, bool oneTime)
    {
        IReadOnlyList<ScheduleEntry>? entries = null;
        var frequency = oneTime ? ScheduleFrequency.OneTime : c.Frequency;
        if (frequency == ScheduleFrequency.CustomDates)
        {
            var t = c.CustomEntries ?? throw new DomainException($"{c.Name}: the custom schedule has no entries.");
            if (t.All(x => x.SharePercent is not null))
            {
                if (t.Sum(x => x.SharePercent!.Value) != 100m) throw new DomainException($"{c.Name}: the custom shares must total 100%.");
                var list = new List<ScheduleEntry>();
                var used = 0m;
                for (var i = 0; i < t.Count; i++)
                {
                    var a = i == t.Count - 1 ? amount - used : policy.Round(amount * t[i].SharePercent!.Value / 100m);
                    used += a;
                    list.Add(new ScheduleEntry(start.AddMonths(t[i].OffsetMonths), a));
                }
                entries = list;
            }
            else entries = t.Select(x => new ScheduleEntry(start.AddMonths(x.OffsetMonths), null)).ToList();
            first = entries[0].DueDate;
        }
        // A fixed installment amount larger than the resolved amount degrades to a single installment.
        var installmentAmount = oneTime ? null : c.InstallmentAmount is { } ia ? Math.Min(ia, amount) : (decimal?)null;
        return new ResolvedCharge(c.ComponentId, c.SortOrder, c.ChargeTypeId, c.Name, c.Category, amount, frequency,
            oneTime || frequency == ScheduleFrequency.OneTime ? null : frequency == ScheduleFrequency.CustomDates ? null : installmentAmount is null ? c.NumberOfInstallments : null,
            installmentAmount, first, oneTime ? null : c.DueDayOfMonth, entries, c.GracePeriodDays, c.LateFeeRuleId, c.WaiverAllowed, c.MaxWaiverPercent,
            c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit, c.Remarks);
    }
}

/// <summary>Late-fee rule terms (spec 5.4 "Late fee rules").</summary>
public sealed record LateFeeTerms(LateFeeMethod Method, decimal Value, decimal? MaxAmount, decimal? MaxPercentOfInstallment);

/// <summary>
/// Late fee for one installment as of a date. Nothing is due on or before the grace end date or when nothing is outstanding.
/// Per-day methods count calendar days after the grace end; per-month methods count started months after the grace end.
/// Percent methods apply to the outstanding amount. Caps: an absolute maximum and/or a percentage of the installment amount.
/// </summary>
public static class LateFeeCalculator
{
    public static int OverdueDays(DateOnly graceEnd, DateOnly asOf) => asOf > graceEnd ? asOf.DayNumber - graceEnd.DayNumber : 0;

    public static int StartedMonths(DateOnly graceEnd, DateOnly asOf)
    {
        if (asOf <= graceEnd) return 0;
        var full = (asOf.Year - graceEnd.Year) * 12 + asOf.Month - graceEnd.Month;
        if (ScheduleGenerationService.AddMonthsClamped(graceEnd, full, graceEnd.Day) > asOf) full--;
        return ScheduleGenerationService.AddMonthsClamped(graceEnd, full, graceEnd.Day) < asOf ? full + 1 : full;
    }

    public static decimal Compute(LateFeeTerms t, decimal installmentAmount, decimal outstanding, DateOnly graceEnd, DateOnly asOf, MoneyPolicy policy)
    {
        if (outstanding <= 0m || asOf <= graceEnd) return 0m;
        var days = OverdueDays(graceEnd, asOf);
        var months = StartedMonths(graceEnd, asOf);
        var fee = t.Method switch
        {
            LateFeeMethod.FixedOneTime => t.Value,
            LateFeeMethod.PercentOneTime => outstanding * t.Value / 100m,
            LateFeeMethod.FixedPerDay => t.Value * days,
            LateFeeMethod.PercentPerDay => outstanding * t.Value / 100m * days,
            LateFeeMethod.FixedPerMonth => t.Value * months,
            LateFeeMethod.PercentPerMonth => outstanding * t.Value / 100m * months,
            _ => 0m,
        };
        if (t.MaxAmount is { } max) fee = Math.Min(fee, max);
        if (t.MaxPercentOfInstallment is { } pct) fee = Math.Min(fee, installmentAmount * pct / 100m);
        return policy.Round(Math.Max(fee, 0m));
    }

    public static void Validate(LateFeeTerms t)
    {
        if (!Enum.IsDefined(t.Method)) throw new DomainException("Unknown late fee method.");
        if (t.Value <= 0m) throw new DomainException("The late fee value must be greater than zero.");
        if (t.Method is LateFeeMethod.PercentOneTime or LateFeeMethod.PercentPerDay or LateFeeMethod.PercentPerMonth && t.Value > 100m) throw new DomainException("A late fee percentage cannot exceed 100%.");
        if (t.MaxAmount is <= 0m) throw new DomainException("The maximum late fee must be greater than zero.");
        if (t.MaxPercentOfInstallment is <= 0m or > 100m) throw new DomainException("The maximum percentage of the installment must be between 0 and 100.");
    }
}
