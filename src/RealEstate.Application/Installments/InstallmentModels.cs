using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Sales;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Installments;

namespace RealEstate.Application.Installments;

// =====================================================================================================
// charge types and late fee rules
// =====================================================================================================
public sealed record ChargeTypeDto(long Id, string Code, string Name, ChargeComponentKind Kind, ChargeCategory DefaultCategory, ScheduleFrequency DefaultFrequency,
    string? Description, bool IsSystem, bool IsActive);

public sealed record SaveChargeTypeRequest(string Code, string Name, ChargeComponentKind Kind, ChargeCategory DefaultCategory, ScheduleFrequency DefaultFrequency, string? Description, bool IsActive);

public sealed class LateFeeRuleListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public bool? IsActive { get; set; }
}

public sealed record LateFeeRuleDto(long Id, string Code, string Name, long? ProjectId, string? ProjectCode, LateFeeMethod Method, decimal Value, decimal? MaxAmount,
    decimal? MaxPercentOfInstallment, string? Description, bool IsActive, bool IsInUse, string RowVersion, DateTime CreatedAtUtc, string? CreatedBy);

public sealed record SaveLateFeeRuleRequest(string Code, string Name, long? ProjectId, LateFeeMethod Method, decimal Value, decimal? MaxAmount, decimal? MaxPercentOfInstallment,
    string? Description, bool IsActive, string? RowVersion);

public sealed record LateFeePreviewRequest(long RuleId, decimal InstallmentAmount, decimal Outstanding, DateOnly GraceEndDate, DateOnly AsOf);
public sealed record LateFeePreviewDto(decimal LateFee, int OverdueDays, int StartedMonths);

// =====================================================================================================
// plan templates
// =====================================================================================================
public sealed record TemplateEntryInput(int OffsetMonths, decimal? SharePercent);
public sealed record ScheduleEntryInput(DateOnly DueDate, decimal? Amount);

public sealed record PlanComponentInput(
    long ChargeTypeId, string Name, ChargeCategory Category, AmountBasis AmountBasis, decimal? Amount, decimal? Percent, ScheduleFrequency Frequency,
    int? NumberOfInstallments, decimal? InstallmentAmount, int FirstDueOffsetMonths, int FirstDueOffsetDays, int? DueDayOfMonth, IReadOnlyList<TemplateEntryInput>? CustomEntries,
    int GracePeriodDays, long? LateFeeRuleId, bool WaiverAllowed, decimal MaxWaiverPercent, TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, string? Remarks,
    int? SortOrder = null);

public sealed record CreateInstallmentPlanRequest(long ProjectId, string Code, string Name, string? Description, InstallmentPlanType PlanType, long? PropertyCategoryId, long? PropertyId,
    long? CustomerId, IReadOnlyList<PlanComponentInput>? Components);

public sealed record UpdateInstallmentPlanRequest(string Name, string? Description, long? PropertyCategoryId, long? PropertyId, long? CustomerId, string? RowVersion);
public sealed record PlanComponentRequest(PlanComponentInput Component, string? RowVersion);
public sealed record PlanRowVersionRequest(string? RowVersion);
public sealed record RetirePlanRequest(string Reason, string? RowVersion);
public sealed record CopyPlanRequest(string Code, string Name);
public sealed record PlanPreviewRequest(decimal Price, decimal BookingAmount, DateOnly StartDate);

public sealed class InstallmentPlanListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public InstallmentPlanType? PlanType { get; set; }
    public InstallmentPlanStatus? Status { get; set; }
}

public sealed record InstallmentPlanListItemDto(long Id, long ProjectId, string ProjectCode, string Code, string Name, InstallmentPlanType PlanType, InstallmentPlanStatus Status,
    string? Target, int ComponentCount, DateTime CreatedAtUtc, string? CreatedBy);

public sealed record PlanComponentDto(long Id, int SortOrder, long ChargeTypeId, string ChargeTypeName, ChargeComponentKind Kind, string Name, ChargeCategory Category, AmountBasis AmountBasis,
    decimal? Amount, decimal? Percent, ScheduleFrequency Frequency, int? NumberOfInstallments, decimal? InstallmentAmount, int FirstDueOffsetMonths, int FirstDueOffsetDays, int? DueDayOfMonth,
    IReadOnlyList<TemplateEntryInput> CustomEntries, int GracePeriodDays, long? LateFeeRuleId, string? LateFeeRuleName, bool WaiverAllowed, decimal MaxWaiverPercent,
    TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, string? Remarks);

