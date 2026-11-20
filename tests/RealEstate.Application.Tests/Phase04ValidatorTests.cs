using RealEstate.Application.Customers;
using RealEstate.Application.Sales;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase04ValidatorTests
{
    private static CreateCustomerRequest Customer(string name = "Ali Khan", IdentityDocumentType type = IdentityDocumentType.Cnic, string id = "35202-1234567-1", string mobile = "0300-1234567", string? email = "a@b.com",
        IReadOnlyList<AddressDto>? addresses = null, IReadOnlyList<ContactDto>? contacts = null, IReadOnlyList<NomineeDto>? nominees = null) =>
        new(name, type, id, mobile, email, "Address", null, addresses, contacts, nominees);

    private static bool Fails(CreateCustomerRequest r, string field) => new CreateCustomerRequestValidator().Validate(r).Errors.Any(e => e.PropertyName == field);

    [Fact]
    public void Customer_Valid_Passes() => Assert.True(new CreateCustomerRequestValidator().Validate(Customer()).IsValid);

    [Fact]
    public void Customer_RequiredFields_AreChecked()
    {
        Assert.True(Fails(Customer(name: ""), "FullName"));
        Assert.True(Fails(Customer(id: ""), "IdentityNumber"));
        Assert.True(Fails(Customer(mobile: ""), "Mobile"));
    }

    [Theory]
    [InlineData(IdentityDocumentType.Cnic, "1234", true)]
    [InlineData(IdentityDocumentType.Cnic, "3520212345671", false)]
    [InlineData(IdentityDocumentType.Cnic, "35202-1234567-1", false)]
    [InlineData(IdentityDocumentType.Passport, "AB1", true)]
    [InlineData(IdentityDocumentType.Passport, "AB12345", false)]
    public void Customer_IdentityNumber_IsValidatedPerType(IdentityDocumentType type, string id, bool fails) => Assert.Equal(fails, Fails(Customer(type: type, id: id), "IdentityNumber"));

    [Theory]
    [InlineData("abc", true)]
    [InlineData("123", true)]
    [InlineData("0300-1234567", false)]
    [InlineData("+92 300 1234567", false)]
    [InlineData("+92 (300) 123-4567 ext", true)]
    public void Customer_Mobile_IsValidated(string mobile, bool fails) => Assert.Equal(fails, Fails(Customer(mobile: mobile), "Mobile"));

    [Fact]
    public void Customer_Email_IsValidatedWhenGiven()
    {
        Assert.True(Fails(Customer(email: "bad"), "Email"));
        Assert.False(Fails(Customer(email: null), "Email"));
        Assert.False(Fails(Customer(email: "  "), "Email"));
    }

    [Fact]
    public void Customer_Nominees_TotalAtMost100_AndEachShareInRange()
    {
        Assert.False(Fails(Customer(nominees: [new("A", "Son", null, null, 50m), new("B", "Son", null, null, 50m)]), "Nominees"));
        Assert.True(Fails(Customer(nominees: [new("A", "Son", null, null, 50m), new("B", "Son", null, null, 50.5m)]), "Nominees"));
        Assert.True(Fails(Customer(nominees: [new("A", "Son", null, null, -1m)]), "Nominees"));
        Assert.True(Fails(Customer(nominees: [new("A", "Son", null, null, 100.5m)]), "Nominees"));
        Assert.True(Fails(Customer(nominees: [new("", "Son", null, null, 10m)]), "Nominees"));
    }

    [Fact]
    public void Customer_Addresses_AtMostOnePrimary_AndContactsNeedPhoneOrEmail()
    {
        Assert.True(Fails(Customer(addresses: [new(AddressType.Home, "A", null, null, true), new(AddressType.Office, "B", null, null, true)]), "Addresses"));
        Assert.False(Fails(Customer(addresses: [new(AddressType.Home, "A", null, null, true), new(AddressType.Office, "B", null, null, false)]), "Addresses"));
        Assert.True(Fails(Customer(addresses: [new(AddressType.Home, "", null, null, false)]), "Addresses"));
        Assert.True(Fails(Customer(contacts: [new("Brother", null, null, null)]), "Contacts"));
        Assert.False(Fails(Customer(contacts: [new("Brother", null, "0300-1111111", null)]), "Contacts"));
    }

    // ---------- bookings ----------
    private static CreateBookingRequest Booking(IReadOnlyList<BookingPropertyInput>? props = null, IReadOnlyList<OwnerInput>? owners = null, decimal amount = 100m, IReadOnlyList<ChargeInput>? charges = null) =>
        new(1, null, PaymentMode.Installment, props ?? [new BookingPropertyInput(1, 0m, null)], owners ?? [new OwnerInput(1, 100m)], charges, amount, false, null, null, false, null);

    private static bool BookingFails(CreateBookingRequest r, string field) => new CreateBookingRequestValidator().Validate(r).Errors.Any(e => e.PropertyName == field);

    [Fact]
    public void Booking_Valid_Passes() => Assert.True(new CreateBookingRequestValidator().Validate(Booking()).IsValid);

    [Fact]
    public void Booking_Properties_AreChecked()
    {
        Assert.True(BookingFails(Booking(props: []), "Properties"));
        Assert.True(BookingFails(Booking(props: [new(1, 0m, null), new(1, 0m, null)]), "Properties"));
        Assert.True(BookingFails(Booking(props: [new(0, 0m, null)]), "Properties"));
        Assert.True(BookingFails(Booking(props: [new(1, 10m, null)]), "Properties"));
        Assert.True(BookingFails(Booking(props: [new(1, 10m, "abc")]), "Properties"));
        Assert.False(BookingFails(Booking(props: [new(1, 10m, "Negotiated discount")]), "Properties"));
        Assert.True(BookingFails(Booking(props: [new(1, -5m, "Negotiated discount")]), "Properties"));
        Assert.True(BookingFails(Booking(props: Enumerable.Range(1, 11).Select(i => new BookingPropertyInput(i, 0m, null)).ToList()), "Properties"));
    }

    [Fact]
    public void Booking_Owners_MustTotal100_AndBeUnique()
    {
        Assert.False(BookingFails(Booking(owners: [new(1, 60m), new(2, 40m)]), "Owners"));
        Assert.True(BookingFails(Booking(owners: [new(1, 60m), new(2, 30m)]), "Owners"));
        Assert.True(BookingFails(Booking(owners: [new(1, 50m), new(1, 50m)]), "Owners"));
        Assert.True(BookingFails(Booking(owners: []), "Owners"));
        Assert.True(BookingFails(Booking(owners: [new(0, 100m)]), "Owners"));
        Assert.True(BookingFails(Booking(owners: [new(1, 100m), new(2, 0m)]), "Owners"));
    }

    [Fact]
    public void Booking_Charges_AreChecked()
    {
        Assert.False(BookingFails(Booking(charges: [new(BookingChargeType.Tax, "Stamp duty", 1000m)]), "Charges"));
        Assert.True(BookingFails(Booking(charges: [new(BookingChargeType.Tax, "", 1000m)]), "Charges"));
        Assert.True(BookingFails(Booking(charges: [new(BookingChargeType.Tax, "x", 0m)]), "Charges"));
        Assert.True(BookingFails(Booking(charges: [new(BookingChargeType.Tax, "x", 10.123m)]), "Charges"));
        Assert.True(BookingFails(Booking(charges: [new((BookingChargeType)99, "x", 10m)]), "Charges"));
    }

    [Fact]
    public void Booking_Amount_MustBeNonNegativeWithTwoDecimals()
    {
        Assert.True(BookingFails(Booking(amount: -1m), "BookingAmount"));
        Assert.True(BookingFails(Booking(amount: 1.005m), "BookingAmount"));
        Assert.False(BookingFails(Booking(amount: 0m), "BookingAmount"));
    }

    [Fact]
    public void Cancel_And_Transfer_RequireAReason()
    {
        var cancel = new CancelBookingRequestValidator();
        Assert.False(cancel.Validate(new CancelBookingRequest("", null, null, null)).IsValid);
        Assert.False(cancel.Validate(new CancelBookingRequest("abc", null, null, null)).IsValid);
        Assert.True(cancel.Validate(new CancelBookingRequest("Customer withdrew", 100m, null, null)).IsValid);
        Assert.False(cancel.Validate(new CancelBookingRequest("Customer withdrew", 0m, null, null)).IsValid);
        Assert.False(cancel.Validate(new CancelBookingRequest("Customer withdrew", -5m, null, null)).IsValid);
        var transfer = new TransferBookingRequestValidator();
        Assert.False(transfer.Validate(new TransferBookingRequest("", [new(1, 100m)], null)).IsValid);
        Assert.False(transfer.Validate(new TransferBookingRequest("Sold to relative", [new(1, 90m)], null)).IsValid);
        Assert.True(transfer.Validate(new TransferBookingRequest("Sold to relative", [new(1, 100m)], null)).IsValid);
        Assert.False(new RejectBookingRequestValidator().Validate(new RejectBookingRequest("", null)).IsValid);
    }
}
