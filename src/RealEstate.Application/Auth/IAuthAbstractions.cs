using RealEstate.Application.Common;

namespace RealEstate.Application.Auth;

public enum CredentialStatus { Valid, Invalid, LockedOut, Inactive }

/// <summary>Identity operations implemented by Infrastructure (ASP.NET Identity).</summary>
public interface IIdentityService
{
    Task<(CredentialStatus Status, UserIdentityInfo? User)> CheckPasswordAsync(string email, string password, CancellationToken ct = default);
    Task<UserIdentityInfo?> FindByIdAsync(Guid userId, CancellationToken ct = default);
    Task<UserIdentityInfo?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default);
    Task RecordLoginAsync(Guid userId, CancellationToken ct = default);
    Task<ProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task UpdateProfileAsync(Guid userId, string fullName, string? phoneNumber, CancellationToken ct = default);
    /// <summary>Returns identity error messages (empty on success).</summary>
    Task<IReadOnlyList<string>> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);
    /// <summary>Creates a password reset token for an active user (null when the e-mail is unknown or the user is inactive).</summary>
    Task<PasswordResetToken?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default);
    /// <summary>Returns identity error messages (empty on success). An unknown e-mail or a bad token yields the same generic error.</summary>
    Task<IReadOnlyList<string>> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken ct = default);
    /// <summary>Sets a new password for the user without the old one (administrative reset).</summary>
    Task<IReadOnlyList<string>> SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);
}

public sealed record PasswordResetToken(Guid UserId, string Email, string FullName, string Token);

public sealed record IssuedAccessToken(string Token, DateTime ExpiresAtUtc);

public interface IAccessTokenFactory
{
    IssuedAccessToken Create(UserIdentityInfo user, IReadOnlyList<string> roles);
}

public sealed record RefreshTokenIssue(string Token, DateTime ExpiresAtUtc);

public sealed record RefreshOutcome(bool Succeeded, Guid UserId, RefreshTokenIssue? Issued);

public interface IRefreshTokenService
{
    Task<RefreshTokenIssue> IssueAsync(Guid userId, string? ip, CancellationToken ct = default);
    /// <summary>Validates and rotates the token. Reuse of an already-rotated token revokes the whole family.</summary>
    Task<RefreshOutcome> RotateAsync(string token, string? ip, CancellationToken ct = default);
    Task RevokeAsync(string token, Guid? expectedUserId, CancellationToken ct = default);
    /// <summary>Revokes every refresh token of the user (used after a password reset).</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken ct = default);
}