public sealed record InstallmentPlanDetailDto(long Id, long ProjectId, string ProjectCode, string ProjectName, string Code, string Name, string? Description, InstallmentPlanType PlanType,
    long? PropertyCategoryId, string? PropertyCategoryName, long? PropertyId, string? PropertyCode, long? CustomerId, string? CustomerName, InstallmentPlanStatus Status,
    string? ActivatedBy, DateTime? ActivatedAtUtc, string? RetiredBy, DateTime? RetiredAtUtc, string? RetireReason, IReadOnlyList<PlanComponentDto> Components,
    decimal SaleConsiderationPercent, bool HasRemainder, bool RequiresApproval, DateTime CreatedAtUtc, string? CreatedBy, string RowVersion);

public sealed record ResolvedChargeDto(int SortOrder, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency, int? NumberOfInstallments,
    decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth, int GracePeriodDays, TaxTreatment TaxTreatment, decimal TaxRatePercent);

public sealed record PlanPreviewDto(IReadOnlyList<ResolvedChargeDto> Charges, SchedulePreviewDto Schedule);

// =====================================================================================================
// agreements, charges and schedules
// =====================================================================================================
public sealed class InstallmentAgreementListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public bool? HasSchedule { get; set; }
    public AgreementStatus? Status { get; set; }
    public PaymentMode? PaymentMode { get; set; }
}

public sealed record InstallmentAgreementListItemDto(long AgreementId, string AgreementNumber, long BookingId, string BookingNumber, long ProjectId, string ProjectCode, string? PrimaryCustomer,
    PaymentMode PaymentMode, AgreementStatus Status, decimal TotalPayable, decimal ChargesTotal, int? ScheduleVersion, decimal? ScheduleOutstanding, bool HasPendingAmendment);

public sealed record AgreementChargeInput(
    long ChargeTypeId, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency, int? NumberOfInstallments, decimal? InstallmentAmount,
    DateOnly FirstDueDate, int? DueDayOfMonth, IReadOnlyList<ScheduleEntryInput>? CustomEntries, int GracePeriodDays, long? LateFeeRuleId, bool WaiverAllowed, decimal MaxWaiverPercent,
    TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit, string? Remarks, int? SortOrder = null);

public sealed record SaveAgreementChargeRequest(AgreementChargeInput Charge, string? RowVersion);
public sealed record AgreementRowVersionRequest(string? RowVersion);
public sealed record ApplyPlanRequest(long PlanId, DateOnly? StartDate, string? RowVersion);
public sealed record GenerateScheduleRequest(string? RowVersion, string? Remarks);

public sealed record AgreementChargeDto(long Id, int SortOrder, long ChargeTypeId, string ChargeTypeName, string Description, ChargeCategory Category, decimal Amount, ScheduleFrequency Frequency,
    int? NumberOfInstallments, decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth, IReadOnlyList<ScheduleEntryInput> CustomEntries, int GracePeriodDays,
    long? LateFeeRuleId, string? LateFeeRuleName, bool WaiverAllowed, decimal MaxWaiverPercent, TaxTreatment TaxTreatment, decimal TaxRatePercent, decimal? RoundingUnit,
    string? Remarks, AgreementChargeStatus Status, long? AddedByAmendmentId);

public sealed record ScheduleLineDto(long? Id, int LineNo, long ChargeId, int InstallmentNo, int InstallmentCount, string Description, DateOnly DueDate, DateOnly GraceEndDate,
    decimal Amount, decimal TaxAmount, bool IsPartial, decimal AmountPaid, decimal AmountWaived, decimal LateFeeWaived, decimal Outstanding, ScheduleItemStatus Status,
    decimal LateFeeDue, bool IsOverdue, bool IsPaidInAdvance, long? PreviousItemId);

public sealed record ReconciliationDto(long ChargeId, string Description, decimal ChargeAmount, decimal ScheduledAmount, decimal Difference);

