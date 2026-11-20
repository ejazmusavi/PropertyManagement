using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

// ===================================================================================================
// plan templates
// ===================================================================================================
public class PlanListViewModel
{
    public PagedResult<InstallmentPlanListItemDto> Plans { get; set; } = default!;
    public InstallmentPlanListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class PlanFormViewModel
{
    public long? Id { get; set; }
    public string? RowVersion { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    [Display(Name = "Plan type")] public InstallmentPlanType PlanType { get; set; } = InstallmentPlanType.StandardProject;
    [Display(Name = "Property category")] public long? PropertyCategoryId { get; set; }
    [Display(Name = "Property")] public long? PropertyId { get; set; }
    [Display(Name = "Customer")] public long? CustomerId { get; set; }
    public string? CustomerLabel { get; set; }
    public IReadOnlyList<CategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<PropertyListItemDto> Properties { get; set; } = [];
}

public class PlanDetailsViewModel
{
    public InstallmentPlanDetailDto Plan { get; set; } = default!;
    public PlanPreviewDto? Preview { get; set; }
    public string? PreviewError { get; set; }
    public decimal PreviewPrice { get; set; } = 1_000_000m;
    public decimal PreviewBookingAmount { get; set; } = 100_000m;
    public DateOnly PreviewStartDate { get; set; }
    public bool Previewed { get; set; }
}

public class TemplateRow
{
    [Display(Name = "Months after start")] public int? OffsetMonths { get; set; }
    [Display(Name = "Share %")] public decimal? SharePercent { get; set; }
}

public class ScheduleRow
{
    [Display(Name = "Due date")] public DateOnly? DueDate { get; set; }
    public decimal? Amount { get; set; }
}

/// <summary>Fields shared by plan components and agreement charges.</summary>
public class ChargeFieldsBase
{
    [Display(Name = "Charge type")] public long ChargeTypeId { get; set; }
    [StringLength(200), Display(Name = "Name")] public string Name { get; set; } = "";
    public ChargeCategory Category { get; set; } = ChargeCategory.SaleConsideration;
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Monthly;
    [Display(Name = "Number of installments")] public int? NumberOfInstallments { get; set; }
    [Display(Name = "Installment amount")] public decimal? InstallmentAmount { get; set; }
    [Display(Name = "Fixed due day of month")] public int? DueDayOfMonth { get; set; }
    [Display(Name = "Grace days")] public int GracePeriodDays { get; set; }
    [Display(Name = "Late fee rule")] public long? LateFeeRuleId { get; set; }
    [Display(Name = "Waiver allowed")] public bool WaiverAllowed { get; set; }
    [Display(Name = "Maximum waiver %")] public decimal MaxWaiverPercent { get; set; }
    [Display(Name = "Tax treatment")] public TaxTreatment TaxTreatment { get; set; } = TaxTreatment.NotApplicable;
    [Display(Name = "Tax rate %")] public decimal TaxRatePercent { get; set; }
    [Display(Name = "Rounding unit")] public decimal? RoundingUnit { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    [Display(Name = "Sort order")] public int? SortOrder { get; set; }
    public string? RowVersion { get; set; }
    public IReadOnlyList<ChargeTypeDto> ChargeTypes { get; set; } = [];
    public IReadOnlyList<LateFeeRuleDto> LateFeeRules { get; set; } = [];
}

public class PlanComponentFormViewModel : ChargeFieldsBase
{
    public long PlanId { get; set; }
    public long? ComponentId { get; set; }
    public string PlanName { get; set; } = "";
    [Display(Name = "Amount basis")] public AmountBasis AmountBasis { get; set; } = AmountBasis.Remainder;
    public decimal? Amount { get; set; }
    [Display(Name = "Percent of price")] public decimal? Percent { get; set; }
    [Display(Name = "First due after (months)")] public int FirstDueOffsetMonths { get; set; }
    [Display(Name = "plus days")] public int FirstDueOffsetDays { get; set; }
    public List<TemplateRow> CustomEntries { get; set; } = [];

    public PlanComponentInput ToInput() => new(ChargeTypeId, Name, Category, AmountBasis, Amount, Percent, Frequency,
        NumberOfInstallments, InstallmentAmount, FirstDueOffsetMonths, FirstDueOffsetDays, DueDayOfMonth,
        Frequency == ScheduleFrequency.CustomDates ? CustomEntries.Where(e => e.OffsetMonths is not null).Select(e => new TemplateEntryInput(e.OffsetMonths!.Value, e.SharePercent)).ToList() : null,
        GracePeriodDays, LateFeeRuleId, WaiverAllowed, MaxWaiverPercent, TaxTreatment, TaxRatePercent, RoundingUnit, Remarks, SortOrder);

    public static PlanComponentFormViewModel From(long planId, string planName, PlanComponentDto c, string rowVersion) => new()
    {
        PlanId = planId, PlanName = planName, ComponentId = c.Id, RowVersion = rowVersion, ChargeTypeId = c.ChargeTypeId, Name = c.Name, Category = c.Category, AmountBasis = c.AmountBasis,
        Amount = c.Amount, Percent = c.Percent, Frequency = c.Frequency, NumberOfInstallments = c.NumberOfInstallments, InstallmentAmount = c.InstallmentAmount,
        FirstDueOffsetMonths = c.FirstDueOffsetMonths, FirstDueOffsetDays = c.FirstDueOffsetDays, DueDayOfMonth = c.DueDayOfMonth,
        CustomEntries = c.CustomEntries.Select(e => new TemplateRow { OffsetMonths = e.OffsetMonths, SharePercent = e.SharePercent }).ToList(),
        GracePeriodDays = c.GracePeriodDays, LateFeeRuleId = c.LateFeeRuleId, WaiverAllowed = c.WaiverAllowed, MaxWaiverPercent = c.MaxWaiverPercent, TaxTreatment = c.TaxTreatment,
        TaxRatePercent = c.TaxRatePercent, RoundingUnit = c.RoundingUnit, Remarks = c.Remarks, SortOrder = c.SortOrder,
    };
}

public class ChargeTypeFormViewModel
{
    public long? Id { get; set; }
    public bool IsSystem { get; set; }
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    public ChargeComponentKind Kind { get; set; } = ChargeComponentKind.CustomCharge;
    [Display(Name = "Default category")] public ChargeCategory DefaultCategory { get; set; } = ChargeCategory.OtherCharges;
    [Display(Name = "Default frequency")] public ScheduleFrequency DefaultFrequency { get; set; } = ScheduleFrequency.OneTime;
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

// ===================================================================================================
// late fee rules
// ===================================================================================================
public class LateFeeListViewModel
{
    public PagedResult<LateFeeRuleDto> Rules { get; set; } = default!;
    public LateFeeRuleListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class LateFeeFormViewModel
{
    public long? Id { get; set; }
    public string? RowVersion { get; set; }
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Project (blank = all projects)")] public long? ProjectId { get; set; }
    public LateFeeMethod Method { get; set; } = LateFeeMethod.PercentPerMonth;
    public decimal? Value { get; set; }
    [Display(Name = "Maximum amount")] public decimal? MaxAmount { get; set; }
    [Display(Name = "Maximum % of installment")] public decimal? MaxPercentOfInstallment { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsInUse { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class LateFeeCalculatorViewModel
{
    public IReadOnlyList<LateFeeRuleDto> Rules { get; set; } = [];
    public long? RuleId { get; set; }
    public decimal? InstallmentAmount { get; set; }
    public decimal? Outstanding { get; set; }
    public DateOnly? GraceEndDate { get; set; }
    public DateOnly? AsOf { get; set; }
    public LateFeePreviewDto? Result { get; set; }
    public string? Error { get; set; }
}

// ===================================================================================================
// agreements and schedule builder
// ===================================================================================================
public class AgreementListViewModel
{
    public PagedResult<InstallmentAgreementListItemDto> Agreements { get; set; } = default!;
    public InstallmentAgreementListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class AgreementDetailsViewModel
{
    public AgreementInstallmentDto Agreement { get; set; } = default!;
    public PaymentScheduleDto? Schedule { get; set; }
}

public class BuilderViewModel
{
    public AgreementInstallmentDto Agreement { get; set; } = default!;
    public SchedulePreviewDto Preview { get; set; } = default!;
    public IReadOnlyList<InstallmentPlanListItemDto> Plans { get; set; } = [];
}

public class AgreementChargeFormViewModel : ChargeFieldsBase
{
    public long AgreementId { get; set; }
    public long? ChargeId { get; set; }
    public string AgreementNumber { get; set; } = "";
    public string CurrencySymbol { get; set; } = "";
    public decimal? Amount { get; set; }
    [Display(Name = "First due date")] public DateOnly? FirstDueDate { get; set; }
    public List<ScheduleRow> CustomEntries { get; set; } = [];

    public AgreementChargeInput ToInput()
    {
        var entries = Frequency == ScheduleFrequency.CustomDates
            ? CustomEntries.Where(e => e.DueDate is not null).Select(e => new ScheduleEntryInput(e.DueDate!.Value, e.Amount)).ToList() : null;
        var first = Frequency == ScheduleFrequency.CustomDates && entries is { Count: > 0 } ? entries[0].DueDate : FirstDueDate ?? default;
        return new AgreementChargeInput(ChargeTypeId, Name, Category, Amount ?? 0m, Frequency, NumberOfInstallments, InstallmentAmount, first, DueDayOfMonth, entries, GracePeriodDays,
            LateFeeRuleId, WaiverAllowed, MaxWaiverPercent, TaxTreatment, TaxRatePercent, RoundingUnit, Remarks, SortOrder);
    }

    public static AgreementChargeFormViewModel From(long agreementId, string number, AgreementChargeDto c, string rowVersion, string symbol) => new()
    {
        AgreementId = agreementId, AgreementNumber = number, ChargeId = c.Id, RowVersion = rowVersion, CurrencySymbol = symbol, ChargeTypeId = c.ChargeTypeId, Name = c.Description, Category = c.Category,
        Amount = c.Amount, Frequency = c.Frequency, NumberOfInstallments = c.NumberOfInstallments, InstallmentAmount = c.InstallmentAmount, FirstDueDate = c.FirstDueDate,
        DueDayOfMonth = c.DueDayOfMonth, CustomEntries = c.CustomEntries.Select(e => new ScheduleRow { DueDate = e.DueDate, Amount = e.Amount }).ToList(), GracePeriodDays = c.GracePeriodDays,
        LateFeeRuleId = c.LateFeeRuleId, WaiverAllowed = c.WaiverAllowed, MaxWaiverPercent = c.MaxWaiverPercent, TaxTreatment = c.TaxTreatment, TaxRatePercent = c.TaxRatePercent,
        RoundingUnit = c.RoundingUnit, Remarks = c.Remarks, SortOrder = c.SortOrder,
    };
}

public class PreviewViewModel
{
    public AgreementInstallmentDto Agreement { get; set; } = default!;
    public SchedulePreviewDto Preview { get; set; } = default!;
}

public class HistoryViewModel
{
    public AgreementHistoryDto History { get; set; } = default!;
    public PaymentScheduleDto? Schedule { get; set; }
    public string CurrencySymbol { get; set; } = "";
}

// ===================================================================================================
// amendments and waivers
// ===================================================================================================
public class DueDateRow
{
    public long ItemId { get; set; }
    public DateOnly? NewDueDate { get; set; }
}

public class AmendmentFormViewModel
{
    public long AgreementId { get; set; }
    public string AgreementNumber { get; set; } = "";
    public string CurrencySymbol { get; set; } = "";
    public AmendmentType Type { get; set; } = AmendmentType.Deferment;
    [Required, StringLength(1000, MinimumLength = 5)] public string Reason { get; set; } = "";
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");

    // reschedule
    public long? RsChargeId { get; set; }
    public ScheduleFrequency RsFrequency { get; set; } = ScheduleFrequency.Monthly;
    public int? RsCount { get; set; }
    public decimal? RsInstallmentAmount { get; set; }
    public DateOnly? RsFirstDueDate { get; set; }
    public int? RsDueDay { get; set; }
    public List<ScheduleRow> RsEntries { get; set; } = [];
    // deferment
    public List<long> DefItemIds { get; set; } = [];
    public int? DefMonths { get; set; }
    public bool DefCascade { get; set; } = true;
    // reduction
    public long? RedItemId { get; set; }
    public decimal? RedNewAmount { get; set; }
    public ReductionRedistribution RedRedistribution { get; set; } = ReductionRedistribution.SpreadOverRemaining;
    public DateOnly? RedNewDueDate { get; set; }
    // additional installments
    public long? AiChargeId { get; set; }
    public int? AiCount { get; set; }
    // due date changes
    public List<DueDateRow> DueChanges { get; set; } = [];
    // penalty change
    public long? PcChargeId { get; set; }
    public long? PcLateFeeRuleId { get; set; }
    public int PcGraceDays { get; set; }
    // waiver
    public long? WvItemId { get; set; }
    public WaiverKind WvKind { get; set; } = WaiverKind.LateFee;
    public decimal? WvAmount { get; set; }
    // additional charge
    public AgreementChargeFormViewModel Charge { get; set; } = new() { Category = ChargeCategory.OtherCharges, Frequency = ScheduleFrequency.OneTime };

    // lookups (not bound)
    public IReadOnlyList<ScheduleLineDto> Lines { get; set; } = [];
    public IReadOnlyList<AgreementChargeDto> Charges { get; set; } = [];
    public IReadOnlyList<ChargeTypeDto> ChargeTypes { get; set; } = [];
    public IReadOnlyList<LateFeeRuleDto> LateFeeRules { get; set; } = [];

    public CreateAmendmentRequest ToRequest() => Type switch
    {
        AmendmentType.Reschedule => new(Type, Reason, IdempotencyKey, Reschedule: new RescheduleInput(RsChargeId ?? 0, RsFrequency, RsCount, RsInstallmentAmount, RsFirstDueDate ?? default, RsDueDay,
            RsFrequency == ScheduleFrequency.CustomDates ? RsEntries.Where(e => e.DueDate is not null).Select(e => new ScheduleEntryInput(e.DueDate!.Value, e.Amount)).ToList() : null)),
        AmendmentType.Deferment => new(Type, Reason, IdempotencyKey, Deferment: new DefermentInput(DefItemIds, DefMonths ?? 0, DefCascade)),
        AmendmentType.InstallmentReduction => new(Type, Reason, IdempotencyKey, Reduction: new ReductionInput(RedItemId ?? 0, RedNewAmount ?? 0m, RedRedistribution, RedNewDueDate)),
        AmendmentType.AdditionalInstallments => new(Type, Reason, IdempotencyKey, AdditionalInstallments: new AdditionalInstallmentsInput(AiChargeId ?? 0, AiCount ?? 0)),
        AmendmentType.DueDateChange => new(Type, Reason, IdempotencyKey, DueDateChanges: DueChanges.Where(d => d.NewDueDate is not null).Select(d => new DueDateChangeInput(d.ItemId, d.NewDueDate!.Value)).ToList()),
        AmendmentType.PenaltyChange => new(Type, Reason, IdempotencyKey, PenaltyChange: new PenaltyChangeInput(PcChargeId ?? 0, PcLateFeeRuleId, PcGraceDays)),
        AmendmentType.Waiver => new(Type, Reason, IdempotencyKey, Waiver: new WaiverInput(WvItemId ?? 0, WvKind, WvAmount ?? 0m)),
        AmendmentType.AdditionalCharge => new(Type, Reason, IdempotencyKey, AdditionalCharge: Charge.ToInput()),
        _ => new(Type, Reason, IdempotencyKey),
    };
}

public class AmendmentQueueViewModel
{
    public PagedResult<AmendmentListItemDto> Amendments { get; set; } = default!;
    public AmendmentListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public bool PendingOnly { get; set; }
}

public class AmendmentDetailsViewModel
{
    public AmendmentDetailDto Amendment { get; set; } = default!;
}

public class WaiverListViewModel
{
    public PagedResult<WaiverDto> Waivers { get; set; } = default!;
    public WaiverStatus? Status { get; set; }
    public long? ProjectId { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}
