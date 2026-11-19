using FluentValidation;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Users;

/// <summary>User administration and self-service profile use cases.</summary>
public class UserService(
    IUserQueryService queries,
    IIdentityService identity,
    IPermissionService permissions,
    IAuditService audit,
    ICurrentUser currentUser,
    IValidator<UpdateProfileRequest> profileValidator,
    IValidator<ChangePasswordRequest> passwordValidator)
{
    public async Task<PagedResult<UserListItemDto>> ListAsync(PagedRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(currentUser, Permissions.UsersView, ct);
        return await queries.ListAsync(request, ct);
    }

    public async Task<DashboardSummaryDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var id = RequireUser();
        return await queries.GetDashboardSummaryAsync(id, ct);
    }

    public async Task<ProfileDto> GetProfileAsync(CancellationToken ct = default)
    {
        var id = RequireUser();
        return await identity.GetProfileAsync(id, ct) ?? throw new NotFoundException("User not found.");
    }

    public async Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default)
    {
        var id = RequireUser();
        await profileValidator.ValidateOrThrowAsync(request, ct);
        var before = await identity.GetProfileAsync(id, ct) ?? throw new NotFoundException("User not found.");
        var phone = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
        await identity.UpdateProfileAsync(id, request.FullName.Trim(), phone, ct);
        await audit.RecordAsync(new AuditEntry("Profile.Updated", "User", id.ToString(),
            OldValues: new { before.FullName, before.PhoneNumber },
            NewValues: new { FullName = request.FullName.Trim(), PhoneNumber = phone }), ct);
    }

    /// <summary>Changes the caller's own password. Returns identity errors (e.g. wrong current password) as a validation failure.</summary>
    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        var id = RequireUser();
        await passwordValidator.ValidateOrThrowAsync(request, ct);
        var errors = await identity.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword, ct);
        if (errors.Count > 0)
        {
            await audit.RecordAsync(new AuditEntry("Password.ChangeFailed", "User", id.ToString()), ct);
            throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(ChangePasswordRequest.CurrentPassword)] = [.. errors] });
        }
        await audit.RecordAsync(new AuditEntry("Password.Changed", "User", id.ToString()), ct);
    }

    private Guid RequireUser() => currentUser.UserId ?? throw new ForbiddenException("Authentication required.");
}
