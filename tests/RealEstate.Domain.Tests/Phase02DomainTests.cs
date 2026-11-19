using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Tests;

public class PropertyTests
{
    private static Property New(decimal size = 10m) => Property.Create(1, " rp-1 ", " 12 ", 1, size, MeasurementUnit.Marla);
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_NormalisesCode_StartsAvailable()
    {
        var p = New(5.5m);
        Assert.Equal("RP-1", p.PropertyCode);
        Assert.Equal("12", p.PropertyNumber);
        Assert.Equal(PropertyStatus.Available, p.Status);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000000000)] [InlineData(1.00001)]
    public void Create_RejectsInvalidSize(decimal size) => Assert.Throws<DomainException>(() => New(size));

    [Fact]
    public void Create_RequiresCodeAndNumber()
    {
        Assert.Throws<DomainException>(() => Property.Create(1, " ", "1", 1, 1, MeasurementUnit.Kanal));
        Assert.Throws<DomainException>(() => Property.Create(1, "c", "", 1, 1, MeasurementUnit.Kanal));
    }

    public static IEnumerable<object[]> ValidTransitions() =>
    [
        [PropertyStatus.Available, PropertyStatus.Reserved], [PropertyStatus.Available, PropertyStatus.Booked], [PropertyStatus.Available, PropertyStatus.Cancelled],
        [PropertyStatus.Reserved, PropertyStatus.Available], [PropertyStatus.Reserved, PropertyStatus.Booked], [PropertyStatus.Booked, PropertyStatus.Sold],
        [PropertyStatus.Sold, PropertyStatus.Allocated], [PropertyStatus.Sold, PropertyStatus.PossessionReady], [PropertyStatus.Allocated, PropertyStatus.PossessionReady],
        [PropertyStatus.PossessionReady, PropertyStatus.PossessionDelivered], [PropertyStatus.PossessionDelivered, PropertyStatus.Transferred], [PropertyStatus.Cancelled, PropertyStatus.Available],
    ];

    [Theory, MemberData(nameof(ValidTransitions))]
    public void ValidTransitions_Allowed(PropertyStatus from, PropertyStatus to) => Assert.True(Property.CanTransition(from, to));

    [Theory]
    [InlineData(PropertyStatus.Available, PropertyStatus.Sold)]
    [InlineData(PropertyStatus.Available, PropertyStatus.PossessionDelivered)]
    [InlineData(PropertyStatus.Reserved, PropertyStatus.Sold)]
    [InlineData(PropertyStatus.Booked, PropertyStatus.Available)]
    [InlineData(PropertyStatus.Booked, PropertyStatus.Reserved)]
    [InlineData(PropertyStatus.Sold, PropertyStatus.Available)]
    [InlineData(PropertyStatus.Transferred, PropertyStatus.Available)]
    [InlineData(PropertyStatus.Transferred, PropertyStatus.Cancelled)]
    [InlineData(PropertyStatus.Cancelled, PropertyStatus.Sold)]
    [InlineData(PropertyStatus.Available, PropertyStatus.Available)]
    public void InvalidTransitions_Rejected(PropertyStatus from, PropertyStatus to) => Assert.False(Property.CanTransition(from, to));

    [Fact]
    public void EveryStatus_HasDefinedTransitionRules_AndTransferredIsTerminal()
    {
        foreach (var s in Enum.GetValues<PropertyStatus>())
            Assert.False(Property.CanTransition(s, s));
        Assert.DoesNotContain(Enum.GetValues<PropertyStatus>(), t => Property.CanTransition(PropertyStatus.Transferred, t));
    }

    [Fact]
    public void ChangeStatus_RequiresReason_AndWritesHistory()
    {
        var p = New();
        Assert.Throws<DomainException>(() => p.ChangeStatus(PropertyStatus.Booked, " ", Now, null, "u"));
        var h = p.ChangeStatus(PropertyStatus.Booked, "Booked by sales", Now, Guid.NewGuid(), "u");
        Assert.Equal(PropertyStatus.Booked, p.Status);
        Assert.Equal(PropertyStatus.Available, h.FromStatus);
        Assert.Equal(PropertyStatus.Booked, h.ToStatus);
        Assert.Throws<DomainException>(() => p.ChangeStatus(PropertyStatus.Available, "oops", Now, null, "u"));
    }

    [Fact]
    public void StructureEditable_OnlyBeforeSale()
    {
        var p = New();
        Assert.True(p.IsStructureEditable);
        p.ChangeStatus(PropertyStatus.Booked, "r", Now, null, "u");
        Assert.False(p.IsStructureEditable);
    }
}

public class PropertyReservationTests
{
    private static readonly DateOnly D = new(2026, 3, 1);
    private static PropertyReservation New(decimal amount = 100m) => PropertyReservation.Create(5, D, D.AddDays(7), amount, " Ali ", null, Guid.NewGuid(), "Boss");

    [Fact]
    public void Create_IsActive_AndHoldsActiveKey()
    {
        var r = New();
        Assert.Equal(ReservationStatus.Active, r.Status);
        Assert.Equal(5, r.ActivePropertyId);
        Assert.Equal("Ali", r.ReservedForName);
    }

    [Fact]
    public void Create_Validates()
    {
        Assert.Throws<DomainException>(() => PropertyReservation.Create(1, D, D.AddDays(-1), 0, "x", null, Guid.NewGuid(), null));
        Assert.Throws<DomainException>(() => New(-1m));
        Assert.Throws<DomainException>(() => New(1.005m));
        Assert.Throws<DomainException>(() => PropertyReservation.Create(1, D, D, 0, " ", null, Guid.NewGuid(), null));
    }

    [Fact]
    public void Close_ClearsActiveKey_AndCannotCloseTwice()
    {
        var r = New();
        r.Close(ReservationStatus.Released, DateTime.UtcNow, "u", "changed mind");
        Assert.Null(r.ActivePropertyId);
        Assert.Equal(ReservationStatus.Released, r.Status);
        Assert.Throws<DomainException>(() => r.Close(ReservationStatus.Expired, DateTime.UtcNow, "u", "x"));
    }

    [Fact]
    public void Expiry_IsAfterExpiryDate()
    {
        var r = New();
        Assert.False(r.IsExpiredOn(D.AddDays(7)));
        Assert.True(r.IsExpiredOn(D.AddDays(8)));
    }
}
