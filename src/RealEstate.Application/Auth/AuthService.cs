using FluentValidation;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;

namespace RealEstate.Application.Auth;

/// <summary>Authentication use cases shared by the Web (cookie) and Api (token) hosts.</summary>
public class AuthService(
    IIdentityService identity,
    IAccessTokenFactory accessTokens,
    IRefreshTokenService refreshTokens,
    IPermissionService permissions,
    IAuditService audit,
    ICurrentUser currentUser,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshRequest> refreshValidator,
    IValidator<LogoutRequest> logoutValidator)
{
    /// <summary>Verifies credentials (without issuing tokens). Used by the MVC login which signs in with a cookie.</summary>
    public async Task<Result<UserIdentityInfo>> VerifyCredentialsAsync(LoginRequest request, CancellationToken ct = default)
    {
        await loginValidator.ValidateOrThrowAsync(request, ct);
        var (status, user) = await identity.CheckPasswordAsync(request.Email.Trim(), request.Password, ct);

        switch (status)
        {
            case CredentialStatus.Valid when user is not null:
                await identity.RecordLoginAsync(user.Id, ct);
                await audit.RecordAsync(new AuditEntry("Login.Succeeded", "User", user.Id.ToString(), UserId: user.Id, UserName: user.Email), ct);
                return Result<UserIdentityInfo>.Success(user);
            case CredentialStatus.LockedOut:
                await audit.RecordAsync(new AuditEntry("Login.LockedOut", "User", null, NewValues: new { Email = request.Email.Trim() }, UserName: request.Email.Trim()), ct);
                return Result<UserIdentityInfo>.Failure(AuthErrors.LockedOut, "Account is temporarily locked. Try again later.");
            case CredentialStatus.Inactive:
                await audit.RecordAsync(new AuditEntry("Login.Failed", "User", user?.Id.ToString(), NewValues: new { Reason = "Inactive" }, UserName: request.Email.Trim()), ct);
                return Result<UserIdentityInfo>.Failure(AuthErrors.InvalidCredentials, "Invalid email or password.");
            default:
                await audit.RecordAsync(new AuditEntry("Login.Failed", "User", null, NewValues: new { Reason = "InvalidCredentials" }, UserName: request.Email.Trim()), ct);
                return Result<UserIdentityInfo>.Failure(AuthErrors.InvalidCredentials, "Invalid email or password.");
        }
    }

    public async Task<Result<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var verified = await VerifyCredentialsAsync(request, ct);
        if (!verified.Succeeded) return Result<TokenResponse>.Failure(verified.Error!.Code, verified.Error.Message);
        return Result<TokenResponse>.Success(await BuildTokensAsync(verified.Value!, ct));
    }

    public async Task<Result<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        await refreshValidator.ValidateOrThrowAsync(request, ct);
        var outcome = await refreshTokens.RotateAsync(request.RefreshToken, currentUser.IpAddress, ct);
        if (!outcome.Succeeded || outcome.Issued is null)
            return Result<TokenResponse>.Failure(AuthErrors.InvalidToken, "Invalid or expired refresh token.");

        var user = await identity.FindByIdAsync(outcome.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await refreshTokens.RevokeAsync(outcome.Issued.Token, outcome.UserId, ct);
            return Result<TokenResponse>.Failure(AuthErrors.InvalidToken, "Invalid or expired refresh token.");
        }

        var roles = await identity.GetRolesAsync(user.Id, ct);
        var access = accessTokens.Create(user, roles);
        return Result<TokenResponse>.Success(new TokenResponse(access.Token, access.ExpiresAtUtc, outcome.Issued.Token, outcome.Issued.ExpiresAtUtc));
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken ct = default)
    {
        await logoutValidator.ValidateOrThrowAsync(request, ct);
        await refreshTokens.RevokeAsync(request.RefreshToken, currentUser.UserId, ct);
        await audit.RecordAsync(new AuditEntry("Logout", "User", currentUser.UserId?.ToString()), ct);
    }

    public async Task<CurrentUserDto> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var id = currentUser.UserId ?? throw new ForbiddenException("Authentication required.");
        var user = await identity.FindByIdAsync(id, ct);
        if (user is null || !user.IsActive) throw new ForbiddenException("Authentication required.");
        var roles = await identity.GetRolesAsync(id, ct);
        var perms = await permissions.GetPermissionsAsync(id, ct);
        return new CurrentUserDto(user.Id, user.Email, user.FullName, roles, perms);
    }

    private async Task<TokenResponse> BuildTokensAsync(UserIdentityInfo user, CancellationToken ct)
    {
        var roles = await identity.GetRolesAsync(user.Id, ct);
        var access = accessTokens.Create(user, roles);
        var refresh = await refreshTokens.IssueAsync(user.Id, currentUser.IpAddress, ct);
        return new TokenResponse(access.Token, access.ExpiresAtUtc, refresh.Token, refresh.ExpiresAtUtc);
    }
}
