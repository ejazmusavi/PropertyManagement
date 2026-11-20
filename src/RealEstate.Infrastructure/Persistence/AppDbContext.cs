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

    public DbSet<PropertyCategory> PropertyCategories => Set<PropertyCategory>();
    public DbSet<PropertyType> PropertyTypes => Set<PropertyType>();
    public DbSet<ProjectBlock> ProjectBlocks => Set<ProjectBlock>();
    public DbSet<ProjectSector> ProjectSectors => Set<ProjectSector>();
    public DbSet<ProjectStreet> ProjectStreets => Set<ProjectStreet>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertyDimension> PropertyDimensions => Set<PropertyDimension>();
    public DbSet<PropertyFeature> PropertyFeatures => Set<PropertyFeature>();
    public DbSet<PropertyDocument> PropertyDocuments => Set<PropertyDocument>();
    public DbSet<PropertyStatusHistory> PropertyStatusHistories => Set<PropertyStatusHistory>();
    public DbSet<PropertyReservation> PropertyReservations => Set<PropertyReservation>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<PricingRuleCondition> PricingRuleConditions => Set<PricingRuleCondition>();
    public DbSet<DiscountRule> DiscountRules => Set<DiscountRule>();
    public DbSet<PropertyPrice> PropertyPrices => Set<PropertyPrice>();
    public DbSet<PropertyPriceHistory> PropertyPriceHistories => Set<PropertyPriceHistory>();
    public DbSet<PropertyPriceAdjustment> PropertyPriceAdjustments => Set<PropertyPriceAdjustment>();
    public DbSet<PricingApproval> PricingApprovals => Set<PricingApproval>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();
    public DbSet<CustomerNominee> CustomerNominees => Set<CustomerNominee>();
    public DbSet<CustomerRelationship> CustomerRelationships => Set<CustomerRelationship>();
    public DbSet<CustomerDocument> CustomerDocuments => Set<CustomerDocument>();
    public DbSet<CustomerOwnership> CustomerOwnerships => Set<CustomerOwnership>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingCustomer> BookingCustomers => Set<BookingCustomer>();
    public DbSet<BookingProperty> BookingProperties => Set<BookingProperty>();
    public DbSet<BookingPricingSnapshot> BookingPricingSnapshots => Set<BookingPricingSnapshot>();
    public DbSet<BookingCharge> BookingCharges => Set<BookingCharge>();
    public DbSet<BookingApproval> BookingApprovals => Set<BookingApproval>();
    public DbSet<BookingStatusHistory> BookingStatusHistories => Set<BookingStatusHistory>();
    public DbSet<BookingRefund> BookingRefunds => Set<BookingRefund>();
    public DbSet<CustomerAgreement> CustomerAgreements => Set<CustomerAgreement>();
    public DbSet<AgreementVersion> AgreementVersions => Set<AgreementVersion>();
    public DbSet<AgreementDocument> AgreementDocuments => Set<AgreementDocument>();
    public DbSet<AgreementAmendment> AgreementAmendments => Set<AgreementAmendment>();
    public DbSet<ChargeType> ChargeTypes => Set<ChargeType>();
    public DbSet<LateFeeRule> LateFeeRules => Set<LateFeeRule>();
    public DbSet<InstallmentPlan> InstallmentPlans => Set<InstallmentPlan>();
    public DbSet<InstallmentPlanComponent> InstallmentPlanComponents => Set<InstallmentPlanComponent>();
    public DbSet<AgreementCharge> AgreementCharges => Set<AgreementCharge>();
    public DbSet<PaymentSchedule> PaymentSchedules => Set<PaymentSchedule>();
    public DbSet<PaymentScheduleItem> PaymentScheduleItems => Set<PaymentScheduleItem>();
    public DbSet<ScheduleAmendment> ScheduleAmendments => Set<ScheduleAmendment>();
    public DbSet<Waiver> Waivers => Set<Waiver>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentReceipt> PaymentReceipts => Set<PaymentReceipt>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<PaymentVerification> PaymentVerifications => Set<PaymentVerification>();
    public DbSet<PaymentReversal> PaymentReversals => Set<PaymentReversal>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundApproval> RefundApprovals => Set<RefundApproval>();
    public DbSet<AgreementLedgerEntry> AgreementLedgerEntries => Set<AgreementLedgerEntry>();
    public DbSet<CustomerLedgerEntry> CustomerLedgerEntries => Set<CustomerLedgerEntry>();
    public DbSet<AccountingPostingRequest> AccountingPostingRequests => Set<AccountingPostingRequest>();
    public DbSet<RecoveryAssignment> RecoveryAssignments => Set<RecoveryAssignment>();
    public DbSet<CustomerFollowUp> CustomerFollowUps => Set<CustomerFollowUp>();

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

    public async Task LockForUpdateAsync<T>(long id, CancellationToken ct = default) where T : class
    {
        if (Database.CurrentTransaction is null) throw new InvalidOperationException("Row locks require an open transaction.");
        var table = Model.FindEntityType(typeof(T))?.GetTableName() ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");
        // The table name comes from the EF model (never from input); the id is a parameter.
        var sql = "SELECT `Id` FROM `" + table + "` WHERE `Id` = {0} FOR UPDATE";
        await Database.ExecuteSqlRawAsync(sql, [id], ct);
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
