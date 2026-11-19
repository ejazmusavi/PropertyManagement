using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Application.Common;

/// <summary>Project-level access: holders of projects.access.all see every project; others only projects they are assigned to.</summary>
public class ProjectAccessService(IAppDbContext db, IPermissionService permissions, ICurrentUser user)
{
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");

    public async Task<bool> HasAllAccessAsync(CancellationToken ct = default) =>
        await permissions.HasPermissionAsync(RequireUser(), Permissions.ProjectsAccessAll, ct);

    /// <summary>Restricts a project query to the projects the current user may see.</summary>
    public async Task<IQueryable<Project>> ScopeAsync(IQueryable<Project> projects, CancellationToken ct = default)
    {
        var id = RequireUser();
        if (await permissions.HasPermissionAsync(id, Permissions.ProjectsAccessAll, ct)) return projects;
        return projects.Where(p => db.UserProjects.Any(up => up.ProjectId == p.Id && up.UserId == id && up.IsActive));
    }

    /// <summary>Throws Forbidden for unassigned (or unknown) projects so project ids cannot be probed.</summary>
    public async Task EnsureAccessAsync(long projectId, CancellationToken ct = default)
    {
        var id = RequireUser();
        if (await permissions.HasPermissionAsync(id, Permissions.ProjectsAccessAll, ct))
        {
            if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project not found.");
            return;
        }
        if (!await db.UserProjects.AnyAsync(up => up.ProjectId == projectId && up.UserId == id && up.IsActive, ct))
            throw new ForbiddenException("You do not have access to this project.");
    }
}
