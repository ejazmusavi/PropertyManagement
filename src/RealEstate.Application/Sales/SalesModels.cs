using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Sales;

namespace RealEstate.Application.Sales;

public sealed record BookingPropertyInput(long PropertyId, decimal ExtraDiscount, string? ExtraDiscountReason);
/// <summary>The first owner in the list is the primary owner (also used for customer-specific pricing).</summary>
public sealed record OwnerInput(long CustomerId, decimal SharePercent);
public sealed record ChargeInput(BookingChargeType ChargeType, string Description, decimal Amount);

public sealed record CreateBookingRequest(long ProjectId, DateOnly? BookingDate, PaymentMode PaymentMode, IReadOnlyList<BookingPropertyInput> Properties, IReadOnlyList<OwnerInput> Owners,
    IReadOnlyList<ChargeInput>? Charges, decimal BookingAmount, bool AuthorizeOutstandingBalance, string? BalanceAuthorizationReason, string? Remarks, bool SubmitForApproval, string? IdempotencyKey);

public sealed record SubmitBookingRequest(string? RowVersion);
public sealed record ApproveBookingRequest(string? Comments, string? RowVersion);
public sealed record RejectBookingRequest(string Comments, string? RowVersion);
public sealed record CancelBookingRequest(string Reason, decimal? RefundAmount, string? RefundReason, string? RowVersion);
public sealed record TransferBookingRequest(string Reason, IReadOnlyList<OwnerInput> Owners, string? RowVersion);
public sealed record DecideRefundRequest(bool Approve, string? Comments, string? RowVersion);

public sealed class BookingListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public BookingStatus? Status { get; set; }
    public PaymentMode? PaymentMode { get; set; }
    public long? CustomerId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record BookingListItemDto(long Id, string BookingNumber, long ProjectId, string ProjectCode, DateOnly BookingDate, BookingStatus Status, PaymentMode PaymentMode,
    string? PrimaryCustomer, int OwnerCount, string Properties, decimal TotalPayable, string? SubmittedBy);

public sealed record BookingPropertyDto(long Id, long PropertyId, string PropertyCode, string PropertyNumber, decimal Size, MeasurementUnit Unit, decimal BasePrice, decimal TotalPremiums,
    decimal TotalDiscounts, decimal TotalPricingCharges, bool IsOverridden, decimal FinalPrice, decimal ExtraDiscount, string? ExtraDiscountReason, decimal LinePrice,
    string SnapshotHash, DateOnly PricedAsOf, DateTime PricedAtUtc, PropertyStatus PropertyStatus);

public sealed record BookingCustomerDto(long CustomerId, string CustomerCode, string FullName, decimal SharePercent, bool IsPrimary, bool IsActive, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, string? EndReason);
public sealed record BookingChargeDto(BookingChargeType ChargeType, string Description, decimal Amount);
public sealed record BookingApprovalDto(BookingDecision Decision, string ApproverName, string? Comments, DateTime DecidedAtUtc);
public sealed record BookingStatusHistoryDto(BookingStatus? FromStatus, BookingStatus ToStatus, string Reason, DateTime ChangedAtUtc, string? ChangedBy);
public sealed record BookingRefundDto(long Id, decimal Amount, string Reason, RefundStatus Status, string RequestedBy, string? DecidedBy, DateTime? DecidedAtUtc, string? DecisionComments, DateTime CreatedAtUtc, string RowVersion);

public sealed record BookingDetailDto(long Id, string BookingNumber, long ProjectId, string ProjectCode, string ProjectName, DateOnly BookingDate, PaymentMode PaymentMode, BookingStatus Status,
    decimal TotalPrice, decimal TotalExtraDiscount, decimal TotalCharges, decimal TotalPayable, decimal BookingAmount, decimal BalanceAfterBooking,
    string? BalanceAuthorizedBy, string? BalanceAuthorizationReason, string? Remarks,
    string? SubmittedBy, DateTime? SubmittedAtUtc, string? ConfirmedBy, DateTime? ConfirmedAtUtc, string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason,
    IReadOnlyList<BookingPropertyDto> Properties, IReadOnlyList<BookingCustomerDto> Customers, IReadOnlyList<BookingChargeDto> Charges,
    IReadOnlyList<BookingApprovalDto> Approvals, IReadOnlyList<BookingStatusHistoryDto> History, IReadOnlyList<BookingRefundDto> Refunds,
    long? AgreementId, string? AgreementNumber, AgreementStatus? AgreementStatus, int? AgreementVersion, int OwnershipRevision, string RowVersion,
    DateTime CreatedAtUtc, string? CreatedBy);

public sealed record AgreementVersionDto(int VersionNo, string Reason, DateTime CreatedAtUtc, string? CreatedBy, string Sha256);
public sealed record AgreementAmendmentDto(AmendmentType AmendmentType, int FromVersionNo, int ToVersionNo, string Reason, string Details, string ApprovedBy, DateTime CreatedAtUtc);
public sealed record AgreementDocumentDto(long Id, string Title, string FileName, string DocumentType, string ContentType, long SizeBytes, DateTime UploadedAtUtc, string? UploadedBy);

