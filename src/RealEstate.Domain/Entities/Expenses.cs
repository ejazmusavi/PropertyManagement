using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>Expense category (spec 7.2). The eleven standard categories are system rows seeded by code; custom ones can be added.</summary>
public class ExpenseCategory : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A supplier or service provider. Never deleted: deactivated.</summary>
public class Vendor : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A company expense recorded against one project. Workflow: Draft -> PendingApproval -> Approved (payable) -> paid in full or in part by <see cref="VendorPayment"/>s.
/// Approved expenses are never deleted: cancelled (only while nothing is paid) or paid.
/// </summary>
public class Expense : AuditableEntity<long>
{
    public string ExpenseNumber { get; set; } = default!;
    public string? IdempotencyKey { get; set; }
    public long ProjectId { get; set; }
    public long CategoryId { get; set; }
    public ExpenseCategory? Category { get; set; }
    public long? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string Description { get; set; } = default!;
    public DateOnly ExpenseDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Sum of the posted (not reversed) payments; maintained inside the locked payment transaction.</summary>
    public decimal AmountPaid { get; set; }
    public string? Reference { get; set; }
    public string? Remarks { get; set; }
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Draft;
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public decimal Outstanding => Status == ExpenseStatus.Approved ? Amount - AmountPaid : 0m;

    public ExpensePaymentStatus PaymentStatus => AmountPaid <= 0m ? ExpensePaymentStatus.Unpaid : AmountPaid >= Amount ? ExpensePaymentStatus.Paid : ExpensePaymentStatus.PartiallyPaid;

    public static bool CanEdit(ExpenseStatus s) => s is ExpenseStatus.Draft or ExpenseStatus.Rejected;

    public static ExpensePaymentStatus PaymentStatusOf(decimal amount, decimal paid) =>
        paid <= 0m ? ExpensePaymentStatus.Unpaid : paid >= amount ? ExpensePaymentStatus.Paid : ExpensePaymentStatus.PartiallyPaid;
}

