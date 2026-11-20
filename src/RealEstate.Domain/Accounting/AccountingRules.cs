using System.Globalization;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Accounting;

/// <summary>
/// What each accounting event offers to posting rules: the amounts a rule line can take, the payload fields a rule may be conditioned on and the accounts the
/// event itself names. Reversal events have no rules: they reverse the journal of the original event (spec 8.3 "journal reversal").
/// </summary>
public static class PostingCatalog
{
    public sealed record EventDefinition(AccountingEventType Type, string Label, string Source, IReadOnlyList<string> AmountKeys, IReadOnlyList<string> ConditionFields,
        IReadOnlyList<string> AccountKeys, AccountingEventType? Reverses, string SpecItems);

    public static readonly IReadOnlyList<EventDefinition> All =
    [
        new(AccountingEventType.PaymentPosted, "Customer payment posted", "Payment", ["Amount", "Allocated", "Advance", "Unallocated"], ["Method"], [], null,
            "Customer payments; customer advances (advance and unallocated parts)"),
        new(AccountingEventType.PaymentReversed, "Customer payment reversed", "Payment", [], [], [], AccountingEventType.PaymentPosted, "Reverses the payment journal"),
        new(AccountingEventType.RefundPaid, "Customer refund paid", "Refund", ["Amount"], ["Method", "AgreementStatus"], [], null, "Refunds"),
        new(AccountingEventType.ReceivableRecognized, "Installment due (receivable recognized)", "AgreementLedgerEntry", ["Amount"], ["ChargeCategory", "EntryType"], [], null,
            "Customer receivables; recognized property sales and development charges (by charge category), per approved policy"),
        new(AccountingEventType.ReceivableDerecognized, "Due reduced (receivable derecognized)", "AgreementLedgerEntry", ["Amount"], ["ChargeCategory", "EntryType"], [], null,
            "Deferments, cancellations and other reductions of recognized dues"),
        new(AccountingEventType.CustomerWaiver, "Customer waiver / discount", "AgreementLedgerEntry", ["Amount"], ["ChargeCategory", "EntryType"], [], null, "Customer discounts granted as waivers"),
        new(AccountingEventType.CustomerWaiverReversed, "Customer waiver reversed", "AgreementLedgerEntry", ["Amount"], ["ChargeCategory", "EntryType"], [], null, "Waiver adjustments"),
        new(AccountingEventType.BookingConfirmed, "Booking confirmed (sale agreement)", "Booking", ["TotalPrice", "TotalExtraDiscount", "TotalCharges", "TotalPayable"], ["PaymentMode"], [], null,
            "Recognized property sales and customer discounts, if the approved policy recognizes the sale on confirmation"),
        new(AccountingEventType.BookingCancelled, "Confirmed booking cancelled", "Booking", [], [], [], AccountingEventType.BookingConfirmed, "Reverses the booking confirmation journal"),
        new(AccountingEventType.ExpenseApproved, "Expense / vendor invoice approved", "Expense", ["Amount"], ["CategoryId", "VendorId"], [], null,
            "Vendor invoices; salaries, operating expenses, construction costs and land acquisition (by expense category)"),
        new(AccountingEventType.ExpenseCancelled, "Approved expense cancelled", "Expense", [], [], [], AccountingEventType.ExpenseApproved, "Reverses the expense journal"),
        new(AccountingEventType.ExpensePaid, "Vendor / expense payment", "VendorPayment", ["Amount"], ["Method", "CategoryId"], [], null, "Vendor payments"),
        new(AccountingEventType.ExpensePaymentReversed, "Vendor payment reversed", "VendorPayment", [], [], [], AccountingEventType.ExpensePaid, "Reverses the vendor payment journal"),
        new(AccountingEventType.GovFeeAssessed, "Government fee assessed", "GovernmentFeeAssessment", ["Amount", "CustomerLiability", "CompanyBorne"], ["Kind"], [], null,
            "Government payables (customer share and company-borne share)"),
        new(AccountingEventType.GovFeeAssessmentCancelled, "Government fee assessment cancelled", "GovernmentFeeAssessment", [], [], [], AccountingEventType.GovFeeAssessed, "Reverses the assessment journal"),
        new(AccountingEventType.GovFeeCollected, "Government fee collected from customer", "GovernmentFeeCollection", ["Amount"], ["Method"], [], null, "Government collections"),
        new(AccountingEventType.GovFeeCollectionReversed, "Government fee collection reversed", "GovernmentFeeCollection", [], [], [], AccountingEventType.GovFeeCollected, "Reverses the collection journal"),
        new(AccountingEventType.GovFeePaid, "Government fee paid to government", "GovernmentFeePayment", ["Amount"], ["Method"], [], null, "Government payments"),
        new(AccountingEventType.GovFeePaymentReversed, "Government payment reversed", "GovernmentFeePayment", [], [], [], AccountingEventType.GovFeePaid, "Reverses the government payment journal"),
        new(AccountingEventType.BankTransfer, "Bank transfer", "JournalEntry", ["Amount"], [], ["FromAccount", "ToAccount"], null, "Bank transfers (between bank accounts or bank and cash)"),
        new(AccountingEventType.CashTransfer, "Cash transfer", "JournalEntry", ["Amount"], [], ["FromAccount", "ToAccount"], null, "Cash transfers (between cash accounts)"),
    ];

