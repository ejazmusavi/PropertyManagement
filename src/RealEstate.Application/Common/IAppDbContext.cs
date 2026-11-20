using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RealEstate.Domain.Entities;

namespace RealEstate.Application.Common;

/// <summary>Persistence abstraction used by application services; implemented by the Infrastructure DbContext.</summary>
public interface IAppDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserProject> UserProjects { get; }
    DbSet<Project> Projects { get; }
    DbSet<ProjectAddress> ProjectAddresses { get; }
    DbSet<ProjectDocument> ProjectDocuments { get; }
    DbSet<ProjectManagerAssignment> ProjectManagerAssignments { get; }
    DbSet<ProjectAreaHistory> ProjectAreaHistories { get; }
    DbSet<ProjectStatusHistory> ProjectStatusHistories { get; }
    DbSet<LandParcel> LandParcels { get; }
    DbSet<LandAcquisition> LandAcquisitions { get; }
    DbSet<LandAdjustment> LandAdjustments { get; }
    DbSet<LandDistribution> LandDistributions { get; }
    DbSet<LandDistributionHistory> LandDistributionHistories { get; }
    DbSet<LandDocument> LandDocuments { get; }
    DbSet<PropertyCategory> PropertyCategories { get; }
    DbSet<PropertyType> PropertyTypes { get; }
    DbSet<ProjectBlock> ProjectBlocks { get; }
    DbSet<ProjectSector> ProjectSectors { get; }
    DbSet<ProjectStreet> ProjectStreets { get; }
    DbSet<Property> Properties { get; }
    DbSet<PropertyDimension> PropertyDimensions { get; }
    DbSet<PropertyFeature> PropertyFeatures { get; }
    DbSet<PropertyDocument> PropertyDocuments { get; }
    DbSet<PropertyStatusHistory> PropertyStatusHistories { get; }
    DbSet<PropertyReservation> PropertyReservations { get; }
    DbSet<PricingRule> PricingRules { get; }
    DbSet<PricingRuleCondition> PricingRuleConditions { get; }
    DbSet<DiscountRule> DiscountRules { get; }
    DbSet<PropertyPrice> PropertyPrices { get; }
    DbSet<PropertyPriceHistory> PropertyPriceHistories { get; }
    DbSet<PropertyPriceAdjustment> PropertyPriceAdjustments { get; }
    DbSet<PricingApproval> PricingApprovals { get; }
    DbSet<Customer> Customers { get; }
    DbSet<CustomerAddress> CustomerAddresses { get; }
    DbSet<CustomerContact> CustomerContacts { get; }
    DbSet<CustomerNominee> CustomerNominees { get; }
    DbSet<CustomerRelationship> CustomerRelationships { get; }
    DbSet<CustomerDocument> CustomerDocuments { get; }
    DbSet<CustomerOwnership> CustomerOwnerships { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<BookingCustomer> BookingCustomers { get; }
    DbSet<BookingProperty> BookingProperties { get; }
    DbSet<BookingPricingSnapshot> BookingPricingSnapshots { get; }
    DbSet<BookingCharge> BookingCharges { get; }
    DbSet<BookingApproval> BookingApprovals { get; }
    DbSet<BookingStatusHistory> BookingStatusHistories { get; }
    DbSet<BookingRefund> BookingRefunds { get; }
    DbSet<CustomerAgreement> CustomerAgreements { get; }
    DbSet<AgreementVersion> AgreementVersions { get; }
    DbSet<AgreementDocument> AgreementDocuments { get; }
    DbSet<AgreementAmendment> AgreementAmendments { get; }
    DbSet<ChargeType> ChargeTypes { get; }
    DbSet<LateFeeRule> LateFeeRules { get; }
    DbSet<InstallmentPlan> InstallmentPlans { get; }
    DbSet<InstallmentPlanComponent> InstallmentPlanComponents { get; }
    DbSet<AgreementCharge> AgreementCharges { get; }
    DbSet<PaymentSchedule> PaymentSchedules { get; }
    DbSet<PaymentScheduleItem> PaymentScheduleItems { get; }
    DbSet<ScheduleAmendment> ScheduleAmendments { get; }
    DbSet<Waiver> Waivers { get; }
    DatabaseFacade Database { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optimistic concurrency conflict (another user changed the record first).</summary>
public class ConcurrencyException() : Domain.Common.DomainException("The record was changed by another user. Reload and try again.");
