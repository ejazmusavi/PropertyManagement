using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Domain.Installments;

/// <summary>Company money policy used by every schedule calculation (decimals and rounding mode).</summary>
public sealed record MoneyPolicy(int Decimals, RoundingMode Mode)
{
    public static readonly MoneyPolicy Default = new(2, RoundingMode.Nearest);
    public decimal MinimumUnit => Decimals switch { 0 => 1m, 1 => 0.1m, 2 => 0.01m, 3 => 0.001m, _ => 0.0001m };
    public decimal Round(decimal v) => MoneyRounding.Round(v, Decimals, Mode);
}

/// <summary>A custom-schedule entry of an agreement charge: a due date and, for irregular schedules, an explicit amount.</summary>
public sealed record ScheduleEntry(DateOnly DueDate, decimal? Amount);

/// <summary>Everything the engine needs to schedule one agreement charge. <see cref="Key"/> is the agreement charge id (or a negative placeholder for previews).</summary>
public sealed record ChargeSpec(
    long Key, int SortOrder, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency,
    int? NumberOfInstallments, decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth, IReadOnlyList<ScheduleEntry>? CustomEntries,
    int GracePeriodDays, TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit);

public sealed record GeneratedItem(
    long ChargeKey, int LineNo, int InstallmentNo, int InstallmentCount, string Description, DateOnly DueDate, DateOnly GraceEndDate,
    decimal Amount, decimal TaxAmount, bool IsPartial);

public sealed record ChargeReconciliation(long ChargeKey, string Description, decimal ChargeAmount, decimal ScheduledAmount)
{
    public decimal Difference => ChargeAmount - ScheduledAmount;
}

public sealed record ScheduleResult(IReadOnlyList<GeneratedItem> Items, IReadOnlyList<ChargeReconciliation> Reconciliation)
{
    public decimal TotalAmount => Items.Sum(i => i.Amount);
    public decimal TotalTax => Items.Sum(i => i.TaxAmount);
    public bool IsReconciled => Reconciliation.All(r => r.Difference == 0m);
}

/// <summary>
/// Deterministic installment schedule engine (spec 5.5). Pure: the same charges and money policy always give the same schedule.
/// <list type="bullet">
/// <item>Regular frequencies (monthly 1, quarterly 3, half-yearly 6, annual 12 months) are computed from the first due date, never chained, so month-end
/// and leap-day dates do not drift: installment k falls on the due day (explicit <c>DueDayOfMonth</c> or the first date's day) clamped to the month's last day.</item>
/// <item>Amounts: regular installments are rounded to the rounding unit with the company rounding mode; the last installment absorbs the difference (partial
/// installment), so every charge reconciles exactly. A fixed installment amount gives ceil(total / amount) installments with a partial last one.</item>
/// <item>Custom dates split equally; custom dates with amounts (irregular schedules) must add up to the charge.</item>
/// <item>Tax is calculated once per charge and split over its installments the same way, so tax also reconciles.</item>
/// </list>
/// </summary>
public static class ScheduleGenerationService
{
    public const int MaxInstallments = 600;

    public static int IntervalMonths(ScheduleFrequency f) => f switch
    {
        ScheduleFrequency.Monthly => 1,
        ScheduleFrequency.Quarterly => 3,
        ScheduleFrequency.HalfYearly => 6,
        ScheduleFrequency.Annual => 12,
        _ => 0,
    };

    public static bool IsRegular(ScheduleFrequency f) => IntervalMonths(f) > 0;

