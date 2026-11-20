using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Expenses;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.GovernmentFees;

// ---------- fee types ----------
public sealed record GovernmentFeeTypeDto(long Id, long? ProjectId, string? ProjectCode, string Code, string Name, GovernmentFeeKind Kind, FeeCalculationMethod Method, decimal? FixedAmount,
    decimal? Rate, FeeBearer DefaultBearer, string? Description, bool IsSystem, bool IsActive, string RowVersion);

public sealed record SaveGovernmentFeeTypeRequest(long? ProjectId, string Code, string Name, GovernmentFeeKind Kind, FeeCalculationMethod Method, decimal? FixedAmount, decimal? Rate,
    FeeBearer DefaultBearer, string? Description, bool IsActive = true, string? RowVersion = null);

// ---------- assessments ----------
public class GovernmentFeeListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public long? CustomerId { get; set; }
    public long? FeeTypeId { get; set; }
    public GovernmentFeeAssessmentStatus? Status { get; set; }
    /// <summary>Customer share not fully collected.</summary>
    public bool CollectionPending { get; set; }
    /// <summary>Government payable not fully paid.</summary>
    public bool PaymentPending { get; set; }
}

public sealed record GovernmentFeeListItemDto(long Id, string AssessmentNumber, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName,
    long FeeTypeId, string FeeTypeName, GovernmentFeeKind Kind, decimal Amount, decimal CustomerLiability, decimal CompanyBorne, decimal Collected, decimal CustomerOutstanding,
    decimal PaidToGovernment, decimal GovernmentPayable, GovernmentFeeAssessmentStatus Status, DateOnly? DueDate);

public sealed record CreateGovernmentFeeAssessmentRequest(long AgreementId, long FeeTypeId, decimal? BasisAmount, decimal? Amount, decimal? CompanyBorneAmount, DateOnly? DueDate,
    string? Reference, string? Remarks, string IdempotencyKey);

public sealed record CancelGovernmentFeeAssessmentRequest(string Reason, string? RowVersion);
public sealed record FeeCollectionRequest(decimal Amount, long PaymentMethodId, DateOnly CollectionDate, string? Reference, string? Remarks, string IdempotencyKey, string? RowVersion = null);
public sealed record GovernmentPaymentRequest(decimal Amount, long PaymentMethodId, DateOnly PaymentDate, string? Reference, string? Remarks, string IdempotencyKey, string? RowVersion = null);
public sealed record ReverseFeeEntryRequest(string Reason);

public sealed record GovernmentFeeCollectionDto(long Id, string CollectionNumber, decimal Amount, DateOnly CollectionDate, string MethodName, string? Reference, string? Remarks, FeeEntryStatus Status,
    string? CreatedBy, DateTime CreatedAtUtc, string? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason);

public sealed record GovernmentFeePaymentDto(long Id, string PaymentNumber, long AssessmentId, string AssessmentNumber, long ProjectId, string ProjectCode, decimal Amount, DateOnly PaymentDate,
    string MethodName, string? Reference, string? Remarks, FeeEntryStatus Status, string? CreatedBy, DateTime CreatedAtUtc, string? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason);

public sealed record GovernmentFeeDocumentDto(long Id, string Title, string FileName, GovernmentFeeDocumentType DocumentType, string ContentType, long SizeBytes, DateTime CreatedAtUtc, string? CreatedBy);

public sealed record GovernmentFeeDetailDto(long Id, string AssessmentNumber, long ProjectId, string ProjectCode, long AgreementId, string AgreementNumber, long CustomerId, string CustomerName,
    long FeeTypeId, string FeeTypeName, GovernmentFeeKind Kind, FeeCalculationMethod Method, decimal? BasisAmount, decimal Amount, decimal CustomerLiability, decimal CompanyBorne,
    decimal Collected, decimal CustomerOutstanding, decimal PaidToGovernment, decimal GovernmentPayable, GovernmentFeeAssessmentStatus Status, DateOnly? DueDate, string? Reference, string? Remarks,
    string? CreatedBy, DateTime CreatedAtUtc, string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason, string CurrencySymbol,
    IReadOnlyList<GovernmentFeeCollectionDto> Collections, IReadOnlyList<GovernmentFeePaymentDto> Payments, IReadOnlyList<GovernmentFeeDocumentDto> Documents, string RowVersion);

