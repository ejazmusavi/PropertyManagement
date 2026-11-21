using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Portal;

// =====================================================================================================
// customer application (what a logged-in customer sees). Nothing here carries a customer id the client chose.
// =====================================================================================================
public sealed record PortalDueDto(long AgreementId, string AgreementNumber, long ItemId, int LineNo, string Description, DateOnly DueDate, decimal Amount, decimal Outstanding, bool IsOverdue);

public sealed record PortalDashboardDto(
    string CustomerName, string CustomerCode, string CurrencySymbol, int TotalProperties, int ActiveAgreements, decimal TotalAgreementValue, decimal PaidAmount, decimal OutstandingAmount,
    decimal OverdueAmount, decimal NotYetDueAmount, decimal AdvanceBalance, int UnreadNotifications, bool OnlinePaymentsEnabled, IReadOnlyList<PortalDueDto> UpcomingDues);

public sealed record PortalPropertyDto(
    long PropertyId, string PropertyCode, string PropertyNumber, string ProjectCode, string ProjectName, string Category, string PropertyType, decimal Size, MeasurementUnit Unit,
    decimal SharePercent, long AgreementId, string AgreementNumber, string BookingNumber, BookingStatus BookingStatus, AgreementStatus AgreementStatus, PaymentMode PaymentMode,
    DateOnly BookingDate, decimal LinePrice);

public sealed record PortalAgreementPropertyDto(long PropertyId, string PropertyCode, string PropertyNumber, decimal Size, MeasurementUnit Unit, decimal LinePrice);

public sealed record PortalAgreementDto(
    long AgreementId, string AgreementNumber, AgreementStatus Status, int VersionNo, DateTime GeneratedAtUtc, string BookingNumber, BookingStatus BookingStatus, DateOnly BookingDate,
    PaymentMode PaymentMode, string ProjectCode, string ProjectName, string CurrencySymbol, decimal SharePercent, bool IsHolder, IReadOnlyList<PortalAgreementPropertyDto> Properties,
    AgreementLedgerSummaryDto Summary, string? AgreementText);

public sealed record PortalInstallmentDto(
    long ItemId, int LineNo, int InstallmentNo, int InstallmentCount, string Description, DateOnly DueDate, decimal Amount, decimal Paid, decimal Waived, decimal Outstanding,
    DateOnly? LastPaymentDate, string Status, bool IsOverdue);

public sealed record PortalScheduleSummaryDto(
    decimal TotalAmount, decimal Paid, decimal Waived, decimal Outstanding, decimal OutstandingDue, decimal Overdue, decimal NotYetDue, int PaidCount, int OutstandingCount, int OverdueCount);

public sealed record PortalScheduleDto(
    long AgreementId, string AgreementNumber, AgreementStatus AgreementStatus, string CurrencySymbol, PortalScheduleSummaryDto Summary, IReadOnlyList<PortalInstallmentDto> Items);

public sealed record PortalPaymentDto(
    long Id, string PaymentNumber, long AgreementId, string AgreementNumber, decimal Amount, string CurrencyCode, DateOnly PaymentDate, string MethodName, string? Reference,
    PaymentStatus Status, PaymentSource Source, DateTime SubmittedAtUtc, long? ReceiptId, string? ReceiptNumber, string? RejectReason, int ProofCount);

public sealed record PortalAllocationDto(string Description, DateOnly? DueDate, decimal Amount, string Status);

public sealed record PortalProofDto(long Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc);

public sealed record PortalPaymentDetailDto(PortalPaymentDto Payment, string CurrencySymbol, decimal Allocated, decimal Advance, decimal Unallocated,
    IReadOnlyList<PortalAllocationDto> Allocations, IReadOnlyList<PortalProofDto> Proofs, string? VerificationNote);

public sealed class PortalPaymentListRequest : PagedRequest
{
    public long? AgreementId { get; set; }
}

public sealed record PortalPaymentMethodDto(long Id, string Code, string Name, PaymentMethodKind Kind, bool RequiresReference);

