using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Payments;

// =====================================================================================================
// payment methods and collection context
// =====================================================================================================
public sealed record PaymentMethodDto(long Id, string Code, string Name, PaymentMethodKind Kind, bool RequiresVerification, bool RequiresReference, bool IsActive);

public sealed record AgreementLookupDto(long AgreementId, string AgreementNumber, string BookingNumber, string ProjectCode, long CustomerId, string CustomerName, string CustomerCode);

public sealed record PartyDto(long CustomerId, string CustomerCode, string FullName, decimal SharePercent, bool IsPrimary);

public sealed record OpenLineDto(long ItemId, int LineNo, long ChargeId, string Description, DateOnly DueDate, decimal Amount, decimal Paid, decimal Waived, decimal Outstanding, bool IsOverdue);

public sealed record ChargeOptionDto(long ChargeId, string Description, ChargeCategory Category, decimal Outstanding);

/// <summary>What a cashier needs to record a payment against an agreement.</summary>
public sealed record CollectionContextDto(long AgreementId, string AgreementNumber, string BookingNumber, long ProjectId, string ProjectCode, AgreementStatus Status, string CurrencySymbol,
    string CurrencyCode, IReadOnlyList<PartyDto> Parties, IReadOnlyList<OpenLineDto> OpenLines, IReadOnlyList<ChargeOptionDto> Charges, decimal OutstandingDue, decimal Overdue,
    decimal NotYetDue, decimal CreditBalance, bool HasSchedule);

// =====================================================================================================
// payments
// =====================================================================================================
public sealed record AllocationLineInput(long ItemId, decimal Amount);

/// <summary>
/// Submits a payment. <see cref="IdempotencyKey"/> is required: a retry with the same key and values returns the original payment.
/// The allocation instruction is applied when the payment is posted (approval) unless the approver overrides it.
/// </summary>
public sealed record CreatePaymentRequest(
    long AgreementId, long? CustomerId, long PaymentMethodId, PaymentSource Source, decimal Amount, DateOnly PaymentDate, string? Reference, string? BankName,
    DateOnly? InstrumentDate, string? PayerName, string? Remarks, AllocationMode AllocationMode, long? ChargeId, IReadOnlyList<AllocationLineInput>? Lines,
    bool RemainderToAdvance, string IdempotencyKey);

public sealed record VerifyPaymentRequest(bool Verified, string Notes, string? BankReference, DateOnly? ClearedOn, string? RowVersion);

/// <summary>Approves and posts a payment. Leave AllocationMode null to use the instruction given at submission.</summary>
public sealed record ApprovePaymentRequest(string? Comments, AllocationMode? AllocationMode, long? ChargeId, IReadOnlyList<AllocationLineInput>? Lines, bool? RemainderToAdvance, string? RowVersion);

public sealed record RejectPaymentRequest(string Reason, string? RowVersion);

/// <summary>Allocates the unallocated balance of a posted payment (optionally re-applying the money held as advance).</summary>
public sealed record AllocatePaymentRequest(AllocationMode Mode, long? ChargeId, IReadOnlyList<AllocationLineInput>? Lines, bool IncludeAdvance, bool RemainderToAdvance, string? RowVersion);

public sealed record ReversePaymentRequest(string Reason, string? RowVersion);

