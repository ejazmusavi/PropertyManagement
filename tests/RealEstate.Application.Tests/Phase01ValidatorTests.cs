using NSubstitute;
using RealEstate.Application.Administration;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Companies;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase01ValidatorTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public void CreateProject_ValidatesCodeAreaAndDates()
    {
        var v = new CreateProjectRequestValidator();
        var ok = new CreateProjectRequest("SRN-1", "Serene", null, "Rawalpindi", null, null, null, 200m, MeasurementUnit.Kanal, null, null);
        Assert.True(v.Validate(ok).IsValid);
        Assert.False(v.Validate(ok with { ProjectCode = "bad code!" }).IsValid);
        Assert.False(v.Validate(ok with { InitialArea = 0 }).IsValid);
        Assert.False(v.Validate(ok with { InitialArea = 1.00001m }).IsValid);
        Assert.False(v.Validate(ok with { City = "" }).IsValid);
        Assert.False(v.Validate(ok with { StartDate = Today, ExpectedCompletionDate = Today.AddDays(-1) }).IsValid);
    }

    [Fact]
    public void AdjustLand_RequiresReasonSupportApproverAndPositiveArea()
    {
        var v = new AdjustLandRequestValidator();
        var ok = new AdjustLandRequest(Today, LandAdjustmentType.Addition, 5m, "Survey correction", "Deed 1", Guid.NewGuid(), null);
        Assert.True(v.Validate(ok).IsValid);
        Assert.False(v.Validate(ok with { Reason = "" }).IsValid);
        Assert.False(v.Validate(ok with { SupportingReference = "" }).IsValid);
        Assert.False(v.Validate(ok with { ApprovedByUserId = Guid.Empty }).IsValid);
        Assert.False(v.Validate(ok with { Area = -1 }).IsValid);
        Assert.False(v.Validate(ok with { AdjustmentDate = Today.AddDays(10) }).IsValid);
    }

    [Fact]
    public void Distribution_RejectsUnclassifiedInput_AndDuplicates()
    {
        var v = new SetLandDistributionRequestValidator();
        Assert.True(v.Validate(new SetLandDistributionRequest([new(LandCategory.Roads, 5m)], "Plan", "v1")).IsValid);
        Assert.False(v.Validate(new SetLandDistributionRequest([new(LandCategory.Unclassified, 5m)], "Plan", "v1")).IsValid);
        Assert.False(v.Validate(new SetLandDistributionRequest([new(LandCategory.Roads, 5m), new(LandCategory.Roads, 1m)], "Plan", "v1")).IsValid);
        Assert.False(v.Validate(new SetLandDistributionRequest([new(LandCategory.Roads, -5m)], "Plan", "v1")).IsValid);
        Assert.False(v.Validate(new SetLandDistributionRequest([new(LandCategory.Roads, 5m)], "", "v1")).IsValid);
        Assert.False(v.Validate(new SetLandDistributionRequest([new(LandCategory.Roads, 5m)], "Plan", "")).IsValid);
    }

    [Fact]
    public void Acquire_ValidatesAreaAndCost()
    {
        var v = new AcquireLandRequestValidator();
        var ok = new AcquireLandRequest(Today, "Seller", 10m, 5000.50m, null, null, null, null);
        Assert.True(v.Validate(ok).IsValid);
        Assert.False(v.Validate(ok with { Cost = 1.234m }).IsValid);
        Assert.False(v.Validate(ok with { Cost = -1m }).IsValid);
        Assert.False(v.Validate(ok with { SellerName = "" }).IsValid);
    }

    [Fact]
    public void Company_ValidatesCurrencyFiscalYearAndTimeZone()
    {
        var v = new UpdateCompanyRequestValidator();
        var ok = new UpdateCompanyRequest("Serene", null, null, "a@b.com", null, null, null, null, null, null, "PKR", "Rs", 2, RoundingMode.Nearest, 7, "UTC", 10, "pdf,jpg", null);
        Assert.True(v.Validate(ok).IsValid);
        Assert.False(v.Validate(ok with { CurrencyCode = "PKRS" }).IsValid);
        Assert.False(v.Validate(ok with { FiscalYearStartMonth = 0 }).IsValid);
        Assert.False(v.Validate(ok with { TimeZoneId = "Nowhere/Land" }).IsValid);
        Assert.False(v.Validate(ok with { AllowedFileExtensions = "p!f" }).IsValid);
        Assert.False(v.Validate(ok with { Email = "nope" }).IsValid);
    }
}

public class UserAdministrationRuleTests
{
    private readonly IAdministrationStore store = Substitute.For<IAdministrationStore>();
    private readonly IIdentityService identity = Substitute.For<IIdentityService>();
    private readonly IPermissionService permissions = Substitute.For<IPermissionService>();
    private readonly ICurrentUser current = Substitute.For<ICurrentUser>();
    private readonly Guid me = Guid.NewGuid();

    private UserAdministrationService Sut() => new(store, identity, permissions, current, Substitute.For<IAuditService>(),
        new CreateUserRequestValidator(), new UpdateUserRequestValidator());

    public UserAdministrationRuleTests()
    {
        current.UserId.Returns(me);
        permissions.HasPermissionAsync(me, Permissions.UsersManage, default).Returns(true);
        store.ListRolesAsync(default).Returns([new RoleDto(Guid.NewGuid(), RoleNames.Administrator, null, true, 1, 1), new RoleDto(Guid.NewGuid(), RoleNames.Cashier, null, true, 0, 0)]);
    }

    [Fact]
    public async Task NonAdministrator_CannotGrantAdministratorRole()
    {
        identity.GetRolesAsync(me, default).Returns([RoleNames.Cashier]);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().CreateAsync(new CreateUserRequest("a@b.com", "A", null, "Str0ng#Passw0rd!", true, [RoleNames.Administrator])));
        await store.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default, default!, default, default!, default);
    }

    [Fact]
    public async Task NonAdministrator_CannotEditAnAdministrator()
    {
        var target = Guid.NewGuid();
        identity.GetRolesAsync(me, default).Returns([RoleNames.Cashier]);
        store.GetUserAsync(target, default).Returns(new UserDetailDto(target, "x@y.com", "X", null, true, [RoleNames.Administrator], null));
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().UpdateAsync(target, new UpdateUserRequest("X", null, true, [RoleNames.Administrator])));
    }

    [Fact]
    public async Task LastActiveAdministrator_CannotBeDeactivated()
    {
        var target = Guid.NewGuid();
        identity.GetRolesAsync(me, default).Returns([RoleNames.Administrator]);
        store.GetUserAsync(target, default).Returns(new UserDetailDto(target, "x@y.com", "X", null, true, [RoleNames.Administrator], null));
        store.CountActiveInRoleAsync(RoleNames.Administrator, default).Returns(1);
        await Assert.ThrowsAsync<DomainException>(() => Sut().UpdateAsync(target, new UpdateUserRequest("X", null, false, [RoleNames.Administrator])));
    }

    [Fact]
    public async Task Without_UsersManage_IsForbidden()
    {
        permissions.HasPermissionAsync(me, Permissions.UsersManage, default).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().UpdateAsync(Guid.NewGuid(), new UpdateUserRequest("X", null, true, [])));
    }
}
