using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase06ValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static CreatePaymentRequest Payment(decimal amount = 1000m, AllocationMode mode = AllocationMode.OldestDueFirst, IReadOnlyList<AllocationLineInput>? lines = null, long? charge = null, string key = "k1") =>
        new(1, null, 1, PaymentSource.Office, amount, Today, null, null, null, null, null, mode, charge, lines, false, key);

    [Fact]
    public void CreatePayment_RequiresPositiveTwoDecimalAmount_AndIdempotencyKey()
    {
        var v = new CreatePaymentRequestValidator();
        Assert.True(v.Validate(Payment()).IsValid);
        Assert.False(v.Validate(Payment(0m)).IsValid);
        Assert.False(v.Validate(Payment(-5m)).IsValid);
        Assert.False(v.Validate(Payment(10.001m)).IsValid);
        Assert.False(v.Validate(Payment(key: "")).IsValid);
    }

    [Fact]
    public void CreatePayment_AllocationInstruction_IsConsistent()
    {
        var v = new CreatePaymentRequestValidator();
        Assert.False(v.Validate(Payment(mode: AllocationMode.ChargeSpecific)).IsValid);
        Assert.True(v.Validate(Payment(mode: AllocationMode.ChargeSpecific, charge: 3)).IsValid);
        Assert.False(v.Validate(Payment(mode: AllocationMode.Manual)).IsValid);
        Assert.True(v.Validate(Payment(mode: AllocationMode.Manual, lines: [new(1, 400m), new(2, 600m)])).IsValid);
        Assert.False(v.Validate(Payment(mode: AllocationMode.Manual, lines: [new(1, 600m), new(2, 600m)])).IsValid); // exceeds payment
        Assert.False(v.Validate(Payment(mode: AllocationMode.SpecificInstallment, lines: [new(1, 1m), new(2, 1m)])).IsValid);
        Assert.False(v.Validate(Payment(mode: AllocationMode.Manual, lines: [new(1, 0m)])).IsValid);
    }

    [Fact]
    public void Verify_Reject_Reverse_RequireReasons()
    {
        Assert.False(new VerifyPaymentRequestValidator().Validate(new VerifyPaymentRequest(true, "", null, null, null)).IsValid);
        Assert.True(new VerifyPaymentRequestValidator().Validate(new VerifyPaymentRequest(true, "Statement checked", null, null, null)).IsValid);
        Assert.False(new RejectPaymentRequestValidator().Validate(new RejectPaymentRequest("no", null)).IsValid);
        Assert.False(new ReversePaymentRequestValidator().Validate(new ReversePaymentRequest("", null)).IsValid);
        Assert.True(new ReversePaymentRequestValidator().Validate(new ReversePaymentRequest("Cheque bounced", null)).IsValid);
    }

    [Fact]
    public void Allocate_CannotChooseUnallocated_AndNeedsItsInputs()
    {
        var v = new AllocatePaymentRequestValidator();
        Assert.False(v.Validate(new AllocatePaymentRequest(AllocationMode.Unallocated, null, null, false, false, null)).IsValid);
        Assert.False(v.Validate(new AllocatePaymentRequest(AllocationMode.ChargeSpecific, null, null, false, false, null)).IsValid);
        Assert.True(v.Validate(new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, true, false, null)).IsValid);
    }

    [Fact]
    public void Refund_And_Recovery_Requests_AreValidated()
    {
        Assert.False(new CreateRefundRequestValidator().Validate(new CreateRefundRequest(1, 0m, "Customer request", null, null)).IsValid);
        Assert.False(new CreateRefundRequestValidator().Validate(new CreateRefundRequest(1, 10m, "no", null, null)).IsValid);
        Assert.True(new CreateRefundRequestValidator().Validate(new CreateRefundRequest(1, 10m, "Customer request", null, null)).IsValid);
        Assert.False(new PayRefundRequestValidator().Validate(new PayRefundRequest(0, Today, null, null)).IsValid);
        Assert.False(new RejectRefundRequestValidator().Validate(new RejectRefundRequest("", null)).IsValid);

        var f = new CreateFollowUpRequestValidator();
        Assert.True(f.Validate(new CreateFollowUpRequest(1, Today, FollowUpMethod.Call, FollowUpOutcome.NoAnswer, null, null, null, "No answer")).IsValid);
        var promise = f.Validate(new CreateFollowUpRequest(1, Today, FollowUpMethod.Call, FollowUpOutcome.PromiseToPay, null, null, null, "Will pay"));
        Assert.False(promise.IsValid);
        Assert.Contains(promise.Errors, e => e.ErrorMessage == "Enter the promised amount.");
        Assert.False(f.Validate(new CreateFollowUpRequest(1, Today, FollowUpMethod.Call, FollowUpOutcome.PromiseToPay, 100m, Today.AddDays(-1), null, "Will pay")).IsValid);
        Assert.False(f.Validate(new CreateFollowUpRequest(1, Today, FollowUpMethod.Call, FollowUpOutcome.Contacted, null, null, Today.AddDays(-1), "Talked")).IsValid);
        Assert.False(new CreateAssignmentRequestValidator().Validate(new CreateAssignmentRequest(1, Guid.Empty, null)).IsValid);
    }

    [Fact]
    public void AmountInWords_IsUsedOnReceipts()
    {
        Assert.Equal("Pakistani Rupees One Hundred Twenty Thousand Fifty and Fifty Paisa Only", AmountInWords.Convert(120_050.50m, "PKR"));
        Assert.Equal("Pakistani Rupees Five Million Only", AmountInWords.Convert(5_000_000m, "PKR"));
        Assert.Equal("US Dollars Zero Only", AmountInWords.Convert(0m, "USD"));
    }
}