public sealed class PaymentListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public long? CustomerId { get; set; }
    public PaymentStatus? Status { get; set; }
    public PaymentMethodKind? Method { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record PaymentListItemDto(long Id, string PaymentNumber, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName,
    string MethodName, PaymentMethodKind Method, PaymentSource Source, decimal Amount, DateOnly PaymentDate, string? Reference, PaymentStatus Status, decimal Allocated,
    decimal Advance, decimal Unallocated, string? ReceiptNumber, string SubmittedBy, DateTime SubmittedAtUtc);

public sealed record AllocationDto(long Id, AllocationKind Kind, AllocationMode Mode, long? ScheduleItemId, int? LineNo, string? Description, DateOnly? DueDate, long? ChargeId,
    decimal Amount, AllocationStatus Status, DateTime AllocatedAtUtc, string AllocatedBy, DateTime? ClosedAtUtc, string? ClosedBy, string? CloseReason, string? RefundNumber);

public sealed record VerificationDto(long Id, VerificationResult Result, string VerifiedBy, DateTime VerifiedAtUtc, string Notes, string? BankReference, DateOnly? ClearedOn);

public sealed record ReversalDto(long Id, decimal Amount, string Reason, string ReversedBy, DateTime ReversedAtUtc, int AllocationsReversed);

public sealed record ReceiptDto(long Id, string ReceiptNumber, long PaymentId, string PaymentNumber, decimal Amount, DateTime IssuedAtUtc, string IssuedBy, int CopiesPrinted,
    DateTime? LastPrintedAtUtc, string? LastPrintedBy, long CustomerId, string CustomerName, string AgreementNumber, string ProjectCode, PaymentStatus PaymentStatus, DateOnly PaymentDate,
    string MethodName);

public sealed record PaymentDetailDto(
    long Id, string PaymentNumber, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, long BookingId, string BookingNumber, long CustomerId, string CustomerCode,
    string CustomerName, long PaymentMethodId, string MethodName, PaymentMethodKind Method, PaymentSource Source, decimal Amount, string CurrencyCode, string CurrencySymbol, DateOnly PaymentDate,
    string? Reference, string? BankName, DateOnly? InstrumentDate, string? PayerName, string? Remarks, PaymentStatus Status, bool VerificationRequired,
    AllocationMode RequestedAllocationMode, long? RequestedChargeId, IReadOnlyList<AllocationLineInput> RequestedLines, bool RequestedRemainderToAdvance,
    string SubmittedBy, DateTime SubmittedAtUtc, string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime? PostedAtUtc, string? RejectedBy, DateTime? RejectedAtUtc, string? RejectReason,
    decimal Allocated, decimal Advance, decimal Unallocated, decimal Refunded, IReadOnlyList<AllocationDto> Allocations, IReadOnlyList<VerificationDto> Verifications, ReversalDto? Reversal,
    ReceiptDto? Receipt, string AccountingStatus, string RowVersion);

/// <summary>Open lines and balances for allocating a posted payment.</summary>
public sealed record AllocationOptionsDto(long PaymentId, string PaymentNumber, long AgreementId, decimal Unallocated, decimal AdvanceHeld, string CurrencySymbol, string RowVersion,
    IReadOnlyList<OpenLineDto> Lines, IReadOnlyList<ChargeOptionDto> Charges);

// =====================================================================================================
// receipts
// =====================================================================================================
public sealed class ReceiptListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? CustomerId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record ReceiptLine(string Label, string Value);

/// <summary>Everything printed on a receipt PDF.</summary>
public sealed record ReceiptDocument(
    string CompanyName, string? CompanyAddress, string? CompanyPhone, string ReceiptNumber, string CopyLabel, int CopyNumber, string PaymentNumber, DateOnly PaymentDate, DateTime IssuedAtUtc,
    string CustomerName, string CustomerCode, string AgreementNumber, string BookingNumber, string ProjectName, string MethodName, string? Reference, string? BankName, DateOnly? InstrumentDate,
    string CurrencySymbol, decimal Amount, string AmountInWords, PaymentStatus Status, IReadOnlyList<ReceiptLine> Allocations, IReadOnlyList<ReceiptLine> Verification,
    IReadOnlyList<ReceiptLine> ReversalHistory, string PrintedBy, DateTime PrintedAtUtc);

/// <summary>Renders a receipt as PDF (implemented in Infrastructure).</summary>
public interface IReceiptPdfRenderer
{
    byte[] Render(ReceiptDocument receipt);
}

// =====================================================================================================
// ledgers
// =====================================================================================================
public sealed record LedgerEntryDto(long Id, DateOnly EntryDate, DateTime PostedAtUtc, LedgerEntryType EntryType, string Description, decimal Debit, decimal Credit, decimal Balance,
    long AgreementId, string? AgreementNumber, long? PaymentId, string? PaymentNumber, long? RefundId, string? RefundNumber);

/// <summary>Contractual account of one agreement. Outstanding counts only installments that have fallen due (posted receivables); NotYetDue is the scheduled remainder.</summary>
public sealed record AgreementLedgerSummaryDto(
    decimal AgreementValue, decimal Discounts, decimal Charges, decimal ContractTotal, decimal ScheduledTotal, decimal DueToDate, decimal PaymentsReceived, decimal PaymentsReversed,
    decimal Waivers, decimal PenaltiesAccrued, decimal Refunds, decimal Outstanding, decimal Overdue, decimal NotYetDue, decimal AdvanceBalance, decimal UnallocatedBalance,
    decimal LedgerBalance, bool Reconciled);

public sealed record AgreementLedgerDto(long AgreementId, string AgreementNumber, string BookingNumber, long ProjectId, string ProjectCode, AgreementStatus Status, long HolderCustomerId,
    string HolderCustomerCode, string HolderCustomerName, string CurrencySymbol, IReadOnlyList<string> Properties, AgreementLedgerSummaryDto Summary, PagedResult<LedgerEntryDto> Entries);

public sealed record CustomerAgreementBalanceDto(long AgreementId, string AgreementNumber, string ProjectCode, AgreementStatus Status, bool IsHolder, decimal Balance, decimal Outstanding,
    decimal Overdue, decimal AdvanceBalance);

public sealed record CustomerLedgerDto(long CustomerId, string CustomerCode, string CustomerName, string CurrencySymbol, decimal TotalDebit, decimal TotalCredit, decimal Balance,
    decimal Outstanding, decimal Overdue, decimal AdvanceBalance, bool Reconciled, IReadOnlyList<CustomerAgreementBalanceDto> Agreements, PagedResult<LedgerEntryDto> Entries);

public sealed record PropertyStatementBookingDto(long BookingId, string BookingNumber, BookingStatus BookingStatus, DateOnly BookingDate, decimal LinePrice, IReadOnlyList<string> OtherProperties,
    AgreementLedgerDto? Ledger);

public sealed record PropertyStatementDto(long PropertyId, string PropertyCode, string ProjectCode, PropertyStatus Status, string CurrencySymbol, IReadOnlyList<PropertyStatementBookingDto> Bookings);

public sealed class LedgerPageRequest
{
    private int _page = 1;
    private int _pageSize = 50;
    public int Page { get => _page; set => _page = value < 1 ? 1 : value; }
    public int PageSize { get => _pageSize; set => _pageSize = value < 1 ? 50 : Math.Min(value, PagedRequest.MaxPageSize); }
}

// =====================================================================================================
// refunds
// =====================================================================================================
public sealed record CreateRefundRequest(long AgreementId, decimal Amount, string Reason, long? BookingRefundId, string? IdempotencyKey);
public sealed record ApproveRefundRequest(string? Comments, string? RowVersion);
public sealed record RejectRefundRequest(string Comments, string? RowVersion);
public sealed record CancelRefundRequest(string Reason, string? RowVersion);
public sealed record PayRefundRequest(long PaymentMethodId, DateOnly PaidOn, string? Reference, string? RowVersion);

public sealed class RefundListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public PaymentRefundStatus? Status { get; set; }
}