public sealed record SchedulePreviewDto(IReadOnlyList<ScheduleLineDto> Items, IReadOnlyList<ReconciliationDto> Reconciliation, decimal TotalAmount, decimal TotalTax,
    decimal ChargesTotal, decimal SaleConsiderationTotal, decimal TotalPayable, decimal PriceDifference, bool IsReconciled, bool CanGenerate, IReadOnlyList<string> Problems);

public sealed record ScheduleSummaryDto(long Id, int VersionNo, PaymentScheduleStatus Status, string Reason, int ItemCount, decimal TotalAmount, decimal TotalTax, decimal Outstanding,
    DateOnly? NextDueDate, decimal? NextDueAmount, DateTime GeneratedAtUtc, string GeneratedBy, DateTime? SupersededAtUtc, long? SourceAmendmentId);

public sealed record PaymentScheduleDto(long Id, long AgreementId, string AgreementNumber, int VersionNo, PaymentScheduleStatus Status, string Reason, DateTime GeneratedAtUtc,
    string GeneratedBy, DateTime? SupersededAtUtc, long? SourceAmendmentId, decimal TotalAmount, decimal TotalTax, decimal TotalPaid, decimal TotalWaived, decimal TotalOutstanding,
    decimal TotalLateFeeDue, int ItemCount, string ContentHash, bool HashVerified, bool IsReconciled, IReadOnlyList<ScheduleLineDto> Items, IReadOnlyList<ReconciliationDto> Reconciliation,
    IReadOnlyList<ScheduleSummaryDto> Versions, string CurrencySymbol);

public sealed record AgreementInstallmentDto(long AgreementId, string AgreementNumber, AgreementStatus Status, int CurrentVersionNo, long BookingId, string BookingNumber, DateOnly BookingDate,
    long ProjectId, string ProjectCode, string ProjectName, PaymentMode PaymentMode, string Customers, string Properties, IReadOnlyList<long> CustomerIds, decimal TotalPayable, decimal BookingAmount,
    long? PlanId, string? PlanName, DateOnly? ScheduleStartDate, IReadOnlyList<AgreementChargeDto> Charges, decimal SaleConsiderationTotal, decimal AdditionalChargesTotal,
    decimal ChargesTotal, decimal PriceDifference, bool ScheduleGenerated, bool CanEditCharges, ScheduleSummaryDto? ActiveSchedule, long? PendingAmendmentId,
    string RowVersion, string CurrencySymbol);

public sealed record ScheduleChangeDto(long AmendmentId, AmendmentType AmendmentType, int? FromScheduleVersion, int? ToScheduleVersion, ScheduleChangeOperation Operation, long? ChargeId,
    string? ChargeDescription, long? FromItemId, long? ToItemId, DateOnly? OldDueDate, DateOnly? NewDueDate, decimal? OldAmount, decimal? NewAmount, string? Note);

public sealed record WaiverDto(long Id, long AgreementId, string AgreementNumber, long AmendmentId, long ScheduleItemId, string ItemDescription, WaiverKind Kind, decimal Amount, string Reason,
    WaiverStatus Status, string RequestedBy, string? DecidedBy, DateTime? DecidedAtUtc, string? DecisionComments, DateTime CreatedAtUtc);

public sealed record AmendmentSummaryDto(long Id, AmendmentType AmendmentType, AmendmentStatus Status, string Reason, string? RequestedBy, string? ApprovedBy, DateTime CreatedAtUtc,
    DateTime? DecidedAtUtc, int FromVersionNo, int? ToVersionNo, int? BaseScheduleVersion, int? ResultScheduleVersion);

public sealed record AgreementHistoryDto(long AgreementId, string AgreementNumber, long BookingId, string BookingNumber, IReadOnlyList<ScheduleSummaryDto> Schedules,
    IReadOnlyList<AmendmentSummaryDto> Amendments, IReadOnlyList<ScheduleChangeDto> Changes, IReadOnlyList<AgreementVersionDto> AgreementVersions, IReadOnlyList<WaiverDto> Waivers);

// =====================================================================================================
// amendments
// =====================================================================================================
public sealed record RescheduleInput(long ChargeId, ScheduleFrequency Frequency, int? NumberOfInstallments, decimal? InstallmentAmount, DateOnly FirstDueDate, int? DueDayOfMonth,
    IReadOnlyList<ScheduleEntryInput>? CustomEntries);