public class GovernmentPaymentListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AssessmentId { get; set; }
    public FeeEntryStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

/// <summary>The five separate positions of spec 7.4 (customer liability, customer collection, government payable, actual payment, company-borne expense).</summary>
public sealed record GovernmentFeeSummaryDto(long? ProjectId, string CurrencySymbol, int Assessments, decimal TotalAssessed, decimal CustomerLiability, decimal CustomerCollected, decimal CustomerOutstanding,
    decimal CompanyBorne, decimal PaidToGovernment, decimal GovernmentPayable, IReadOnlyList<ExpenseAmountRowDto> ByFeeType);

// ---------- validators ----------
public class SaveGovernmentFeeTypeRequestValidator : AbstractValidator<SaveGovernmentFeeTypeRequest>
{
    public SaveGovernmentFeeTypeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.DefaultBearer).IsInEnum();
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.FixedAmount).NotNull().WithMessage("Enter the fixed amount.").When(x => x.Method == FeeCalculationMethod.Fixed);
        RuleFor(x => x.FixedAmount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).When(x => x.Method == FeeCalculationMethod.Fixed && x.FixedAmount.HasValue);
        RuleFor(x => x.Rate).NotNull().WithMessage("Enter a rate above 0 and up to 100 percent.").When(x => x.Method == FeeCalculationMethod.PercentOfBasis);
        RuleFor(x => x.Rate).GreaterThan(0m).LessThanOrEqualTo(100m).WithMessage("Enter a rate above 0 and up to 100 percent.").When(x => x.Method == FeeCalculationMethod.PercentOfBasis && x.Rate.HasValue);
        RuleFor(x => x.FixedAmount).Must(v => v is null || MoneyRules.TwoDecimals(v.Value)).WithMessage("At most two decimal places.");
        RuleFor(x => x.Rate).Must(v => v is null || v.Value == decimal.Round(v.Value, 4)).WithMessage("At most four decimal places.");
    }
}

public class CreateGovernmentFeeAssessmentRequestValidator : AbstractValidator<CreateGovernmentFeeAssessmentRequest>
{
    public CreateGovernmentFeeAssessmentRequestValidator()
    {
        RuleFor(x => x.AgreementId).GreaterThan(0);
        RuleFor(x => x.FeeTypeId).GreaterThan(0);
        RuleFor(x => x.BasisAmount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).When(x => x.BasisAmount.HasValue);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).When(x => x.Amount.HasValue);
        RuleFor(x => x.Amount).Must(v => v is null || MoneyRules.TwoDecimals(v.Value)).WithMessage("At most two decimal places.");
        RuleFor(x => x.CompanyBorneAmount).GreaterThanOrEqualTo(0m).Must(v => v is null || MoneyRules.TwoDecimals(v.Value)).When(x => x.CompanyBorneAmount.HasValue).WithMessage("Enter a non-negative amount with at most two decimal places.");
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class CancelGovernmentFeeAssessmentRequestValidator : AbstractValidator<CancelGovernmentFeeAssessmentRequest>
{
    public CancelGovernmentFeeAssessmentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class FeeCollectionRequestValidator : AbstractValidator<FeeCollectionRequest>
{
    public FeeCollectionRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class GovernmentPaymentRequestValidator : AbstractValidator<GovernmentPaymentRequest>
{
    public GovernmentPaymentRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class ReverseFeeEntryRequestValidator : AbstractValidator<ReverseFeeEntryRequest>
{
    public ReverseFeeEntryRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}