public sealed record RefundListItemDto(long Id, string RefundNumber, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName, decimal Amount,
    PaymentRefundStatus Status, string RequestedBy, DateTime RequestedAtUtc, DateOnly? PaidOn);

public sealed record RefundApprovalDto(long Id, BookingDecision Decision, string ApproverName, string? Comments, DateTime DecidedAtUtc);

public sealed record RefundDetailDto(long Id, string RefundNumber, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, AgreementStatus AgreementStatus, long CustomerId,
    string CustomerName, long? BookingRefundId, decimal? BookingRefundAmount, decimal Amount, string Reason, PaymentRefundStatus Status, string RequestedBy, DateTime RequestedAtUtc,
    string? MethodName, DateOnly? PaidOn, string? PaymentReference, string? PaidBy, DateTime? PaidAtUtc, string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason,
    decimal RefundableNow, string CurrencySymbol, IReadOnlyList<RefundApprovalDto> Approvals, IReadOnlyList<AllocationDto> Sources, string RowVersion);

public sealed record RefundableDto(long AgreementId, string AgreementNumber, AgreementStatus Status, decimal Refundable, decimal Reserved, decimal Available, string CurrencySymbol,
    IReadOnlyList<BookingRefundOptionDto> BookingRefunds);

public sealed record BookingRefundOptionDto(long Id, decimal Amount, string Reason, RefundStatus Status, decimal AlreadyRefunded);

// =====================================================================================================
// recovery
// =====================================================================================================
public sealed class DueListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? CustomerId { get; set; }
    public Guid? OfficerUserId { get; set; }
    /// <summary>Upcoming dues: days ahead (default 30). Overdue: minimum days overdue (default 1).</summary>
    public int? Days { get; set; }
}

