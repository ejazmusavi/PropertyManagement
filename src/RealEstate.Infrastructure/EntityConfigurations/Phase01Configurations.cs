using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

internal static class ConfigHelpers
{
    public static void Audit<T>(this EntityTypeBuilder<T> b) where T : class, IAuditable
    {
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ModifiedAtUtc).HasPrecision(6);
    }

    public static void Document<T>(this EntityTypeBuilder<T> b) where T : StoredDocument
    {
        b.Audit();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.DeletedAtUtc).HasPrecision(6);
        b.Property(x => x.DeletedBy).HasMaxLength(256);
        b.Property(x => x.DeleteReason).HasMaxLength(1000);
    }
}

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("Projects");
        b.Audit();
        b.Property(x => x.ProjectCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.ProjectName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.City).HasMaxLength(100).IsRequired();
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.InitialArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.CurrentArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.MeasurementUnit).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.ProjectCode).IsUnique();
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.City);
        b.HasIndex(x => x.ProjectManagerId);
        b.HasOne(x => x.AddressDetails).WithOne().HasForeignKey<ProjectAddress>(a => a.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Projects_Areas", "`InitialArea` > 0 AND `CurrentArea` > 0"));
    }
}

public class ProjectAddressConfiguration : IEntityTypeConfiguration<ProjectAddress>
{
    public void Configure(EntityTypeBuilder<ProjectAddress> b)
    {
        b.ToTable("ProjectAddresses");
        b.Audit();
        b.Property(x => x.AddressLine).HasMaxLength(500);
        b.Property(x => x.Locality).HasMaxLength(150);
        b.Property(x => x.Province).HasMaxLength(100);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);
        b.Property(x => x.Landmark).HasMaxLength(250);
        b.Property(x => x.Latitude).HasColumnType("decimal(9,6)");
        b.Property(x => x.Longitude).HasColumnType("decimal(9,6)");
        b.HasIndex(x => x.ProjectId).IsUnique();
    }
}

public class ProjectDocumentConfiguration : IEntityTypeConfiguration<ProjectDocument>
{
    public void Configure(EntityTypeBuilder<ProjectDocument> b)
    {
        b.ToTable("ProjectDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}

public class ProjectManagerAssignmentConfiguration : IEntityTypeConfiguration<ProjectManagerAssignment>
{
    public void Configure(EntityTypeBuilder<ProjectManagerAssignment> b)
    {
        b.ToTable("ProjectManagerAssignments");
        b.Audit();
        b.Property(x => x.AssignedFromUtc).HasPrecision(6);
        b.Property(x => x.AssignedToUtc).HasPrecision(6);
        b.Property(x => x.AssignedBy).HasMaxLength(256);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.AssignedToUtc });
        b.HasIndex(x => x.UserId);
    }
}

public class ProjectStatusHistoryConfiguration : IEntityTypeConfiguration<ProjectStatusHistory>
{
    public void Configure(EntityTypeBuilder<ProjectStatusHistory> b)
    {
        b.ToTable("ProjectStatusHistories");
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}

public class ProjectAreaHistoryConfiguration : IEntityTypeConfiguration<ProjectAreaHistory>
{
    public void Configure(EntityTypeBuilder<ProjectAreaHistory> b)
    {
        b.ToTable("ProjectAreaHistories");
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.Property(x => x.PreviousArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.Change).HasColumnType("decimal(18,4)");
        b.Property(x => x.NewArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Acquisition).WithMany().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Adjustment).WithMany().OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.ChangedAtUtc });
    }
}

public class LandParcelConfiguration : IEntityTypeConfiguration<LandParcel>
{
    public void Configure(EntityTypeBuilder<LandParcel> b)
    {
        b.ToTable("LandParcels");
        b.Audit();
        b.Property(x => x.ParcelCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.TitleReference).HasMaxLength(200);
        b.Property(x => x.Location).HasMaxLength(300);
        b.Property(x => x.Area).HasColumnType("decimal(18,4)");
        b.Property(x => x.ParcelType).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.ParcelCode }).IsUnique();
    }
}

public class LandAcquisitionConfiguration : IEntityTypeConfiguration<LandAcquisition>
{
    public void Configure(EntityTypeBuilder<LandAcquisition> b)
    {
        b.ToTable("LandAcquisitions");
        b.Audit();
        b.Property(x => x.SellerName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ReferenceNumber).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Area).HasColumnType("decimal(18,4)");
        b.Property(x => x.Cost).HasColumnType("decimal(18,2)");
        b.HasOne(x => x.Parcel).WithMany().OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}

public class LandAdjustmentConfiguration : IEntityTypeConfiguration<LandAdjustment>
{
    public void Configure(EntityTypeBuilder<LandAdjustment> b)
    {
        b.ToTable("LandAdjustments");
        b.Audit();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PreviousArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.AdjustmentArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.NewArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.SupportingReference).HasMaxLength(300).IsRequired();
        b.Property(x => x.ApprovedByName).HasMaxLength(256).IsRequired();
        b.Property(x => x.ApprovedAtUtc).HasPrecision(6);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}

public class LandDistributionConfiguration : IEntityTypeConfiguration<LandDistribution>
{
    public void Configure(EntityTypeBuilder<LandDistribution> b)
    {
        b.ToTable("LandDistributions");
        b.Audit();
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Area).HasColumnType("decimal(18,4)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Category }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_LandDistributions_Area", "`Area` >= 0"));
    }
}

public class LandDistributionHistoryConfiguration : IEntityTypeConfiguration<LandDistributionHistory>
{
    public void Configure(EntityTypeBuilder<LandDistributionHistory> b)
    {
        b.ToTable("LandDistributionHistories");
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.PreviousArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.NewArea).HasColumnType("decimal(18,4)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.ChangedAtUtc });
    }
}

public class LandDocumentConfiguration : IEntityTypeConfiguration<LandDocument>
{
    public void Configure(EntityTypeBuilder<LandDocument> b)
    {
        b.ToTable("LandDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(30);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Parcel).WithMany().HasForeignKey(x => x.LandParcelId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Acquisition).WithMany().HasForeignKey(x => x.LandAcquisitionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Adjustment).WithMany().HasForeignKey(x => x.LandAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}
