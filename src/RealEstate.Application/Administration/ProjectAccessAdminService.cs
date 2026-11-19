using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Application.Administration;

/// <summary>Project access assignment (which users may work in which project).</summary>
public class ProjectAccessAdminService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access,
    IUserQueryService users, IAuditService audit)
{
    public async Task<IReadOnlyList<ProjectAccessUserDto>> ListAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsAccessAssign, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var manager = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.ProjectManagerId).FirstOrDefaultAsync(ct);
        var rows = await db.UserProjects.AsNoTracking().Where(x => x.ProjectId == projectId && x.IsActive).ToListAsync(ct);
        var lookup = (await users.LookupAsync(rows.Select(r => r.UserId).ToList(), ct)).ToDictionary(u => u.Id);
        return rows.Where(r => lookup.ContainsKey(r.UserId)).Select(r =>
        {
            var u = lookup[r.UserId];
            return new ProjectAccessUserDto(u.Id, u.FullName, u.Email, u.IsActive, manager == u.Id, r.ModifiedAtUtc ?? r.CreatedAtUtc);
        }).OrderBy(x => x.FullName).ToList();
    }

    public async Task<IReadOnlyList<UserLookupDto>> ListCandidatesAsync(long projectId, string? search, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsAccessAssign, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var assigned = (await db.UserProjects.AsNoTracking().Where(x => x.ProjectId == projectId && x.IsActive).Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        return (await users.SearchActiveAsync(search, 200, ct)).Where(u => !assigned.Contains(u.Id)).Take(50).ToList();
    }

    public async Task AssignAsync(long projectId, Guid userId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsAccessAssign, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var target = (await users.LookupAsync([userId], ct)).FirstOrDefault();
        if (target is null || !target.IsActive) throw new RequestValidationException(new Dictionary<string, string[]> { ["UserId"] = ["Select an active user."] });
        var row = await db.UserProjects.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == userId, ct);
        if (row is { IsActive: true }) throw new DomainException("The user already has access to this project.");
        if (row is null) db.UserProjects.Add(new UserProject { ProjectId = projectId, UserId = userId, IsActive = true });
        else row.IsActive = true;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ProjectAccess.Granted", "Project", projectId.ToString(), NewValues: new { userId, target.Email }), ct);
    }

    public async Task RevokeAsync(long projectId, Guid userId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsAccessAssign, ct);
        await access.EnsureAccessAsync(projectId, ct);
        if (string.IsNullOrWhiteSpace(reason)) throw new RequestValidationException(new Dictionary<string, string[]> { ["Reason"] = ["A reason is required."] });
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        if (project.ProjectManagerId == userId) throw new DomainException("The project manager's access cannot be revoked. Assign a different manager first.");
        var row = await db.UserProjects.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == userId && x.IsActive, ct) ?? throw new NotFoundException("Assignment not found.");
        row.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ProjectAccess.Revoked", "Project", projectId.ToString(), OldValues: new { userId }, Reason: reason.Trim()), ct);
    }
}
