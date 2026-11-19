using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Application.Users;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class DimensionInput
{
    [StringLength(60)] public string? Name { get; set; }
    public decimal? Value { get; set; }
    [StringLength(20)] public string? Unit { get; set; }
}

public class FeatureInput
{
    [StringLength(100)] public string? Name { get; set; }
    [StringLength(200)] public string? Value { get; set; }
}

public class PropertyFormViewModel
{
    public long? Id { get; set; }
    public string? RowVersion { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public PropertyStatus? CurrentStatus { get; set; }
    [Required, StringLength(40), Display(Name = "Property code")] public string PropertyCode { get; set; } = "";
    [Required, StringLength(30), Display(Name = "Property number")] public string PropertyNumber { get; set; } = "";
    [Display(Name = "Block")] public long? BlockId { get; set; }
    [Display(Name = "Sector")] public long? SectorId { get; set; }
    [Display(Name = "Street")] public long? StreetId { get; set; }
    [Display(Name = "Property type")] public long PropertyTypeId { get; set; }
    public decimal Size { get; set; }
    [Display(Name = "Measurement unit")] public MeasurementUnit MeasurementUnit { get; set; } = MeasurementUnit.Marla;
    [StringLength(500), Display(Name = "Location description")] public string? LocationDescription { get; set; }
    [Display(Name = "Floor number")] public int? FloorNumber { get; set; }
    [Display(Name = "Corner")] public bool IsCorner { get; set; }
    [Display(Name = "Main road facing")] public bool FacesMainRoad { get; set; }
    [Display(Name = "Park facing")] public bool FacesPark { get; set; }
    [Display(Name = "Development status")] public DevelopmentStatus DevelopmentStatus { get; set; } = DevelopmentStatus.Undeveloped;
    [StringLength(2000)] public string? Remarks { get; set; }
    public List<DimensionInput> Dimensions { get; set; } = [];
    public List<FeatureInput> Features { get; set; } = [];

    public IReadOnlyList<PropertyTypeDto> Types { get; set; } = [];
    public IReadOnlyList<ZoneDto> Blocks { get; set; } = [];
    public IReadOnlyList<ZoneDto> Sectors { get; set; } = [];
    public IReadOnlyList<ZoneDto> Streets { get; set; } = [];
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public bool StructureLocked { get; set; }

    public IReadOnlyList<DimensionDto> ToDimensions() =>
        Dimensions.Where(d => !string.IsNullOrWhiteSpace(d.Name) && d.Value.HasValue).Select(d => new DimensionDto(d.Name!, d.Value!.Value, d.Unit)).ToList();
    public IReadOnlyList<FeatureDto> ToFeatures() =>
        Features.Where(f => !string.IsNullOrWhiteSpace(f.Name)).Select(f => new FeatureDto(f.Name!, f.Value)).ToList();
}

public class PropertyListViewModel
{
    public PagedResult<PropertyListItemDto> Properties { get; set; } = default!;
    public PropertyListRequest Filter { get; set; } = new();
    public bool Descending { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<PropertyTypeDto> Types { get; set; } = [];
    public IReadOnlyList<ZoneDto> Blocks { get; set; } = [];
}

public class PropertyDetailsViewModel
{
    public PropertyDetailDto Property { get; set; } = default!;
    public IReadOnlyList<ReservationDto> Reservations { get; set; } = [];
    public IReadOnlyList<PropertyDocumentDto> Documents { get; set; } = [];
    public PagedResult<PropertyStatusHistoryDto> History { get; set; } = default!;
    public IReadOnlyList<UserLookupDto> Approvers { get; set; } = [];
    public DateOnly Today { get; set; }
    public ReserveViewModel Reserve { get; set; } = new();
}

public class ReserveViewModel
{
    public string? RowVersion { get; set; }
    [DataType(DataType.Date), Display(Name = "Reservation date")] public DateOnly ReservationDate { get; set; }
    [DataType(DataType.Date), Display(Name = "Expiry date")] public DateOnly ExpiryDate { get; set; }
    [Display(Name = "Reservation amount")] public decimal Amount { get; set; }
    [Required, StringLength(200), Display(Name = "Reserved for (customer)")] public string ReservedForName { get; set; } = "";
    [StringLength(200), Display(Name = "Customer contact")] public string? ReservedForContact { get; set; }
    [Display(Name = "Approved by")] public Guid? ApprovedByUserId { get; set; }
}

public class ZonesViewModel
{
    public ZoneKind Kind { get; set; }
    public long ProjectId { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<ZoneDto> Zones { get; set; } = [];
}

public class TypesViewModel
{
    public IReadOnlyList<CategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<PropertyTypeDto> Types { get; set; } = [];
}

public class InventoryDashboardViewModel
{
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public InventorySummaryDto? Summary { get; set; }
}

public class InventoryReportsViewModel
{
    public long? ProjectId { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public InventoryReportDto Report { get; set; } = default!;
}

public class InventoryHistoryViewModel
{
    public InventoryHistoryRequest Filter { get; set; } = new();
    public PagedResult<PropertyStatusHistoryDto> History { get; set; } = default!;
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}