public sealed record DefermentInput(IReadOnlyList<long> ItemIds, int Months, bool CascadeSubsequent);
public sealed record ReductionInput(long ItemId, decimal NewAmount, ReductionRedistribution Redistribution, DateOnly? NewDueDate);
public sealed record AdditionalInstallmentsInput(long ChargeId, int AdditionalCount);
public sealed record DueDateChangeInput(long ItemId, DateOnly NewDueDate);
public sealed record PenaltyChangeInput(long ChargeId, long? LateFeeRuleId, int GracePeriodDays);
public sealed record WaiverInput(long ItemId, WaiverKind Kind, decimal Amount);

/// <summary>Exactly the section matching <see cref="Type"/> must be supplied. Installment ids refer to the agreement's active schedule.</summary>
public sealed record CreateAmendmentRequest(AmendmentType Type, string Reason, string? IdempotencyKey = null,
    RescheduleInput? Reschedule = null, DefermentInput? Deferment = null, ReductionInput? Reduction = null, AdditionalInstallmentsInput? AdditionalInstallments = null,
    IReadOnlyList<DueDateChangeInput>? DueDateChanges = null, PenaltyChangeInput? PenaltyChange = null, WaiverInput? Waiver = null, AgreementChargeInput? AdditionalCharge = null);

public sealed record DecideAmendmentRequest(string? Comments, string? RowVersion);
public sealed record RejectAmendmentRequest(string Comments, string? RowVersion);

public sealed class AmendmentListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public AmendmentStatus? Status { get; set; }
    public AmendmentType? Type { get; set; }
}

public sealed record AmendmentListItemDto(long Id, long AgreementId, string AgreementNumber, string BookingNumber, string ProjectCode, AmendmentType AmendmentType, AmendmentStatus Status,
    string Reason, string? RequestedBy, DateTime CreatedAtUtc, string? DecidedBy, DateTime? DecidedAtUtc, decimal? WaiverAmount);

public sealed record AmendmentDetailDto(long Id, long AgreementId, string AgreementNumber, long BookingId, string BookingNumber, AmendmentType AmendmentType, AmendmentStatus Status,
    string Reason, string Summary, string Details, string? RequestedBy, DateTime CreatedAtUtc, string? ApprovedBy, DateTime? DecidedAtUtc, string? DecisionComments,
    int FromVersionNo, int? ToVersionNo, int? BaseScheduleVersion, int? ResultScheduleVersion, bool IsStale, IReadOnlyList<ScheduleChangeDto> Changes,
    SchedulePreviewDto? Preview, string? PreviewError, WaiverDto? Waiver, bool CanDecide, bool CanCancel, string RowVersion, string CurrencySymbol);

// =====================================================================================================
// validators (request shape; the engine enforces the financial rules)
// =====================================================================================================
public class SaveChargeTypeRequestValidator : AbstractValidator<SaveChargeTypeRequest>
{
    public SaveChargeTypeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' or '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.DefaultCategory).IsInEnum();
        RuleFor(x => x.DefaultFrequency).IsInEnum();
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class SaveLateFeeRuleRequestValidator : AbstractValidator<SaveLateFeeRuleRequest>
{
    public SaveLateFeeRuleRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' or '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Value).GreaterThan(0).PrecisionScale(18, 4, true);
        RuleFor(x => x.Value).LessThanOrEqualTo(100).When(x => x.Method is LateFeeMethod.PercentOneTime or LateFeeMethod.PercentPerDay or LateFeeMethod.PercentPerMonth)
            .WithMessage("A late fee percentage cannot exceed 100%.");
        RuleFor(x => x.MaxAmount).GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.MaxAmount is not null);
        RuleFor(x => x.MaxPercentOfInstallment).InclusiveBetween(0.0001m, 100m).When(x => x.MaxPercentOfInstallment is not null);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