    /// <summary>Adds whole months to the month of <paramref name="anchor"/> and places the date on <paramref name="day"/>, clamped to the month's last day.</summary>
    public static DateOnly AddMonthsClamped(DateOnly anchor, int months, int day)
    {
        var first = new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(months);
        return new DateOnly(first.Year, first.Month, Math.Min(day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    public static ScheduleResult Generate(IEnumerable<ChargeSpec> charges, MoneyPolicy policy)
    {
        var list = charges.ToList();
        var raw = new List<(ChargeSpec Spec, int No, int Count, DateOnly Due, decimal Amount, decimal Tax, bool Partial)>();
        foreach (var c in list)
        {
            var lines = ScheduleCharge(c, policy);
            raw.AddRange(lines.Select(l => (c, l.No, lines.Count, l.Due, l.Amount, l.Tax, l.Partial)));
        }
        var ordered = raw.OrderBy(r => r.Due).ThenBy(r => r.Spec.SortOrder).ThenBy(r => r.Spec.Key).ThenBy(r => r.No).ToList();
        var items = ordered.Select((r, i) => new GeneratedItem(r.Spec.Key, i + 1, r.No, r.Count, ItemDescription(r.Spec.Description, r.No, r.Count),
            r.Due, r.Due.AddDays(r.Spec.GracePeriodDays), r.Amount, r.Tax, r.Partial)).ToList();
        return new ScheduleResult(items, Reconcile(list.Select(c => (c.Key, c.Description, c.Amount)), items.Select(i => (i.ChargeKey, i.Amount))));
    }

    public static string ItemDescription(string chargeDescription, int no, int count) => count == 1 ? chargeDescription : $"{chargeDescription} {no}/{count}";

    public static IReadOnlyList<ChargeReconciliation> Reconcile(IEnumerable<(long Key, string Description, decimal Amount)> charges, IEnumerable<(long ChargeKey, decimal Amount)> items)
    {
        var sums = items.GroupBy(i => i.ChargeKey).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        return charges.Select(c => new ChargeReconciliation(c.Key, c.Description, c.Amount, sums.GetValueOrDefault(c.Key))).ToList();
    }

    /// <summary>Validates one charge and returns its installments in order (1-based numbers).</summary>
    public static IReadOnlyList<(int No, DateOnly Due, decimal Amount, decimal Tax, bool Partial)> ScheduleCharge(ChargeSpec c, MoneyPolicy policy)
    {
        Validate(c, policy);
        IReadOnlyList<DateOnly> dates;
        IReadOnlyList<decimal> amounts;
        var partialIndex = -1;
        switch (c.Frequency)
        {
            case ScheduleFrequency.OneTime:
                dates = [c.FirstDueDate];
                amounts = [c.Amount];
                break;
            case ScheduleFrequency.CustomDates:
                var entries = c.CustomEntries!;
                dates = entries.Select(e => e.DueDate).ToList();
                if (entries.All(e => e.Amount is not null)) amounts = entries.Select(e => e.Amount!.Value).ToList();
                else (amounts, partialIndex) = Split(c.Amount, entries.Count, policy, c.RoundingUnit);
                break;
            default:
                int count;
                if (c.InstallmentAmount is { } each)
                {
                    count = (int)Math.Ceiling(c.Amount / each);
                    if (count > MaxInstallments) throw new DomainException($"{c.Description}: the installment amount gives more than {MaxInstallments} installments.");
                    var fixedAmounts = Enumerable.Repeat(each, count).ToList();
                    fixedAmounts[^1] = c.Amount - each * (count - 1);
                    amounts = fixedAmounts;
                    partialIndex = fixedAmounts[^1] != each ? count - 1 : -1;
                }
                else
                {
                    count = c.NumberOfInstallments!.Value;
                    (amounts, partialIndex) = Split(c.Amount, count, policy, c.RoundingUnit);
                }
                dates = RegularDates(c.FirstDueDate, c.Frequency, count, c.DueDayOfMonth);
                break;
        }
        var taxes = SplitTax(c, amounts, policy);
        return dates.Select((d, i) => (i + 1, d, amounts[i], taxes[i], i == partialIndex)).ToList();
    }

    /// <summary>Installment k (0-based) of a regular schedule. The first installment is the first due date itself.</summary>
    public static IReadOnlyList<DateOnly> RegularDates(DateOnly first, ScheduleFrequency frequency, int count, int? dueDay)
    {
        var interval = IntervalMonths(frequency);
        if (interval == 0) throw new DomainException("Regular due dates need a monthly, quarterly, half-yearly or annual frequency.");
        var day = dueDay ?? first.Day;
        var result = new List<DateOnly>(count) { first };
        for (var k = 1; k < count; k++) result.Add(AddMonthsClamped(first, k * interval, day));
        return result;
    }

    /// <summary>Equal split rounded to the rounding unit; the last installment takes the difference. Returns the index of the balancing (partial) installment or -1.</summary>
    public static (IReadOnlyList<decimal> Amounts, int PartialIndex) Split(decimal total, int count, MoneyPolicy policy, decimal? roundingUnit)
    {
        if (count < 1) throw new DomainException("At least one installment is required.");
        if (count == 1) return ([total], -1);
        var unit = roundingUnit ?? policy.MinimumUnit;
        var regular = RoundToUnit(total / count, unit, policy.Mode);
        var last = total - regular * (count - 1);
        if (last <= 0m)
        {
            // Rounding up overshot the total: round the regular installments down instead so the last one stays positive.
            regular = RoundToUnit(total / count, unit, RoundingMode.Down);
            last = total - regular * (count - 1);
        }
        if (regular <= 0m || last <= 0m)
            throw new DomainException($"{policy.Round(total).ToString("N2", CultureInfo.InvariantCulture)} cannot be split into {count} installments with a rounding unit of {unit.ToString(CultureInfo.InvariantCulture)}.");
        var list = Enumerable.Repeat(regular, count).ToList();
        list[^1] = last;
        return (list, last != regular ? count - 1 : -1);
    }

    public static decimal RoundToUnit(decimal value, decimal unit, RoundingMode mode)
    {
        if (unit <= 0m) throw new DomainException("The rounding unit must be positive.");
        var q = value / unit;
        var r = mode switch
        {
            RoundingMode.Bankers => Math.Round(q, 0, MidpointRounding.ToEven),
            RoundingMode.Up => Math.Ceiling(q),
            RoundingMode.Down => Math.Floor(q),
            _ => Math.Round(q, 0, MidpointRounding.AwayFromZero),
        };
        return r * unit;
    }

    /// <summary>Total tax of a charge amount: added on top (exclusive) or contained in it (inclusive).</summary>
    public static decimal ChargeTax(decimal amount, TaxTreatment treatment, decimal ratePercent, MoneyPolicy policy) => treatment switch
    {
        TaxTreatment.Exclusive => policy.Round(amount * ratePercent / 100m),
        TaxTreatment.Inclusive => policy.Round(amount * ratePercent / (100m + ratePercent)),
        _ => 0m,
    };

    /// <summary>Splits the charge tax over the installments proportionally; the last installment takes the rounding difference.</summary>
    public static IReadOnlyList<decimal> SplitTax(ChargeSpec c, IReadOnlyList<decimal> amounts, MoneyPolicy policy) =>
        SplitTax(ChargeTax(c.Amount, c.TaxTreatment, c.TaxRatePercent, policy), c.Amount, amounts, policy);

    public static IReadOnlyList<decimal> SplitTax(decimal totalTax, decimal totalAmount, IReadOnlyList<decimal> amounts, MoneyPolicy policy)
    {
        if (totalTax == 0m || amounts.Count == 0) return amounts.Select(_ => 0m).ToList();
        var result = new List<decimal>(amounts.Count);
        var used = 0m;
        for (var i = 0; i < amounts.Count - 1; i++)
        {
            var t = policy.Round(totalTax * amounts[i] / totalAmount);
            result.Add(t);
            used += t;
        }
        var lastTax = totalTax - used;
        if (lastTax < 0m) throw new DomainException("The tax cannot be split over these installments; use fewer installments.");
        result.Add(lastTax);
        return result;
    }

    public static void Validate(ChargeSpec c, MoneyPolicy policy)
    {
        string Name() => string.IsNullOrWhiteSpace(c.Description) ? "Charge" : c.Description;
        if (c.Amount <= 0m) throw new DomainException($"{Name()}: the amount must be greater than zero.");
        if (policy.Round(c.Amount) != c.Amount) throw new DomainException($"{Name()}: the amount has more than {policy.Decimals} decimals.");
        if (c.GracePeriodDays is < 0 or > 365) throw new DomainException($"{Name()}: the grace period must be between 0 and 365 days.");
        if (c.TaxRatePercent is < 0m or > 100m) throw new DomainException($"{Name()}: the tax rate must be between 0 and 100%.");
        if (c.TaxTreatment is TaxTreatment.Inclusive or TaxTreatment.Exclusive && c.TaxRatePercent <= 0m) throw new DomainException($"{Name()}: enter the tax rate.");
        if (c.TaxTreatment is TaxTreatment.NotApplicable or TaxTreatment.Exempt && c.TaxRatePercent != 0m) throw new DomainException($"{Name()}: a tax rate applies only to inclusive or exclusive tax.");
        if (c.RoundingUnit is { } u)
        {
            if (u <= 0m) throw new DomainException($"{Name()}: the rounding unit must be positive.");
            if (decimal.Remainder(u, policy.MinimumUnit) != 0m) throw new DomainException($"{Name()}: the rounding unit must be a multiple of {policy.MinimumUnit.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (c.DueDayOfMonth is { } d && (d is < 1 or > 31 || !IsRegular(c.Frequency))) throw new DomainException($"{Name()}: a fixed due day (1-31) applies only to monthly, quarterly, half-yearly or annual schedules.");
        switch (c.Frequency)
        {
            case ScheduleFrequency.OneTime:
                if (c.NumberOfInstallments is not (null or 1) || c.InstallmentAmount is not null) throw new DomainException($"{Name()}: a one-time payment has exactly one installment.");
                if (c.CustomEntries is { Count: > 0 }) throw new DomainException($"{Name()}: custom dates apply only to custom schedules.");
                break;
            case ScheduleFrequency.CustomDates:
                var e = c.CustomEntries;
                if (e is null || e.Count == 0) throw new DomainException($"{Name()}: enter at least one custom due date.");
                if (e.Count > MaxInstallments) throw new DomainException($"{Name()}: at most {MaxInstallments} custom dates are allowed.");
                if (c.InstallmentAmount is not null || c.NumberOfInstallments is not null && c.NumberOfInstallments != e.Count) throw new DomainException($"{Name()}: the number of installments is the number of custom dates.");
                for (var i = 1; i < e.Count; i++) if (e[i].DueDate <= e[i - 1].DueDate) throw new DomainException($"{Name()}: custom due dates must be in increasing order without duplicates.");
                if (e[0].DueDate != c.FirstDueDate) throw new DomainException($"{Name()}: the first due date must be the first custom date.");
                var withAmount = e.Count(x => x.Amount is not null);
                if (withAmount != 0 && withAmount != e.Count) throw new DomainException($"{Name()}: give an amount for every custom date or for none (equal split).");
                if (withAmount == e.Count)
                {
                    if (e.Any(x => x.Amount <= 0m || policy.Round(x.Amount!.Value) != x.Amount)) throw new DomainException($"{Name()}: every custom amount must be positive with at most {policy.Decimals} decimals.");
                    var sum = e.Sum(x => x.Amount!.Value);
                    if (sum != c.Amount) throw new DomainException($"{Name()}: the custom amounts add up to {sum.ToString("N2", CultureInfo.InvariantCulture)} but the charge is {c.Amount.ToString("N2", CultureInfo.InvariantCulture)}.");
                }
                break;
            default:
                if (!IsRegular(c.Frequency)) throw new DomainException($"{Name()}: unknown frequency.");
                if (c.CustomEntries is { Count: > 0 }) throw new DomainException($"{Name()}: custom dates apply only to custom schedules.");
                if ((c.NumberOfInstallments is null) == (c.InstallmentAmount is null)) throw new DomainException($"{Name()}: give either the number of installments or the installment amount.");
                if (c.NumberOfInstallments is { } n && n is < 1 or > MaxInstallments) throw new DomainException($"{Name()}: the number of installments must be between 1 and {MaxInstallments}.");
                if (c.InstallmentAmount is { } a && (a <= 0m || a > c.Amount || policy.Round(a) != a)) throw new DomainException($"{Name()}: the installment amount must be positive, not more than the charge, with at most {policy.Decimals} decimals.");
                break;
        }
    }

    /// <summary>SHA-256 of the canonical schedule terms (lines, due dates, amounts, tax, grace). Payment progress is not part of the hash.</summary>
    public static string Hash(IEnumerable<(int LineNo, long ChargeId, int InstallmentNo, DateOnly DueDate, DateOnly GraceEndDate, decimal Amount, decimal TaxAmount)> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items.OrderBy(x => x.LineNo))
            sb.Append(CultureInfo.InvariantCulture, $"{i.LineNo}|{i.ChargeId}|{i.InstallmentNo}|{i.DueDate:yyyy-MM-dd}|{i.GraceEndDate:yyyy-MM-dd}|{i.Amount:0.00##}|{i.TaxAmount:0.00##}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
