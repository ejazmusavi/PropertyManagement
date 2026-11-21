using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Auth;

namespace RealEstate.Infrastructure.Identity;

public class IdentityService(UserManager<ApplicationUser> users, TimeProvider clock) : IIdentityService
{
    public async Task<(CredentialStatus Status, UserIdentityInfo? User)> CheckPasswordAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            // Hash anyway to keep timing similar for unknown accounts.
            users.PasswordHasher.HashPassword(new ApplicationUser(), password);
            return (CredentialStatus.Invalid, null);
        }

        if (await users.IsLockedOutAsync(user)) return (CredentialStatus.LockedOut, null);

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return (await users.IsLockedOutAsync(user) ? CredentialStatus.LockedOut : CredentialStatus.Invalid, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        var info = Map(user);
        return (user.IsActive ? CredentialStatus.Valid : CredentialStatus.Inactive, info);
    }

    public async Task<UserIdentityInfo?> FindByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? null : Map(user);
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? [] : [.. await users.GetRolesAsync(user)];
    }

    public async Task RecordLoginAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return;
        user.LastLoginAtUtc = clock.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);
    }

    public async Task<ProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return null;
        return new ProfileDto(user.Id, user.Email!, user.FullName, user.PhoneNumber, [.. await users.GetRolesAsync(user)], user.LastLoginAtUtc);
    }

    public async Task UpdateProfileAsync(Guid userId, string fullName, string? phoneNumber, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("User not found.");
        user.FullName = fullName;
        user.PhoneNumber = phoneNumber;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return ["User not found."];
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded) return [];
        return [.. result.Errors.Select(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch) ? "Current password is incorrect." : e.Description)];
    }

    public async Task<UserIdentityInfo?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var u = await users.FindByEmailAsync(email);
        return u is null ? null : Map(u);
    }

    public async Task<PasswordResetToken?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.IsActive) return null;
        return new PasswordResetToken(user.Id, user.Email!, user.FullName, await users.GeneratePasswordResetTokenAsync(user));
    }

    public async Task<IReadOnlyList<string>> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.IsActive) return ["The reset code is invalid or has expired."];
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded) { await users.ResetAccessFailedCountAsync(user); return []; }
        return [.. result.Errors.Select(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken) ? "The reset code is invalid or has expired." : e.Description)];
    }

    public async Task<IReadOnlyList<string>> SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return ["User not found."];
        var result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), newPassword);
        return result.Succeeded ? [] : [.. result.Errors.Select(e => e.Description)];
    }

    private static UserIdentityInfo Map(ApplicationUser u) => new(u.Id, u.Email!, u.FullName, u.IsActive);
}
