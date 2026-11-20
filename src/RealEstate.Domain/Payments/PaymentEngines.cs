using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Payments;

/// <summary>An open schedule line as seen by the allocation engine.</summary>
public sealed record AllocItem(long ItemId, long ChargeId, int LineNo, DateOnly DueDate, decimal Outstanding);

/// <summary>A requested or planned amount for one schedule line.</summary>
public sealed record AllocLine(long ItemId, decimal Amount);

/// <summary>Result of planning an allocation: amounts per line, amount held as advance, amount left unallocated. Lines + Advance + Unallocated = amount.</summary>
public sealed record AllocationPlan(IReadOnlyList<AllocLine> Lines, decimal Advance, decimal Unallocated)
{
    public decimal Allocated => Lines.Sum(l => l.Amount);
}

/// <summary>
/// Deterministic payment allocation (spec 6.4): oldest-due-first, manual, specific installment, charge-specific, advance balance and unallocated balance.
/// Pure: the caller loads the open lines and persists the plan. Never allocates more than the amount or more than a line's outstanding balance.
/// </summary>
public static class AllocationEngine
{
    public static AllocationPlan Plan(IReadOnlyList<AllocItem> items, decimal amount, AllocationMode mode, IReadOnlyList<AllocLine>? lines = null,
        long? chargeId = null, bool remainderToAdvance = false)
    {
        if (amount <= 0m) throw new DomainException("The amount to allocate must be greater than zero.");
        if (amount != decimal.Round(amount, 2)) throw new DomainException("Amounts cannot have more than two decimal places.");
        var open = items.Where(i => i.Outstanding > 0m).ToList();
        var planned = new List<AllocLine>();
        var left = amount;

        void Oldest(IEnumerable<AllocItem> candidates)
        {
            foreach (var i in candidates.OrderBy(i => i.DueDate).ThenBy(i => i.LineNo).ThenBy(i => i.ItemId))
            {
                if (left == 0m) break;
                var part = Math.Min(left, i.Outstanding);
                planned.Add(new AllocLine(i.ItemId, part));
                left -= part;
            }
        }

        switch (mode)
        {
            case AllocationMode.OldestDueFirst:
                Oldest(open);
                break;
            case AllocationMode.ChargeSpecific:
                if (chargeId is null) throw new DomainException("Select the charge to allocate to.");
                var ofCharge = open.Where(i => i.ChargeId == chargeId).ToList();
                if (ofCharge.Count == 0) throw new DomainException("The selected charge has no outstanding installments.");
                Oldest(ofCharge);
                break;
            case AllocationMode.SpecificInstallment:
            case AllocationMode.Manual:
                if (lines is not { Count: > 0 }) throw new DomainException("Enter at least one installment and amount.");
                if (mode == AllocationMode.SpecificInstallment && lines.Count != 1) throw new DomainException("Specific installment allocation applies to exactly one installment.");
                if (lines.Select(l => l.ItemId).Distinct().Count() != lines.Count) throw new DomainException("Each installment can appear only once.");
                foreach (var l in lines)
                {
                    var item = items.FirstOrDefault(i => i.ItemId == l.ItemId) ?? throw new DomainException("An installment was not found on the active schedule.");
                    if (l.Amount <= 0m) throw new DomainException("Allocation amounts must be greater than zero.");
                    if (l.Amount != decimal.Round(l.Amount, 2)) throw new DomainException("Amounts cannot have more than two decimal places.");
                    if (l.Amount > item.Outstanding) throw new DomainException($"Line {item.LineNo}: the amount exceeds the outstanding balance ({item.Outstanding:N2}).");
                    if (l.Amount > left) throw new DomainException("The allocation exceeds the amount available.");
                    planned.Add(new AllocLine(item.ItemId, l.Amount));
                    left -= l.Amount;
                }
                break;
            case AllocationMode.Advance:
                return new AllocationPlan([], amount, 0m);
            case AllocationMode.Unallocated:
                return new AllocationPlan([], 0m, amount);
            default:
                throw new DomainException("Unknown allocation mode.");
        }
        return remainderToAdvance ? new AllocationPlan(planned, left, 0m) : new AllocationPlan(planned, 0m, left);
    }
}

/// <summary>A line of the active schedule with the root of its version chain (stable identity across amendments).</summary>
public sealed record DueLine(long RootId, long ItemId, DateOnly DueDate, decimal Amount, decimal Waived, string Description);

/// <summary>A receivable ledger posting needed to bring the ledger in line with what has fallen due. Positive Due/Waived = new posting, negative = reversal.</summary>
public sealed record ReceivableDelta(long RootId, long? ItemId, DateOnly EntryDate, decimal DueDelta, decimal WaiverDelta, string Description, bool FirstPosting);

/// <summary>
/// Posted receivables ("do not confuse a scheduled installment with an actual posted receivable"): a schedule line becomes a receivable on the agreement
/// ledger only when it falls due. The ledger keeps, per line chain, what has been posted; this engine returns the difference to the target
/// (due lines of an active agreement: their amount and waived amount; everything else, including lines moved to the future by an amendment and every line
/// of a cancelled agreement: zero). Differences are posted as adjustments, so the result is idempotent and self-correcting.
/// </summary>
public static class ReceivableEngine
{
    public static IReadOnlyList<ReceivableDelta> Compute(IEnumerable<DueLine> activeLines, IReadOnlyDictionary<long, (decimal Due, decimal Waived)> posted,
        DateOnly today, bool agreementActive)
    {
        var targets = new Dictionary<long, (decimal Due, decimal Waived, long? ItemId, DateOnly Date, string Description)>();
        if (agreementActive)
            foreach (var l in activeLines.Where(l => l.DueDate <= today))
            {
                var t = targets.GetValueOrDefault(l.RootId, (0m, 0m, null, l.DueDate, l.Description));
                targets[l.RootId] = (t.Item1 + l.Amount, t.Item2 + l.Waived, l.ItemId, l.DueDate < t.Item4 ? l.DueDate : t.Item4, l.Description);
            }
        var result = new List<ReceivableDelta>();
        foreach (var root in targets.Keys.Union(posted.Keys).Order())
        {
            var t = targets.TryGetValue(root, out var x) ? x : (0m, 0m, (long?)null, today, "Receivable adjustment");
            var p = posted.TryGetValue(root, out var px) ? px : (Due: 0m, Waived: 0m);
            var dueDelta = t.Item1 - p.Due;
            var waiverDelta = t.Item2 - p.Waived;
            if (dueDelta == 0m && waiverDelta == 0m) continue;
            var first = p.Due == 0m && dueDelta > 0m && t.Item3 is not null;
            // A first posting is dated on the due date; corrections are dated today.
            result.Add(new ReceivableDelta(root, t.Item3, first ? t.Item4 : today, dueDelta, waiverDelta, t.Item5, first));
        }
        return result;
    }

    /// <summary>Resolves each item to the first item of its version chain (PreviousItemId links).</summary>
    public static Dictionary<long, long> Roots(IReadOnlyDictionary<long, long?> previousById)
    {
        var roots = new Dictionary<long, long>();
        foreach (var id in previousById.Keys)
        {
            var cur = id;
            var guard = 0;
            while (previousById.TryGetValue(cur, out var prev) && prev is { } p && previousById.ContainsKey(p) && guard++ < 1000) cur = p;
            roots[id] = cur;
        }
        return roots;
    }
}
