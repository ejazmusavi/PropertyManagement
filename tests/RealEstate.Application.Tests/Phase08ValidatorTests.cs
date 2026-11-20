using RealEstate.Application.Accounting;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

/// <summary>Phase 8 request validation and classification rules.</summary>
public class Phase08ValidatorTests
{
    private static readonly DateOnly D = new(2026, 6, 1);

    [Fact]
    public void CreateJournal_RequiresTwoBalancedOneSidedLines()
    {
        var v = new CreateJournalRequestValidator();
        Assert.True(v.Validate(new CreateJournalRequest(D, "Ok", null, [new(1, 10m, 0m), new(2, 0m, 10m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "Unbalanced", null, [new(1, 10m, 0m), new(2, 0m, 9m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "One line", null, [new(1, 10m, 0m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "Both sides", null, [new(1, 10m, 10m), new(2, 0m, 0m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "No account", null, [new(0, 10m, 0m), new(2, 0m, 10m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "", null, [new(1, 10m, 0m), new(2, 0m, 10m)])).IsValid);
        Assert.False(v.Validate(new CreateJournalRequest(D, "Negative", null, [new(1, -10m, 0m), new(2, 0m, -10m)])).IsValid);
    }

    [Fact]
    public void PostingRule_NeedsLines_AndAConditionValueWithAConditionField()
    {
        var v = new SavePostingRuleRequestValidator();
        PostingRuleLineInput[] lines = [new(EntrySide.Debit, "Amount", 1, null), new(EntrySide.Credit, "Amount", 2, null)];
        Assert.True(v.Validate(new SavePostingRuleRequest("R", AccountingEventType.PaymentPosted, null, "Method", "Cash", 100, D, null, lines)).IsValid);
        Assert.False(v.Validate(new SavePostingRuleRequest("R", AccountingEventType.PaymentPosted, null, "Method", null, 100, D, null, lines)).IsValid);
        Assert.False(v.Validate(new SavePostingRuleRequest("R", AccountingEventType.PaymentPosted, null, null, null, 0, D, null, lines)).IsValid);
        Assert.False(v.Validate(new SavePostingRuleRequest("R", AccountingEventType.PaymentPosted, null, null, null, 100, D, null, [lines[0]])).IsValid);
        Assert.False(v.Validate(new SavePostingRuleRequest("", AccountingEventType.PaymentPosted, null, null, null, 100, D, null, lines)).IsValid);
    }

    [Fact]
    public void CostAllocation_PercentagesTotal100_ProjectsUnique()
    {
        var v = new CreateCostAllocationRequestValidator();
        Assert.True(v.Validate(new CreateCostAllocationRequest(D, 1, 1, null, 900m, "By plots", [new(2, 60m), new(3, 40m)])).IsValid);
        Assert.False(v.Validate(new CreateCostAllocationRequest(D, 1, 1, null, 900m, "By plots", [new(2, 60m), new(3, 30m)])).IsValid);
        Assert.False(v.Validate(new CreateCostAllocationRequest(D, 1, 1, null, 900m, "By plots", [new(2, 50m), new(2, 50m)])).IsValid);
        Assert.False(v.Validate(new CreateCostAllocationRequest(D, 1, 1, null, 0m, "By plots", [new(2, 100m)])).IsValid);
        Assert.False(v.Validate(new CreateCostAllocationRequest(D, 1, 1, null, 900m, "x", [new(2, 100m)])).IsValid);
    }

    [Fact]
    public void Transfer_StatementImport_Periods_AndBankAccounts()
    {
        Assert.False(new CreateTransferRequestValidator().Validate(new CreateTransferRequest(5, 5, 10m, D, "Same", null, null)).IsValid);
        Assert.False(new CreateTransferRequestValidator().Validate(new CreateTransferRequest(5, 6, 10.001m, D, "Fraction", null, null)).IsValid);
        Assert.True(new CreateTransferRequestValidator().Validate(new CreateTransferRequest(5, 6, 10m, D, "Deposit", null, null)).IsValid);

        var import = new ImportStatementRequestValidator();
        Assert.True(import.Validate(new ImportStatementRequest([new(D, "Deposit", null, 10m, 0m)])).IsValid);
        Assert.False(import.Validate(new ImportStatementRequest([new(D, "Both", null, 10m, 5m)])).IsValid);
        Assert.False(import.Validate(new ImportStatementRequest([new(D, "", null, 10m, 0m)])).IsValid);
        Assert.False(import.Validate(new ImportStatementRequest([])).IsValid);

        Assert.False(new PeriodActionRequestValidator().Validate(new PeriodActionRequest(PeriodLockAction.Close, "no")).IsValid);
        Assert.True(new PeriodActionRequestValidator().Validate(new PeriodActionRequest(PeriodLockAction.Close, "Month end")).IsValid);
        Assert.False(new CreateFiscalYearRequestValidator().Validate(new CreateFiscalYearRequest("FY", D, D)).IsValid);
        Assert.False(new SaveBankAccountRequestValidator().Validate(new SaveBankAccountRequest("B1", "HBL", "HBL", null, "Co", "1", null, "PK", 1, null)).IsValid);
        Assert.False(new SaveAccountRequestValidator().Validate(new SaveAccountRequest("BAD CODE", "x", 1, null)).IsValid);
        Assert.False(new SaveAccountGroupRequestValidator().Validate(new SaveAccountGroupRequest("G1", "Group", null, AccountClassification.None, CashFlowSection.Operating, 1, null)).IsValid);
    }

    [Theory]
    [InlineData(AccountClassification.Cash, AccountNature.Asset)]
    [InlineData(AccountClassification.OtherAssets, AccountNature.Asset)]
    [InlineData(AccountClassification.CustomerAdvances, AccountNature.Liability)]
    [InlineData(AccountClassification.Reserves, AccountNature.Equity)]
    [InlineData(AccountClassification.DevelopmentRevenue, AccountNature.Revenue)]
    [InlineData(AccountClassification.OtherExpenses, AccountNature.Expense)]
    public void Classification_BelongsToItsNature(AccountClassification c, AccountNature n) => Assert.Equal(n, ChartOfAccountsService.NatureOf(c));

    [Fact]
    public void NoneClassification_HasNoNature() => Assert.Null(ChartOfAccountsService.NatureOf(AccountClassification.None));
}
