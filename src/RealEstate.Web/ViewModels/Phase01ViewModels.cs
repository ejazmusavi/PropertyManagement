using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Projects;
using RealEstate.Application.Users;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class CompanyViewModel
{
    [Required, StringLength(200), Display(Name = "Company name")] public string Name { get; set; } = "";
    [StringLength(250), Display(Name = "Legal name")] public string? LegalName { get; set; }
    [StringLength(50), Display(Name = "Tax number")] public string? TaxNumber { get; set; }
    [EmailAddress, StringLength(256)] public string? Email { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(250)] public string? Website { get; set; }
    [StringLength(500)] public string? Address { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(100)] public string? Country { get; set; }
    [StringLength(20), Display(Name = "Postal code")] public string? PostalCode { get; set; }
    [Required, StringLength(3, MinimumLength = 3), Display(Name = "Currency code")] public string CurrencyCode { get; set; } = "PKR";
    [Required, StringLength(8), Display(Name = "Currency symbol")] public string CurrencySymbol { get; set; } = "Rs";
    [Range(0, 4), Display(Name = "Decimal places")] public int CurrencyDecimalPlaces { get; set; } = 2;
    [Display(Name = "Rounding")] public RoundingMode RoundingMode { get; set; } = RoundingMode.Nearest;
    [Range(1, 12), Display(Name = "Fiscal year starts in month")] public int FiscalYearStartMonth { get; set; } = 7;
    [Required, StringLength(100), Display(Name = "Time zone")] public string TimeZoneId { get; set; } = "Asia/Karachi";
    [Range(1, 100), Display(Name = "Max upload size (MB)")] public int MaxUploadSizeMb { get; set; } = 10;
    [Required, StringLength(300), Display(Name = "Allowed file extensions")] public string AllowedFileExtensions { get; set; } = "";
    public string? RowVersion { get; set; }
    public bool HasLogo { get; set; }
}

public class ProjectFormViewModel
{
    public long? Id { get; set; }
    public string? RowVersion { get; set; }
    [Required, StringLength(30), Display(Name = "Project code")] public string ProjectCode { get; set; } = "";
    [Required, StringLength(200), Display(Name = "Project name")] public string ProjectName { get; set; } = "";
    [StringLength(2000)] public string? Description { get; set; }
    [Required, StringLength(100)] public string City { get; set; } = "";
    [StringLength(500)] public string? Address { get; set; }
    [DataType(DataType.Date), Display(Name = "Start date")] public DateOnly? StartDate { get; set; }
    [DataType(DataType.Date), Display(Name = "Expected completion")] public DateOnly? ExpectedCompletionDate { get; set; }
    [Display(Name = "Initial area")] public decimal InitialArea { get; set; }
    [Display(Name = "Measurement unit")] public MeasurementUnit MeasurementUnit { get; set; } = MeasurementUnit.Kanal;
    [Display(Name = "Project manager")] public Guid? ProjectManagerId { get; set; }
    [Display(Name = "Address line"), StringLength(500)] public string? AddressLine { get; set; }
    [StringLength(150)] public string? Locality { get; set; }
    [StringLength(100)] public string? Province { get; set; }
    [StringLength(100)] public string? Country { get; set; }
    [Display(Name = "Postal code"), StringLength(20)] public string? PostalCode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    [StringLength(250)] public string? Landmark { get; set; }
    public IReadOnlyList<UserLookupDto> Managers { get; set; } = [];

    public ProjectAddressDto ToAddress() => new(AddressLine, Locality, Province, Country, PostalCode, Latitude, Longitude, Landmark);
}

public class ProjectListViewModel
{
    public required PagedResult<ProjectListItemDto> Projects { get; init; }
    public string? Search { get; init; }
    public ProjectStatus? Status { get; init; }
    public string? City { get; init; }
    public string? SortBy { get; init; }
    public bool Descending { get; init; }
    public IReadOnlyList<string> Cities { get; init; } = [];
}

public class ProjectDetailsViewModel
{
    public required ProjectDetailDto Project { get; init; }
    public required IReadOnlyList<DocumentDto> Documents { get; init; }
}

