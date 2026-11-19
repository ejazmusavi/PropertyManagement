using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class PropertyCategoryConfiguration : IEntityTypeConfiguration<PropertyCategory>
{
    public void Configure(EntityTypeBuilder<PropertyCategory> b)
    {
        b.ToTable("PropertyCategories");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class PropertyTypeConfiguration : IEntityTypeConfiguration<PropertyType>
{
    public void Configure(EntityTypeBuilder<PropertyType> b)
    {
        b.ToTable("PropertyTypes");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Form).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name).IsUnique();
        b.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class ZoneConfig
{
    public static void Configure<T>(EntityTypeBuilder<T> b, string table) where T : ProjectZone
    {
        b.ToTable(table);
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
    }
}

public class ProjectBlockConfiguration : IEntityTypeConfiguration<ProjectBlock>
{ public void Configure(EntityTypeBuilder<ProjectBlock> b) => ZoneConfig.Configure(b, "ProjectBlocks"); }
public class ProjectSectorConfiguration : IEntityTypeConfiguration<ProjectSector>
{ public void Configure(EntityTypeBuilder<ProjectSector> b) => ZoneConfig.Configure(b, "ProjectSectors"); }
public class ProjectStreetConfiguration : IEntityTypeConfiguration<ProjectStreet>
{ public void Configure(EntityTypeBuilder<ProjectStreet> b) => ZoneConfig.Configure(b, "ProjectStreets"); }

public class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> b)
    {
        b.ToTable("Properties");
        b.Audit();
        b.Property(x => x.PropertyCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.PropertyNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Size).HasColumnType("decimal(18,4)");
        b.Property(x => x.MeasurementUnit).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DevelopmentStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.LocationDescription).HasMaxLength(500);
        b.Property(x => x.Remarks).HasMaxLength(2000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectBlock>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectSector>().WithMany().HasForeignKey(x => x.SectorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectStreet>().WithMany().HasForeignKey(x => x.StreetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PropertyType).WithMany().HasForeignKey(x => x.PropertyTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Dimensions).WithOne().HasForeignKey(d => d.PropertyId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Features).WithOne().HasForeignKey(d => d.PropertyId).OnDelete(DeleteBehavior.Cascade);
        // Property code is unique within its project (decision: see architecture-decisions.md).
        b.HasIndex(x => new { x.ProjectId, x.PropertyCode }).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status });
        b.HasIndex(x => x.PropertyTypeId);
        b.HasIndex(x => x.BlockId);
        b.ToTable(t => t.HasCheckConstraint("CK_Properties_Size", "`Size` > 0"));
    }
}

public class PropertyDimensionConfiguration : IEntityTypeConfiguration<PropertyDimension>
{
    public void Configure(EntityTypeBuilder<PropertyDimension> b)
    {
        b.ToTable("PropertyDimensions");
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(20);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.HasIndex(x => new { x.PropertyId, x.Name }).IsUnique();
    }
}

public class PropertyFeatureConfiguration : IEntityTypeConfiguration<PropertyFeature>
{
    public void Configure(EntityTypeBuilder<PropertyFeature> b)
    {
        b.ToTable("PropertyFeatures");
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Value).HasMaxLength(200);
        b.HasIndex(x => new { x.PropertyId, x.Name }).IsUnique();
    }
}

public class PropertyDocumentConfiguration : IEntityTypeConfiguration<PropertyDocument>
{
    public void Configure(EntityTypeBuilder<PropertyDocument> b)
    {
        b.ToTable("PropertyDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(30);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PropertyId);
    }
}

public class PropertyStatusHistoryConfiguration : IEntityTypeConfiguration<PropertyStatusHistory>
{
    public void Configure(EntityTypeBuilder<PropertyStatusHistory> b)
    {
        b.ToTable("PropertyStatusHistories");
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.HasOne(x => x.Property).WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PropertyId, x.ChangedAtUtc });
        b.HasIndex(x => x.ChangedAtUtc);
    }
}

public class PropertyReservationConfiguration : IEntityTypeConfiguration<PropertyReservation>
{
    public void Configure(EntityTypeBuilder<PropertyReservation> b)
    {
        b.ToTable("PropertyReservations");
        b.Audit();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ReservedForName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ReservedForContact).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(256);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ClosedAtUtc).HasPrecision(6);
        b.Property(x => x.ClosedBy).HasMaxLength(256);
        b.Property(x => x.CloseReason).HasMaxLength(1000);
        b.HasOne(x => x.Property).WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PropertyId, x.Status });
        b.HasIndex(x => new { x.Status, x.ExpiryDate });
        // Only one Active reservation per property: ActivePropertyId is null once closed, and NULLs do not collide in a unique index.
        b.HasIndex(x => x.ActivePropertyId).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_PropertyReservations_Dates", "`ExpiryDate` >= `ReservationDate` AND `Amount` >= 0"));
    }
}
