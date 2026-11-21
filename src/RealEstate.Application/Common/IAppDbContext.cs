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
    DbSet<PaymentMethod> PaymentMethods { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentReceipt> PaymentReceipts { get; }
    DbSet<PaymentAllocation> PaymentAllocations { get; }
    DbSet<PaymentVerification> PaymentVerifications { get; }
    DbSet<PaymentReversal> PaymentReversals { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<RefundApproval> RefundApprovals { get; }
    DbSet<AgreementLedgerEntry> AgreementLedgerEntries { get; }
    DbSet<CustomerLedgerEntry> CustomerLedgerEntries { get; }
    DbSet<AccountingPostingRequest> AccountingPostingRequests { get; }
    DbSet<RecoveryAssignment> RecoveryAssignments { get; }
    DbSet<CustomerFollowUp> CustomerFollowUps { get; }
    DbSet<ExpenseCategory> ExpenseCategories { get; }
    DbSet<Vendor> Vendors { get; }
    DbSet<Expense> Expenses { get; }
    DbSet<ExpenseApproval> ExpenseApprovals { get; }
    DbSet<ExpenseAttachment> ExpenseAttachments { get; }
    DbSet<VendorInvoice> VendorInvoices { get; }
    DbSet<VendorPayment> VendorPayments { get; }
    DbSet<GovernmentFeeType> GovernmentFeeTypes { get; }
    DbSet<GovernmentFeeAssessment> GovernmentFeeAssessments { get; }
    DbSet<GovernmentFeeCollection> GovernmentFeeCollections { get; }
    DbSet<GovernmentFeePayment> GovernmentFeePayments { get; }
    DbSet<GovernmentFeeDocument> GovernmentFeeDocuments { get; }
    // Phase 8
    DbSet<AccountGroup> AccountGroups { get; }
    DbSet<ChartOfAccount> ChartOfAccounts { get; }
    DbSet<FiscalYear> FiscalYears { get; }
    DbSet<AccountingPeriod> AccountingPeriods { get; }
    DbSet<AccountingPeriodLock> AccountingPeriodLocks { get; }
    DbSet<CostCenter> CostCenters { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalEntryLine> JournalEntryLines { get; }
    DbSet<GeneralLedgerEntry> GeneralLedgerEntries { get; }
    DbSet<AccountingVoucher> AccountingVouchers { get; }
    DbSet<PostingRule> PostingRules { get; }
    DbSet<PostingRuleLine> PostingRuleLines { get; }
    DbSet<BankAccount> BankAccounts { get; }
    DbSet<CashAccount> CashAccounts { get; }
    DbSet<BankTransaction> BankTransactions { get; }
    DbSet<BankReconciliation> BankReconciliations { get; }
    DbSet<ProjectCostAllocation> ProjectCostAllocations { get; }
    DbSet<ProjectCostAllocationLine> ProjectCostAllocationLines { get; }
    // Phase 10
    DbSet<ApplicationSetting> ApplicationSettings { get; }
    DbSet<CustomerAccount> CustomerAccounts { get; }
    DbSet<PaymentProofDocument> PaymentProofDocuments { get; }
    DbSet<Announcement> Announcements { get; }
    DbSet<CustomerNotification> CustomerNotifications { get; }
    DbSet<SupportRequest> SupportRequests { get; }
    DbSet<SupportRequestMessage> SupportRequestMessages { get; }
    DbSet<GatewayTransaction> GatewayTransactions { get; }
    DbSet<PaymentWebhookLog> PaymentWebhookLogs { get; }
    DatabaseFacade Database { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Phase 6: SELECT ... FOR UPDATE on one row of the entity's table (requires an open transaction); waits for other writers of that row.</summary>
    Task LockForUpdateAsync<T>(long id, CancellationToken ct = default) where T : class;
}

/// <summary>Optimistic concurrency conflict (another user changed the record first).</summary>
public class ConcurrencyException() : Domain.Common.DomainException("The record was changed by another user. Reload and try again.");