internal static class InstallmentRuleSets
{
    public static void Common<T>(AbstractValidator<T> v, Func<T, string> name, Func<T, ScheduleFrequency> freq, Func<T, int?> count, Func<T, decimal?> each,
        Func<T, int?> dueDay, Func<T, int> grace, Func<T, bool> waiver, Func<T, decimal> maxWaiver, Func<T, TaxTreatment> tax, Func<T, decimal> rate, Func<T, decimal?> unit,
        Func<T, string?> remarks)
    {
        v.RuleFor(x => name(x)).NotEmpty().WithName("Name").MaximumLength(200);
        v.RuleFor(x => freq(x)).IsInEnum().WithName("Frequency");
        v.RuleFor(x => count(x)).InclusiveBetween(1, ScheduleGenerationService.MaxInstallments).When(x => count(x) is not null).WithName("NumberOfInstallments");
        v.RuleFor(x => each(x)).GreaterThan(0).PrecisionScale(18, 2, true).When(x => each(x) is not null).WithName("InstallmentAmount");
        v.RuleFor(x => dueDay(x)).InclusiveBetween(1, 31).When(x => dueDay(x) is not null).WithName("DueDayOfMonth");
        v.RuleFor(x => grace(x)).InclusiveBetween(0, 365).WithName("GracePeriodDays");
        v.RuleFor(x => maxWaiver(x)).InclusiveBetween(0.01m, 100m).When(waiver).WithName("MaxWaiverPercent").WithMessage("Enter the maximum waiver percentage (0.01-100) when waivers are allowed.");
        v.RuleFor(x => maxWaiver(x)).Equal(0m).When(x => !waiver(x)).WithName("MaxWaiverPercent").WithMessage("A maximum waiver applies only when waivers are allowed.");
        v.RuleFor(x => tax(x)).IsInEnum().WithName("TaxTreatment");
        v.RuleFor(x => rate(x)).InclusiveBetween(0m, 100m).PrecisionScale(18, 4, true).WithName("TaxRatePercent");
        v.RuleFor(x => rate(x)).GreaterThan(0m).When(x => tax(x) is TaxTreatment.Inclusive or TaxTreatment.Exclusive).WithName("TaxRatePercent").WithMessage("Enter the tax rate.");
        v.RuleFor(x => rate(x)).Equal(0m).When(x => tax(x) is TaxTreatment.NotApplicable or TaxTreatment.Exempt).WithName("TaxRatePercent").WithMessage("A tax rate applies only to inclusive or exclusive tax.");
        v.RuleFor(x => unit(x)).GreaterThan(0).PrecisionScale(18, 2, true).When(x => unit(x) is not null).WithName("RoundingUnit");
        v.RuleFor(x => remarks(x)).MaximumLength(1000).WithName("Remarks");
    }
}

