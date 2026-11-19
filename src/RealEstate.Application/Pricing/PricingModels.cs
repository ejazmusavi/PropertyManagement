using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Pricing;

// ---------- rules ----------
public sealed record ConditionDto(PricingConditionField Field, string Value);

public sealed record PricingRuleDto(long Id, long ProjectId, string ProjectCode, string Code, string Name, string? Description, PricingRuleKind Kind, PricingFactor Factor, PricingScope Scope,
    long? BlockId, string? BlockName, long? StreetId, string? StreetName, long? CategoryId, string? CategoryName, long? PropertyId, string? PropertyCode, long? CustomerId, string? CustomerReference,
    PricingMethod Method, decimal Value, MeasurementUnit? Unit, int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo, PricingRuleStatus Status, string? StatusReason,
    IReadOnlyList<ConditionDto> Conditions, string RowVersion, DateTime CreatedAtUtc, string? CreatedBy, long? ApprovalId);

public sealed class PricingRuleListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public PricingScope? Scope { get; set; }
    public PricingRuleKind? Kind { get; set; }
    public PricingRuleStatus? Status { get; set; }
    /// <summary>Only rules effective on this date.</summary>
    public DateOnly? ActiveOn { get; set; }
}

public sealed record CreatePricingRuleRequest(long ProjectId, string Code, string Name, string? Description, PricingRuleKind Kind, PricingFactor Factor, PricingScope Scope,
    long? BlockId, long? StreetId, long? CategoryId, long? PropertyId, long? CustomerId, string? CustomerReference, PricingMethod Method, decimal Value, MeasurementUnit? Unit,
    int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo, IReadOnlyList<ConditionDto>? Conditions);

public sealed record UpdateRuleDetailsRequest(string Name, string? Description, string? RowVersion);
public sealed record RetireRequest(string Reason, string? RowVersion);

// ---------- discounts ----------
public sealed record DiscountRuleDto(long Id, long ProjectId, string ProjectCode, string Code, string Name, string? Description, PricingScope Scope,
    long? BlockId, string? BlockName, long? StreetId, string? StreetName, long? CategoryId, string? CategoryName, long? PropertyId, string? PropertyCode,
    PricingMethod Method, decimal Value, bool IsStackable, int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo, PricingRuleStatus Status, string? StatusReason,
    string RowVersion, DateTime CreatedAtUtc, string? CreatedBy);

public sealed class DiscountListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public PricingRuleStatus? Status { get; set; }
    public DateOnly? ActiveOn { get; set; }
}

public sealed record CreateDiscountRuleRequest(long ProjectId, string Code, string Name, string? Description, PricingScope Scope, long? BlockId, long? StreetId, long? CategoryId, long? PropertyId,
    PricingMethod Method, decimal Value, bool IsStackable, int Priority, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record RateCardDto(long ProjectId, string ProjectCode, string ProjectName, DateOnly AsOfDate, IReadOnlyList<PricingRuleDto> BaseRates, IReadOnlyList<PricingRuleDto> Premiums,
    IReadOnlyList<PricingRuleDto> Charges, IReadOnlyList<DiscountRuleDto> Discounts);

// ---------- calculation ----------
public sealed record PriceLineDto(string Source, long? SourceId, string Code, string Name, PricingScope? Scope, PricingRuleKind? Kind, PricingFactor? Factor, PricingMethod Method, decimal Value, decimal Amount);

/// <summary>Full, self-describing price calculation. Also the payload of the immutable pricing snapshot (version 1).</summary>
public sealed record PriceCalculationDto(int SnapshotVersion, long PropertyId, string PropertyCode, long ProjectId, string ProjectCode, DateOnly AsOfDate, long? CustomerId,
    decimal Size, MeasurementUnit Unit, decimal BaseRate, MeasurementUnit? BaseRateUnit, PricingMethod BaseMethod, decimal BasePrice, decimal SizeBasedPrice,
    IReadOnlyList<PriceLineDto> Premiums, decimal TotalPremiums, IReadOnlyList<PriceLineDto> Discounts, decimal TotalDiscounts, IReadOnlyList<PriceLineDto> Charges, decimal TotalCharges,
    decimal ComputedPrice, bool IsOverridden, decimal? OverridePrice, decimal FinalPrice, IReadOnlyList<PriceLineDto> AppliedRules, int DecimalPlaces, RoundingMode Rounding);

public sealed record CalculatePriceRequest(long PropertyId, long? CustomerId, DateOnly? AsOfDate);

/// <summary>An immutable copy of a calculation. Phase 4 stores this with each booking; it is never recalculated.</summary>
public sealed record PriceSnapshotDto(string Json, string Hash, PriceCalculationDto Calculation);

// ---------- property prices ----------
public sealed record PropertyPriceDto(long Id, long PropertyId, decimal? BaseRate, MeasurementUnit? RateUnit, decimal BasePrice, decimal SizeBasedPrice, decimal TotalPremiums, decimal TotalDiscounts,
    decimal TotalCharges, decimal FinalPrice, bool IsOverridden, DateOnly AsOfDate, string SnapshotHash, DateTime PublishedAtUtc, string? PublishedBy, string RowVersion);

public sealed record AdjustmentDto(long Id, long PropertyId, string PropertyCode, AdjustmentKind Kind, PricingMethod Method, decimal Value, long? CustomerId, string? CustomerReference,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, AdjustmentStatus Status, long ApprovalId, string Reason, DateTime ApprovedAtUtc, DateTime? RevokedAtUtc, string? RevokedBy, string? RevokeReason, string RowVersion);

public sealed record PropertyPriceViewDto(long PropertyId, string PropertyCode, long ProjectId, string ProjectCode, string ProjectName, PropertyStatus PropertyStatus, bool PriceFrozen,
    PropertyPriceDto? Published, PriceCalculationDto? Calculation, string? CalculationError, IReadOnlyList<AdjustmentDto> Adjustments);

public sealed record PublishPriceRequest(string? Reason);
public sealed record PublishOutcomeDto(bool Changed, PropertyPriceDto Price);
public sealed record PublishSkippedDto(string PropertyCode, string Reason);
public sealed record PublishProjectResultDto(int Published, int Unchanged, int Frozen, IReadOnlyList<PublishSkippedDto> Skipped);

public sealed record PropertyPriceListItemDto(long PropertyId, long ProjectId, string ProjectCode, string PropertyCode, string PropertyNumber, string TypeName, decimal Size, MeasurementUnit Unit,
    PropertyStatus Status, decimal? PublishedPrice, DateTime? PublishedAtUtc, bool IsOverridden);

public sealed class PropertyPriceListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public PropertyStatus? Status { get; set; }
    public bool? Published { get; set; }
}

