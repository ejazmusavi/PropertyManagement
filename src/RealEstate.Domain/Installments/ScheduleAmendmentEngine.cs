using System.Globalization;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Installments;

/// <summary>Schedule-relevant state of an agreement charge.</summary>
public sealed record ChargeState(
    long Key, int SortOrder, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency, DateOnly FirstDueDate, int? DueDayOfMonth,
    int GracePeriodDays, long? LateFeeRuleId, TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, bool WaiverAllowed, decimal MaxWaiverPercent);

/// <summary>A line of the current schedule (with its payment progress).</summary>
public sealed record ItemState(
    long Id, long ChargeKey, int LineNo, int InstallmentNo, DateOnly DueDate, DateOnly GraceEndDate, decimal Amount, decimal TaxAmount, bool IsPartial,
    decimal Paid, decimal Waived, decimal LateFeeWaived, DateOnly? LastPaymentDate)
{
    public decimal Outstanding => Amount - Paid - Waived;
    public bool HasSettlement => Paid > 0m || Waived > 0m;
}

public abstract record AmendmentCommand;
/// <summary>Re-spreads the outstanding balance of a charge with new terms. Settled installments are kept; a partly paid one is closed at its paid amount.</summary>
public sealed record RescheduleCommand(long ChargeKey, ScheduleFrequency Frequency, int? NumberOfInstallments, decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth,
    IReadOnlyList<ScheduleEntry>? CustomEntries) : AmendmentCommand;
public sealed record DefermentCommand(IReadOnlyList<long> ItemIds, int Months, bool CascadeSubsequent) : AmendmentCommand;
public sealed record ReductionCommand(long ItemId, decimal NewAmount, ReductionRedistribution Redistribution, DateOnly? NewDueDate) : AmendmentCommand;
public sealed record AdditionalInstallmentsCommand(long ChargeKey, int AdditionalCount) : AmendmentCommand;
public sealed record DueDateChange(long ItemId, DateOnly NewDueDate);
public sealed record DueDateChangeCommand(IReadOnlyList<DueDateChange> Changes) : AmendmentCommand;
public sealed record PenaltyChangeCommand(long ChargeKey, long? LateFeeRuleId, int GracePeriodDays) : AmendmentCommand;
/// <summary><paramref name="LateFeeDue"/> is the late fee of the item as of today (computed by the caller from the item's rule).</summary>
public sealed record WaiverCommand(long ItemId, WaiverKind Kind, decimal Amount, decimal LateFeeDue) : AmendmentCommand;
public sealed record AdditionalChargeCommand(ChargeSpec Charge, long? LateFeeRuleId, bool WaiverAllowed, decimal MaxWaiverPercent) : AmendmentCommand;

public sealed record AmendedItem(
    long? PreviousItemId, long ChargeKey, int LineNo, int InstallmentNo, int InstallmentCount, string Description, DateOnly DueDate, DateOnly GraceEndDate,
    decimal Amount, decimal TaxAmount, bool IsPartial, decimal Paid, decimal Waived, decimal LateFeeWaived, DateOnly? LastPaymentDate)
{
    public decimal Outstanding => Amount - Paid - Waived;
    public ScheduleItemStatus Status => Outstanding == 0m ? (Paid == 0m ? ScheduleItemStatus.Waived : ScheduleItemStatus.Paid)
        : Paid > 0m || Waived > 0m ? ScheduleItemStatus.PartiallyPaid : ScheduleItemStatus.Pending;
}

/// <summary>One recorded change. Item references point to the previous version's item id (FromItemId) or, for new lines, to an index in <see cref="AmendmentOutcome.Items"/>.</summary>
public sealed record ChangeDraft(ScheduleChangeOperation Operation, long ChargeKey, long? FromItemId, int? ToItemIndex, DateOnly? OldDueDate, DateOnly? NewDueDate,
    decimal? OldAmount, decimal? NewAmount, string? Note);

