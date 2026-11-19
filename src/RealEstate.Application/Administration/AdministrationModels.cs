using FluentValidation;

namespace RealEstate.Application.Administration;

public sealed record UserDetailDto(Guid Id, string Email, string FullName, string? PhoneNumber, bool IsActive, IReadOnlyList<string> Roles, DateTime? LastLoginAtUtc);
public sealed record CreateUserRequest(string Email, string FullName, string? PhoneNumber, string Password, bool IsActive, IReadOnlyList<string> Roles);
public sealed record UpdateUserRequest(string FullName, string? PhoneNumber, bool IsActive, IReadOnlyList<string> Roles);

public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, int UserCount, int PermissionCount);
public sealed record RoleDetailDto(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<string> PermissionCodes);
public sealed record PermissionDto(int Id, string Code, string Name, string Module);
public sealed record CreateRoleRequest(string Name, string? Description, IReadOnlyList<string> PermissionCodes);
public sealed record UpdateRoleRequest(string Name, string? Description, IReadOnlyList<string> PermissionCodes);
public sealed record PermissionMatrixDto(IReadOnlyList<RoleDetailDto> Roles, IReadOnlyList<PermissionDto> Permissions);
public sealed record SavePermissionMatrixRequest(IReadOnlyDictionary<Guid, IReadOnlyList<string>> Grants);

public sealed record ProjectAccessUserDto(Guid UserId, string FullName, string Email, bool IsActive, bool IsProjectManager, DateTime AssignedAtUtc);
public sealed record AssignProjectAccessRequest(Guid UserId);

/// <summary>Identity-backed user and role storage implemented by Infrastructure.</summary>
public interface IAdministrationStore
{
    Task<UserDetailDto?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<(Guid? Id, IReadOnlyList<string> Errors)> CreateUserAsync(string email, string fullName, string? phone, string password, bool isActive, IReadOnlyList<string> roles, CancellationToken ct = default);
    Task<IReadOnlyList<string>> UpdateUserAsync(Guid id, string fullName, string? phone, bool isActive, IReadOnlyList<string> roles, CancellationToken ct = default);
    Task<int> CountActiveInRoleAsync(string roleName, CancellationToken ct = default);
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default);
    Task<RoleDetailDto?> GetRoleAsync(Guid id, CancellationToken ct = default);
    Task<(Guid? Id, IReadOnlyList<string> Errors)> CreateRoleAsync(string name, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<string>> UpdateRoleAsync(Guid id, string name, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeleteRoleAsync(Guid id, CancellationToken ct = default);
    Task SetRolePermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken ct = default);
}

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.PhoneNumber).MaximumLength(30);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
        RuleFor(x => x.Roles).NotNull();
    }
}

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.PhoneNumber).MaximumLength(30);
        RuleFor(x => x.Roles).NotNull();
    }
}

public class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.PermissionCodes).NotNull();
    }
}

public class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.PermissionCodes).NotNull();
    }
}
