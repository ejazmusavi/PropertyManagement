using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Tests;

/// <summary>Phase 8 pure accounting rules: posting rule selection and line building, journal balance, periods, vouchers, splits and cash flow attribution.</summary>
public class Phase08DomainTests
{
    private static PostingRule Rule(long id, AccountingEventType type = AccountingEventType.PaymentPosted, long? project = null, string? field = null, string? value = null, int priority = 100,
        DateOnly? from = null, PostingRuleStatus status = PostingRuleStatus.Active, params PostingRuleLine[] lines) => new()
    {
        Id = id, Name = "R" + id, EventType = type, ProjectId = project, ConditionField = field, ConditionValue = value, Priority = priority, EffectiveFrom = from ?? new DateOnly(2020, 1, 1), Status = status,
        Lines = lines.Length > 0 ? [.. lines] : [Line(1, EntrySide.Debit, 10), Line(2, EntrySide.Credit, 20)],
    };

    private static PostingRuleLine Line(int no, EntrySide side, long? account, string amount = "Amount", string? key = null) => new() { LineNo = no, Side = side, AccountId = account, AccountKey = key, AmountKey = amount };
    private static Dictionary<string, string?> P(params (string K, string? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);
    private static readonly DateOnly D = new(2026, 6, 15);

    // ----- rule selection -----
    [Fact]
    public void SelectRule_PrefersProjectRule_ThenCondition_ThenPriority_ThenLaterEffectiveDate()
    {
        var company = Rule(1);
        var conditioned = Rule(2, field: "Method", value: "Cash");
        var project = Rule(3, project: 7);
        var projectConditioned = Rule(4, project: 7, field: "Method", value: "cash");
        var all = new[] { company, conditioned, project, projectConditioned };
        Assert.Same(projectConditioned, PostingRuleEngine.SelectRule(all, AccountingEventType.PaymentPosted, 7, D, P(("Method", "Cash"))));
        Assert.Same(project, PostingRuleEngine.SelectRule(all, AccountingEventType.PaymentPosted, 7, D, P(("Method", "Cheque"))));
        Assert.Same(conditioned, PostingRuleEngine.SelectRule(all, AccountingEventType.PaymentPosted, 8, D, P(("Method", "CASH"))));
        Assert.Same(company, PostingRuleEngine.SelectRule(all, AccountingEventType.PaymentPosted, 8, D, P(("Method", "Card"))));

        var low = Rule(5, priority: 10); var high = Rule(6, priority: 50);
        Assert.Same(low, PostingRuleEngine.SelectRule([high, low], AccountingEventType.PaymentPosted, 1, D, P()));
        var old = Rule(7, from: new DateOnly(2020, 1, 1)); var replacement = Rule(8, from: new DateOnly(2026, 1, 1));
        Assert.Same(replacement, PostingRuleEngine.SelectRule([old, replacement], AccountingEventType.PaymentPosted, 1, D, P()));
        Assert.Same(old, PostingRuleEngine.SelectRule([old, replacement], AccountingEventType.PaymentPosted, 1, new DateOnly(2025, 12, 31), P()));
    }

    [Fact]
    public void SelectRule_IgnoresInactiveFutureAndOtherEvents_AndRejectsAmbiguity()
    {
        var draft = Rule(1, status: PostingRuleStatus.Draft);
        var retired = Rule(2, status: PostingRuleStatus.Retired);
        var future = Rule(3, from: new DateOnly(2030, 1, 1));
        var otherEvent = Rule(4, type: AccountingEventType.RefundPaid);
        var otherProject = Rule(5, project: 99);
        var ex = Assert.Throws<DomainException>(() => PostingRuleEngine.SelectRule([draft, retired, future, otherEvent, otherProject], AccountingEventType.PaymentPosted, 1, D, P()));
        Assert.Contains("No active, approved posting rule", ex.Message);
        var a = Rule(6); var b = Rule(7);
        Assert.Contains("same priority", Assert.Throws<DomainException>(() => PostingRuleEngine.SelectRule([a, b], AccountingEventType.PaymentPosted, 1, D, P())).Message);
    }

    // ----- line building -----
    [Fact]
    public void BuildLines_UsesAmountsAndAccounts_SkipsZero_AndRequiresBalance()
    {
        var rule = Rule(1, lines: [Line(1, EntrySide.Debit, 10), Line(2, EntrySide.Credit, 20, "Allocated"), Line(3, EntrySide.Credit, 30, "Advance"), Line(4, EntrySide.Credit, 40, "Unallocated")]);
        var lines = PostingRuleEngine.BuildLines(rule, P(("Amount", "1000.50"), ("Allocated", "700"), ("Advance", "300.50"), ("Unallocated", "0")));
        Assert.Equal(3, lines.Count);
        Assert.Equal(1000.50m, lines.Single(l => l.Side == EntrySide.Debit).Amount);
        Assert.DoesNotContain(lines, l => l.AccountId == 40);

        Assert.Contains("does not balance", Assert.Throws<DomainException>(() => PostingRuleEngine.BuildLines(rule, P(("Amount", "1000"), ("Allocated", "700"), ("Advance", "100"), ("Unallocated", "0")))).Message);
        Assert.Contains("no amount 'Advance'", Assert.Throws<DomainException>(() => PostingRuleEngine.BuildLines(rule, P(("Amount", "1"), ("Allocated", "1")))).Message);
        Assert.Contains("negative", Assert.Throws<DomainException>(() => PostingRuleEngine.BuildLines(Rule(2), P(("Amount", "-5")))).Message);
        Assert.Contains("no amounts", Assert.Throws<DomainException>(() => PostingRuleEngine.BuildLines(Rule(3), P(("Amount", "0")))).Message);
    }

    [Fact]
    public void BuildLines_ResolvesAccountsNamedByTheEvent()
    {
        var rule = Rule(1, type: AccountingEventType.BankTransfer, lines: [Line(1, EntrySide.Debit, null, key: "ToAccount"), Line(2, EntrySide.Credit, null, key: "FromAccount")]);
        var lines = PostingRuleEngine.BuildLines(rule, P(("Amount", "250"), ("FromAccount", "11"), ("ToAccount", "12")));
        Assert.Equal(12, lines.Single(l => l.Side == EntrySide.Debit).AccountId);
        Assert.Equal(11, lines.Single(l => l.Side == EntrySide.Credit).AccountId);
        Assert.Throws<DomainException>(() => PostingRuleEngine.BuildLines(rule, P(("Amount", "250"), ("FromAccount", "11"))));
    }

    [Fact]
    public void ValidateDefinition_ChecksCatalogAmounts_AccountSources_Sides_AndConditions()
    {
        Assert.Empty(PostingRuleEngine.ValidateDefinition(AccountingEventType.GovFeeAssessed, [Line(1, EntrySide.Debit, 1, "CompanyBorne"), Line(2, EntrySide.Debit, 2, "CustomerLiability"), Line(3, EntrySide.Credit, 3)], "Kind"));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.ExpenseApproved, [Line(1, EntrySide.Debit, 1, "Discount"), Line(2, EntrySide.Credit, 2)], null));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.ExpenseApproved, [Line(1, EntrySide.Debit, 1), Line(2, EntrySide.Debit, 2)], null));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.ExpenseApproved, [Line(1, EntrySide.Debit, 1, key: "ToAccount"), Line(2, EntrySide.Credit, 2)], null));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.ExpenseApproved, [Line(1, EntrySide.Debit, null), Line(2, EntrySide.Credit, 2)], null));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.ExpenseApproved, [Line(1, EntrySide.Debit, 1), Line(2, EntrySide.Credit, 2)], "Method"));
        Assert.NotEmpty(PostingRuleEngine.ValidateDefinition(AccountingEventType.PaymentReversed, [Line(1, EntrySide.Debit, 1), Line(2, EntrySide.Credit, 2)], null));
    }

    [Fact]
    public void Catalog_CoversEveryEvent_AndReversalsPointToRuleEvents()
    {
        foreach (var t in Enum.GetValues<AccountingEventType>()) Assert.Equal(t, PostingCatalog.Get(t).Type);
        foreach (var d in PostingCatalog.All.Where(d => d.Reverses is not null))
        {
            var original = PostingCatalog.Get(d.Reverses!.Value);
            Assert.Null(original.Reverses);
            Assert.Equal(original.Source, d.Source);
            Assert.Empty(d.AmountKeys);
        }
        Assert.All(PostingCatalog.RuleEvents, d => Assert.NotEmpty(d.AmountKeys));
        Assert.True(Enum.GetNames<AccountingEventType>().All(n => n.Length <= 25)); // stored as varchar(25)
    }

    // ----- journal balance -----
    [Theory]
    [InlineData(new double[] { 100, 0, 0, 100 }, true)]
    [InlineData(new double[] { 100, 0, 0, 60, 0, 40 }, true)]
    [InlineData(new double[] { 100, 0, 0, 99.99 }, false)]
    [InlineData(new double[] { 100, 100, 0, 0 }, false)]
    [InlineData(new double[] { 100, 0 }, false)]
    [InlineData(new double[] { 0, 0, 0, 0 }, false)]
    [InlineData(new double[] { -5, 0, 0, -5 }, false)]
    [InlineData(new double[] { 10.001, 0, 0, 10.001 }, false)]
    public void JournalBalancer_Validates(double[] pairs, bool valid)
    {
        var lines = Enumerable.Range(0, pairs.Length / 2).Select(i => ((decimal)pairs[i * 2], (decimal)pairs[i * 2 + 1])).ToList();
        Assert.Equal(valid, JournalBalancer.Validate(lines).Count == 0);
    }

    // ----- periods and vouchers -----
    [Fact]
    public void MonthlyPeriods_CoverTheYear_IncludingLeapFebruary()
    {
        var p = PeriodCalendar.MonthlyPeriods(new DateOnly(2027, 7, 1), new DateOnly(2028, 6, 30));
        Assert.Equal(12, p.Count);
        Assert.Equal("Jul 2027", p[0].Name);
        Assert.Equal(new DateOnly(2028, 2, 29), p[7].End);
        Assert.Equal(new DateOnly(2028, 6, 30), p[^1].End);
        for (var i = 1; i < p.Count; i++) Assert.Equal(p[i - 1].End.AddDays(1), p[i].Start);
        Assert.Throws<DomainException>(() => PeriodCalendar.MonthlyPeriods(new DateOnly(2027, 7, 2), new DateOnly(2028, 6, 30)));
        Assert.Throws<DomainException>(() => PeriodCalendar.MonthlyPeriods(new DateOnly(2027, 7, 1), new DateOnly(2028, 6, 29)));
        Assert.Throws<DomainException>(() => PeriodCalendar.MonthlyPeriods(new DateOnly(2027, 1, 1), new DateOnly(2028, 12, 31)));
        Assert.Single(PeriodCalendar.MonthlyPeriods(new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31)));
    }

    [Fact]
    public void PostingDecision_AndPeriodTransitions()
    {
        Assert.Equal(PeriodCalendar.PostingDecision.Allowed, PeriodCalendar.CanPost(FiscalYearStatus.Open, AccountingPeriodStatus.Open));
        Assert.Equal(PeriodCalendar.PostingDecision.RequiresOverride, PeriodCalendar.CanPost(FiscalYearStatus.Open, AccountingPeriodStatus.Closed));
        Assert.Equal(PeriodCalendar.PostingDecision.Rejected, PeriodCalendar.CanPost(FiscalYearStatus.Open, AccountingPeriodStatus.Locked));
        Assert.Equal(PeriodCalendar.PostingDecision.Rejected, PeriodCalendar.CanPost(FiscalYearStatus.Closed, AccountingPeriodStatus.Open));
        Assert.Equal(AccountingPeriodStatus.Closed, PeriodCalendar.Apply(AccountingPeriodStatus.Open, PeriodLockAction.Close));
        Assert.Equal(AccountingPeriodStatus.Locked, PeriodCalendar.Apply(AccountingPeriodStatus.Closed, PeriodLockAction.Lock));
        Assert.Equal(AccountingPeriodStatus.Locked, PeriodCalendar.Apply(AccountingPeriodStatus.Open, PeriodLockAction.Lock));
        Assert.Equal(AccountingPeriodStatus.Open, PeriodCalendar.Apply(AccountingPeriodStatus.Locked, PeriodLockAction.Reopen));
        Assert.Throws<DomainException>(() => PeriodCalendar.Apply(AccountingPeriodStatus.Locked, PeriodLockAction.Close));
        Assert.Throws<DomainException>(() => PeriodCalendar.Apply(AccountingPeriodStatus.Open, PeriodLockAction.Reopen));
        Assert.Throws<DomainException>(() => PeriodCalendar.Apply(AccountingPeriodStatus.Closed, PeriodLockAction.Close));
        Assert.Equal("JV2627-000042", PeriodCalendar.VoucherNumber(VoucherType.JV, new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30), 42));
    }

    // ----- balances, splits, cash flow -----
    [Fact]
    public void NormalBalance_FollowsTheNature()
    {
        Assert.Equal(70m, AccountingMath.NormalBalance(AccountNature.Asset, 100m, 30m));
        Assert.Equal(70m, AccountingMath.NormalBalance(AccountNature.Expense, 100m, 30m));
        Assert.Equal(-70m, AccountingMath.NormalBalance(AccountNature.Liability, 100m, 30m));
        Assert.Equal(70m, AccountingMath.NormalBalance(AccountNature.Revenue, 30m, 100m));
        Assert.Equal(70m, AccountingMath.NormalBalance(AccountNature.Equity, 30m, 100m));
    }

    [Fact]
    public void Split_PutsTheRoundingRemainderOnTheLastLine()
    {
        Assert.Equal([33.33m, 33.33m, 33.34m], AccountingMath.Split(100m, [33.3333m, 33.3333m, 33.3334m]));
        Assert.Equal([60_000m, 30_000m], AccountingMath.Split(90_000m, [66.6667m, 33.3333m]).Select(x => decimal.Round(x, 0)).ToArray());
        Assert.Equal(90_000m, AccountingMath.Split(90_000m, [66.6667m, 33.3333m]).Sum());
        Assert.Throws<DomainException>(() => AccountingMath.Split(100m, [50m, 40m]));
        Assert.Throws<DomainException>(() => AccountingMath.Split(100m, []));
        Assert.Throws<DomainException>(() => AccountingMath.Split(0.01m, [50m, 50m]));
    }

    [Fact]
    public void CashFlow_AttributesNonCashLines_AndIgnoresTransfers()
    {
        var receipt = AccountingMath.CashFlow([(true, CashFlowSection.Operating, 500m, 0m), (false, CashFlowSection.Operating, 0m, 400m), (false, CashFlowSection.Financing, 0m, 100m)]);
        Assert.Equal(400m, receipt[CashFlowSection.Operating]);
        Assert.Equal(100m, receipt[CashFlowSection.Financing]);
        var purchase = AccountingMath.CashFlow([(false, CashFlowSection.Investing, 900m, 0m), (true, CashFlowSection.Operating, 0m, 900m)]);
        Assert.Equal(-900m, purchase[CashFlowSection.Investing]);
        Assert.Empty(AccountingMath.CashFlow([(true, CashFlowSection.Operating, 200m, 0m), (true, CashFlowSection.Operating, 0m, 200m)]));
        Assert.Empty(AccountingMath.CashFlow([(false, CashFlowSection.Operating, 200m, 0m), (false, CashFlowSection.Operating, 0m, 200m)]));
    }

    [Fact]
    public void Journal_EditRules_AndDebitNormalNatures()
    {
        Assert.True(JournalEntry.CanEdit(JournalStatus.Draft));
        Assert.True(JournalEntry.CanEdit(JournalStatus.Rejected));
        Assert.False(JournalEntry.CanEdit(JournalStatus.Posted));
        Assert.False(JournalEntry.CanEdit(JournalStatus.PendingApproval));
        Assert.False(JournalEntry.CanEdit(JournalStatus.Cancelled));
        Assert.True(ChartOfAccount.IsDebitNormal(AccountNature.Asset));
        Assert.False(ChartOfAccount.IsDebitNormal(AccountNature.Revenue));
    }
}
