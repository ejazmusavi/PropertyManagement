using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Administration;

public class RoleService(IAdministrationStore store, IAppDbContext db, IPermissionService permissions, ICurrentUser user, IAuditService audit,
    IValidator<CreateRoleRequest> createValidator, IValidator<UpdateRoleRequest> updateValidator)
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesView, ct);
        return await store.ListRolesAsync(ct);
    }

    public async Task<RoleDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesView, ct);
        return await store.GetRoleAsync(id, ct) ?? throw new NotFoundException("Role not found.");
    }

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesView, ct);
        return await db.Permissions.AsNoTracking().OrderBy(p => p.Module).ThenBy(p => p.Name).Select(p => new PermissionDto(p.Id, p.Code, p.Name, p.Module)).ToListAsync(ct);
    }

    public async Task<PermissionMatrixDto> GetMatrixAsync(CancellationToken ct = default)
    {
        var perms = await ListPermissionsAsync(ct);
        var roles = new List<RoleDetailDto>();
        foreach (var r in await store.ListRolesAsync(ct)) roles.Add((await store.GetRoleAsync(r.Id, ct))!);
        return new PermissionMatrixDto(roles, perms);
    }

    public async Task<RoleDetailDto> CreateAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesManage, ct);
        await createValidator.ValidateOrThrowAsync(request, ct);
        var codes = await ValidateCodesAsync(request.PermissionCodes, ct);
        var (id, errors) = await store.CreateRoleAsync(request.Name.Trim(), Clean(request.Description), ct);
        if (id is null) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(request.Name)] = [.. errors] });
        await store.SetRolePermissionsAsync(id.Value, codes, ct);
        await audit.RecordAsync(new AuditEntry("Role.Created", "Role", id.ToString(), NewValues: new { Name = request.Name.Trim(), Permissions = codes }), ct);
        return (await store.GetRoleAsync(id.Value, ct))!;
    }

    public async Task<RoleDetailDto> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesManage, ct);
        await updateValidator.ValidateOrThrowAsync(request, ct);
        var before = await store.GetRoleAsync(id, ct) ?? throw new NotFoundException("Role not found.");
        var codes = await ValidateCodesAsync(request.PermissionCodes, ct);
        var name = request.Name.Trim();
        if (before.IsSystem && !string.Equals(name, before.Name, StringComparison.Ordinal)) throw new DomainException("System roles cannot be renamed.");
        if (before.Name == RoleNames.Administrator && !codes.OrderBy(c => c).SequenceEqual(before.PermissionCodes.OrderBy(c => c)))
            throw new DomainException("The Administrator role always holds every permission and cannot be changed.");

        var errors = await store.UpdateRoleAsync(id, name, Clean(request.Description), ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(request.Name)] = [.. errors] });
        if (before.Name != RoleNames.Administrator) await store.SetRolePermissionsAsync(id, codes, ct);
        await audit.RecordAsync(new AuditEntry("Role.Updated", "Role", id.ToString(),
            new { before.Name, before.Description, Permissions = before.PermissionCodes }, new { Name = name, Description = Clean(request.Description), Permissions = codes }), ct);
        return (await store.GetRoleAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesManage, ct);
        var role = await store.GetRoleAsync(id, ct) ?? throw new NotFoundException("Role not found.");
        if (role.IsSystem) throw new DomainException("System roles cannot be deleted.");
        var errors = await store.DeleteRoleAsync(id, ct);
        if (errors.Count > 0) throw new DomainException(errors[0]);
        await audit.RecordAsync(new AuditEntry("Role.Deleted", "Role", id.ToString(), new { role.Name, Permissions = role.PermissionCodes }), ct);
    }

    /// <summary>Saves permission grants for several roles at once (the permission matrix screen).</summary>
    public async Task SaveMatrixAsync(SavePermissionMatrixRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.RolesManage, ct);
        foreach (var (roleId, requested) in request.Grants)
        {
            var role = await store.GetRoleAsync(roleId, ct) ?? throw new NotFoundException("Role not found.");
            if (role.Name == RoleNames.Administrator) continue; // always full
            var codes = await ValidateCodesAsync(requested, ct);
            if (codes.OrderBy(c => c).SequenceEqual(role.PermissionCodes.OrderBy(c => c))) continue;
            await store.SetRolePermissionsAsync(roleId, codes, ct);
            await audit.RecordAsync(new AuditEntry("Role.PermissionsChanged", "Role", roleId.ToString(),
                new { role.Name, Permissions = role.PermissionCodes }, new { role.Name, Permissions = codes }), ct);
        }
    }

    private async Task<IReadOnlyList<string>> ValidateCodesAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        var distinct = codes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
        var known = (await db.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync(ct)).ToHashSet();
        var bad = distinct.Where(c => !known.Contains(c)).ToList();
        if (bad.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { ["PermissionCodes"] = [$"Unknown permission(s): {string.Join(", ", bad)}."] });
        return distinct;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