    public static EventDefinition Get(AccountingEventType t) => All.First(d => d.Type == t);
    public static bool IsReversal(AccountingEventType t) => Get(t).Reverses is not null;
    public static IEnumerable<EventDefinition> RuleEvents => All.Where(d => d.Reverses is null);
}

/// <summary>A balanced journal line produced by the posting engine.</summary>
public sealed record ResolvedLine(EntrySide Side, long AccountId, decimal Amount, long? CostCenterId, string? Description);

/// <summary>Selects the posting rule for an event and turns it into journal lines. Pure and deterministic.</summary>
public static class PostingRuleEngine
{
    /// <summary>
    /// Most specific active rule wins: project rule over company-wide, conditioned over unconditioned, lower priority number, later effective date.
    /// Two equally specific rules with the same priority and effective date are ambiguous and post nothing.
    /// </summary>
    public static PostingRule SelectRule(IEnumerable<PostingRule> rules, AccountingEventType type, long? projectId, DateOnly date, IReadOnlyDictionary<string, string?> payload)
    {
        var candidates = rules
            .Where(r => r.Status == PostingRuleStatus.Active && r.EventType == type && r.EffectiveFrom <= date && (r.ProjectId is null || r.ProjectId == projectId))
            .Where(r => r.ConditionField is null || (payload.TryGetValue(r.ConditionField, out var v) && string.Equals(v, r.ConditionValue, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.ProjectId is not null).ThenByDescending(r => r.ConditionField is not null).ThenBy(r => r.Priority).ThenByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id)
            .ToList();
        if (candidates.Count == 0)
            throw new DomainException($"No active, approved posting rule matches this {PostingCatalog.Get(type).Label.ToLowerInvariant()} event.");
        var best = candidates[0];
        if (candidates.Count > 1)
        {
            var second = candidates[1];
            if ((second.ProjectId is not null) == (best.ProjectId is not null) && (second.ConditionField is not null) == (best.ConditionField is not null)
                && second.Priority == best.Priority && second.EffectiveFrom == best.EffectiveFrom)
                throw new DomainException($"Posting rules '{best.Name}' and '{second.Name}' both match with the same priority; set different priorities or conditions.");
        }
        return best;
    }

    /// <summary>Resolves rule lines into amounts and accounts; zero amounts are skipped; the result must balance and be greater than zero.</summary>
    public static IReadOnlyList<ResolvedLine> BuildLines(PostingRule rule, IReadOnlyDictionary<string, string?> payload)
    {
        var lines = new List<ResolvedLine>();
        foreach (var l in rule.Lines.OrderBy(x => x.LineNo))
        {
            if (!payload.TryGetValue(l.AmountKey, out var raw) || !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                throw new DomainException($"The event has no amount '{l.AmountKey}' required by rule '{rule.Name}'.");
            if (amount < 0m) throw new DomainException($"Amount '{l.AmountKey}' is negative.");
            if (amount == 0m) continue;
            long accountId;
            if (l.AccountId is { } a) accountId = a;
            else if (l.AccountKey is { } k && payload.TryGetValue(k, out var acc) && long.TryParse(acc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) accountId = parsed;
            else throw new DomainException($"The event does not name the account '{l.AccountKey}' required by rule '{rule.Name}'.");
            lines.Add(new ResolvedLine(l.Side, accountId, amount, l.CostCenterId, l.Description));
        }
        var debit = lines.Where(x => x.Side == EntrySide.Debit).Sum(x => x.Amount);
        var credit = lines.Where(x => x.Side == EntrySide.Credit).Sum(x => x.Amount);
        if (debit != credit) throw new DomainException($"Rule '{rule.Name}' does not balance for this event (debit {debit:N2}, credit {credit:N2}).");
        if (debit == 0m) throw new DomainException($"Rule '{rule.Name}' produces no amounts for this event.");
        return lines;
    }

    /// <summary>A rule is well formed when it has at least one debit and one credit line, each with exactly one account source and a catalog amount.</summary>
    public static IReadOnlyList<string> ValidateDefinition(AccountingEventType type, IEnumerable<PostingRuleLine> lines, string? conditionField)
    {
        var def = PostingCatalog.Get(type);
        var errors = new List<string>();
        if (def.Reverses is not null) errors.Add("Reversal events reverse the original journal and take no posting rule.");
        var list = lines.ToList();
        if (!list.Any(l => l.Side == EntrySide.Debit) || !list.Any(l => l.Side == EntrySide.Credit)) errors.Add("A rule needs at least one debit and one credit line.");
        foreach (var l in list)
        {
            if (!def.AmountKeys.Contains(l.AmountKey)) errors.Add($"Line {l.LineNo}: amount '{l.AmountKey}' is not offered by this event ({string.Join(", ", def.AmountKeys)}).");
            if ((l.AccountId is null) == (l.AccountKey is null)) errors.Add($"Line {l.LineNo}: choose either a fixed account or an account named by the event.");
            if (l.AccountKey is { } k && !def.AccountKeys.Contains(k)) errors.Add($"Line {l.LineNo}: the event does not name an account '{k}'.");
        }
        if (conditionField is not null && !def.ConditionFields.Contains(conditionField)) errors.Add($"The event has no condition field '{conditionField}'.");
        return errors;
    }
}

/// <summary>Journal balance and line rules (spec 8.3: total debit = total credit).</summary>
public static class JournalBalancer
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<(decimal Debit, decimal Credit)> lines)
    {
        var errors = new List<string>();
        if (lines.Count < 2) errors.Add("A journal needs at least two lines.");
        for (var i = 0; i < lines.Count; i++)
        {
            var (d, c) = lines[i];
            if (d < 0m || c < 0m) errors.Add($"Line {i + 1}: amounts cannot be negative.");
            else if ((d > 0m) == (c > 0m)) errors.Add($"Line {i + 1}: enter either a debit or a credit amount.");
            if (decimal.Round(d, 2) != d || decimal.Round(c, 2) != c) errors.Add($"Line {i + 1}: amounts support at most 2 decimal places.");
        }
        var td = lines.Sum(l => l.Debit);
        var tc = lines.Sum(l => l.Credit);
        if (td != tc) errors.Add($"The journal does not balance: debit {td:N2}, credit {tc:N2}.");
        if (td == 0m) errors.Add("The journal total must be greater than zero.");
        return errors;
    }

    public static bool IsBalanced(IEnumerable<(decimal Debit, decimal Credit)> lines)
    {
        var l = lines.ToList();
        return l.Count >= 2 && l.Sum(x => x.Debit) == l.Sum(x => x.Credit) && l.Sum(x => x.Debit) > 0m;
    }
}

/// <summary>Fiscal year calendar and period posting rules.</summary>
public static class PeriodCalendar
{
    public static IReadOnlyList<(int No, string Name, DateOnly Start, DateOnly End)> MonthlyPeriods(DateOnly start, DateOnly end)
    {
        if (start.Day != 1) throw new DomainException("A fiscal year must start on the first day of a month.");
        if (end != new DateOnly(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month))) throw new DomainException("A fiscal year must end on the last day of a month.");
        if (end < start) throw new DomainException("The fiscal year end must be after its start.");
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month + 1;
        if (months > 18) throw new DomainException("A fiscal year can have at most 18 months.");
        var list = new List<(int, string, DateOnly, DateOnly)>();
        for (var i = 0; i < months; i++)
        {
            var s = start.AddMonths(i);
            var e = new DateOnly(s.Year, s.Month, DateTime.DaysInMonth(s.Year, s.Month));
            list.Add((i + 1, s.ToString("MMM yyyy", CultureInfo.InvariantCulture), s, e));
        }
        return list;
    }

