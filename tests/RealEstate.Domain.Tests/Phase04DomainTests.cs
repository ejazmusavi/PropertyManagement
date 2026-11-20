using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Sales;

namespace RealEstate.Domain.Tests;

public class Phase04DomainTests
{
    // ---------- ownership ----------
    [Theory]
    [InlineData(100)]
    public void Ownership_SingleOwnerWithFullShare_IsValid(double share) => BookingRules.ValidateOwnership([(decimal)share]);

    [Fact]
    public void Ownership_JointOwners_MustTotalExactly100()
    {
        BookingRules.ValidateOwnership([60m, 40m]);
        BookingRules.ValidateOwnership([33.3333m, 33.3333m, 33.3334m]);
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([60m, 30m]));
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([60m, 50m]));
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([33.33m, 33.33m, 33.33m]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Ownership_NonPositiveShare_IsRejected(double share) => Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([(decimal)share, 100m - (decimal)share]));

    [Fact]
    public void Ownership_EmptyOrTooPrecise_IsRejected()
    {
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([]));
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([50.00001m, 49.99999m]));
        Assert.Throws<DomainException>(() => BookingRules.ValidateOwnership([100.5m]));
    }

    [Fact]
    public void Allocation_NeverExceeds100()
    {
        BookingRules.EnsureAllocationWithinLimit(0m, 100m);
        BookingRules.EnsureAllocationWithinLimit(40m, 60m);
        Assert.Throws<DomainException>(() => BookingRules.EnsureAllocationWithinLimit(40m, 60.0001m));
        Assert.Throws<DomainException>(() => BookingRules.EnsureAllocationWithinLimit(100m, 1m));
    }

    // ---------- state machine ----------
    [Theory]
    [InlineData(BookingStatus.Draft, BookingStatus.PendingApproval, true)]
    [InlineData(BookingStatus.Draft, BookingStatus.Cancelled, true)]
    [InlineData(BookingStatus.Draft, BookingStatus.Confirmed, false)]
    [InlineData(BookingStatus.PendingApproval, BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.PendingApproval, BookingStatus.Rejected, true)]
    [InlineData(BookingStatus.PendingApproval, BookingStatus.Cancelled, true)]
    [InlineData(BookingStatus.Confirmed, BookingStatus.Cancelled, true)]
    [InlineData(BookingStatus.Confirmed, BookingStatus.Draft, false)]
    [InlineData(BookingStatus.Rejected, BookingStatus.Confirmed, false)]
    [InlineData(BookingStatus.Rejected, BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Draft, false)]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Confirmed, false)]
    public void BookingStateMachine_AllowsOnlyDefinedTransitions(BookingStatus from, BookingStatus to, bool allowed) => Assert.Equal(allowed, BookingRules.CanTransition(from, to));

    [Fact]
    public void Booking_Move_RecordsHistory_AndRejectsInvalidMoves()
    {
        var b = new Booking();
        var h = b.Move(BookingStatus.PendingApproval, "Submitted", DateTime.UtcNow, Guid.NewGuid(), "u");
        Assert.Equal(BookingStatus.Draft, h.FromStatus); Assert.Equal(BookingStatus.PendingApproval, h.ToStatus); Assert.Equal(BookingStatus.PendingApproval, b.Status);
        Assert.Throws<DomainException>(() => b.Move(BookingStatus.Draft, "back", DateTime.UtcNow, null, null));
        Assert.Throws<DomainException>(() => b.Move(BookingStatus.Confirmed, " ", DateTime.UtcNow, null, null));
        b.Move(BookingStatus.Confirmed, "ok", DateTime.UtcNow, null, null);
        b.Move(BookingStatus.Cancelled, "cancel", DateTime.UtcNow, null, null);
        Assert.Throws<DomainException>(() => b.Move(BookingStatus.Cancelled, "again", DateTime.UtcNow, null, null));
    }

    [Fact]
    public void HoldsProperties_OnlyWhileOpenOrConfirmed()
    {
        Assert.True(BookingRules.HoldsProperties(BookingStatus.Draft));
        Assert.True(BookingRules.HoldsProperties(BookingStatus.PendingApproval));
        Assert.True(BookingRules.HoldsProperties(BookingStatus.Confirmed));
        Assert.False(BookingRules.HoldsProperties(BookingStatus.Rejected));
        Assert.False(BookingRules.HoldsProperties(BookingStatus.Cancelled));
    }

    // ---------- totals ----------
    [Fact]
    public void Totals_AreSummedExactly_WithDecimalArithmetic()
    {
        var l1 = BookingRules.Line(1_000_000.10m, 0.10m, 2, RoundingMode.Nearest);
        var l2 = BookingRules.Line(2_500_000.25m, 100_000m, 2, RoundingMode.Nearest);
        var t = BookingRules.Sum([l1, l2], [25_000.05m, 0.01m]);
        Assert.Equal(3_500_000.35m, t.TotalPrice);
        Assert.Equal(100_000.10m, t.TotalExtraDiscount);
        Assert.Equal(25_000.06m, t.TotalCharges);
        Assert.Equal(3_425_000.31m, t.TotalPayable);
        Assert.Equal(1_000_000m, l1.LinePrice);
    }

    [Fact]
    public void Line_DiscountCannotBeNegativeOrExceedPrice_AndIsRounded()
    {
        Assert.Throws<DomainException>(() => BookingRules.Line(100m, -1m, 2, RoundingMode.Nearest));
        Assert.Throws<DomainException>(() => BookingRules.Line(100m, 100.01m, 2, RoundingMode.Nearest));
        Assert.Equal(100m, BookingRules.Line(100m, 100m, 2, RoundingMode.Nearest).ExtraDiscount);
        Assert.Equal(0m, BookingRules.Line(100m, 100m, 2, RoundingMode.Nearest).LinePrice);
        Assert.Equal(10.13m, BookingRules.Line(100m, 10.125m, 2, RoundingMode.Nearest).ExtraDiscount);
        Assert.Equal(10.12m, BookingRules.Line(100m, 10.125m, 2, RoundingMode.Bankers).ExtraDiscount);
        Assert.Equal(10.12m, BookingRules.Line(100m, 10.129m, 2, RoundingMode.Down).ExtraDiscount);
    }

    // ---------- cash sale rule ----------
    [Fact]
    public void CashSale_PartialPaymentNeedsAuthorization()
    {
        BookingRules.ValidateBookingAmount(PaymentMode.Cash, 1000m, 1000m, false);
        BookingRules.ValidateBookingAmount(PaymentMode.Cash, 400m, 1000m, true);
        Assert.Throws<DomainException>(() => BookingRules.ValidateBookingAmount(PaymentMode.Cash, 400m, 1000m, false));
        Assert.Throws<DomainException>(() => BookingRules.ValidateBookingAmount(PaymentMode.Cash, 1001m, 1000m, true));
        Assert.Throws<DomainException>(() => BookingRules.ValidateBookingAmount(PaymentMode.Cash, -1m, 1000m, true));
    }

    [Fact]
    public void InstallmentSale_DownPaymentDoesNotNeedAuthorization()
    {
        BookingRules.ValidateBookingAmount(PaymentMode.Installment, 0m, 1000m, false);
        BookingRules.ValidateBookingAmount(PaymentMode.Installment, 300m, 1000m, false);
        Assert.Throws<DomainException>(() => BookingRules.ValidateBookingAmount(PaymentMode.Installment, 2000m, 1000m, false));
    }

    // ---------- customer rules ----------
    [Theory]
    [InlineData("35202-1234567-1", "3520212345671")]
    [InlineData(" 35202 1234567 1 ", "3520212345671")]
    public void Cnic_IsNormalizedToDigits(string raw, string expected) => Assert.Equal(expected, CustomerRules.NormalizeIdentity(IdentityDocumentType.Cnic, raw));

    [Fact]
    public void Passport_IsNormalizedToUpperAlphanumeric() => Assert.Equal("AB12345", CustomerRules.NormalizeIdentity(IdentityDocumentType.Passport, " ab-12 345 "));

    [Fact]
    public void IdentityValidation_ChecksLengthPerDocumentType()
    {
        Assert.Null(CustomerRules.IdentityError(IdentityDocumentType.Cnic, "3520212345671"));
        Assert.NotNull(CustomerRules.IdentityError(IdentityDocumentType.Cnic, "123"));
        Assert.NotNull(CustomerRules.IdentityError(IdentityDocumentType.Cnic, "35202123456712"));
        Assert.Null(CustomerRules.IdentityError(IdentityDocumentType.Passport, "AB12345"));
        Assert.NotNull(CustomerRules.IdentityError(IdentityDocumentType.Passport, "AB1"));
        Assert.NotNull(CustomerRules.IdentityError(IdentityDocumentType.Passport, new string('A', 21)));
    }

    [Theory]
    [InlineData(CustomerStatus.Active, CustomerStatus.Inactive, true)]
    [InlineData(CustomerStatus.Inactive, CustomerStatus.Active, true)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Blocked, true)]
    [InlineData(CustomerStatus.Blocked, CustomerStatus.Active, false)]
    [InlineData(CustomerStatus.Blocked, CustomerStatus.Inactive, true)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Active, true)]
    public void CustomerStatusChanges_FollowTheTransitionRules(CustomerStatus from, CustomerStatus to, bool allowed) => Assert.Equal(allowed, CustomerRules.CanChangeStatus(from, to));

    [Fact]
    public void Ownership_Close_PreservesRowAndFreesActiveKey()
    {
        var o = new CustomerOwnership { CustomerId = 1, PropertyId = 2, IsActive = true, ActiveKey = CustomerOwnership.KeyFor(2, 1) };
        Assert.Equal("2:1", o.ActiveKey);
        var now = DateTime.UtcNow;
        o.Close(now, "Transfer");
        Assert.False(o.IsActive); Assert.Null(o.ActiveKey); Assert.Equal(now, o.EndedAtUtc); Assert.Equal("Transfer", o.EndReason);
        Assert.Throws<DomainException>(() => o.Close(now, "again"));
    }

    // ---------- agreement text ----------
    private static AgreementData Data(int version = 1, string owner = "Ali Khan") => new("Serene Estates", "AGR-000001", version, "Agreement generated", new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
        "BK-000001", new DateOnly(2026, 11, 19), "P1", "Serene City", PaymentMode.Installment, "Rs",
        [new AgreementOwner("CUS-000001", owner, "3520212345671", 100m, true)],
        [new AgreementLine("PLT-1", "1", 5m, MeasurementUnit.Marla, 5_000_000m, 0m, 5_000_000m, "abc123")], [new AgreementChargeLine("Registration: fee", 25_000m)],
        5_000_000m, 0m, 25_000m, 5_025_000m, 500_000m, null);

    [Fact]
    public void AgreementText_IsDeterministic_AndContainsTheKeyTerms()
    {
        var a = AgreementText.BuildAgreement(Data()); var b = AgreementText.BuildAgreement(Data());
        Assert.Equal(a, b);
        Assert.Equal(AgreementText.Sha256(a), AgreementText.Sha256(b));
        foreach (var s in new[] { "AGR-000001", "BK-000001", "Ali Khan", "100%", "PLT-1", "5,025,000.00", "500,000.00", "4,525,000.00", "Installment", "abc123", "Registration: fee" }) Assert.Contains(s, a);
    }

    [Fact]
    public void AgreementText_ChangesWhenOwnershipChanges()
    {
        var v1 = AgreementText.BuildAgreement(Data(1)); var v2 = AgreementText.BuildAgreement(Data(2, "Sara Khan"));
        Assert.NotEqual(AgreementText.Sha256(v1), AgreementText.Sha256(v2));
        Assert.Contains("Sara Khan", v2); Assert.DoesNotContain("Sara Khan", v1);
    }

    [Fact]
    public void Receipt_StatesAmountsAndDoesNotClaimPayment()
    {
        var r = AgreementText.BuildReceipt(Data());
        Assert.Contains("BOOKING CONFIRMATION RECEIPT", r); Assert.Contains("500,000.00", r); Assert.Contains("confirms the booking only", r);
    }

    [Fact]
    public void AgreementText_ShowsBalanceAuthorization_WhenPresent()
    {
        var d = Data() with { PaymentMode = PaymentMode.Cash, BalanceAuthorization = "Director - pays after sale" };
        Assert.Contains("Outstanding balance authorized: Director - pays after sale", AgreementText.BuildAgreement(d));
    }
}