public class PlanComponentInputValidator : AbstractValidator<PlanComponentInput>
{
    public PlanComponentInputValidator()
    {
        InstallmentRuleSets.Common<PlanComponentInput>(this, x => x.Name, x => x.Frequency, x => x.NumberOfInstallments, x => x.InstallmentAmount, x => x.DueDayOfMonth, x => x.GracePeriodDays,
            x => x.WaiverAllowed, x => x.MaxWaiverPercent, x => x.TaxTreatment, x => x.TaxRatePercent, x => x.RoundingUnit, x => x.Remarks);
        RuleFor(x => x.ChargeTypeId).GreaterThan(0).WithMessage("Choose the charge type.");
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.AmountBasis).IsInEnum();
        RuleFor(x => x.Amount).NotNull().GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.AmountBasis == AmountBasis.FixedAmount).WithMessage("Enter the amount.");
        RuleFor(x => x.Amount).Null().When(x => x.AmountBasis != AmountBasis.FixedAmount).WithMessage("An amount applies only to fixed-amount components.");
        RuleFor(x => x.Percent).NotNull().InclusiveBetween(0.0001m, 100m).PrecisionScale(18, 4, true).When(x => x.AmountBasis == AmountBasis.PercentOfPrice).WithMessage("Enter a percentage between 0 and 100.");
        RuleFor(x => x.Percent).Null().When(x => x.AmountBasis != AmountBasis.PercentOfPrice).WithMessage("A percentage applies only to percent-of-price components.");
        RuleFor(x => x.Category).Equal(ChargeCategory.SaleConsideration).When(x => x.AmountBasis == AmountBasis.Remainder).WithMessage("Only a sale-consideration component can take the remainder.");
        RuleFor(x => x.FirstDueOffsetMonths).InclusiveBetween(0, 600);
        RuleFor(x => x.FirstDueOffsetDays).InclusiveBetween(0, 3650);
        RuleFor(x => x).Custom((x, ctx) =>
        {
            switch (x.Frequency)
            {
                case ScheduleFrequency.OneTime:
                    if (x.NumberOfInstallments is not (null or 1) || x.InstallmentAmount is not null) ctx.AddFailure("NumberOfInstallments", "A one-time payment has exactly one installment.");
                    if (x.DueDayOfMonth is not null) ctx.AddFailure("DueDayOfMonth", "A fixed due day applies only to regular schedules.");
                    if (x.CustomEntries is { Count: > 0 }) ctx.AddFailure("CustomEntries", "Custom dates apply only to custom schedules.");
                    break;
                case ScheduleFrequency.CustomDates:
                    var e = x.CustomEntries;
                    if (e is null || e.Count == 0) { ctx.AddFailure("CustomEntries", "Enter at least one custom date (months after start)."); break; }
                    if (e.Count > ScheduleGenerationService.MaxInstallments) ctx.AddFailure("CustomEntries", "Too many custom dates.");
                    if (e.Any(t => t.OffsetMonths is < 0 or > 600)) ctx.AddFailure("CustomEntries", "Offsets must be between 0 and 600 months.");
                    for (var i = 1; i < e.Count; i++) if (e[i].OffsetMonths <= e[i - 1].OffsetMonths) { ctx.AddFailure("CustomEntries", "Offsets must increase."); break; }
                    var shares = e.Count(t => t.SharePercent is not null);
                    if (shares != 0 && shares != e.Count) ctx.AddFailure("CustomEntries", "Give a share for every custom date or for none.");
                    if (shares == e.Count && (e.Any(t => t.SharePercent <= 0) || e.Sum(t => t.SharePercent!.Value) != 100m)) ctx.AddFailure("CustomEntries", "Custom shares must be positive and total 100%.");
                    if (x.NumberOfInstallments is not null || x.InstallmentAmount is not null || x.DueDayOfMonth is not null) ctx.AddFailure("NumberOfInstallments", "Custom schedules take their installments from the custom dates.");
                    break;
                default:
                    if ((x.NumberOfInstallments is null) == (x.InstallmentAmount is null)) ctx.AddFailure("NumberOfInstallments", "Give either the number of installments or the installment amount.");
                    if (x.CustomEntries is { Count: > 0 }) ctx.AddFailure("CustomEntries", "Custom dates apply only to custom schedules.");
                    break;
            }
        });
    }
}

public class CreateInstallmentPlanRequestValidator : AbstractValidator<CreateInstallmentPlanRequest>
{
    public CreateInstallmentPlanRequestValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' or '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.PlanType).IsInEnum();
        RuleFor(x => x.PropertyCategoryId).NotNull().When(x => x.PlanType == InstallmentPlanType.PropertyCategory).WithMessage("Choose the property category.");
        RuleFor(x => x.PropertyCategoryId).Null().When(x => x.PlanType != InstallmentPlanType.PropertyCategory).WithMessage("A property category applies only to property-category plans.");
        RuleFor(x => x.PropertyId).NotNull().When(x => x.PlanType == InstallmentPlanType.PropertySpecific).WithMessage("Choose the property.");
        RuleFor(x => x.PropertyId).Null().When(x => x.PlanType is not (InstallmentPlanType.PropertySpecific or InstallmentPlanType.CustomerSpecific)).WithMessage("A property applies only to property- or customer-specific plans.");
        RuleFor(x => x.CustomerId).NotNull().When(x => x.PlanType == InstallmentPlanType.CustomerSpecific).WithMessage("Choose the customer.");
        RuleFor(x => x.CustomerId).Null().When(x => x.PlanType != InstallmentPlanType.CustomerSpecific).WithMessage("A customer applies only to customer-specific plans.");
        RuleForEach(x => x.Components).SetValidator(new PlanComponentInputValidator());
        RuleFor(x => x.Components).Must(c => c is null || c.Count <= 50).WithMessage("At most 50 components are allowed.");
    }
}