    public enum PostingDecision { Allowed, RequiresOverride, Rejected }

    public static PostingDecision CanPost(FiscalYearStatus year, AccountingPeriodStatus period) =>
        year == FiscalYearStatus.Closed || period == AccountingPeriodStatus.Locked ? PostingDecision.Rejected
        : period == AccountingPeriodStatus.Closed ? PostingDecision.RequiresOverride : PostingDecision.Allowed;

    /// <summary>Allowed status changes: Open -> Closed/Locked, Closed -> Locked/Open, Locked -> Open (reopen; audited with a reason).</summary>
    public static AccountingPeriodStatus Apply(AccountingPeriodStatus from, PeriodLockAction action) => (from, action) switch
    {
        (AccountingPeriodStatus.Open, PeriodLockAction.Close) => AccountingPeriodStatus.Closed,
        (AccountingPeriodStatus.Open or AccountingPeriodStatus.Closed, PeriodLockAction.Lock) => AccountingPeriodStatus.Locked,
        (AccountingPeriodStatus.Closed or AccountingPeriodStatus.Locked, PeriodLockAction.Reopen) => AccountingPeriodStatus.Open,
        _ => throw new DomainException($"A {from.ToString().ToLowerInvariant()} period cannot be {Verb(action)}."),
    };

    private static string Verb(PeriodLockAction a) => a switch { PeriodLockAction.Close => "closed", PeriodLockAction.Lock => "locked", _ => "reopened" };