public sealed record AmendmentOutcome(IReadOnlyList<AmendedItem> Items, IReadOnlyList<ChangeDraft> Changes, IReadOnlyList<ChargeState> Charges, IReadOnlyList<ChargeReconciliation> Reconciliation)
{
    public decimal TotalAmount => Items.Sum(i => i.Amount);
    public bool IsReconciled => Reconciliation.All(r => r.Difference == 0m);
}

/// <summary>
/// Applies one amendment (spec 5.6) to the current schedule and returns the complete next schedule version plus a change list. Pure and deterministic.
/// Installments that already carry a payment or waiver are never moved or re-priced (except closing a partly paid line at its settled amount when the
/// balance is re-spread); every charge must still reconcile to its amount afterwards.
/// </summary>
public static class ScheduleAmendmentEngine
{
    private sealed class Work
    {
        public long? Prev; public long ChargeKey; public int Order; public DateOnly Due; public DateOnly Grace; public decimal Amount; public decimal Tax; public bool Partial;
        public decimal Paid; public decimal Waived; public decimal LateFeeWaived; public DateOnly? LastPaid;
        public decimal Outstanding => Amount - Paid - Waived;
        public bool HasSettlement => Paid > 0m || Waived > 0m;
        public bool IsPending => !HasSettlement;
    }

