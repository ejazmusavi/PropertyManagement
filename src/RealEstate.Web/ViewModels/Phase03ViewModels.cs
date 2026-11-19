using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class RuleFormViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    public PricingRuleKind Kind { get; set; } = PricingRuleKind.BaseRate;
    public PricingFactor Factor { get; set; } = PricingFactor.Size;
    public PricingScope Scope { get; set; } = PricingScope.Project;
    public long? BlockId { get; set; }
    public long? StreetId { get; set; }
    public long? CategoryId { get; set; }
    public long? PropertyId { get; set; }
    public long? CustomerId { get; set; }
    [StringLength(200)] public string? CustomerReference { get; set; }
    public PricingMethod Method { get; set; } = PricingMethod.PerUnit;
    public decimal? Value { get; set; }
    public MeasurementUnit? Unit { get; set; } = MeasurementUnit.Marla;
    public int Priority { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    // optional conditions
    public decimal? MinSize { get; set; }
    public decimal? MaxSize { get; set; }
    public long? ConditionTypeId { get; set; }
    public long? ConditionSectorId { get; set; }
    public DevelopmentStatus? ConditionDevelopment { get; set; }
    public bool? ConditionCorner { get; set; }
    public bool? ConditionMainRoad { get; set; }
    public bool? ConditionPark { get; set; }
    public int? MinFloor { get; set; }
    public int? MaxFloor { get; set; }

    public IReadOnlyList<PropertyTypeDto> Types { get; set; } = [];
    public IReadOnlyList<ZoneDto> Blocks { get; set; } = [];
    public IReadOnlyList<ZoneDto> Sectors { get; set; } = [];
    public IReadOnlyList<ZoneDto> Streets { get; set; } = [];
    public IReadOnlyList<CategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<PropertyPriceListItemDto> Properties { get; set; } = [];

    public IReadOnlyList<ConditionDto> ToConditions()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var l = new List<ConditionDto>();
        if (MinSize is { } a) l.Add(new(PricingConditionField.MinSize, a.ToString(inv)));
        if (MaxSize is { } b) l.Add(new(PricingConditionField.MaxSize, b.ToString(inv)));
        if (ConditionTypeId is { } t) l.Add(new(PricingConditionField.PropertyType, t.ToString(inv)));
        if (ConditionSectorId is { } s) l.Add(new(PricingConditionField.Sector, s.ToString(inv)));
        if (ConditionDevelopment is { } d) l.Add(new(PricingConditionField.DevelopmentStatus, d.ToString()));
        if (ConditionCorner is { } c) l.Add(new(PricingConditionField.Corner, c ? "true" : "false"));
        if (ConditionMainRoad is { } m) l.Add(new(PricingConditionField.MainRoad, m ? "true" : "false"));
        if (ConditionPark is { } p) l.Add(new(PricingConditionField.Park, p ? "true" : "false"));
        if (MinFloor is { } f1) l.Add(new(PricingConditionField.MinFloor, f1.ToString(inv)));
        if (MaxFloor is { } f2) l.Add(new(PricingConditionField.MaxFloor, f2.ToString(inv)));
        return l;
    }
}

public class DiscountFormViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    public PricingScope Scope { get; set; } = PricingScope.Project;
    public long? BlockId { get; set; }
    public long? StreetId { get; set; }
    public long? CategoryId { get; set; }
    public long? PropertyId { get; set; }
    public PricingMethod Method { get; set; } = PricingMethod.Percentage;
    public decimal? Value { get; set; }
    public bool IsStackable { get; set; }
    public int Priority { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public IReadOnlyList<ZoneDto> Blocks { get; set; } = [];
    public IReadOnlyList<ZoneDto> Streets { get; set; } = [];
    public IReadOnlyList<CategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<PropertyPriceListItemDto> Properties { get; set; } = [];
}

public class RulesListViewModel
{
    public PricingRuleListRequest Filter { get; set; } = new();
    public PagedResult<PricingRuleDto> Rules { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class DiscountsListViewModel
{
    public DiscountListRequest Filter { get; set; } = new();
    public PagedResult<DiscountRuleDto> Discounts { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class RateCardViewModel
{
    public long? ProjectId { get; set; }
    public DateOnly? AsOf { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public RateCardDto? Card { get; set; }
}

public class CalculatorViewModel
{
    public long? ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public long? CustomerId { get; set; }
    public DateOnly? AsOf { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<PropertyPriceListItemDto> Properties { get; set; } = [];
    public PriceCalculationDto? Result { get; set; }
    public string? Error { get; set; }
}

public class PropertyPricesViewModel
{
    public PropertyPriceListRequest Filter { get; set; } = new();
    public PagedResult<PropertyPriceListItemDto> Prices { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class PropertyPriceViewModel
{
    public PropertyPriceViewDto View { get; set; } = default!;
    public long? CustomerId { get; set; }
    public DateOnly? AsOf { get; set; }
    public DateOnly Today { get; set; }
    public ApprovalFormViewModel Request { get; set; } = new();
}

public class ApprovalFormViewModel
{
    public long PropertyId { get; set; }
    public PricingApprovalType Type { get; set; } = PricingApprovalType.PriceOverride;
    public long? CustomerId { get; set; }
    [StringLength(200)] public string? CustomerReference { get; set; }
    public AdjustmentKind? AdjustmentKind { get; set; } = RealEstate.Domain.Enums.AdjustmentKind.Discount;
    public PricingMethod? Method { get; set; } = PricingMethod.FixedAmount;
    public decimal? Value { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    [StringLength(1000)] public string? Reason { get; set; }
}

public class PriceHistoryViewModel
{
    public PriceHistoryRequest Filter { get; set; } = new();
    public PagedResult<PriceHistoryDto> History { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class ApprovalsViewModel
{
    public ApprovalListRequest Filter { get; set; } = new();
    public PagedResult<ApprovalDto> Approvals { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class ApprovalDetailsViewModel
{
    public ApprovalDto Approval { get; set; } = default!;
    public Guid CurrentUserId { get; set; }
}