public sealed record DueLineDto(long ItemId, long AgreementId, string AgreementNumber, string ProjectCode, long CustomerId, string CustomerName, string? CustomerPhone, int LineNo,
    string Description, DateOnly DueDate, DateOnly GraceEndDate, decimal Amount, decimal Paid, decimal Waived, decimal Outstanding, int DaysOverdue, decimal LateFee, string? OfficerName);

public sealed record AgingDto(decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);

public sealed record DueListDto(PagedResult<DueLineDto> Lines, decimal TotalOutstanding, AgingDto? Aging);

public sealed class DefaulterListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public int MinDaysOverdue { get; set; } = 30;
    public int MinInstallments { get; set; } = 1;
}

public sealed record DefaulterDto(long AgreementId, string AgreementNumber, string ProjectCode, long CustomerId, string CustomerName, string? CustomerPhone, int OverdueInstallments,
    decimal OverdueAmount, DateOnly OldestDueDate, int DaysOverdue, DateOnly? LastPaymentDate, string? OfficerName, DateOnly? LastFollowUpDate, FollowUpOutcome? LastOutcome);

public sealed record OfficerPerformanceRequest(DateOnly? From, DateOnly? To, long? ProjectId);

public sealed record OfficerPerformanceDto(Guid OfficerUserId, string OfficerName, int ActiveAssignments, int ClosedAssignments, int FollowUps, int Promises, decimal PromisedAmount,
    decimal Collected, decimal OverdueAtAssignment, decimal CurrentOverdue, decimal RecoveryRatePercent);

public sealed record RecoveryDashboardDto(decimal OverdueAmount, int OverdueAgreements, int OverdueInstallments, decimal UpcomingAmount30, int UpcomingInstallments30,
    int Defaulters90, decimal CollectedThisMonth, int PendingVerification, int PendingApproval, int UnassignedOverdueAgreements, int FollowUpsDue, AgingDto Aging, string CurrencySymbol,
    IReadOnlyList<DefaulterDto> TopDefaulters);

public sealed record CreateAssignmentRequest(long AgreementId, Guid OfficerUserId, string? Notes);
public sealed record CloseAssignmentRequest(string Reason, string? RowVersion);

public sealed class AssignmentListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public Guid? OfficerUserId { get; set; }
    public RecoveryAssignmentStatus? Status { get; set; }
    public long? AgreementId { get; set; }
}

public sealed record AssignmentDto(long Id, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName, Guid OfficerUserId,
    string OfficerName, DateOnly AssignedOn, string AssignedBy, RecoveryAssignmentStatus Status, decimal OverdueAtAssignment, decimal CurrentOverdue, string? Notes, DateOnly? ClosedOn,
    string? ClosedBy, string? CloseReason, string RowVersion);

public sealed record OfficerOptionDto(Guid UserId, string FullName);

public sealed record CreateFollowUpRequest(long AgreementId, DateOnly FollowUpDate, FollowUpMethod Method, FollowUpOutcome Outcome, decimal? PromisedAmount, DateOnly? PromisedDate,
    DateOnly? NextFollowUpDate, string Notes);

public sealed class FollowUpListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public long? CustomerId { get; set; }
    public Guid? OfficerUserId { get; set; }
    public FollowUpOutcome? Outcome { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record FollowUpDto(long Id, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName, Guid OfficerUserId,
    string OfficerName, DateOnly FollowUpDate, FollowUpMethod Method, FollowUpOutcome Outcome, decimal? PromisedAmount, DateOnly? PromisedDate, DateOnly? NextFollowUpDate,
    decimal OverdueAtFollowUp, string Notes, DateTime CreatedAtUtc);

// =====================================================================================================
// validators
// =====================================================================================================
internal static class MoneyRules
{
    public const decimal Max = 9_999_999_999_999_999.99m;
    public static bool TwoDecimals(decimal v) => v == decimal.Round(v, 2);
}

public class AllocationLineInputValidator : AbstractValidator<AllocationLineInput>
{
    public AllocationLineInputValidator()
    {
        RuleFor(x => x.ItemId).GreaterThan(0);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
    }
}

public class CreatePaymentRequestValidator : AbstractValidator<CreatePaymentRequest>
{
    public CreatePaymentRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.BankName).MaximumLength(150);
        RuleFor(x => x.PayerName).MaximumLength(200);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.AllocationMode).IsInEnum();
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ChargeId).NotNull().When(x => x.AllocationMode == AllocationMode.ChargeSpecific).WithMessage("Select the charge to allocate to.");
        RuleFor(x => x.Lines).NotEmpty().When(x => x.AllocationMode is AllocationMode.Manual or AllocationMode.SpecificInstallment).WithMessage("Enter the installments and amounts.");
        RuleFor(x => x.Lines!.Count).Equal(1).When(x => x.AllocationMode == AllocationMode.SpecificInstallment && x.Lines is { Count: > 0 })
            .WithName("Lines").WithMessage("Specific installment allocation applies to exactly one installment.");
        RuleForEach(x => x.Lines).SetValidator(new AllocationLineInputValidator());
        RuleFor(x => x).Must(x => x.Lines is null || x.Lines.Sum(l => l.Amount) <= x.Amount).WithName("Lines").WithMessage("The allocation exceeds the payment amount.");
    }
}