public sealed record PortalBankAccountDto(string BankName, string? Branch, string AccountTitle, string AccountNumber, string? Iban, string CurrencyCode);

public sealed record PortalPaymentInstructionsDto(string? Note, IReadOnlyList<PortalBankAccountDto> BankAccounts, IReadOnlyList<PortalPaymentMethodDto> Methods, bool OnlinePaymentsEnabled);

/// <summary>Payment proof submitted by the customer (sent as multipart with the proof file). The payer is the authenticated customer, never a client value.</summary>
public sealed record SubmitPaymentProofRequest(long AgreementId, long PaymentMethodId, decimal Amount, DateOnly PaymentDate, string? Reference, string? BankName, DateOnly? InstrumentDate,
    string? Remarks, string IdempotencyKey);

public sealed record InitiateOnlinePaymentRequest(long AgreementId, decimal Amount, string IdempotencyKey);

public sealed record PortalOnlinePaymentDto(long Id, string TransactionNumber, long AgreementId, decimal Amount, string CurrencyCode, GatewayTransactionStatus Status, string? RedirectUrl,
    string? FailureReason, DateTime InitiatedAtUtc, DateTime? CompletedAtUtc, string? PaymentNumber);

public sealed record PortalNotificationDto(long Id, NotificationKind Kind, string Title, string Message, string? RelatedType, long? RelatedId, DateTime CreatedAtUtc, bool IsRead);

public sealed record PortalNotificationListDto(PagedResult<PortalNotificationDto> Notifications, int Unread);

public sealed record PortalAnnouncementDto(long Id, string Title, string Body, DateTime PublishedAtUtc, DateTime? ExpiresAtUtc);

public sealed record PortalSupportMessageDto(long Id, SupportAuthorType AuthorType, string AuthorName, string Body, DateTime CreatedAtUtc);

public sealed record PortalSupportRequestDto(long Id, string RequestNumber, SupportCategory Category, string Subject, SupportRequestStatus Status, long? AgreementId, string? AgreementNumber,
    DateTime CreatedAtUtc, DateTime LastActivityAtUtc, int MessageCount);

public sealed record PortalSupportRequestDetailDto(PortalSupportRequestDto Request, IReadOnlyList<PortalSupportMessageDto> Messages, string RowVersion);

public sealed record CreateSupportRequestRequest(SupportCategory Category, string Subject, string Message, long? AgreementId);

public sealed record SupportReplyRequest(string Message);

public sealed record PortalProfileDto(string CustomerCode, string FullName, string IdentityNumberMasked, string Mobile, string? Email, string? Address, string LoginEmail);

public sealed record UpdatePortalProfileRequest(string Mobile, string? Email, string? Address);

public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

// =====================================================================================================
// staff side
// =====================================================================================================
public sealed record CustomerAccountDto(long Id, long CustomerId, string CustomerName, string CustomerCode, Guid UserId, string LoginEmail, bool IsActive, string? DisabledReason, DateTime? DisabledAtUtc,
    DateTime? LastLoginAtUtc, string RowVersion);

public sealed record CreateCustomerAccountRequest(string Email, string Password);

public sealed record DisableCustomerAccountRequest(string Reason, string? RowVersion);

public sealed record ResetCustomerPasswordRequest(string NewPassword);

public sealed record AnnouncementDto(long Id, string Title, string Body, long? ProjectId, string? ProjectCode, DateTime PublishFromUtc, DateTime? ExpiresAtUtc, bool IsActive, string RowVersion);

public sealed record SaveAnnouncementRequest(string Title, string Body, long? ProjectId, DateTime? PublishFromUtc, DateTime? ExpiresAtUtc, bool IsActive, string? RowVersion);

public sealed record PaymentInstructionsNoteDto(string? Note);

public sealed class SupportListRequest : PagedRequest
{
    public SupportRequestStatus? Status { get; set; }
    public long? CustomerId { get; set; }
}

