using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>Payment method catalog (spec 6.2). The six standard methods are system rows seeded by code.</summary>
public class PaymentMethod : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public PaymentMethodKind Kind { get; set; }
    /// <summary>Payments with this method are verified (bank statement, cheque clearing...) before approval.</summary>
    public bool RequiresVerification { get; set; }
    /// <summary>A bank/transaction/cheque reference must be entered.</summary>
    public bool RequiresReference { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Money received (or claimed to be received) against a customer agreement. Workflow: submission -> verification (where required) -> approval ->
/// posting (allocation, receipt, ledger, accounting outbox in one transaction). Posted payments are never deleted; they are reversed.
/// </summary>
public class Payment : AuditableEntity<long>
{
    public string PaymentNumber { get; set; } = default!;
    /// <summary>Client-generated key: a retried submission returns the original payment instead of creating a second one.</summary>
    public string IdempotencyKey { get; set; } = default!;
    /// <summary>SHA-256 of the submitted values; a reused key with different values is rejected.</summary>
    public string RequestHash { get; set; } = default!;
    public long ProjectId { get; set; }
    public long AgreementId { get; set; }
    public long BookingId { get; set; }
    /// <summary>The paying customer (validated to be a current party of the booking).</summary>
    public long CustomerId { get; set; }
    public long PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public PaymentMethodKind MethodKind { get; set; }
    public PaymentSource Source { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public DateOnly PaymentDate { get; set; }
    public string? Reference { get; set; }
    public string? BankName { get; set; }
    /// <summary>Cheque/instrument date where applicable.</summary>
    public DateOnly? InstrumentDate { get; set; }
    public string? PayerName { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Method + normalized reference while the payment is live (not rejected/reversed). Unique: the same bank reference cannot be recorded twice.</summary>
    public string? ActiveReferenceKey { get; set; }

    public AllocationMode RequestedAllocationMode { get; set; } = AllocationMode.OldestDueFirst;
    /// <summary>JSON list of {ItemId, Amount} for manual/specific allocation requests.</summary>
    public string? RequestedAllocationJson { get; set; }
    public long? RequestedChargeId { get; set; }
    public bool RequestedRemainderToAdvance { get; set; }

    public PaymentStatus Status { get; set; }
    public bool VerificationRequired { get; set; }
    public Guid SubmittedByUserId { get; set; }
    public string SubmittedBy { get; set; } = default!;
    public DateTime SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectReason { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) => (from, to) switch
    {
        (PaymentStatus.PendingVerification, PaymentStatus.PendingApproval) => true,
        (PaymentStatus.PendingVerification, PaymentStatus.Rejected) => true,
        (PaymentStatus.PendingApproval, PaymentStatus.Posted) => true,
        (PaymentStatus.PendingApproval, PaymentStatus.Rejected) => true,
        (PaymentStatus.Posted, PaymentStatus.Reversed) => true,
        _ => false,
    };

    public void Move(PaymentStatus to)
    {
        if (!CanTransition(Status, to)) throw new DomainException($"A payment cannot move from {Status} to {to}.");
        Status = to;
        if (to is PaymentStatus.Rejected or PaymentStatus.Reversed) ActiveReferenceKey = null;
    }
}

/// <summary>The receipt of a posted payment (one per payment). Reprints are duplicate copies and are counted.</summary>
public class PaymentReceipt : AuditableEntity<long>
{
    public long PaymentId { get; set; }
    public string ReceiptNumber { get; set; } = default!;
    public decimal Amount { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public string IssuedBy { get; set; } = default!;
    /// <summary>Number of copies produced; the first is the original, later ones are marked as duplicates.</summary>
    public int CopiesPrinted { get; set; }
    public DateTime? LastPrintedAtUtc { get; set; }
    public string? LastPrintedBy { get; set; }
}

/// <summary>
/// Part of a payment applied to a schedule line, held as advance, or paid back as a refund. Never deleted: corrections reverse (payment reversal)
/// or release (advance re-applied, refund of a cancelled agreement) the row. Active allocations of a payment never exceed its amount.
/// </summary>
public class PaymentAllocation : AuditableEntity<long>
{
    public long PaymentId { get; set; }
    public long AgreementId { get; set; }
    public AllocationKind Kind { get; set; }
    public AllocationMode Mode { get; set; }
    public long? ScheduleItemId { get; set; }
    public long? AgreementChargeId { get; set; }
    public long? RefundId { get; set; }
    public decimal Amount { get; set; }
    public AllocationStatus Status { get; set; } = AllocationStatus.Active;
    public DateTime AllocatedAtUtc { get; set; }
    public string AllocatedBy { get; set; } = default!;
    public DateTime? ClosedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public string? CloseReason { get; set; }
    public long? ReversalId { get; set; }
}

/// <summary>A verification decision on a submitted payment (bank statement, cheque clearing, proof check).</summary>
public class PaymentVerification : Entity<long>
{
    public long PaymentId { get; set; }
    public VerificationResult Result { get; set; }
    public Guid VerifiedByUserId { get; set; }
    public string VerifiedBy { get; set; } = default!;
    public DateTime VerifiedAtUtc { get; set; }
    public string Notes { get; set; } = default!;
    public string? BankReference { get; set; }
    public DateOnly? ClearedOn { get; set; }
}

/// <summary>Reversal of a posted payment (bounced cheque, error). The payment, its allocations and ledger entries are kept; offsetting entries are added.</summary>
public class PaymentReversal : Entity<long>
{
    public long PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = default!;
    public Guid ReversedByUserId { get; set; }
    public string ReversedBy { get; set; } = default!;
    public DateTime ReversedAtUtc { get; set; }
    public int AllocationsReversed { get; set; }
}

/// <summary>Money paid back to a customer from the agreement's credit (unallocated/advance money, or payments of a cancelled agreement). Maker-checker approved.</summary>
public class Refund : AuditableEntity<long>
{
    public string RefundNumber { get; set; } = default!;
    public string? IdempotencyKey { get; set; }
    public long ProjectId { get; set; }
    public long AgreementId { get; set; }
    public long CustomerId { get; set; }
    /// <summary>Optional Phase 4 refund decision taken when the booking was cancelled.</summary>
    public long? BookingRefundId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = default!;
    public PaymentRefundStatus Status { get; set; } = PaymentRefundStatus.Requested;
    public Guid RequestedByUserId { get; set; }
    public string RequestedBy { get; set; } = default!;
    public DateTime RequestedAtUtc { get; set; }
    public long? PaymentMethodId { get; set; }
    public DateOnly? PaidOn { get; set; }
    public string? PaymentReference { get; set; }
    public string? PaidBy { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class RefundApproval : Entity<long>
{
    public long RefundId { get; set; }
    public BookingDecision Decision { get; set; }
    public Guid ApproverUserId { get; set; }
    public string ApproverName { get; set; } = default!;
    public string? Comments { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

/// <summary>
/// Agreement-specific ledger (contractual account). Debits: installments that have fallen due (posted receivables - not the schedule), reversed payments,
/// refunds paid. Credits: payments received, waivers. Balance = debits - credits (positive: owed by the customer; negative: customer credit). Append-only.
/// </summary>
public class AgreementLedgerEntry : Entity<long>
{
    public long AgreementId { get; set; }
    public long ProjectId { get; set; }
    /// <summary>The account holder (primary customer) when the entry was posted.</summary>
    public long CustomerId { get; set; }
    public DateOnly EntryDate { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string PostedBy { get; set; } = default!;
    public LedgerEntryType EntryType { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Description { get; set; } = default!;
    public long? PaymentId { get; set; }
    public long? RefundId { get; set; }
    /// <summary>First schedule line of the chain this receivable belongs to (stable across schedule versions).</summary>
    public long? ScheduleItemRootId { get; set; }
    public long? ScheduleItemId { get; set; }
    /// <summary>Set for one-off entries (payment received/reversed, refund paid); unique, so a retried operation cannot post twice.</summary>
    public string? UniqueKey { get; set; }
}

/// <summary>Customer consolidated ledger: every agreement entry mirrored to the account holder, plus balance transfers when the holder changes.</summary>
public class CustomerLedgerEntry : Entity<long>
{
    public long CustomerId { get; set; }
    public long AgreementId { get; set; }
    public long ProjectId { get; set; }
    public long? AgreementLedgerEntryId { get; set; }
    public DateOnly EntryDate { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string PostedBy { get; set; } = default!;
    public LedgerEntryType EntryType { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Description { get; set; } = default!;
    public long? PaymentId { get; set; }
    public long? RefundId { get; set; }
}

/// <summary>
/// Accounting outbox (spec-issues #1/#7): a financial event waiting for the Phase 8 posting rules. Written in the same transaction as the event;
/// unique per event and source, so retries never create a second posting.
/// </summary>
public class AccountingPostingRequest : Entity<long>
{
    public AccountingEventType EventType { get; set; }
    public string SourceType { get; set; } = default!;
    public long SourceId { get; set; }
    public long ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly EventDate { get; set; }
    public string PayloadJson { get; set; } = default!;
    public AccountingPostingStatus Status { get; set; } = AccountingPostingStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

/// <summary>A recovery officer assigned to follow up an agreement. One active assignment per agreement; closed rows are kept.</summary>
public class RecoveryAssignment : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public long AgreementId { get; set; }
    public long CustomerId { get; set; }
    public Guid OfficerUserId { get; set; }
    public string OfficerName { get; set; } = default!;
    public DateOnly AssignedOn { get; set; }
    public string AssignedBy { get; set; } = default!;
    public RecoveryAssignmentStatus Status { get; set; } = RecoveryAssignmentStatus.Active;
    /// <summary>Equals AgreementId while active, null when closed. Unique.</summary>
    public long? ActiveAgreementId { get; set; }
    /// <summary>Overdue amount when assigned (baseline for performance reports).</summary>
    public decimal OverdueAtAssignment { get; set; }
    public string? Notes { get; set; }
    public DateOnly? ClosedOn { get; set; }
    public string? ClosedBy { get; set; }
    public string? CloseReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A recorded follow-up contact with a customer about dues. Append-only.</summary>
public class CustomerFollowUp : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public long AgreementId { get; set; }
    public long CustomerId { get; set; }
    public long? AssignmentId { get; set; }
    public Guid OfficerUserId { get; set; }
    public string OfficerName { get; set; } = default!;
    public DateOnly FollowUpDate { get; set; }
    public FollowUpMethod Method { get; set; }
    public FollowUpOutcome Outcome { get; set; }
    public decimal? PromisedAmount { get; set; }
    public DateOnly? PromisedDate { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public decimal OverdueAtFollowUp { get; set; }
    public string Notes { get; set; } = default!;
}