/// <summary>Approval decision on an expense (maker-checker: the approver differs from the submitter). History is kept.</summary>
public class ExpenseApproval : Entity<long>
{
    public long ExpenseId { get; set; }
    public BookingDecision Decision { get; set; }
    public Guid ApproverUserId { get; set; }
    public string ApproverName { get; set; } = default!;
    public string? Comments { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

/// <summary>Supporting document (invoice, receipt...) in secure storage; soft-deleted only, with a reason.</summary>
public class ExpenseAttachment : StoredDocument
{
    public long ExpenseId { get; set; }
    public ExpenseDocumentType DocumentType { get; set; }
}

/// <summary>
/// A vendor's bill, registered together with its payable <see cref="Expense"/> (one invoice per expense). Payables are tracked on the expense;
/// the invoice adds the vendor's number and due date and rejects duplicate invoice numbers per vendor.
/// </summary>
public class VendorInvoice : AuditableEntity<long>
{
    public string InvoiceNumber { get; set; } = default!;
    public long VendorId { get; set; }
    public long ProjectId { get; set; }
    public long ExpenseId { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
    /// <summary>"vendorId:INVOICE" while the expense is live (unique); cleared when it is rejected/cancelled so the number can be re-entered.</summary>
    public string? ActiveKey { get; set; }
}

/// <summary>A payment of an approved expense (to a vendor or other payee). Posted payments are never deleted: they are reversed.</summary>
public class VendorPayment : AuditableEntity<long>
{
    public string PaymentNumber { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public long ExpenseId { get; set; }
    public long ProjectId { get; set; }
    public long? VendorId { get; set; }
    public long PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaymentDate { get; set; }
    public string? Reference { get; set; }
    public string? Remarks { get; set; }
    public VendorPaymentStatus Status { get; set; } = VendorPaymentStatus.Posted;
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

// ---------------------------------------------------------------------------------------------------
// Government fees (spec 7.4)
// ---------------------------------------------------------------------------------------------------

/// <summary>Configurable government fee (stamp duty, registration, transfer, taxes, NOC, development charges, other). Company-wide (no project) or project-specific.</summary>
public class GovernmentFeeType : AuditableEntity<long>
{
    public long? ProjectId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public GovernmentFeeKind Kind { get; set; }
    public FeeCalculationMethod Method { get; set; } = FeeCalculationMethod.Manual;
    /// <summary>Amount charged for <see cref="FeeCalculationMethod.Fixed"/> types.</summary>
    public decimal? FixedAmount { get; set; }
    /// <summary>Percentage (DECIMAL(18,4)) of the basis amount for <see cref="FeeCalculationMethod.PercentOfBasis"/> types.</summary>
    public decimal? Rate { get; set; }
    public FeeBearer DefaultBearer { get; set; } = FeeBearer.Customer;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];

    /// <summary>The fee amount for a type and basis; null when the amount is entered manually.</summary>
    public decimal? Calculate(decimal? basis, int decimals, RoundingMode mode) => Method switch
    {
        FeeCalculationMethod.Fixed => FixedAmount,
        FeeCalculationMethod.PercentOfBasis when basis is { } b && Rate is { } r => Pricing.MoneyRounding.Round(b * r / 100m, decimals, mode),
        _ => null,
    };
}

/// <summary>
/// A government fee assessed for a customer agreement. Amount = what is payable to government = customer liability + company-borne part.
/// Customer collections, actual payments to government and the company-borne expense are tracked separately.
/// </summary>
public class GovernmentFeeAssessment : AuditableEntity<long>
{
    public string AssessmentNumber { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public long ProjectId { get; set; }
    public long AgreementId { get; set; }
    public long BookingId { get; set; }
    public long CustomerId { get; set; }
    public long FeeTypeId { get; set; }
    public GovernmentFeeType? FeeType { get; set; }
    public decimal? BasisAmount { get; set; }
    public decimal Amount { get; set; }
    /// <summary>The part the customer owes (collected from the customer).</summary>
    public decimal CustomerLiability { get; set; }
    /// <summary>The part the company bears as its own expense.</summary>
    public decimal CompanyBorne { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? Reference { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Sum of posted customer collections.</summary>
    public decimal CollectedAmount { get; set; }
    /// <summary>Sum of posted payments to government.</summary>
    public decimal PaidAmount { get; set; }
    public GovernmentFeeAssessmentStatus Status { get; set; } = GovernmentFeeAssessmentStatus.Assessed;
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public decimal CustomerOutstanding => Status == GovernmentFeeAssessmentStatus.Assessed ? CustomerLiability - CollectedAmount : 0m;
    public decimal GovernmentPayable => Status == GovernmentFeeAssessmentStatus.Assessed ? Amount - PaidAmount : 0m;
}

/// <summary>Money collected from the customer for an assessed fee (customer collection). Reversed, never deleted.</summary>
public class GovernmentFeeCollection : AuditableEntity<long>
{
    public string CollectionNumber { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public long AssessmentId { get; set; }
    public long ProjectId { get; set; }
    public long CustomerId { get; set; }
    public long PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly CollectionDate { get; set; }
    public string? Reference { get; set; }
    public string? Remarks { get; set; }
    public FeeEntryStatus Status { get; set; } = FeeEntryStatus.Posted;
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }
}

/// <summary>An actual payment to government (challan/receipt number as reference). Reversed, never deleted.</summary>
public class GovernmentFeePayment : AuditableEntity<long>
{
    public string PaymentNumber { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public long AssessmentId { get; set; }
    public long ProjectId { get; set; }
    public long PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaymentDate { get; set; }
    public string? Reference { get; set; }
    public string? Remarks { get; set; }
    public FeeEntryStatus Status { get; set; } = FeeEntryStatus.Posted;
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }
}

/// <summary>Government receipt / challan / other attachment of a fee assessment; soft-deleted only.</summary>
public class GovernmentFeeDocument : StoredDocument
{
    public long AssessmentId { get; set; }
    public GovernmentFeeDocumentType DocumentType { get; set; }
}