    public static string VoucherNumber(VoucherType type, DateOnly fyStart, DateOnly fyEnd, int sequence) =>
        $"{type}{fyStart.Year % 100:D2}{fyEnd.Year % 100:D2}-{sequence:D6}";
}

/// <summary>Balance helpers for statements.</summary>
public static class AccountingMath
{
    /// <summary>Balance in the account's normal direction (debit-normal for assets and expenses, credit-normal otherwise).</summary>
    public static decimal NormalBalance(AccountNature nature, decimal debit, decimal credit) => ChartOfAccount.IsDebitNormal(nature) ? debit - credit : credit - debit;

    /// <summary>Splits an amount by percentages (must total 100) with the rounding remainder on the last line.</summary>
    public static IReadOnlyList<decimal> Split(decimal amount, IReadOnlyList<decimal> percentages)
    {
        if (percentages.Count == 0) throw new DomainException("Add at least one allocation line.");
        if (percentages.Any(p => p <= 0m)) throw new DomainException("Allocation percentages must be greater than zero.");
        if (percentages.Sum() != 100m) throw new DomainException($"Allocation percentages must total 100 (now {percentages.Sum():0.####}).");
        var result = new List<decimal>();
        for (var i = 0; i < percentages.Count - 1; i++) result.Add(decimal.Round(amount * percentages[i] / 100m, 2, MidpointRounding.AwayFromZero));
        result.Add(amount - result.Sum());
        if (result.Any(r => r <= 0m)) throw new DomainException("The amount is too small for this allocation.");
        return result;
    }

    /// <summary>
    /// Direct-method cash flow of one journal: when it touches cash/bank, each non-cash line contributes the opposite of its net debit to its section, so the
    /// contributions add up to the journal's net cash movement. Transfers between cash and bank accounts contribute nothing.
    /// </summary>
    public static IReadOnlyDictionary<CashFlowSection, decimal> CashFlow(IEnumerable<(bool IsCash, CashFlowSection Section, decimal Debit, decimal Credit)> journalLines)
    {
        var lines = journalLines.ToList();
        var result = new Dictionary<CashFlowSection, decimal>();
        if (!lines.Any(l => l.IsCash)) return result;
        foreach (var l in lines.Where(l => !l.IsCash))
            result[l.Section] = result.GetValueOrDefault(l.Section) + (l.Credit - l.Debit);
        return result;
    }
}