public sealed record SupportListItemDto(long Id, string RequestNumber, long CustomerId, string CustomerName, string CustomerCode, SupportCategory Category, string Subject,
    SupportRequestStatus Status, string? AssignedTo, DateTime CreatedAtUtc, DateTime LastActivityAtUtc);

public sealed record SupportDetailDto(SupportListItemDto Request, long? AgreementId, string? AgreementNumber, IReadOnlyList<PortalSupportMessageDto> Messages, string RowVersion);

public sealed record SupportStaffReplyRequest(string Message, SupportRequestStatus? Status, string? RowVersion);

public sealed record SupportStatusRequest(SupportRequestStatus Status, string? RowVersion);

public sealed record PaymentProofAdminDto(long Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc, string UploadedBy);

public sealed record CreateNotificationRequest(long CustomerId, string Title, string Message);

// =====================================================================================================
// validators
// =====================================================================================================
public class SubmitPaymentProofRequestValidator : AbstractValidator<SubmitPaymentProofRequest>
{
    public SubmitPaymentProofRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Amount).GreaterThan(0m).Must(PaymentRules.TwoDecimals).WithMessage("The amount can have at most two decimals.");
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.BankName).MaximumLength(150);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class InitiateOnlinePaymentRequestValidator : AbstractValidator<InitiateOnlinePaymentRequest>
{
    public InitiateOnlinePaymentRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.Amount).GreaterThan(0m).Must(PaymentRules.TwoDecimals).WithMessage("The amount can have at most two decimals.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class CreateSupportRequestRequestValidator : AbstractValidator<CreateSupportRequestRequest>
{
    public CreateSupportRequestRequestValidator()
    {
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Subject).NotEmpty().MinimumLength(3).MaximumLength(200);
        RuleFor(x => x.Message).NotEmpty().MinimumLength(3).MaximumLength(4000);
    }
}

public class SupportReplyRequestValidator : AbstractValidator<SupportReplyRequest>
{
    public SupportReplyRequestValidator() => RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
}

public class SupportStaffReplyRequestValidator : AbstractValidator<SupportStaffReplyRequest>
{
    public SupportStaffReplyRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
    }
}

public class UpdatePortalProfileRequestValidator : AbstractValidator<UpdatePortalProfileRequest>
{
    public UpdatePortalProfileRequestValidator()
    {
        RuleFor(x => x.Mobile).NotEmpty().MaximumLength(30).Matches(@"^[0-9+\-\s()]{7,30}$").WithMessage("Enter a valid mobile number.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(500);
    }
}

public class ChangePasswordRequestDtoValidator : AbstractValidator<ChangePasswordRequestDto>
{
    public ChangePasswordRequestDtoValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(12).MaximumLength(128);
    }
}

public class CreateCustomerAccountRequestValidator : AbstractValidator<CreateCustomerAccountRequest>
{
    public CreateCustomerAccountRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
    }
}

public class DisableCustomerAccountRequestValidator : AbstractValidator<DisableCustomerAccountRequest>
{
    public DisableCustomerAccountRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(500);
}

public class ResetCustomerPasswordRequestValidator : AbstractValidator<ResetCustomerPasswordRequest>
{
    public ResetCustomerPasswordRequestValidator() => RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(12).MaximumLength(128);
}

public class SaveAnnouncementRequestValidator : AbstractValidator<SaveAnnouncementRequest>
{
    public SaveAnnouncementRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.ExpiresAtUtc).GreaterThan(x => x.PublishFromUtc).When(x => x.ExpiresAtUtc is not null && x.PublishFromUtc is not null)
            .WithMessage("The expiry must be after the publish time.");
    }
}

public class CreateNotificationRequestValidator : AbstractValidator<CreateNotificationRequest>
{
    public CreateNotificationRequestValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThan(0);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
    }
}

internal static class PaymentRules
{
    public static bool TwoDecimals(decimal v) => v == decimal.Round(v, 2);
}