public sealed record PriceHistoryDto(long Id, long PropertyId, string PropertyCode, decimal? PreviousPrice, decimal NewPrice, PriceChangeSource Source, string Reason, DateTime ChangedAtUtc,
    string? ChangedBy, string SnapshotHash, long? ApprovalId, long? AdjustmentId);

public sealed class PriceHistoryRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public PriceChangeSource? Source { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

// ---------- approvals ----------
public sealed record ApprovalDto(long Id, PricingApprovalType Type, PricingApprovalStatus Status, long ProjectId, string ProjectCode, long? PropertyId, string? PropertyCode, long? CustomerId,
    string? CustomerReference, long? PricingRuleId, AdjustmentKind? AdjustmentKind, PricingMethod? Method, decimal Value, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal? OriginalPrice,
    decimal? ProposedPrice, string Reason, Guid RequestedByUserId, string? RequestedByName, DateTime RequestedAtUtc, string? DecidedByName, DateTime? DecidedAtUtc, string? DecisionComment,
    long? AdjustmentId, string RowVersion);

/// <summary>PriceOverride: Value = requested price. CustomerDiscount: customer + Method (Percentage/FixedAmount) + Value. SpecialPricing: customer + Value = negotiated price.
/// PricingException: AdjustmentKind (Discount/Surcharge) + Method + Value, no customer.</summary>
public sealed record RequestPricingApprovalRequest(PricingApprovalType Type, long PropertyId, long? CustomerId, string? CustomerReference, AdjustmentKind? AdjustmentKind, PricingMethod? Method,
    decimal Value, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string Reason);

public sealed record DecidePricingApprovalRequest(bool Approve, string? Comment, string? RowVersion);
public sealed record CancelPricingApprovalRequest(string? Comment, string? RowVersion);

public sealed class ApprovalListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public PricingApprovalStatus? Status { get; set; }
    public PricingApprovalType? Type { get; set; }
}

// ---------- validators ----------
internal static class PricingValidation
{
    public static bool MaxDecimals(decimal v, int places) => decimal.Round(v, places) == v;
    public const decimal MaxAmount = 999_999_999_999m;
}

public class CreatePricingRuleValidator : AbstractValidator<CreatePricingRuleRequest>
{
    public CreatePricingRuleValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Code may contain letters, digits, hyphen and underscore only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Factor).IsInEnum();
        RuleFor(x => x.Scope).IsInEnum();
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Unit).IsInEnum().When(x => x.Unit is not null);
        RuleFor(x => x.Priority).InclusiveBetween(0, 1000);
        RuleFor(x => x.CustomerReference).MaximumLength(200);
        RuleFor(x => x.Value).GreaterThan(0).LessThanOrEqualTo(PricingValidation.MaxAmount);
        RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 4)).WithMessage("At most 4 decimal places.");
        RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 2)).When(x => x.Method == PricingMethod.FixedAmount).WithMessage("Fixed amounts support at most 2 decimal places.");
        RuleFor(x => x.Value).LessThanOrEqualTo(100m).When(x => x.Method == PricingMethod.Percentage).WithMessage("A percentage cannot exceed 100.");
        RuleFor(x => x.Method).Must(m => m is PricingMethod.PerUnit or PricingMethod.FixedAmount).When(x => x.Kind == PricingRuleKind.BaseRate).WithMessage("A base rate is either per unit or a fixed total price.");
        RuleFor(x => x.Unit).NotNull().When(x => x.Method == PricingMethod.PerUnit).WithMessage("A per-unit rate needs a measurement unit.");
        RuleFor(x => x.EffectiveTo).GreaterThanOrEqualTo(x => x.EffectiveFrom).When(x => x.EffectiveTo is not null).WithMessage("The end date cannot be before the start date.");
        RuleFor(x => x.Conditions).Must(c => c is null || c.Count <= 12).WithMessage("At most 12 conditions.");
    }
}