    public static AmendmentOutcome Apply(IReadOnlyList<ChargeState> charges, IReadOnlyList<ItemState> items, AmendmentCommand command, MoneyPolicy policy, DateOnly today)
    {
        var chargeMap = charges.ToDictionary(c => c.Key);
        var work = items.OrderBy(i => i.LineNo).Select((i, n) => new Work
        {
            Prev = i.Id, ChargeKey = i.ChargeKey, Order = n, Due = i.DueDate, Grace = i.GraceEndDate, Amount = i.Amount, Tax = i.TaxAmount, Partial = i.IsPartial,
            Paid = i.Paid, Waived = i.Waived, LateFeeWaived = i.LateFeeWaived, LastPaid = i.LastPaymentDate,
        }).ToList();
        var changes = new List<(ChangeDraft Draft, Work? Target)>();
        var touched = new HashSet<long>();
        var nextOrder = work.Count;

        Work Item(long id) => work.FirstOrDefault(w => w.Prev == id) ?? throw new DomainException($"Installment {id} is not part of the current schedule.");
        ChargeState Charge(long key) => chargeMap.TryGetValue(key, out var c) ? c : throw new DomainException("The charge is not part of this agreement.");
        void Record(ScheduleChangeOperation op, Work w, DateOnly? oldDue, DateOnly? newDue, decimal? oldAmt, decimal? newAmt, string? note) =>
            changes.Add((new ChangeDraft(op, w.ChargeKey, w.Prev, null, oldDue, newDue, oldAmt, newAmt, note), w));
        Work Add(long chargeKey, DateOnly due, decimal amount, bool partial)
        {
            var c = Charge(chargeKey);
            var w = new Work { ChargeKey = chargeKey, Order = nextOrder++, Due = due, Grace = due.AddDays(c.GracePeriodDays), Amount = amount, Partial = partial };
            work.Add(w);
            Record(ScheduleChangeOperation.Added, w, null, due, null, amount, null);
            return w;
        }
        void Money(decimal v, string what)
        {
            if (v <= 0m) throw new DomainException($"The {what} must be greater than zero.");
            if (policy.Round(v) != v) throw new DomainException($"The {what} has more than {policy.Decimals} decimals.");
        }

        void Reschedule(ChargeState c, ScheduleFrequency freq, int? count, decimal? each, DateOnly first, int? dueDay, IReadOnlyList<ScheduleEntry>? entries, bool requireFuture)
        {
            if (requireFuture && first < today) throw new DomainException("The new first due date cannot be in the past.");
            foreach (var w in work.Where(w => w.ChargeKey == c.Key).ToList())
            {
                if (w.IsPending)
                {
                    work.Remove(w);
                    Record(ScheduleChangeOperation.Removed, w, w.Due, null, w.Amount, null, "Replaced by the new terms");
                }
                else if (w.Outstanding > 0m)
                {
                    var old = w.Amount;
                    w.Amount = w.Paid + w.Waived; w.Partial = false;
                    Record(ScheduleChangeOperation.Settled, w, w.Due, w.Due, old, w.Amount, "Closed at the settled amount; balance re-spread");
                }
            }
            var outstanding = c.Amount - work.Where(w => w.ChargeKey == c.Key).Sum(w => w.Amount);
            if (outstanding <= 0m) throw new DomainException($"{c.Description}: nothing is outstanding to reschedule.");
            var spec = new ChargeSpec(c.Key, c.SortOrder, c.Description, c.Category, outstanding, freq, count, each, first, dueDay, entries, c.GracePeriodDays,
                c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit);
            foreach (var l in ScheduleGenerationService.ScheduleCharge(spec, policy)) Add(c.Key, l.Due, l.Amount, l.Partial);
            chargeMap[c.Key] = c with { Frequency = freq, FirstDueDate = chargeMap[c.Key].FirstDueDate, DueDayOfMonth = dueDay };
            touched.Add(c.Key);
        }

        switch (command)
        {
            case RescheduleCommand r:
            {
                var c = Charge(r.ChargeKey);
                if (r.InstallmentAmount is { } ia) Money(ia, "installment amount");
                Reschedule(c, r.Frequency, r.NumberOfInstallments, r.InstallmentAmount, r.FirstDueDate, r.DueDayOfMonth, r.CustomEntries, requireFuture: true);
                break;
            }
            case AdditionalInstallmentsCommand a:
            {
                var c = Charge(a.ChargeKey);
                if (!ScheduleGenerationService.IsRegular(c.Frequency)) throw new DomainException($"{c.Description}: additional installments apply to regular (monthly, quarterly, half-yearly, annual) schedules; use rescheduling instead.");
                if (a.AdditionalCount is < 1 or > 120) throw new DomainException("Add between 1 and 120 installments.");
                var pending = work.Where(w => w.ChargeKey == c.Key && w.IsPending).OrderBy(w => w.Due).ToList();
                var partly = work.Count(w => w.ChargeKey == c.Key && w.HasSettlement && w.Outstanding > 0m);
                if (pending.Count + partly == 0) throw new DomainException($"{c.Description}: there are no unpaid installments to extend.");
                var first = pending.Count > 0 ? pending[0].Due : work.Where(w => w.ChargeKey == c.Key).Max(w => w.Due);
                var total = pending.Count + a.AdditionalCount;
                if (total > ScheduleGenerationService.MaxInstallments) throw new DomainException($"At most {ScheduleGenerationService.MaxInstallments} installments are allowed.");
                Reschedule(c, c.Frequency, total, null, first, c.DueDayOfMonth ?? c.FirstDueDate.Day, null, requireFuture: false);
                break;
            }
            case DefermentCommand d:
            {
                if (d.Months is < 1 or > 120) throw new DomainException("Defer by 1 to 120 months.");
                if (d.ItemIds.Count == 0) throw new DomainException("Choose at least one installment to defer.");
                var selected = d.ItemIds.Distinct().Select(Item).ToList();
                if (selected.Any(w => !w.IsPending)) throw new DomainException("Only installments without any payment or waiver can be deferred.");
                var targets = selected.ToHashSet();
                if (d.CascadeSubsequent)
                    foreach (var g in selected.GroupBy(w => w.ChargeKey))
                    {
                        var from = g.Min(w => w.Due);
                        foreach (var w in work.Where(w => w.ChargeKey == g.Key && w.IsPending && w.Due > from)) targets.Add(w);
                    }
                foreach (var w in targets.OrderBy(w => w.Order))
                {
                    var old = w.Due;
                    w.Due = ScheduleGenerationService.AddMonthsClamped(old, d.Months, old.Day);
                    w.Grace = w.Due.AddDays(Charge(w.ChargeKey).GracePeriodDays);
                    Record(ScheduleChangeOperation.DueDateChanged, w, old, w.Due, w.Amount, w.Amount, $"Deferred {d.Months} month(s)");
                    touched.Add(w.ChargeKey);
                }
                break;
            }
            case ReductionCommand r:
            {
                var w = Item(r.ItemId);
                if (!w.IsPending) throw new DomainException("Only installments without any payment or waiver can be reduced.");
                Money(r.NewAmount, "new installment amount");
                if (r.NewAmount >= w.Amount) throw new DomainException("The new amount must be lower than the current installment amount.");
                var diff = w.Amount - r.NewAmount;
                var old = w.Amount;
                w.Amount = r.NewAmount; w.Partial = false;
                Record(ScheduleChangeOperation.AmountChanged, w, w.Due, w.Due, old, w.Amount, "Installment reduced");
                var later = work.Where(x => x != w && x.ChargeKey == w.ChargeKey && x.IsPending && (x.Due > w.Due || x.Due == w.Due && x.Order > w.Order)).OrderBy(x => x.Due).ThenBy(x => x.Order).ToList();
                switch (r.Redistribution)
                {
                    case ReductionRedistribution.SpreadOverRemaining:
                        if (later.Count == 0) throw new DomainException("There are no later unpaid installments of this charge to spread the difference over.");
                        var (parts, _) = ScheduleGenerationService.Split(diff, later.Count, policy, null);
                        for (var i = 0; i < later.Count; i++)
                        {
                            var o = later[i].Amount; later[i].Amount += parts[i];
                            Record(ScheduleChangeOperation.AmountChanged, later[i], later[i].Due, later[i].Due, o, later[i].Amount, "Share of the reduced amount");
                        }
                        break;
                    case ReductionRedistribution.AddToLast:
                        if (later.Count == 0) throw new DomainException("There is no later unpaid installment of this charge to add the difference to.");
                        var last = later[^1]; var lo = last.Amount; last.Amount += diff;
                        Record(ScheduleChangeOperation.AmountChanged, last, last.Due, last.Due, lo, last.Amount, "Reduced amount added");
                        break;
                    case ReductionRedistribution.NewInstallment:
                        if (r.NewDueDate is not { } nd) throw new DomainException("Enter the due date of the new installment.");
                        if (nd < today) throw new DomainException("The new installment cannot fall due in the past.");
                        Add(w.ChargeKey, nd, diff, false);
                        break;
                    default: throw new DomainException("Choose how the reduced amount is redistributed.");
                }
                touched.Add(w.ChargeKey);
                break;
            }
            case DueDateChangeCommand dc:
            {
                if (dc.Changes.Count == 0) throw new DomainException("Enter at least one new due date.");
                if (dc.Changes.Select(c => c.ItemId).Distinct().Count() != dc.Changes.Count) throw new DomainException("Each installment can be changed only once per amendment.");
                foreach (var ch in dc.Changes)
                {
                    var w = Item(ch.ItemId);
                    if (w.Outstanding <= 0m) throw new DomainException("A settled installment cannot be moved.");
                    if (ch.NewDueDate == w.Due) throw new DomainException($"The new due date of installment {ch.ItemId} is the same as the current one.");
                    var old = w.Due;
                    w.Due = ch.NewDueDate; w.Grace = w.Due.AddDays(Charge(w.ChargeKey).GracePeriodDays);
                    Record(ScheduleChangeOperation.DueDateChanged, w, old, w.Due, w.Amount, w.Amount, "Due date changed");
                    touched.Add(w.ChargeKey);
                }
                break;
            }
            case PenaltyChangeCommand p:
            {
                var c = Charge(p.ChargeKey);
                if (p.GracePeriodDays is < 0 or > 365) throw new DomainException("The grace period must be between 0 and 365 days.");
                if (p.LateFeeRuleId == c.LateFeeRuleId && p.GracePeriodDays == c.GracePeriodDays) throw new DomainException("The late fee rule and grace period are unchanged.");
                foreach (var w in work.Where(w => w.ChargeKey == c.Key && w.Outstanding > 0m))
                {
                    var g = w.Due.AddDays(p.GracePeriodDays);
                    if (g == w.Grace) continue;
                    var old = w.Grace; w.Grace = g;
                    Record(ScheduleChangeOperation.GraceChanged, w, old, g, w.Amount, w.Amount, $"Grace period {c.GracePeriodDays} -> {p.GracePeriodDays} days");
                }
                chargeMap[c.Key] = c with { GracePeriodDays = p.GracePeriodDays, LateFeeRuleId = p.LateFeeRuleId };
                break;
            }
            case WaiverCommand wv:
            {
                var w = Item(wv.ItemId);
                var c = Charge(w.ChargeKey);
                if (!c.WaiverAllowed) throw new DomainException($"{c.Description}: waivers are not allowed for this charge.");
                Money(wv.Amount, "waiver amount");
                if (wv.Kind == WaiverKind.Principal)
                {
                    if (wv.Amount > w.Outstanding) throw new DomainException($"The waiver exceeds the outstanding amount ({w.Outstanding.ToString("N2", CultureInfo.InvariantCulture)}).");
                    var limit = policy.Round(w.Amount * c.MaxWaiverPercent / 100m);
                    if (w.Waived + wv.Amount > limit) throw new DomainException($"Waivers on this installment cannot exceed {c.MaxWaiverPercent.ToString("0.##", CultureInfo.InvariantCulture)}% ({limit.ToString("N2", CultureInfo.InvariantCulture)}).");
                    var old = w.Waived; w.Waived += wv.Amount;
                    Record(ScheduleChangeOperation.Waived, w, w.Due, w.Due, old, w.Waived, "Principal waived");
                }
                else if (wv.Kind == WaiverKind.LateFee)
                {
                    var available = wv.LateFeeDue - w.LateFeeWaived;
                    if (available <= 0m) throw new DomainException("No late fee is due on this installment.");
                    if (wv.Amount > available) throw new DomainException($"The waiver exceeds the late fee due ({available.ToString("N2", CultureInfo.InvariantCulture)}).");
                    var old = w.LateFeeWaived; w.LateFeeWaived += wv.Amount;
                    Record(ScheduleChangeOperation.Waived, w, w.Due, w.Due, old, w.LateFeeWaived, "Late fee waived");
                }
                else throw new DomainException("Unknown waiver type.");
                break;
            }
            case AdditionalChargeCommand ac:
            {
                var s = ac.Charge;
                if (s.Category == ChargeCategory.SaleConsideration) throw new DomainException("An additional charge cannot be part of the sale consideration; choose development, utility, government fee or other charges.");
                if (chargeMap.ContainsKey(s.Key)) throw new DomainException("Duplicate charge key.");
                if (s.FirstDueDate < today) throw new DomainException("An additional charge cannot fall due in the past.");
                chargeMap[s.Key] = new ChargeState(s.Key, s.SortOrder, s.Description, s.Category, s.Amount, s.Frequency, s.FirstDueDate, s.DueDayOfMonth, s.GracePeriodDays,
                    ac.LateFeeRuleId, s.TaxTreatment, s.TaxRatePercent, s.RoundingUnit, ac.WaiverAllowed, ac.MaxWaiverPercent);
                foreach (var l in ScheduleGenerationService.ScheduleCharge(s, policy)) Add(s.Key, l.Due, l.Amount, l.Partial);
                touched.Add(s.Key);
                break;
            }
            default: throw new DomainException("Unknown amendment.");
        }

        // ---- re-tax touched charges: settled lines keep their tax, the rest of the charge tax is split over the open lines ----
        foreach (var key in touched)
        {
            var c = chargeMap[key];
            var lines = work.Where(w => w.ChargeKey == key).OrderBy(w => w.Due).ThenBy(w => w.Order).ToList();
            var totalTax = ScheduleGenerationService.ChargeTax(c.Amount, c.TaxTreatment, c.TaxRatePercent, policy);
            var open = lines.Where(w => w.IsPending).ToList();
            var remaining = totalTax - lines.Where(w => !w.IsPending).Sum(w => w.Tax);
            var taxes = open.Count == 0 ? [] : ScheduleGenerationService.SplitTax(Math.Max(remaining, 0m), open.Sum(w => w.Amount), open.Select(w => w.Amount).ToList(), policy);
            for (var i = 0; i < open.Count; i++) open[i].Tax = taxes[i];
        }

        // ---- validate and number ----
        foreach (var w in work) if (w.Amount <= 0m) throw new DomainException("Every installment must keep a positive amount.");
        var numbered = new Dictionary<Work, (int No, int Count)>();
        foreach (var g in work.GroupBy(w => w.ChargeKey))
        {
            var ordered = g.OrderBy(w => w.Due).ThenBy(w => w.Order).ToList();
            for (var i = 0; i < ordered.Count; i++) numbered[ordered[i]] = (i + 1, ordered.Count);
        }
        var final = work.OrderBy(w => w.Due).ThenBy(w => chargeMap[w.ChargeKey].SortOrder).ThenBy(w => w.ChargeKey).ThenBy(w => numbered[w].No).ToList();
        var result = final.Select((w, i) => new AmendedItem(w.Prev, w.ChargeKey, i + 1, numbered[w].No, numbered[w].Count,
            ScheduleGenerationService.ItemDescription(chargeMap[w.ChargeKey].Description, numbered[w].No, numbered[w].Count), w.Due, w.Grace, w.Amount, w.Tax, w.Partial,
            w.Paid, w.Waived, w.LateFeeWaived, w.LastPaid)).ToList();
        var index = final.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);
        var drafts = changes.Select(c => c.Target is not null && index.TryGetValue(c.Target, out var ix) ? c.Draft with { ToItemIndex = ix } : c.Draft).ToList();
        var states = chargeMap.Values.OrderBy(c => c.SortOrder).ThenBy(c => c.Key).ToList();
        var rec = ScheduleGenerationService.Reconcile(states.Select(c => (c.Key, c.Description, c.Amount)), result.Select(i => (i.ChargeKey, i.Amount)));
        var bad = rec.FirstOrDefault(r => r.Difference != 0m);
        if (bad is not null) throw new DomainException($"{bad.Description}: the amended schedule does not reconcile (difference {bad.Difference.ToString("N2", CultureInfo.InvariantCulture)}).");
        return new AmendmentOutcome(result, drafts, states, rec);
    }

    public static ItemState ToState(PaymentScheduleItem i) => new(i.Id, i.AgreementChargeId, i.LineNo, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount, i.IsPartial,
        i.AmountPaid, i.AmountWaived, i.LateFeeWaived, i.LastPaymentDate);
}

/// <summary>
/// How customer money maps onto a schedule (representation of partial and advance payments; Phase 6 records the actual payments).
/// Money is applied to the oldest open installment first; a payment that does not cover an installment leaves it PartiallyPaid; money beyond
/// the due installments settles future installments in advance; anything beyond the whole schedule is returned as unallocated credit.
/// </summary>
public static class ScheduleSettlement
{
    public static decimal Allocate(IEnumerable<PaymentScheduleItem> items, decimal amount, DateOnly paidOn)
    {
        if (amount <= 0m) throw new DomainException("A payment amount must be greater than zero.");
        var left = amount;
        foreach (var i in items.Where(i => i.Outstanding > 0m).OrderBy(i => i.DueDate).ThenBy(i => i.LineNo))
        {
            if (left == 0m) break;
            var part = Math.Min(left, i.Outstanding);
            i.RecordPayment(part, paidOn);
            left -= part;
        }
        return left;
    }
}