public class VerifyPaymentRequestValidator : AbstractValidator<VerifyPaymentRequest>
{
    public VerifyPaymentRequestValidator()
    {
        RuleFor(x => x.Notes).NotEmpty().MinimumLength(3).MaximumLength(1000);
        RuleFor(x => x.BankReference).MaximumLength(100);
    }
}

public class RejectPaymentRequestValidator : AbstractValidator<RejectPaymentRequest>
{
    public RejectPaymentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class ReversePaymentRequestValidator : AbstractValidator<ReversePaymentRequest>
{
    public ReversePaymentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class AllocatePaymentRequestValidator : AbstractValidator<AllocatePaymentRequest>
{
    public AllocatePaymentRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().NotEqual(AllocationMode.Unallocated).WithMessage("Choose how to allocate the balance.");
        RuleFor(x => x.ChargeId).NotNull().When(x => x.Mode == AllocationMode.ChargeSpecific).WithMessage("Select the charge to allocate to.");
        RuleFor(x => x.Lines).NotEmpty().When(x => x.Mode is AllocationMode.Manual or AllocationMode.SpecificInstallment).WithMessage("Enter the installments and amounts.");
        RuleForEach(x => x.Lines).SetValidator(new AllocationLineInputValidator());
    }
}

public class CreateRefundRequestValidator : AbstractValidator<CreateRefundRequest>
{
    public CreateRefundRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}

public class RejectRefundRequestValidator : AbstractValidator<RejectRefundRequest>
{
    public RejectRefundRequestValidator() => RuleFor(x => x.Comments).NotEmpty().MinimumLength(3).MaximumLength(1000);
}

public class CancelRefundRequestValidator : AbstractValidator<CancelRefundRequest>
{
    public CancelRefundRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class PayRefundRequestValidator : AbstractValidator<PayRefundRequest>
{
    public PayRefundRequestValidator()
    {
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

public class CreateAssignmentRequestValidator : AbstractValidator<CreateAssignmentRequest>
{
    public CreateAssignmentRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.OfficerUserId).NotEmpty();
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public class CloseAssignmentRequestValidator : AbstractValidator<CloseAssignmentRequest>
{
    public CloseAssignmentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(1000);
}

public class CreateFollowUpRequestValidator : AbstractValidator<CreateFollowUpRequest>
{
    public CreateFollowUpRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Notes).NotEmpty().MinimumLength(3).MaximumLength(2000);
        RuleFor(x => x.PromisedAmount).Cascade(CascadeMode.Stop).NotNull().WithMessage("Enter the promised amount.").GreaterThan(0m).WithMessage("Enter the promised amount.")
            .When(x => x.Outcome == FollowUpOutcome.PromiseToPay);
        RuleFor(x => x.PromisedDate).NotNull().When(x => x.Outcome == FollowUpOutcome.PromiseToPay).WithMessage("Enter the promised payment date.");
        RuleFor(x => x.PromisedAmount).Must(v => v is null || MoneyRules.TwoDecimals(v.Value)).WithMessage("At most two decimal places.");
        RuleFor(x => x.NextFollowUpDate).GreaterThanOrEqualTo(x => x.FollowUpDate).When(x => x.NextFollowUpDate is not null).WithMessage("The next follow-up cannot be before this follow-up.");
        RuleFor(x => x.PromisedDate).GreaterThanOrEqualTo(x => x.FollowUpDate).When(x => x.PromisedDate is not null).WithMessage("The promised date cannot be before the follow-up.");
    }
}