public class ChangeStatusViewModel
{
    public long ProjectId { get; set; }
    public ProjectStatus Status { get; set; }
    [Required, StringLength(1000)] public string Reason { get; set; } = "";
    public string? RowVersion { get; set; }
}

public class ProjectAccessViewModel
{
    public required ProjectDetailDto Project { get; init; }
    public required IReadOnlyList<ProjectAccessUserDto> Assigned { get; init; }
    public required IReadOnlyList<UserLookupDto> Candidates { get; init; }
}

public class AcquireLandViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public MeasurementUnit Unit { get; set; }
    public string? RowVersion { get; set; }
    [DataType(DataType.Date), Display(Name = "Acquisition date")] public DateOnly AcquisitionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Required, StringLength(200), Display(Name = "Seller")] public string SellerName { get; set; } = "";
    public decimal Area { get; set; }
    [Display(Name = "Cost (optional)")] public decimal? Cost { get; set; }
    [StringLength(100), Display(Name = "Reference / registry number")] public string? ReferenceNumber { get; set; }
    [StringLength(50), Display(Name = "Parcel code (optional)")] public string? ParcelCode { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    public IFormFile? Document { get; set; }
}

public class AdjustLandViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public MeasurementUnit Unit { get; set; }
    public decimal CurrentArea { get; set; }
    public string? RowVersion { get; set; }
    [DataType(DataType.Date), Display(Name = "Adjustment date")] public DateOnly AdjustmentDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public LandAdjustmentType Type { get; set; } = LandAdjustmentType.Addition;
    public decimal Area { get; set; }
    [Required, StringLength(1000)] public string Reason { get; set; } = "";
    [Required, StringLength(300), Display(Name = "Supporting document reference")] public string SupportingReference { get; set; } = "";
    [Required, Display(Name = "Approved by")] public Guid? ApprovedByUserId { get; set; }
    public IFormFile? Document { get; set; }
    public IReadOnlyList<UserLookupDto> Approvers { get; set; } = [];
}

public class DistributionViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public MeasurementUnit Unit { get; set; }
    public decimal TotalArea { get; set; }
    public Dictionary<LandCategory, decimal> Areas { get; set; } = [];
    [Required, StringLength(1000)] public string Reason { get; set; } = "";
    public decimal Unclassified { get; set; }
    /// <summary>Version of the distribution this form was loaded from (optimistic concurrency).</summary>
    public string DistributionVersion { get; set; } = "";
}

public class AddParcelViewModel
{
    public long ProjectId { get; set; }
    [Required, StringLength(50), Display(Name = "Parcel code")] public string ParcelCode { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    public decimal Area { get; set; }
    [StringLength(200), Display(Name = "Title reference")] public string? TitleReference { get; set; }
    [StringLength(300)] public string? Location { get; set; }
}

public class LandDocumentsViewModel
{
    public required LandOverviewDto Overview { get; init; }
    public required IReadOnlyList<LandDocumentDto> Documents { get; init; }
}

public class AreaHistoryViewModel
{
    public required LandOverviewDto Overview { get; init; }
    public required PagedResult<AreaHistoryDto> History { get; init; }
    public required PagedResult<DistributionHistoryDto> DistributionHistory { get; init; }
}

public class UserFormViewModel
{
    public Guid? Id { get; set; }
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(200), Display(Name = "Full name")] public string FullName { get; set; } = "";
    [StringLength(30), Display(Name = "Phone number")] public string? PhoneNumber { get; set; }
    [DataType(DataType.Password), StringLength(128, MinimumLength = 12)] public string? Password { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public List<string> Roles { get; set; } = [];
    public IReadOnlyList<RoleDto> AllRoles { get; set; } = [];
}

public class RoleFormViewModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(100, MinimumLength = 2)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsAdministrator { get; set; }
    public List<string> PermissionCodes { get; set; } = [];
    public IReadOnlyList<PermissionDto> AllPermissions { get; set; } = [];
}
