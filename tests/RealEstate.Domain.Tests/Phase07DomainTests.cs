using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Tests;

public class Phase07DomainTests
{
    // ----------------------------------------------------------------------------- expenses
    [Theory]
    [InlineData(1000, 0, ExpensePaymentStatus.Unpaid)]
    [InlineData(1000, 0.01, ExpensePaymentStatus.PartiallyPaid)]
    [InlineData(1000, 999.99, ExpensePaymentStatus.PartiallyPaid)]
    [InlineData(1000, 1000, ExpensePaymentStatus.Paid)]
    public void PaymentStatus_IsDerivedFromThePaidAmount(double amount, double paid, ExpensePaymentStatus expected)
    {
        Assert.Equal(expected, Expense.PaymentStatusOf((decimal)amount, (decimal)paid));
        var e = new Expense { Amount = (decimal)amount, AmountPaid = (decimal)paid, Status = ExpenseStatus.Approved };
        Assert.Equal(expected, e.PaymentStatus);
    }

    [Fact]
    public void Outstanding_IsOnlyOpenForApprovedExpenses()
    {
        Assert.Equal(400m, new Expense { Amount = 1000m, AmountPaid = 600m, Status = ExpenseStatus.Approved }.Outstanding);
        foreach (var s in new[] { ExpenseStatus.Draft, ExpenseStatus.PendingApproval, ExpenseStatus.Rejected, ExpenseStatus.Cancelled })
            Assert.Equal(0m, new Expense { Amount = 1000m, Status = s }.Outstanding);
    }

    [Fact]
    public void OnlyDraftAndRejectedExpensesCanBeEdited()
    {
        Assert.True(Expense.CanEdit(ExpenseStatus.Draft));
        Assert.True(Expense.CanEdit(ExpenseStatus.Rejected));
        foreach (var s in new[] { ExpenseStatus.PendingApproval, ExpenseStatus.Approved, ExpenseStatus.Cancelled }) Assert.False(Expense.CanEdit(s));
    }

    // ----------------------------------------------------------------------------- government fees
    [Fact]
    public void FeeType_Fixed_ReturnsTheConfiguredAmount_IgnoringTheBasis()
    {
        var t = new GovernmentFeeType { Method = FeeCalculationMethod.Fixed, FixedAmount = 2_500m };
        Assert.Equal(2_500m, t.Calculate(null, 2, RoundingMode.Nearest));
        Assert.Equal(2_500m, t.Calculate(9_999_999m, 2, RoundingMode.Nearest));
    }

    [Fact]
    public void FeeType_Percent_AppliesTheRateToTheBasis_WithConfiguredRounding()
    {
        var t = new GovernmentFeeType { Method = FeeCalculationMethod.PercentOfBasis, Rate = 1.5m };
        Assert.Equal(75_000m, t.Calculate(5_000_000m, 2, RoundingMode.Nearest));
        var odd = new GovernmentFeeType { Method = FeeCalculationMethod.PercentOfBasis, Rate = 0.333m };
        Assert.Equal(16_650m, odd.Calculate(5_000_000m, 0, RoundingMode.Nearest));
        Assert.Equal(1.00m, new GovernmentFeeType { Method = FeeCalculationMethod.PercentOfBasis, Rate = 0.1m }.Calculate(1_000m, 2, RoundingMode.Nearest));
        Assert.Null(t.Calculate(null, 2, RoundingMode.Nearest));
    }

    [Fact]
    public void FeeType_Manual_HasNoCalculatedAmount() =>
        Assert.Null(new GovernmentFeeType { Method = FeeCalculationMethod.Manual }.Calculate(1_000m, 2, RoundingMode.Nearest));

    [Fact]
    public void Assessment_KeepsTheFivePositionsApart()
    {
        // fee 100,000 = customer 60,000 + company 40,000; customer paid 25,000; 70,000 paid to government
        var a = new GovernmentFeeAssessment { Amount = 100_000m, CustomerLiability = 60_000m, CompanyBorne = 40_000m, CollectedAmount = 25_000m, PaidAmount = 70_000m, Status = GovernmentFeeAssessmentStatus.Assessed };
        Assert.Equal(35_000m, a.CustomerOutstanding);
        Assert.Equal(30_000m, a.GovernmentPayable);
        Assert.Equal(a.Amount, a.CustomerLiability + a.CompanyBorne);
    }

    [Fact]
    public void CancelledAssessment_HasNothingOutstanding()
    {
        var a = new GovernmentFeeAssessment { Amount = 100m, CustomerLiability = 100m, Status = GovernmentFeeAssessmentStatus.Cancelled };
        Assert.Equal(0m, a.CustomerOutstanding);
        Assert.Equal(0m, a.GovernmentPayable);
    }

    // ----------------------------------------------------------------------------- permissions / catalog
    [Fact]
    public void Phase7Permissions_AreInTheCatalog_AndGrantedToTheExpectedRoles()
    {
        var codes = Permissions.All.Select(p => p.Code).ToHashSet();
        foreach (var c in new[] { Permissions.ExpensesView, Permissions.ExpensesManage, Permissions.ExpensesApprove, Permissions.ExpensesPay, Permissions.ExpensesReverse, Permissions.ExpensesSetup,
                     Permissions.VendorsView, Permissions.VendorsManage, Permissions.GovFeesView, Permissions.GovFeesSetup, Permissions.GovFeesAssess, Permissions.GovFeesPay })
            Assert.Contains(c, codes);
        Assert.Equal(codes.Count, Permissions.All.Count); // no duplicates
        var accountant = DefaultRoles.All.Single(r => r.Name == RoleNames.Accountant).Permissions;
        Assert.Contains(Permissions.ExpensesApprove, accountant);
        Assert.Contains(Permissions.ExpensesPay, accountant);
        var pm = DefaultRoles.All.Single(r => r.Name == RoleNames.ProjectManager).Permissions;
        Assert.Contains(Permissions.ExpensesManage, pm);
        Assert.DoesNotContain(Permissions.ExpensesApprove, pm); // maker-checker: managers submit, they do not approve
        Assert.DoesNotContain(Permissions.ExpensesPay, pm);
    }

    [Fact]
    public void Phase7AccountingEvents_FitTheTextColumn()
    {
        foreach (var e in Enum.GetValues<AccountingEventType>()) Assert.True(e.ToString().Length <= 25, e.ToString());
    }
}