public class CreateDiscountRuleValidator : AbstractValidator<CreateDiscountRuleRequest>
{
    public CreateDiscountRuleValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Code may contain letters, digits, hyphen and underscore only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Scope).IsInEnum().Must(s => s != PricingScope.Customer).WithMessage("Customer-specific discounts need a pricing approval; they cannot be configured as a discount rule.");
        RuleFor(x => x.Method).Must(m => m is PricingMethod.Percentage or PricingMethod.FixedAmount).WithMessage("A discount is a percentage or a fixed amount.");
        RuleFor(x => x.Value).GreaterThan(0).LessThanOrEqualTo(PricingValidation.MaxAmount);
        RuleFor(x => x.Value).LessThanOrEqualTo(100m).When(x => x.Method == PricingMethod.Percentage).WithMessage("A percentage cannot exceed 100.");
        RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 4)).When(x => x.Method == PricingMethod.Percentage).WithMessage("At most 4 decimal places.");
        RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 2)).When(x => x.Method == PricingMethod.FixedAmount).WithMessage("Fixed amounts support at most 2 decimal places.");
        RuleFor(x => x.Priority).InclusiveBetween(0, 1000);
        RuleFor(x => x.EffectiveTo).GreaterThanOrEqualTo(x => x.EffectiveFrom).When(x => x.EffectiveTo is not null).WithMessage("The end date cannot be before the start date.");
    }
}

public class RequestPricingApprovalValidator : AbstractValidator<RequestPricingApprovalRequest>
{
    public RequestPricingApprovalValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.PropertyId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
        RuleFor(x => x.CustomerReference).MaximumLength(200);
        RuleFor(x => x.Value).GreaterThan(0).LessThanOrEqualTo(PricingValidation.MaxAmount);
        RuleFor(x => x.EffectiveTo).GreaterThanOrEqualTo(x => x.EffectiveFrom).When(x => x.EffectiveTo is not null && x.EffectiveFrom is not null);
        When(x => x.Type is PricingApprovalType.PriceOverride or PricingApprovalType.SpecialPricing, () =>
            RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 2)).WithMessage("A price supports at most 2 decimal places."));
        When(x => x.Type is PricingApprovalType.CustomerDiscount or PricingApprovalType.PricingException, () =>
        {
            RuleFor(x => x.Method).NotNull().Must(m => m is PricingMethod.Percentage or PricingMethod.FixedAmount).WithMessage("Choose percentage or fixed amount.");
            RuleFor(x => x.Value).LessThanOrEqualTo(100m).When(x => x.Method == PricingMethod.Percentage).WithMessage("A percentage cannot exceed 100.");
            RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 4)).When(x => x.Method == PricingMethod.Percentage).WithMessage("At most 4 decimal places.");
            RuleFor(x => x.Value).Must(v => PricingValidation.MaxDecimals(v, 2)).When(x => x.Method == PricingMethod.FixedAmount).WithMessage("Fixed amounts support at most 2 decimal places.");
        });
        When(x => x.Type == PricingApprovalType.PricingException, () =>
            RuleFor(x => x.AdjustmentKind).NotNull().Must(k => k is AdjustmentKind.Discount or AdjustmentKind.Surcharge).WithMessage("An exception is a discount or a surcharge."));
        When(x => x.Type is PricingApprovalType.CustomerDiscount or PricingApprovalType.SpecialPricing, () =>
            RuleFor(x => x.CustomerId).NotNull().GreaterThan(0).WithMessage("A customer is required."));
        When(x => x.Type == PricingApprovalType.PricingException, () =>
            RuleFor(x => x.CustomerId).Null().WithMessage("A pricing exception applies to the property, not to a customer."));
    }
}

public class DecidePricingApprovalValidator : AbstractValidator<DecidePricingApprovalRequest>
{
    public DecidePricingApprovalValidator()
    {
        RuleFor(x => x.Comment).MaximumLength(1000);
        RuleFor(x => x.Comment).NotEmpty().WithMessage("A comment is required to reject a request.").When(x => !x.Approve);
    }
}
