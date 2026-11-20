using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class ChargeTypeConfiguration : IEntityTypeConfiguration<ChargeType>
{
    public void Configure(EntityTypeBuilder<ChargeType> b)
    {
        b.ToTable("ChargeTypes");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DefaultCategory).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DefaultFrequency).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class LateFeeRuleConfiguration : IEntityTypeConfiguration<LateFeeRule>
{
    public void Configure(EntityTypeBuilder<LateFeeRule> b)
    {
        b.ToTable("LateFeeRules", t =>
        {
            t.HasCheckConstraint("CK_LateFeeRules_Value", "`Value` > 0");
            t.HasCheckConstraint("CK_LateFeeRules_MaxAmount", "`MaxAmount` IS NULL OR `MaxAmount` > 0");
            t.HasCheckConstraint("CK_LateFeeRules_MaxPercent", "`MaxPercentOfInstallment` IS NULL OR (`MaxPercentOfInstallment` > 0 AND `MaxPercentOfInstallment` <= 100)");
        });
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.MaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.MaxPercentOfInstallment).HasColumnType("decimal(18,4)");
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class InstallmentPlanConfiguration : IEntityTypeConfiguration<InstallmentPlan>
{
    public void Configure(EntityTypeBuilder<InstallmentPlan> b)
    {
        b.ToTable("InstallmentPlans");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.PlanType).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ActivatedBy).HasMaxLength(256);
        b.Property(x => x.RetiredBy).HasMaxLength(256);
        b.Property(x => x.RetireReason).HasMaxLength(1000);
        b.Property(x => x.ActivatedAtUtc).HasPrecision(6);
        b.Property(x => x.RetiredAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PropertyCategory>().WithMany().HasForeignKey(x => x.PropertyCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(c => c.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status, x.PlanType });
    }
}

internal static class ChargeTermsConfiguration
{
    public static void Terms<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.Property<ChargeCategory>("Category").HasConversion<string>().HasMaxLength(30);
        b.Property<ScheduleFrequency>("Frequency").HasConversion<string>().HasMaxLength(20);
        b.Property<decimal?>("InstallmentAmount").HasColumnType("decimal(18,2)");
        b.Property<string?>("CustomScheduleJson").HasColumnType("longtext");
        b.Property<decimal>("MaxWaiverPercent").HasColumnType("decimal(18,4)");
        b.Property<TaxTreatment>("TaxTreatment").HasConversion<string>().HasMaxLength(20);
        b.Property<decimal>("TaxRatePercent").HasColumnType("decimal(18,4)");
        b.Property<decimal?>("RoundingUnit").HasColumnType("decimal(18,2)");
        b.Property<string?>("Remarks").HasMaxLength(1000);
        b.HasOne<LateFeeRule>().WithMany().HasForeignKey("LateFeeRuleId").OnDelete(DeleteBehavior.Restrict);
    }
}

public class InstallmentPlanComponentConfiguration : IEntityTypeConfiguration<InstallmentPlanComponent>
{
    public void Configure(EntityTypeBuilder<InstallmentPlanComponent> b)
    {
        b.ToTable("InstallmentPlanComponents", t =>
        {
            t.HasCheckConstraint("CK_InstallmentPlanComponents_Amount", "`Amount` IS NULL OR `Amount` > 0");
            t.HasCheckConstraint("CK_InstallmentPlanComponents_Percent", "`Percent` IS NULL OR (`Percent` > 0 AND `Percent` <= 100)");
        });
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.AmountBasis).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Percent).HasColumnType("decimal(18,4)");
        b.HasOne(x => x.ChargeType).WithMany().HasForeignKey(x => x.ChargeTypeId).OnDelete(DeleteBehavior.Restrict);
        ChargeTermsConfiguration.Terms(b);
        b.HasIndex(x => new { x.PlanId, x.SortOrder });
    }
}

