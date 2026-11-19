using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Persistence;

/// <summary>Applies migrations and synchronises system data (permission catalog, Administrator role). Contains no credentials.</summary>
public class DatabaseInitializer(AppDbContext db, ILogger<DatabaseInitializer> logger)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync(ct);
        await SyncSystemDataAsync(ct);
    }

    public async Task SyncSystemDataAsync(CancellationToken ct = default)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        foreach (var def in Permissions.All)
        {
            if (existing.TryGetValue(def.Code, out var p))
            {
                p.Name = def.Name;
                p.Module = def.Module;
            }
            else
            {
                var created = new Permission { Code = def.Code, Name = def.Name, Module = def.Module };
                db.Permissions.Add(created);
                existing[def.Code] = created;
            }
        }
        await db.SaveChangesAsync(ct);

        var admin = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.Administrator.ToUpperInvariant(), ct);
        if (admin is null)
        {
            admin = new ApplicationRole(RoleNames.Administrator) { NormalizedName = RoleNames.Administrator.ToUpperInvariant(), Description = "Full access to every function." };
            db.Roles.Add(admin);
            await db.SaveChangesAsync(ct);
        }

        // The Administrator role always holds the complete permission catalog.
        var granted = (await db.RolePermissions.Where(rp => rp.RoleId == admin.Id).Select(rp => rp.PermissionId).ToListAsync(ct)).ToHashSet();
        foreach (var p in existing.Values.Where(p => !granted.Contains(p.Id)))
            db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = p.Id });
        await db.SaveChangesAsync(ct);
    }
}
