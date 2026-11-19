namespace RealEstate.Application.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);

public sealed record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc);

public sealed record CurrentUserDto(Guid Id, string Email, string FullName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public sealed record UserIdentityInfo(Guid Id, string Email, string FullName, bool IsActive);

public sealed record ProfileDto(Guid Id, string Email, string FullName, string? PhoneNumber, IReadOnlyList<string> Roles, DateTime? LastLoginAtUtc);
public sealed record UpdateProfileRequest(string FullName, string? PhoneNumber);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmNewPassword);

public static class AuthErrors
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string LockedOut = "auth.locked_out";
    public const string Inactive = "auth.inactive";
    public const string InvalidToken = "auth.invalid_token";
}