public class AgreementChargeConfiguration : IEntityTypeConfiguration<AgreementCharge>
{
    public void Configure(EntityTypeBuilder<AgreementCharge> b)
    {
        b.ToTable("AgreementCharges", t => t.HasCheckConstraint("CK_AgreementCharges_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne(x => x.ChargeType).WithMany().HasForeignKey(x => x.ChargeTypeId).OnDelete(DeleteBehavior.Restrict);
        ChargeTermsConfiguration.Terms(b);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InstallmentPlanComponent>().WithMany().HasForeignKey(x => x.SourcePlanComponentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<AgreementAmendment>().WithMany().HasForeignKey(x => x.AddedByAmendmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AgreementId, x.Status, x.SortOrder });
    }
}

public class PaymentScheduleConfiguration : IEntityTypeConfiguration<PaymentSchedule>
{
    public void Configure(EntityTypeBuilder<PaymentSchedule> b)
    {
        b.ToTable("PaymentSchedules", t => t.HasCheckConstraint("CK_PaymentSchedules_Total", "`TotalAmount` > 0"));
        b.Audit();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalTax).HasColumnType("decimal(18,2)");
        b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.GeneratedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.GeneratedAtUtc).HasPrecision(6);
        b.Property(x => x.SupersededAtUtc).HasPrecision(6);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementAmendment>().WithMany().HasForeignKey(x => x.SourceAmendmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AgreementId, x.VersionNo }).IsUnique();
        b.HasIndex(x => x.ActiveAgreementId).IsUnique(); // one active schedule per agreement
    }
}

public class PaymentScheduleItemConfiguration : IEntityTypeConfiguration<PaymentScheduleItem>
{
    public void Configure(EntityTypeBuilder<PaymentScheduleItem> b)
    {
        b.ToTable("PaymentScheduleItems", t =>
        {
            t.HasCheckConstraint("CK_PaymentScheduleItems_Amount", "`Amount` > 0");
            t.HasCheckConstraint("CK_PaymentScheduleItems_Settlement", "`AmountPaid` >= 0 AND `AmountWaived` >= 0 AND `LateFeeWaived` >= 0 AND `AmountPaid` + `AmountWaived` <= `Amount`");
        });
        b.Property(x => x.Description).HasMaxLength(250).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.AmountPaid).HasColumnType("decimal(18,2)");
        b.Property(x => x.AmountWaived).HasColumnType("decimal(18,2)");
        b.Property(x => x.LateFeeWaived).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Ignore(x => x.Outstanding);
        b.Ignore(x => x.IsSettled);
        b.Ignore(x => x.HasSettlement);
        b.Ignore(x => x.IsPaidInAdvance);
        b.HasOne<AgreementCharge>().WithMany().HasForeignKey(x => x.AgreementChargeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentScheduleItem>().WithMany().HasForeignKey(x => x.PreviousItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ScheduleId, x.LineNo }).IsUnique();
        b.HasIndex(x => new { x.DueDate, x.Status });
    }
}

public class ScheduleAmendmentConfiguration : IEntityTypeConfiguration<ScheduleAmendment>
{
    public void Configure(EntityTypeBuilder<ScheduleAmendment> b)
    {
        b.ToTable("ScheduleAmendments");
        b.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.OldAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.NewAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<AgreementAmendment>().WithMany().HasForeignKey(x => x.AmendmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentSchedule>().WithMany().HasForeignKey(x => x.FromScheduleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentSchedule>().WithMany().HasForeignKey(x => x.ToScheduleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementCharge>().WithMany().HasForeignKey(x => x.AgreementChargeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentScheduleItem>().WithMany().HasForeignKey(x => x.FromItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentScheduleItem>().WithMany().HasForeignKey(x => x.ToItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AmendmentId);
    }
}

public class WaiverConfiguration : IEntityTypeConfiguration<Waiver>
{
    public void Configure(EntityTypeBuilder<Waiver> b)
    {
        b.ToTable("Waivers", t => t.HasCheckConstraint("CK_Waivers_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.DecidedBy).HasMaxLength(256);
        b.Property(x => x.DecisionComments).HasMaxLength(1000);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementAmendment>().WithMany().HasForeignKey(x => x.AmendmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentScheduleItem>().WithMany().HasForeignKey(x => x.ScheduleItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementCharge>().WithMany().HasForeignKey(x => x.AgreementChargeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.AgreementId });
    }
}
