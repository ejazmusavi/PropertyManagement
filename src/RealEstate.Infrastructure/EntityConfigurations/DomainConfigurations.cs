using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies");
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.LegalName).HasMaxLength(250);
        b.Property(x => x.TaxNumber).HasMaxLength(50);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ModifiedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("Permissions");
        b.Property(x => x.Code).HasMaxLength(100).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Module).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermissions");
        b.HasKey(x => new { x.RoleId, x.PermissionId });
        b.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserProjectConfiguration : IEntityTypeConfiguration<UserProject>
{
    public void Configure(EntityTypeBuilder<UserProject> b)
    {
        b.ToTable("UserProjects");
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ModifiedAtUtc).HasPrecision(6);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        // ProjectId gets its foreign key to Project in Phase 1.
        b.HasIndex(x => new { x.UserId, x.ProjectId }).IsUnique();
        b.HasIndex(x => x.ProjectId);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.Property(x => x.OccurredAtUtc).HasPrecision(6);
        b.Property(x => x.UserName).HasMaxLength(256);
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(100);
        b.Property(x => x.OldValues).HasColumnType("longtext");
        b.Property(x => x.NewValues).HasColumnType("longtext");
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(100);
        b.HasIndex(x => x.OccurredAtUtc);
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.HasIndex(x => x.UserId);
    }
}

public class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
    public void Configure(EntityTypeBuilder<ApplicationSetting> b)
    {
        b.ToTable("ApplicationSettings");
        b.Property(x => x.Key).HasMaxLength(150).IsRequired();
        b.Property(x => x.Value).HasMaxLength(2000);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ModifiedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Key).IsUnique();
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ReplacedByTokenHash).HasMaxLength(64);
        b.Property(x => x.CreatedByIp).HasMaxLength(64);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ExpiresAtUtc).HasPrecision(6);
        b.Property(x => x.RevokedAtUtc).HasPrecision(6);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.FamilyId);
        b.HasIndex(x => x.UserId);
    }
}
