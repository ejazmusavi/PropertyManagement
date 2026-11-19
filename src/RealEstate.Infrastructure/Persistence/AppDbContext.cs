using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser? currentUser = null, TimeProvider? clock = null)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IAppDbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectAddress> ProjectAddresses => Set<ProjectAddress>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<ProjectManagerAssignment> ProjectManagerAssignments => Set<ProjectManagerAssignment>();
    public DbSet<ProjectAreaHistory> ProjectAreaHistories => Set<ProjectAreaHistory>();
    public DbSet<ProjectStatusHistory> ProjectStatusHistories => Set<ProjectStatusHistory>();
    public DbSet<LandParcel> LandParcels => Set<LandParcel>();
    public DbSet<LandAcquisition> LandAcquisitions => Set<LandAcquisition>();
    public DbSet<LandAdjustment> LandAdjustments => Set<LandAdjustment>();
    public DbSet<LandDistribution> LandDistributions => Set<LandDistribution>();
    public DbSet<LandDistributionHistory> LandDistributionHistories => Set<LandDistributionHistory>();
    public DbSet<LandDocument> LandDocuments => Set<LandDocument>();

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserProject> UserProjects => Set<UserProject>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void ApplyAuditFields()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var actor = currentUser?.UserName ?? "system";

        foreach (var entry in ChangeTracker.Entries())
        {
            // The audit log is append-only.
            if (entry.Entity is AuditLog && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new DomainException("Audit records cannot be modified or deleted.");

            if (entry.Entity is not IAuditable auditable) continue;
            if (entry.State == EntityState.Added)
            {
                auditable.CreatedAtUtc = now;
                auditable.CreatedBy = actor;
            }
            else if (entry.State == EntityState.Modified)
            {
                auditable.ModifiedAtUtc = now;
                auditable.ModifiedBy = actor;
                entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
            }
        }
    }
}