public sealed record AgreementDto(long Id, string AgreementNumber, long BookingId, string BookingNumber, AgreementStatus Status, int CurrentVersionNo, DateTime GeneratedAtUtc,
    string? CancelReason, int ViewingVersionNo, string Content, bool ContentVerified, string ReceiptContent, IReadOnlyList<AgreementVersionDto> Versions,
    IReadOnlyList<AgreementAmendmentDto> Amendments, IReadOnlyList<AgreementDocumentDto> Documents);

internal static class SalesRuleSets
{
    public static void Owners<T>(AbstractValidator<T> v, Func<T, IReadOnlyList<OwnerInput>?> owners)
    {
        v.RuleFor(x => owners(x)).Custom((list, ctx) =>
        {
            if (list is null || list.Count == 0) { ctx.AddFailure("Owners", "At least one owner is required."); return; }
            if (list.Count > 10) ctx.AddFailure("Owners", "At most 10 owners are allowed.");
            if (list.Any(o => o.CustomerId <= 0)) ctx.AddFailure("Owners", "Choose a customer for every owner row.");
            if (list.GroupBy(o => o.CustomerId).Any(g => g.Count() > 1)) ctx.AddFailure("Owners", "A customer can appear only once.");
            try { BookingRules.ValidateOwnership(list.Select(o => o.SharePercent).ToList()); }
            catch (DomainException ex) { ctx.AddFailure("Owners", ex.Message); }
        });
    }
}

public class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0).WithMessage("Choose a project.");
        RuleFor(x => x.PaymentMode).IsInEnum();
        RuleFor(x => x.BookingAmount).GreaterThanOrEqualTo(0).Must(a => decimal.Round(a, 2) == a).WithMessage("The booking amount must be zero or more with at most 2 decimals.");
        RuleFor(x => x.Remarks).MaximumLength(2000);
        RuleFor(x => x.BalanceAuthorizationReason).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(64);
        RuleFor(x => x.Properties).Custom((list, ctx) =>
        {
            if (list is null || list.Count == 0) { ctx.AddFailure("Properties", "Select at least one property."); return; }
            if (list.Count > 10) ctx.AddFailure("Properties", "A booking can include at most 10 properties.");
            if (list.Any(p => p.PropertyId <= 0)) ctx.AddFailure("Properties", "Invalid property.");
            if (list.GroupBy(p => p.PropertyId).Any(g => g.Count() > 1)) ctx.AddFailure("Properties", "A property can be selected only once.");
            foreach (var p in list)
            {
                if (p.ExtraDiscount < 0 || decimal.Round(p.ExtraDiscount, 2) != p.ExtraDiscount) ctx.AddFailure("Properties", "A discount must be zero or more with at most 2 decimals.");
                if (p.ExtraDiscount > 0 && (p.ExtraDiscountReason?.Trim().Length ?? 0) < 5) ctx.AddFailure("Properties", "A reason of at least 5 characters is required for a discount.");
            }
        });
        SalesRuleSets.Owners(this, x => x.Owners);
        RuleFor(x => x.Charges).Custom((list, ctx) =>
        {
            if (list is null) return;
            if (list.Count > 20) ctx.AddFailure("Charges", "At most 20 charges are allowed.");
            foreach (var c in list)
            {
                if (!Enum.IsDefined(c.ChargeType)) ctx.AddFailure("Charges", "Unknown charge type.");
                if (string.IsNullOrWhiteSpace(c.Description) || c.Description.Length > 200) ctx.AddFailure("Charges", "Each charge needs a description of at most 200 characters.");
                if (c.Amount <= 0 || decimal.Round(c.Amount, 2) != c.Amount) ctx.AddFailure("Charges", "A charge must be greater than zero with at most 2 decimals.");
            }
        });
    }
}

public class CancelBookingRequestValidator : AbstractValidator<CancelBookingRequest>
{
    public CancelBookingRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => (r?.Trim().Length ?? 0) >= 5).WithMessage("A reason of at least 5 characters is required.").MaximumLength(1000);
        RuleFor(x => x.RefundAmount).Must(a => a is null || (a > 0 && decimal.Round(a.Value, 2) == a)).WithMessage("The refund must be greater than zero with at most 2 decimals.");
        RuleFor(x => x.RefundReason).MaximumLength(1000);
    }
}

public class TransferBookingRequestValidator : AbstractValidator<TransferBookingRequest>
{
    public TransferBookingRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => (r?.Trim().Length ?? 0) >= 5).WithMessage("A reason of at least 5 characters is required.").MaximumLength(1000);
        SalesRuleSets.Owners(this, x => x.Owners);
    }
}

public class RejectBookingRequestValidator : AbstractValidator<RejectBookingRequest>
{
    public RejectBookingRequestValidator() => RuleFor(x => x.Comments).Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("A comment is required when rejecting.").MaximumLength(1000);
}

/// <summary>What the booking screens need to know about the caller and the company currency.</summary>
public sealed record BookingSettingsDto(string CurrencySymbol, int DecimalPlaces, bool CanDiscount, bool CanAuthorizeBalance, bool CanRefund, Guid? UserId, string? UserName);
