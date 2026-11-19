using RealEstate.Application.Inventory;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase02ValidatorTests
{
    private static CreatePropertyRequest Valid() => new(1, "RP-1", "12", null, null, null, 1, 5m, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Undeveloped, null, null, null);

    [Fact] public void ValidProperty_Passes() => Assert.True(new CreatePropertyRequestValidator().Validate(Valid()).IsValid);

    [Theory]
    [InlineData("")] [InlineData("bad code")] [InlineData("-x")]
    public void Code_Invalid(string code) => Assert.False(new CreatePropertyRequestValidator().Validate(Valid() with { PropertyCode = code }).IsValid);

    [Theory]
    [InlineData(0)] [InlineData(-3)] [InlineData(1.00001)]
    public void Size_Invalid(double size) => Assert.False(new CreatePropertyRequestValidator().Validate(Valid() with { Size = (decimal)size }).IsValid);

    [Fact]
    public void DuplicateDimensionNames_Rejected() =>
        Assert.False(new CreatePropertyRequestValidator().Validate(Valid() with { Dimensions = [new("Length", 1, "ft"), new("length", 2, "ft")] }).IsValid);

    [Fact]
    public void NegativeDimension_Rejected() =>
        Assert.False(new CreatePropertyRequestValidator().Validate(Valid() with { Dimensions = [new("Length", -1, "ft")] }).IsValid);

    [Fact]
    public void Reserve_Validates()
    {
        var v = new ReservePropertyRequestValidator();
        var d = new DateOnly(2026, 1, 1);
        Assert.True(v.Validate(new ReservePropertyRequest(d, d.AddDays(5), 100m, null, "Ali", null, Guid.NewGuid(), null)).IsValid);
        Assert.False(v.Validate(new ReservePropertyRequest(d, d.AddDays(-1), 100m, null, "Ali", null, Guid.NewGuid(), null)).IsValid);
        Assert.False(v.Validate(new ReservePropertyRequest(d, d, -1m, null, "Ali", null, Guid.NewGuid(), null)).IsValid);
        Assert.False(v.Validate(new ReservePropertyRequest(d, d, 1.234m, null, "Ali", null, Guid.NewGuid(), null)).IsValid);
        Assert.False(v.Validate(new ReservePropertyRequest(d, d, 1m, null, "", null, Guid.NewGuid(), null)).IsValid);
        Assert.False(v.Validate(new ReservePropertyRequest(d, d, 1m, null, "Ali", null, Guid.Empty, null)).IsValid);
    }

    [Fact]
    public void ReasonsRequired()
    {
        Assert.False(new ReleaseReservationRequestValidator().Validate(new ReleaseReservationRequest("", null)).IsValid);
        Assert.False(new ChangePropertyStatusRequestValidator().Validate(new ChangePropertyStatusRequest(PropertyStatus.Booked, "x", null)).IsValid);
        Assert.True(new ChangePropertyStatusRequestValidator().Validate(new ChangePropertyStatusRequest(PropertyStatus.Booked, "Customer booked", null)).IsValid);
    }

    [Fact]
    public void Setup_Validates()
    {
        Assert.False(new SaveCategoryRequestValidator().Validate(new SaveCategoryRequest("", "n", null, true)).IsValid);
        Assert.True(new SaveZoneRequestValidator().Validate(new SaveZoneRequest("B1", "Block 1", true)).IsValid);
        Assert.False(new SavePropertyTypeRequestValidator().Validate(new SavePropertyTypeRequest("T", "x", 0, PropertyForm.Plot, false, true)).IsValid);
    }
}
