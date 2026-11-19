using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    public void Configure(EntityTypeBuilder<PricingRule> b)
    {
        b.ToTable("PricingRules");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Factor).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Unit).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.CustomerReference).HasMaxLength(200);
        b.Property(x => x.StatusReason).HasMaxLength(1000);
        b.Property(x => x.StatusChangedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectBlock>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectStreet>().WithMany().HasForeignKey(x => x.StreetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PropertyCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Conditions).WithOne().HasForeignKey(c => c.PricingRuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ProjectId, x.Status, x.Kind });
        b.HasIndex(x => new { x.ProjectId, x.Code });
        b.HasIndex(x => x.PropertyId);
        b.ToTable(t => t.HasCheckConstraint("CK_PricingRules_Value", "`Value` >= 0 AND (`EffectiveTo` IS NULL OR `EffectiveTo` >= `EffectiveFrom` OR `Status` = 'Retired')"));
    }
}

public class PricingRuleConditionConfiguration : IEntityTypeConfiguration<PricingRuleCondition>
{
    public void Configure(EntityTypeBuilder<PricingRuleCondition> b)
    {
        b.ToTable("PricingRuleConditions");
        b.Property(x => x.Field).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Value).HasMaxLength(60).IsRequired();
        b.HasIndex(x => x.PricingRuleId);
    }
}

public class DiscountRuleConfiguration : IEntityTypeConfiguration<DiscountRule>
{
    public void Configure(EntityTypeBuilder<DiscountRule> b)
    {
        b.ToTable("DiscountRules");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.StatusReason).HasMaxLength(1000);
        b.Property(x => x.StatusChangedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectBlock>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectStreet>().WithMany().HasForeignKey(x => x.StreetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PropertyCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Status });
        b.ToTable(t => t.HasCheckConstraint("CK_DiscountRules_Value", "`Value` >= 0"));
    }
}

public class PropertyPriceConfiguration : IEntityTypeConfiguration<PropertyPrice>
{
    public void Configure(EntityTypeBuilder<PropertyPrice> b)
    {
        b.ToTable("PropertyPrices");
        b.Audit();
        b.Property(x => x.BaseRate).HasColumnType("decimal(18,4)");
        b.Property(x => x.RateUnit).HasConversion<string>().HasMaxLength(20);
        foreach (var p in new[] { nameof(PropertyPrice.BasePrice), nameof(PropertyPrice.SizeBasedPrice), nameof(PropertyPrice.TotalPremiums), nameof(PropertyPrice.TotalDiscounts), nameof(PropertyPrice.TotalCharges), nameof(PropertyPrice.FinalPrice) })
            b.Property(p).HasColumnType("decimal(18,2)");
        b.Property(x => x.SnapshotJson).HasColumnType("longtext").IsRequired();
        b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.PublishedAtUtc).HasPrecision(6);
        b.Property(x => x.PublishedBy).HasMaxLength(256);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        // One current price per property: concurrent first publications collide here instead of creating two prices.
        b.HasIndex(x => x.PropertyId).IsUnique();
        b.HasIndex(x => x.ProjectId);
        b.ToTable(t => t.HasCheckConstraint("CK_PropertyPrices_Final", "`FinalPrice` >= 0"));
    }
}

public class PropertyPriceHistoryConfiguration : IEntityTypeConfiguration<PropertyPriceHistory>
{
    public void Configure(EntityTypeBuilder<PropertyPriceHistory> b)
    {
        b.ToTable("PropertyPriceHistories");
        b.Property(x => x.PreviousPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.NewPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.SnapshotJson).HasColumnType("longtext").IsRequired();
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PropertyId, x.ChangedAtUtc });
        b.HasIndex(x => new { x.ProjectId, x.ChangedAtUtc });
    }
}

public class PricingApprovalConfiguration : IEntityTypeConfiguration<PricingApproval>
{
    public void Configure(EntityTypeBuilder<PricingApproval> b)
    {
        b.ToTable("PricingApprovals");
        b.Audit();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AdjustmentKind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.OriginalPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.ProposedPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.CustomerReference).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.RequestedByName).HasMaxLength(256);
        b.Property(x => x.RequestedAtUtc).HasPrecision(6);
        b.Property(x => x.DecidedByName).HasMaxLength(256);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.Property(x => x.DecisionComment).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PricingRule>().WithMany().HasForeignKey(x => x.PricingRuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Status });
        b.HasIndex(x => new { x.PropertyId, x.Status });
    }
}

public class PropertyPriceAdjustmentConfiguration : IEntityTypeConfiguration<PropertyPriceAdjustment>
{
    public void Configure(EntityTypeBuilder<PropertyPriceAdjustment> b)
    {
        b.ToTable("PropertyPriceAdjustments");
        b.Audit();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.CustomerReference).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ApprovedAtUtc).HasPrecision(6);
        b.Property(x => x.RevokedAtUtc).HasPrecision(6);
        b.Property(x => x.RevokedBy).HasMaxLength(256);
        b.Property(x => x.RevokeReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PricingApproval>().WithMany().HasForeignKey(x => x.ApprovalId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PropertyId, x.Status });
        b.HasIndex(x => x.ApprovalId).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_PropertyPriceAdjustments_Value", "`Value` > 0"));
    }
}
