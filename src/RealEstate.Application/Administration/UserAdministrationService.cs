using FluentValidation;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Administration;

public class UserAdministrationService(IAdministrationStore store, IIdentityService identity, IPermissionService permissions, ICurrentUser user,
    IAuditService audit, IValidator<CreateUserRequest> createValidator, IValidator<UpdateUserRequest> updateValidator)
{
    public async Task<UserDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.UsersView, ct);
        return await store.GetUserAsync(id, ct) ?? throw new NotFoundException("User not found.");
    }

    /// <summary>Roles that can be assigned while managing users.</summary>
    public async Task<IReadOnlyList<RoleDto>> ListAssignableRolesAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.UsersManage, ct);
        return (await store.ListRolesAsync(ct)).Where(r => r.Name != RoleNames.Customer).ToList();
    }

    public async Task<UserDetailDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.UsersManage, ct);
        await createValidator.ValidateOrThrowAsync(request, ct);
        var roles = await ResolveRolesAsync(request.Roles, ct);
        await EnsureCanGrantAdministratorAsync(roles, [], ct);

        var (id, errors) = await store.CreateUserAsync(request.Email.Trim(), request.FullName.Trim(), Clean(request.PhoneNumber), request.Password, request.IsActive, roles, ct);
        if (id is null) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(request.Email)] = [.. errors] });
        await audit.RecordAsync(new AuditEntry("User.Created", "User", id.ToString(), NewValues: new { Email = request.Email.Trim(), FullName = request.FullName.Trim(), request.IsActive, Roles = roles }), ct);
        return (await store.GetUserAsync(id.Value, ct))!;
    }

    public async Task<UserDetailDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.UsersManage, ct);
        await updateValidator.ValidateOrThrowAsync(request, ct);
        var before = await store.GetUserAsync(id, ct) ?? throw new NotFoundException("User not found.");
        if (before.Roles.Contains(RoleNames.Customer)) throw new ForbiddenException("Customer application logins are managed from the customer record.");
        var roles = await ResolveRolesAsync(request.Roles, ct);

        var callerIsAdmin = await CallerIsAdministratorAsync(ct);
        var targetIsAdmin = before.Roles.Contains(RoleNames.Administrator);
        if (targetIsAdmin && !callerIsAdmin) throw new ForbiddenException("Only an Administrator can change an Administrator account.");
        await EnsureCanGrantAdministratorAsync(roles, before.Roles, ct);

        var staysAdminAndActive = roles.Contains(RoleNames.Administrator) && request.IsActive;
        if (targetIsAdmin && before.IsActive && !staysAdminAndActive && await store.CountActiveInRoleAsync(RoleNames.Administrator, ct) <= 1)
            throw new DomainException("At least one active Administrator must remain.");
        if (id == user.UserId && !request.IsActive) throw new DomainException("You cannot deactivate your own account.");

        var errors = await store.UpdateUserAsync(id, request.FullName.Trim(), Clean(request.PhoneNumber), request.IsActive, roles, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { [string.Empty] = [.. errors] });
        await audit.RecordAsync(new AuditEntry("User.Updated", "User", id.ToString(),
            new { before.FullName, before.PhoneNumber, before.IsActive, before.Roles },
            new { FullName = request.FullName.Trim(), PhoneNumber = Clean(request.PhoneNumber), request.IsActive, Roles = roles }), ct);
        return (await store.GetUserAsync(id, ct))!;
    }

    private async Task<IReadOnlyList<string>> ResolveRolesAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        var known = (await store.ListRolesAsync(ct)).Select(r => r.Name).ToList();
        var resolved = new List<string>();
        foreach (var name in requested.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(name, RoleNames.Customer, StringComparison.OrdinalIgnoreCase))
                throw new RequestValidationException(new Dictionary<string, string[]> { ["Roles"] = ["The Customer role is assigned only by creating a customer application login on the customer record."] });
            var match = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new RequestValidationException(new Dictionary<string, string[]> { ["Roles"] = [$"Role '{name}' does not exist."] });
            resolved.Add(match);
        }
        return resolved;
    }

    private async Task EnsureCanGrantAdministratorAsync(IReadOnlyList<string> newRoles, IReadOnlyList<string> oldRoles, CancellationToken ct)
    {
        var changes = newRoles.Contains(RoleNames.Administrator) != oldRoles.Contains(RoleNames.Administrator);
        if (changes && !await CallerIsAdministratorAsync(ct)) throw new ForbiddenException("Only an Administrator can grant or revoke the Administrator role.");
    }

    private async Task<bool> CallerIsAdministratorAsync(CancellationToken ct) =>
        user.UserId is { } id && (await identity.GetRolesAsync(id, ct)).Contains(RoleNames.Administrator);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
