using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Infrastructure.Persistence;

namespace RealEstate.Infrastructure.Identity;

public class PermissionService(AppDbContext db) : IPermissionService
{
    public Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken ct = default) =>
        Query(userId).AnyAsync(code => code == permissionCode, ct);

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default) =>
        await Query(userId).OrderBy(c => c).ToListAsync(ct);

    // Inactive users hold no permissions.
    private IQueryable<string> Query(Guid userId) =>
        (from u in db.Users
         where u.Id == userId && u.IsActive
         join ur in db.UserRoles on u.Id equals ur.UserId
         join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
         select rp.Permission.Code).Distinct();
}
