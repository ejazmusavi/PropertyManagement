using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> b)
    {
        b.ToTable("NotificationTemplates");
        b.Audit();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Language).HasMaxLength(10).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => new { x.Type, x.Channel, x.Language }).IsUnique();
    }
}

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("NotificationPreferences");
        b.Audit();
        b.Property(x => x.OwnerKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OwnerKey, x.Channel, x.TypeKey }).IsUnique();
        b.HasIndex(x => new { x.TypeKey, x.IsEnabled });
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.Audit();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        b.Property(x => x.DataJson).HasColumnType("longtext");
        b.Property(x => x.RelatedType).HasMaxLength(40);
        b.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ScheduledForUtc).HasPrecision(6);
        b.Property(x => x.ReadAtUtc).HasPrecision(6);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Deliveries).WithOne().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
        // One notification per business event: makes queuing idempotent.
        b.HasIndex(x => x.DedupeKey).IsUnique();
        b.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.RecipientUserId, x.ReadAtUtc });
        b.HasIndex(x => new { x.Status, x.Type });
    }
}

public class NotificationDeliveryLogConfiguration : IEntityTypeConfiguration<NotificationDeliveryLog>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryLog> b)
    {
        b.ToTable("NotificationDeliveryLogs", t => t.HasCheckConstraint("CK_NotificationDeliveryLogs_Attempts", "`Attempts` >= 0 AND `Attempts` <= `MaxAttempts` + 1"));
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Recipient).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.NextAttemptAtUtc).HasPrecision(6);
        b.Property(x => x.LastAttemptAtUtc).HasPrecision(6);
        b.Property(x => x.SentAtUtc).HasPrecision(6);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ProviderReference).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        // One delivery per notification and channel.
        b.HasIndex(x => new { x.NotificationId, x.Channel }).IsUnique();
        b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
    }
}

public class BackgroundJobRunConfiguration : IEntityTypeConfiguration<BackgroundJobRun>
{
    public void Configure(EntityTypeBuilder<BackgroundJobRun> b)
    {
        b.ToTable("BackgroundJobRuns");
        b.Property(x => x.JobKey).HasMaxLength(60).IsRequired();
        b.Property(x => x.TriggeredBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.StartedAtUtc).HasPrecision(6);
        b.Property(x => x.FinishedAtUtc).HasPrecision(6);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Message).HasMaxLength(2000);
        b.HasIndex(x => new { x.JobKey, x.StartedAtUtc });
    }
}

public class SpatialReferenceSystemConfiguration : IEntityTypeConfiguration<SpatialReferenceSystem>
{
    public void Configure(EntityTypeBuilder<SpatialReferenceSystem> b)
    {
        b.ToTable("SpatialReferenceSystems");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Unit).HasMaxLength(30).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Srid).IsUnique();
    }
}

internal static class BoundaryConfig
{
    public static void Common<T>(EntityTypeBuilder<T> b) where T : BoundaryBase
    {
        b.Audit();
        b.Property(x => x.GeometryType).HasMaxLength(20).IsRequired();
        b.Property(x => x.GeometryJson).HasColumnType("longtext").IsRequired();
        b.Property(x => x.MinX).HasPrecision(18, 8);
        b.Property(x => x.MinY).HasPrecision(18, 8);
        b.Property(x => x.MaxX).HasPrecision(18, 8);
        b.Property(x => x.MaxY).HasPrecision(18, 8);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SurveyReference).HasMaxLength(100).IsRequired();
        b.Property(x => x.SurveyedBy).HasMaxLength(200).IsRequired();
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.VerifiedBy).HasMaxLength(256);
        b.Property(x => x.VerifiedAtUtc).HasPrecision(6);
        b.Property(x => x.DecisionComment).HasMaxLength(500);
        b.Property(x => x.SupersededAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<SpatialReferenceSystem>().WithMany().HasForeignKey(x => x.SrsId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MapDocument>().WithMany().HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProjectBoundaryConfiguration : IEntityTypeConfiguration<ProjectBoundary>
{
    public void Configure(EntityTypeBuilder<ProjectBoundary> b)
    {
        b.ToTable("ProjectBoundaries", t => t.HasCheckConstraint("CK_ProjectBoundaries_Active", "(`Status` = 'Verified' AND `ActiveProjectId` IS NOT NULL) OR (`Status` <> 'Verified' AND `ActiveProjectId` IS NULL)"));
        BoundaryConfig.Common(b);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.VersionNo }).IsUnique();
        // A project has at most one Verified boundary in force.
        b.HasIndex(x => x.ActiveProjectId).IsUnique();
    }
}

public class PropertyBoundaryConfiguration : IEntityTypeConfiguration<PropertyBoundary>
{
    public void Configure(EntityTypeBuilder<PropertyBoundary> b)
    {
        b.ToTable("PropertyBoundaries", t => t.HasCheckConstraint("CK_PropertyBoundaries_Active", "(`Status` = 'Verified' AND `ActivePropertyId` IS NOT NULL) OR (`Status` <> 'Verified' AND `ActivePropertyId` IS NULL)"));
        BoundaryConfig.Common(b);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PropertyId, x.VersionNo }).IsUnique();
        b.HasIndex(x => x.ActivePropertyId).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status });
    }
}

public class MapLayerConfiguration : IEntityTypeConfiguration<MapLayer>
{
    public void Configure(EntityTypeBuilder<MapLayer> b)
    {
        b.ToTable("MapLayers");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.StyleJson).HasMaxLength(2000);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SpatialReferenceSystem>().WithMany().HasForeignKey(x => x.SrsId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MapDocument>().WithMany().HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
    }
}

public class MapDocumentConfiguration : IEntityTypeConfiguration<MapDocument>
{
    public void Configure(EntityTypeBuilder<MapDocument> b)
    {
        b.ToTable("MapDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SurveyReference).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SpatialReferenceSystem>().WithMany().HasForeignKey(x => x.SrsId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProjectId);
    }
}
