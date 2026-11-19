using FluentValidation;
using NSubstitute;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;

namespace RealEstate.Application.Tests;

public class AuthServiceTests
{
    private readonly IIdentityService identity = Substitute.For<IIdentityService>();
    private readonly IAccessTokenFactory access = Substitute.For<IAccessTokenFactory>();
    private readonly IRefreshTokenService refresh = Substitute.For<IRefreshTokenService>();
    private readonly IPermissionService permissions = Substitute.For<IPermissionService>();
    private readonly IAuditService audit = Substitute.For<IAuditService>();
    private readonly ICurrentUser current = Substitute.For<ICurrentUser>();
    private readonly UserIdentityInfo user = new(Guid.NewGuid(), "a@b.com", "A B", true);

    private AuthService Sut() => new(identity, access, refresh, permissions, audit, current,
        new LoginRequestValidator(), new RefreshRequestValidator(), new LogoutRequestValidator());

    [Fact]
    public async Task Login_Valid_ReturnsTokensAndAudits()
    {
        identity.CheckPasswordAsync("a@b.com", "pw", default).Returns((CredentialStatus.Valid, user));
        identity.GetRolesAsync(user.Id, default).Returns(["Administrator"]);
        access.Create(user, Arg.Any<IReadOnlyList<string>>()).Returns(new IssuedAccessToken("jwt", DateTime.UtcNow.AddMinutes(15)));
        refresh.IssueAsync(user.Id, Arg.Any<string?>(), default).Returns(new RefreshTokenIssue("rt", DateTime.UtcNow.AddDays(1)));

        var result = await Sut().LoginAsync(new LoginRequest(" a@b.com ", "pw"));

        Assert.True(result.Succeeded);
        Assert.Equal("jwt", result.Value!.AccessToken);
        Assert.Equal("rt", result.Value.RefreshToken);
        await identity.Received().RecordLoginAsync(user.Id, default);
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action == "Login.Succeeded"), default);
    }

    [Theory]
    [InlineData(CredentialStatus.Invalid, AuthErrors.InvalidCredentials)]
    [InlineData(CredentialStatus.Inactive, AuthErrors.InvalidCredentials)]
    [InlineData(CredentialStatus.LockedOut, AuthErrors.LockedOut)]
    public async Task Login_Failure_ReturnsErrorWithoutTokens(CredentialStatus status, string code)
    {
        identity.CheckPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), default).Returns((status, null));

        var result = await Sut().LoginAsync(new LoginRequest("a@b.com", "bad"));

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        await refresh.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default);
        await identity.DidNotReceiveWithAnyArgs().RecordLoginAsync(default, default);
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action.StartsWith("Login.")), default);
    }

    [Fact]
    public async Task Login_InactiveAndUnknownGiveSameMessage()
    {
        identity.CheckPasswordAsync("inactive@b.com", "pw", default).Returns((CredentialStatus.Inactive, user));
        identity.CheckPasswordAsync("none@b.com", "pw", default).Returns((CredentialStatus.Invalid, null));
        var a = await Sut().LoginAsync(new LoginRequest("inactive@b.com", "pw"));
        var b = await Sut().LoginAsync(new LoginRequest("none@b.com", "pw"));
        Assert.Equal(a.Error!.Message, b.Error!.Message);
    }

    [Fact]
    public async Task Login_BlankInput_ThrowsValidation() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Sut().LoginAsync(new LoginRequest("", "")));

    [Fact]
    public async Task Refresh_InvalidToken_Fails()
    {
        refresh.RotateAsync("x", Arg.Any<string?>(), default).Returns(new RefreshOutcome(false, Guid.Empty, null));
        var r = await Sut().RefreshAsync(new RefreshRequest("x"));
        Assert.False(r.Succeeded);
        Assert.Equal(AuthErrors.InvalidToken, r.Error!.Code);
    }

    [Fact]
    public async Task Refresh_ForDeactivatedUser_RevokesAndFails()
    {
        refresh.RotateAsync("x", Arg.Any<string?>(), default).Returns(new RefreshOutcome(true, user.Id, new RefreshTokenIssue("new", DateTime.UtcNow)));
        identity.FindByIdAsync(user.Id, default).Returns(user with { IsActive = false });
        var r = await Sut().RefreshAsync(new RefreshRequest("x"));
        Assert.False(r.Succeeded);
        await refresh.Received().RevokeAsync("new", user.Id, default);
    }

    [Fact]
    public async Task Refresh_Valid_ReturnsRotatedPair()
    {
        refresh.RotateAsync("x", Arg.Any<string?>(), default).Returns(new RefreshOutcome(true, user.Id, new RefreshTokenIssue("new", DateTime.UtcNow.AddDays(1))));
        identity.FindByIdAsync(user.Id, default).Returns(user);
        identity.GetRolesAsync(user.Id, default).Returns([]);
        access.Create(user, Arg.Any<IReadOnlyList<string>>()).Returns(new IssuedAccessToken("jwt2", DateTime.UtcNow.AddMinutes(15)));
        var r = await Sut().RefreshAsync(new RefreshRequest("x"));
        Assert.True(r.Succeeded);
        Assert.Equal("new", r.Value!.RefreshToken);
    }

    [Fact]
    public async Task Logout_RevokesTokenForCurrentUser()
    {
        current.UserId.Returns(user.Id);
        await Sut().LogoutAsync(new LogoutRequest("rt"));
        await refresh.Received().RevokeAsync("rt", user.Id, default);
        await audit.Received().RecordAsync(Arg.Is<AuditEntry>(e => e.Action == "Logout"), default);
    }

    [Fact]
    public async Task GetCurrentUser_Anonymous_IsForbidden()
    {
        current.UserId.Returns((Guid?)null);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetCurrentUserAsync());
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsRolesAndPermissions()
    {
        current.UserId.Returns(user.Id);
        identity.FindByIdAsync(user.Id, default).Returns(user);
        identity.GetRolesAsync(user.Id, default).Returns(["Administrator"]);
        permissions.GetPermissionsAsync(user.Id, default).Returns(["users.view"]);
        var me = await Sut().GetCurrentUserAsync();
        Assert.Equal(["Administrator"], me.Roles);
        Assert.Equal(["users.view"], me.Permissions);
    }
}