public class UpdateInstallmentPlanRequestValidator : AbstractValidator<UpdateInstallmentPlanRequest>
{
    public UpdateInstallmentPlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public class AgreementChargeInputValidator : AbstractValidator<AgreementChargeInput>
{
    public AgreementChargeInputValidator()
    {
        InstallmentRuleSets.Common<AgreementChargeInput>(this, x => x.Description, x => x.Frequency, x => x.NumberOfInstallments, x => x.InstallmentAmount, x => x.DueDayOfMonth,
            x => x.GracePeriodDays, x => x.WaiverAllowed, x => x.MaxWaiverPercent, x => x.TaxTreatment, x => x.TaxRatePercent, x => x.RoundingUnit, x => x.Remarks);
        RuleFor(x => x.ChargeTypeId).GreaterThan(0).WithMessage("Choose the charge type.");
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(x => x.CustomEntries).Must(e => e is null || e.Count <= ScheduleGenerationService.MaxInstallments).WithMessage("Too many custom dates.");
        RuleForEach(x => x.CustomEntries).ChildRules(e => e.RuleFor(y => y.Amount).GreaterThan(0).PrecisionScale(18, 2, true).When(y => y.Amount is not null));
    }
}

public class CreateAmendmentRequestValidator : AbstractValidator<CreateAmendmentRequest>
{
    public CreateAmendmentRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum().Must(t => t >= AmendmentType.Reschedule).WithMessage("Choose a schedule amendment type.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(64);
        RuleFor(x => x).Custom((x, ctx) =>
        {
            var given = new (AmendmentType Type, bool Present)[]
            {
                (AmendmentType.Reschedule, x.Reschedule is not null), (AmendmentType.Deferment, x.Deferment is not null), (AmendmentType.InstallmentReduction, x.Reduction is not null),
                (AmendmentType.AdditionalInstallments, x.AdditionalInstallments is not null), (AmendmentType.DueDateChange, x.DueDateChanges is { Count: > 0 }),
                (AmendmentType.PenaltyChange, x.PenaltyChange is not null), (AmendmentType.Waiver, x.Waiver is not null), (AmendmentType.AdditionalCharge, x.AdditionalCharge is not null),
            };
            if (!given.Any(g => g.Type == x.Type && g.Present)) ctx.AddFailure("Type", $"Enter the {x.Type} details.");
            if (given.Any(g => g.Type != x.Type && g.Present)) ctx.AddFailure("Type", "Only the details of the selected amendment type may be supplied.");
        });
        RuleFor(x => x.AdditionalCharge!).SetValidator(new AgreementChargeInputValidator()).When(x => x.AdditionalCharge is not null);
        RuleFor(x => x.AdditionalCharge!.Category).NotEqual(ChargeCategory.SaleConsideration).When(x => x.AdditionalCharge is not null)
            .WithMessage("An additional charge cannot be part of the sale consideration.");
        RuleFor(x => x.Deferment!.Months).InclusiveBetween(1, 120).When(x => x.Deferment is not null);
        RuleFor(x => x.Deferment!.ItemIds).NotEmpty().When(x => x.Deferment is not null).WithMessage("Choose at least one installment.");
        RuleFor(x => x.Reduction!.NewAmount).GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.Reduction is not null);
        RuleFor(x => x.Reduction!.Redistribution).IsInEnum().When(x => x.Reduction is not null);
        RuleFor(x => x.AdditionalInstallments!.AdditionalCount).InclusiveBetween(1, 120).When(x => x.AdditionalInstallments is not null);
        RuleFor(x => x.PenaltyChange!.GracePeriodDays).InclusiveBetween(0, 365).When(x => x.PenaltyChange is not null);
        RuleFor(x => x.Waiver!.Amount).GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.Waiver is not null);
        RuleFor(x => x.Waiver!.Kind).IsInEnum().When(x => x.Waiver is not null);
        RuleFor(x => x.Reschedule!.Frequency).IsInEnum().When(x => x.Reschedule is not null);
        RuleFor(x => x.Reschedule!.InstallmentAmount).GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.Reschedule?.InstallmentAmount is not null);
    }
}

public class RejectAmendmentRequestValidator : AbstractValidator<RejectAmendmentRequest>
{
    public RejectAmendmentRequestValidator() => RuleFor(x => x.Comments).NotEmpty().MinimumLength(3).MaximumLength(1000);
}

public class RetirePlanRequestValidator : AbstractValidator<RetirePlanRequest>
{
    public RetirePlanRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class CopyPlanRequestValidator : AbstractValidator<CopyPlanRequest>
{
    public CopyPlanRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' or '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
