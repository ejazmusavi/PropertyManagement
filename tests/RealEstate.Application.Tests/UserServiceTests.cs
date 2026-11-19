using NSubstitute;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Tests;

public class UserServiceTests
{
    private readonly IUserQueryService queries = Substitute.For<IUserQueryService>();
    private readonly IIdentityService identity = Substitute.For<IIdentityService>();
    private readonly IPermissionService permissions = Substitute.For<IPermissionService>();
    private readonly IAuditService audit = Substitute.For<IAuditService>();
    private readonly ICurrentUser current = Substitute.For<ICurrentUser>();
    private readonly Guid me = Guid.NewGuid();

    public UserServiceTests() => current.UserId.Returns(me);

    private UserService Sut() => new(queries, identity, permissions, audit, current,
        new UpdateProfileRequestValidator(), new ChangePasswordRequestValidator());

    [Fact]
    public async Task List_WithoutPermission_IsForbidden()
    {
        permissions.HasPermissionAsync(me, Permissions.UsersView, default).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().ListAsync(new PagedRequest()));
        await queries.DidNotReceiveWithAnyArgs().ListAsync(default!, default);
    }

    [Fact]
    public async Task List_WithPermission_ReturnsPage()
    {
        permissions.HasPermissionAsync(me, Permissions.UsersView, default).Returns(true);
        var page = new PagedResult<UserListItemDto>([], 1, 20, 0);
        queries.ListAsync(Arg.Any<PagedRequest>(), default).Returns(page);
        Assert.Same(page, await Sut().ListAsync(new PagedRequest()));
    }

    [Fact]
    public async Task List_Anonymous_IsForbidden()
    {
        current.UserId.Returns((Guid?)null);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().ListAsync(new PagedRequest()));
    }

    [Fact]
    public async Task UpdateProfile_AuditsOldAndNewValues()
    {
        identity.GetProfileAsync(me, default).Returns(new ProfileDto(me, "a@b.com", "Old", null, [], null));
        await Sut().UpdateProfileAsync(new UpdateProfileRequest(" New ", " 0300 1234567 "));
        await identity.Received().UpdateProfileAsync(me, "New", "0300 1234567", default);
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action == "Profile.Updated" && e.OldValues != null && e.NewValues != null), default);
    }

    [Fact]
    public async Task UpdateProfile_InvalidPhone_Throws() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Sut().UpdateProfileAsync(new UpdateProfileRequest("Name", "abc")));

    [Theory]
    [InlineData("short1A!", "short1A!")]
    [InlineData("alllowercase123!", "alllowercase123!")]
    [InlineData("NoDigitsHere!!!!", "NoDigitsHere!!!!")]
    [InlineData("NoSymbolsHere1234", "NoSymbolsHere1234")]
    public async Task ChangePassword_WeakPassword_Throws(string pw, string confirm) =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Sut().ChangePasswordAsync(new ChangePasswordRequest("Old#Password123", pw, confirm)));

    [Fact]
    public async Task ChangePassword_Mismatch_Throws() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Sut().ChangePasswordAsync(new ChangePasswordRequest("Old#Password123", "New#Password123", "Different#Password123")));

    [Fact]
    public async Task ChangePassword_SameAsCurrent_Throws() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Sut().ChangePasswordAsync(new ChangePasswordRequest("Same#Password123", "Same#Password123", "Same#Password123")));

    [Fact]
    public async Task ChangePassword_WrongCurrent_ThrowsAndAuditsFailure()
    {
        identity.ChangePasswordAsync(me, "Old#Password123", "New#Password123", default).Returns(["Current password is incorrect."]);
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => Sut().ChangePasswordAsync(new ChangePasswordRequest("Old#Password123", "New#Password123", "New#Password123")));
        Assert.Contains("CurrentPassword", ex.Errors.Keys);
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action == "Password.ChangeFailed"), default);
    }

    [Fact]
    public async Task ChangePassword_Success_AuditsWithoutPasswordValues()
    {
        identity.ChangePasswordAsync(me, "Old#Password123", "New#Password123", default).Returns([]);
        await Sut().ChangePasswordAsync(new ChangePasswordRequest("Old#Password123", "New#Password123", "New#Password123"));
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action == "Password.Changed" && e.NewValues == null && e.OldValues == null), default);
    }
}

public class PagedRequestTests
{
    [Fact]
    public void Clamps_PageAndSize()
    {
        var r = new PagedRequest { Page = -3, PageSize = 100000 };
        Assert.Equal(1, r.Page);
        Assert.Equal(PagedRequest.MaxPageSize, r.PageSize);
        r.PageSize = 0;
        Assert.Equal(20, r.PageSize);
    }

    [Fact]
    public void PagedResult_ComputesPages()
    {
        var p = new PagedResult<int>([1], 2, 10, 25);
        Assert.Equal(3, p.TotalPages);
        Assert.True(p.HasPrevious);
        Assert.True(p.HasNext);
    }
}
