using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Users;

namespace RealEstate.Infrastructure.Persistence;

public class UserQueryService(AppDbContext db) : IUserQueryService
{
    public async Task<PagedResult<UserListItemDto>> ListAsync(PagedRequest request, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(u => u.Email!.Contains(term) || u.FullName.Contains(term));
        }

        var total = await query.CountAsync(ct);
        query = (request.SortBy?.ToLowerInvariant(), request.Descending) switch
        {
            ("email", false) => query.OrderBy(u => u.Email),
            ("email", true) => query.OrderByDescending(u => u.Email),
            ("lastlogin", false) => query.OrderBy(u => u.LastLoginAtUtc),
            ("lastlogin", true) => query.OrderByDescending(u => u.LastLoginAtUtc),
            (_, true) => query.OrderByDescending(u => u.FullName),
            _ => query.OrderBy(u => u.FullName),
        };

        var page = await query.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(u => new { u.Id, u.Email, u.FullName, u.IsActive, u.LastLoginAtUtc })
            .ToListAsync(ct);

        var ids = page.Select(u => u.Id).ToList();
        var roles = await (from ur in db.UserRoles
                           where ids.Contains(ur.UserId)
                           join r in db.Roles on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Name }).ToListAsync(ct);
        var byUser = roles.ToLookup(x => x.UserId, x => x.Name!);

        var items = page.Select(u => new UserListItemDto(u.Id, u.Email!, u.FullName, u.IsActive,
            [.. byUser[u.Id].OrderBy(n => n)], u.LastLoginAtUtc)).ToList();
        return new PagedResult<UserListItemDto>(items, request.Page, request.PageSize, total);
    }

    public async Task<IReadOnlyList<UserLookupDto>> LookupAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new UserLookupDto(u.Id, u.FullName, u.Email!, u.IsActive)).ToListAsync(ct);

    public async Task<IReadOnlyList<UserLookupDto>> SearchActiveAsync(string? search, int take, CancellationToken ct = default)
    {
        var q = db.Users.AsNoTracking().Where(u => u.IsActive);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(u => u.FullName.Contains(t) || u.Email!.Contains(t)); }
        return await q.OrderBy(u => u.FullName).Take(take).Select(u => new UserLookupDto(u.Id, u.FullName, u.Email!, u.IsActive)).ToListAsync(ct);
    }

    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(Guid userId, CancellationToken ct = default)
    {
        var company = await db.Companies.AsNoTracking().Select(c => c.Name).FirstOrDefaultAsync(ct);
        var activeUsers = await db.Users.CountAsync(u => u.IsActive, ct);
        var roles = await db.Roles.CountAsync(ct);
        var projects = await db.UserProjects.CountAsync(p => p.UserId == userId && p.IsActive, ct);
        var lastLogin = await db.Users.Where(u => u.Id == userId).Select(u => u.LastLoginAtUtc).FirstOrDefaultAsync(ct);
        return new DashboardSummaryDto(company, activeUsers, roles, projects, lastLogin);
    }
}
