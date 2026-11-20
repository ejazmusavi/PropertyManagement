using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Sales;

namespace RealEstate.Domain.Entities;

public class Booking : AuditableEntity<long>
{
    public string BookingNumber { get; set; } = default!;
    public string? IdempotencyKey { get; set; }
    public long ProjectId { get; set; }
    public DateOnly BookingDate { get; set; }
    public PaymentMode PaymentMode { get; set; }
    public BookingStatus Status { get; private set; } = BookingStatus.Draft;

    public decimal TotalPrice { get; set; }
    public decimal TotalExtraDiscount { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal TotalPayable { get; set; }
    /// <summary>Amount due at booking (cash: normally the full price; installments: the down payment). Collection itself is Phase 6.</summary>
    public decimal BookingAmount { get; set; }

    public Guid? BalanceAuthorizedByUserId { get; set; }
    public string? BalanceAuthorizedBy { get; set; }
    public string? BalanceAuthorizationReason { get; set; }
    public string? Remarks { get; set; }

    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Incremented by every ownership change; also guarantees that concurrent transfers conflict.</summary>
    public int OwnershipRevision { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<BookingCustomer> Customers { get; set; } = [];
    public List<BookingProperty> Properties { get; set; } = [];
    public List<BookingCharge> Charges { get; set; } = [];

    public BookingStatusHistory Move(BookingStatus to, string reason, DateTime nowUtc, Guid? userId, string? userName)
    {
        if (!BookingRules.CanTransition(Status, to)) throw new DomainException($"A booking cannot move from {Status} to {to}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required.");
        var h = new BookingStatusHistory { Booking = this, FromStatus = Status, ToStatus = to, Reason = reason.Trim(), ChangedAtUtc = nowUtc, ChangedByUserId = userId, ChangedBy = userName };
        Status = to;
        return h;
    }
}

/// <summary>A party to the booking with an ownership share. Rows are closed (never deleted) when ownership is transferred.</summary>
public class BookingCustomer : AuditableEntity<long>
{
    public long BookingId { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public decimal SharePercent { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public string? EndReason { get; set; }
}

public class BookingProperty : AuditableEntity<long>
{
    public long BookingId { get; set; }
    public long PropertyId { get; set; }
    public Property? Property { get; set; }
    /// <summary>Equals PropertyId while the booking holds the property, null once cancelled. Unique index: a property cannot be on two live bookings.</summary>
    public long? ActivePropertyId { get; set; }
    public decimal ExtraDiscount { get; set; }
    public string? ExtraDiscountReason { get; set; }
    public decimal LinePrice { get; set; }
    public BookingPricingSnapshot? PricingSnapshot { get; set; }
}

/// <summary>Immutable copy of the Phase 3 price calculation taken at booking time; never recalculated.</summary>
public class BookingPricingSnapshot : Entity<long>
{
    public long BookingPropertyId { get; set; }
    public int SnapshotVersion { get; set; }
    public string SnapshotJson { get; set; } = default!;
    public string SnapshotHash { get; set; } = default!;
    public DateOnly AsOfDate { get; set; }
    public long? CustomerId { get; set; }
    public decimal Size { get; set; }
    public MeasurementUnit Unit { get; set; }
    public decimal BasePrice { get; set; }
    public decimal TotalPremiums { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalPricingCharges { get; set; }
    public bool IsOverridden { get; set; }
    public decimal FinalPrice { get; set; }
    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>An additional charge on the booking (registration, tax...). Charges that came from pricing rules are part of the snapshot price instead.</summary>
public class BookingCharge : AuditableEntity<long>
{
    public long BookingId { get; set; }
    public BookingChargeType ChargeType { get; set; }
    public string Description { get; set; } = default!;
    public decimal Amount { get; set; }
}

public class BookingApproval : Entity<long>
{
    public long BookingId { get; set; }
    public BookingDecision Decision { get; set; }
    public Guid ApproverUserId { get; set; }
    public string ApproverName { get; set; } = default!;
    public string? Comments { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

public class BookingStatusHistory : Entity<long>
{
    public long BookingId { get; set; }
    public Booking? Booking { get; set; }
    public BookingStatus? FromStatus { get; set; }
    public BookingStatus ToStatus { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime ChangedAtUtc { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
}

/// <summary>A refund owed to the customer after cancellation. Approval is maker-checker; the payout itself belongs to Phase 6.</summary>
public class BookingRefund : AuditableEntity<long>
{
    public long BookingId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = default!;
    public RefundStatus Status { get; set; } = RefundStatus.Requested;
    public Guid RequestedByUserId { get; set; }
    public string RequestedBy { get; set; } = default!;
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComments { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class CustomerAgreement : AuditableEntity<long>
{
    public string AgreementNumber { get; set; } = default!;
    public long BookingId { get; set; }
    public AgreementStatus Status { get; set; } = AgreementStatus.Active;
    public int CurrentVersionNo { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<AgreementVersion> Versions { get; set; } = [];
}

/// <summary>An immutable generated text of the agreement. Every amendment adds a new version; old versions are kept.</summary>
public class AgreementVersion : AuditableEntity<long>
{
    public long AgreementId { get; set; }
    public int VersionNo { get; set; }
    public string Content { get; set; } = default!;
    public string ContentSha256 { get; set; } = default!;
    public string Reason { get; set; } = default!;
}

public class AgreementDocument : StoredDocument
{
    public long AgreementId { get; set; }
    public AgreementDocumentType DocumentType { get; set; }
}

public class AgreementAmendment : AuditableEntity<long>
{
    public long AgreementId { get; set; }
    public AmendmentType AmendmentType { get; set; }
    public int FromVersionNo { get; set; }
    public int ToVersionNo { get; set; }
    public string Reason { get; set; } = default!;
    /// <summary>JSON with the previous and the new owners and shares.</summary>
    public string Details { get; set; } = default!;
    public Guid ApprovedByUserId { get; set; }
    public string ApprovedBy { get; set; } = default!;
}
