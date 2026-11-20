using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase07ValidatorTests
{
    private static readonly DateOnly D = new(2026, 11, 20);

    private static CreateExpenseRequest Expense(decimal amount = 1000m, string description = "Office rent November", DateOnly? due = null) =>
        new(1, 1, null, description, D, amount, due, null, null, null);

    [Fact]
    public void Expense_RequiresPositiveTwoDecimalAmount_AndDescription()
    {
        var v = new CreateExpenseRequestValidator();
        Assert.True(v.Validate(Expense()).IsValid);
        Assert.False(v.Validate(Expense(0m)).IsValid);
        Assert.False(v.Validate(Expense(-1m)).IsValid);
        Assert.False(v.Validate(Expense(10.005m)).IsValid);
        Assert.False(v.Validate(Expense(description: "")).IsValid);
        Assert.False(v.Validate(Expense(description: "ab")).IsValid);
        Assert.False(v.Validate(Expense(due: D.AddDays(-1))).IsValid);
        Assert.True(v.Validate(Expense(due: D)).IsValid);
    }

    [Fact]
    public void Payment_RequiresIdempotencyKey_AndPositiveAmount()
    {
        var v = new PayExpenseRequestValidator();
        Assert.True(v.Validate(new PayExpenseRequest(100m, 1, D, null, null, "k1")).IsValid);
        Assert.False(v.Validate(new PayExpenseRequest(100m, 1, D, null, null, "")).IsValid);
        Assert.False(v.Validate(new PayExpenseRequest(0m, 1, D, null, null, "k1")).IsValid);
        Assert.False(v.Validate(new PayExpenseRequest(1.234m, 1, D, null, null, "k1")).IsValid);
        Assert.False(v.Validate(new PayExpenseRequest(1m, 0, D, null, null, "k1")).IsValid);
    }

    [Fact]
    public void RejectionCancellationAndReversal_NeedAReason()
    {
        Assert.False(new RejectExpenseRequestValidator().Validate(new RejectExpenseRequest("", null)).IsValid);
        Assert.True(new RejectExpenseRequestValidator().Validate(new RejectExpenseRequest("Missing invoice", null)).IsValid);
        Assert.False(new CancelExpenseRequestValidator().Validate(new CancelExpenseRequest("no", null)).IsValid);
        Assert.False(new ReverseVendorPaymentRequestValidator().Validate(new ReverseVendorPaymentRequest("")).IsValid);
        Assert.False(new ReverseFeeEntryRequestValidator().Validate(new ReverseFeeEntryRequest("x")).IsValid);
        Assert.True(new ReverseFeeEntryRequestValidator().Validate(new ReverseFeeEntryRequest("Bank returned it")).IsValid);
    }

    [Fact]
    public void VendorInvoice_DueDateCannotPrecedeInvoiceDate()
    {
        var v = new CreateVendorInvoiceRequestValidator();
        Assert.True(v.Validate(new CreateVendorInvoiceRequest(1, 1, "INV-1", 1, D, D.AddDays(30), 500m, null, null, null)).IsValid);
        Assert.False(v.Validate(new CreateVendorInvoiceRequest(1, 1, "INV-1", 1, D, D.AddDays(-1), 500m, null, null, null)).IsValid);
        Assert.False(v.Validate(new CreateVendorInvoiceRequest(1, 1, "", 1, D, D, 500m, null, null, null)).IsValid);
    }

    [Fact]
    public void Vendor_NeedsAName_AndAValidEmail()
    {
        var v = new SaveVendorRequestValidator();
        Assert.True(v.Validate(new SaveVendorRequest("Acme Builders", null, null, null, null, null, null)).IsValid);
        Assert.False(v.Validate(new SaveVendorRequest("", null, null, null, null, null, null)).IsValid);
        Assert.False(v.Validate(new SaveVendorRequest("Acme", null, null, "not-an-email", null, null, null)).IsValid);
    }

    [Fact]
    public void FeeType_ConfigurationMatchesTheCalculationMethod()
    {
        var v = new SaveGovernmentFeeTypeRequestValidator();
        SaveGovernmentFeeTypeRequest T(FeeCalculationMethod m, decimal? fixedAmount = null, decimal? rate = null) =>
            new(null, "STAMP-2", "Stamp duty", GovernmentFeeKind.StampDuty, m, fixedAmount, rate, FeeBearer.Customer, null);
        Assert.True(v.Validate(T(FeeCalculationMethod.Manual)).IsValid);
        Assert.True(v.Validate(T(FeeCalculationMethod.Fixed, 500m)).IsValid);
        Assert.False(v.Validate(T(FeeCalculationMethod.Fixed)).IsValid);
        Assert.True(v.Validate(T(FeeCalculationMethod.PercentOfBasis, rate: 1.5m)).IsValid);
        Assert.False(v.Validate(T(FeeCalculationMethod.PercentOfBasis)).IsValid);
        Assert.False(v.Validate(T(FeeCalculationMethod.PercentOfBasis, rate: 101m)).IsValid);
        Assert.False(v.Validate(T(FeeCalculationMethod.PercentOfBasis, rate: 1.00001m)).IsValid);
        Assert.False(v.Validate(new SaveGovernmentFeeTypeRequest(null, "bad code!", "x", GovernmentFeeKind.Other, FeeCalculationMethod.Manual, null, null, FeeBearer.Customer, null)).IsValid);
    }

    [Fact]
    public void Assessment_ValidatesAmountsAndKey()
    {
        var v = new CreateGovernmentFeeAssessmentRequestValidator();
        Assert.True(v.Validate(new CreateGovernmentFeeAssessmentRequest(1, 1, null, 1000m, 0m, null, null, null, "k")).IsValid);
        Assert.False(v.Validate(new CreateGovernmentFeeAssessmentRequest(1, 1, null, 1000m, -1m, null, null, null, "k")).IsValid);
        Assert.False(v.Validate(new CreateGovernmentFeeAssessmentRequest(1, 1, null, 0m, null, null, null, null, "k")).IsValid);
        Assert.False(v.Validate(new CreateGovernmentFeeAssessmentRequest(1, 1, null, 1000m, null, null, null, null, "")).IsValid);
        Assert.False(v.Validate(new CreateGovernmentFeeAssessmentRequest(1, 1, null, 10.123m, null, null, null, null, "k")).IsValid);
    }

    [Fact]
    public void FeeCollectionAndPayment_RequirePositiveAmountAndKey()
    {
        Assert.True(new FeeCollectionRequestValidator().Validate(new FeeCollectionRequest(10m, 1, D, null, null, "k")).IsValid);
        Assert.False(new FeeCollectionRequestValidator().Validate(new FeeCollectionRequest(0m, 1, D, null, null, "k")).IsValid);
        Assert.False(new GovernmentPaymentRequestValidator().Validate(new GovernmentPaymentRequest(10m, 1, D, null, null, "")).IsValid);
        Assert.True(new GovernmentPaymentRequestValidator().Validate(new GovernmentPaymentRequest(10m, 1, D, "CH-1", null, "k")).IsValid);
    }
}
