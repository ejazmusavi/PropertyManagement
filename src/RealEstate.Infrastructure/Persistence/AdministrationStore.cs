using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Administration;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Persistence;

public class AdministrationStore(AppDbContext db, UserManager<ApplicationUser> users, RoleManager<ApplicationRole> roles, TimeProvider clock) : IAdministrationStore
{
    public async Task<UserDetailDto?> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        var u = await users.FindByIdAsync(id.ToString());
        return u is null ? null : new UserDetailDto(u.Id, u.Email!, u.FullName, u.PhoneNumber, u.IsActive, [.. (await users.GetRolesAsync(u)).Order()], u.LastLoginAtUtc);
    }

    public async Task<(Guid? Id, IReadOnlyList<string> Errors)> CreateUserAsync(string email, string fullName, string? phone, string password, bool isActive, IReadOnlyList<string> roleNames, CancellationToken ct = default)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true, FullName = fullName, PhoneNumber = phone, IsActive = isActive,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) return (null, [.. created.Errors.Select(e => e.Description)]);
        if (roleNames.Count > 0)
        {
            var added = await users.AddToRolesAsync(user, roleNames);
            if (!added.Succeeded) return (null, [.. added.Errors.Select(e => e.Description)]); // transaction rolls back on dispose
        }
        await tx.CommitAsync(ct);
        return (user.Id, []);
    }

    public async Task<IReadOnlyList<string>> UpdateUserAsync(Guid id, string fullName, string? phone, bool isActive, IReadOnlyList<string> roleNames, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException("User not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        user.FullName = fullName; user.PhoneNumber = phone; user.IsActive = isActive;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) return [.. updated.Errors.Select(e => e.Description)];

        var current = await users.GetRolesAsync(user);
        var remove = current.Where(r => !roleNames.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        var add = roleNames.Where(r => !current.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        if (remove.Count > 0 && !(await users.RemoveFromRolesAsync(user, remove)).Succeeded) return ["Could not remove roles."];
        if (add.Count > 0 && !(await users.AddToRolesAsync(user, add)).Succeeded) return ["Could not add roles."];
        await tx.CommitAsync(ct);
        return [];
    }

    public async Task<int> CountActiveInRoleAsync(string roleName, CancellationToken ct = default) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               join u in db.Users on ur.UserId equals u.Id
               where r.Name == roleName && u.IsActive
               select u.Id).CountAsync(ct);

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var list = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var userCounts = await db.UserRoles.GroupBy(x => x.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var permCounts = await db.RolePermissions.GroupBy(x => x.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return [.. list.Select(r => new RoleDto(r.Id, r.Name!, r.Description, r.IsSystem, userCounts.GetValueOrDefault(r.Id), permCounts.GetValueOrDefault(r.Id)))];
    }

    public async Task<RoleDetailDto?> GetRoleAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return null;
        var codes = await db.RolePermissions.Where(rp => rp.RoleId == id).Select(rp => rp.Permission.Code).OrderBy(c => c).ToListAsync(ct);
        return new RoleDetailDto(r.Id, r.Name!, r.Description, r.IsSystem, codes);
    }

    public async Task<(Guid? Id, IReadOnlyList<string> Errors)> CreateRoleAsync(string name, string? description, CancellationToken ct = default)
    {
        var role = new ApplicationRole(name) { Description = description, IsSystem = false };
        var result = await roles.CreateAsync(role);
        return result.Succeeded ? (role.Id, []) : (null, [.. result.Errors.Select(e => e.Description)]);
    }

    public async Task<IReadOnlyList<string>> UpdateRoleAsync(Guid id, string name, string? description, CancellationToken ct = default)
    {
        var role = await roles.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException("Role not found.");
        role.Name = name; role.Description = description;
        var result = await roles.UpdateAsync(role);
        return result.Succeeded ? [] : [.. result.Errors.Select(e => e.Description)];
    }

    public async Task<IReadOnlyList<string>> DeleteRoleAsync(Guid id, CancellationToken ct = default)
    {
        if (await db.UserRoles.AnyAsync(x => x.RoleId == id, ct)) return ["The role is assigned to users. Remove it from them first."];
        var role = await roles.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException("Role not found.");
        var result = await roles.DeleteAsync(role);
        return result.Succeeded ? [] : [.. result.Errors.Select(e => e.Description)];
    }

    public async Task SetRolePermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken ct = default)
    {
        var wanted = await db.Permissions.Where(p => permissionCodes.Contains(p.Code)).Select(p => p.Id).ToListAsync(ct);
        var existing = await db.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync(ct);
        db.RolePermissions.RemoveRange(existing.Where(e => !wanted.Contains(e.PermissionId)));
        foreach (var pid in wanted.Where(w => existing.All(e => e.PermissionId != w)))
            db.RolePermissions.Add(new Domain.Entities.RolePermission { RoleId = roleId, PermissionId = pid });
        await db.SaveChangesAsync(ct);
    }
}
