using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Inventory;

// ---------- setup ----------
public sealed record CategoryDto(long Id, string Code, string Name, string? Description, bool IsSystem, bool IsActive, int TypeCount);
public sealed record SaveCategoryRequest(string Code, string Name, string? Description, bool IsActive);

public sealed record PropertyTypeDto(long Id, string Code, string Name, long CategoryId, string CategoryName, PropertyForm Form, bool RequiresFloor, bool IsSystem, bool IsActive);
public sealed record SavePropertyTypeRequest(string Code, string Name, long CategoryId, PropertyForm Form, bool RequiresFloor, bool IsActive);

public sealed record ZoneDto(long Id, long ProjectId, string Code, string Name, bool IsActive, int PropertyCount);
public sealed record SaveZoneRequest(string Code, string Name, bool IsActive);

// ---------- properties ----------
public sealed record DimensionDto(string Name, decimal Value, string? Unit);
public sealed record FeatureDto(string Name, string? Value);

public sealed record PropertyListItemDto(long Id, long ProjectId, string ProjectCode, string PropertyCode, string PropertyNumber, string? Block, string? Sector, string? Street,
    string TypeName, string CategoryName, decimal Size, MeasurementUnit Unit, PropertyStatus Status, DevelopmentStatus DevelopmentStatus, bool IsCorner, bool FacesMainRoad, bool FacesPark, int? FloorNumber);

public sealed class PropertyListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? BlockId { get; set; }
    public long? SectorId { get; set; }
    public long? StreetId { get; set; }
    public long? TypeId { get; set; }
    public long? CategoryId { get; set; }
    public PropertyStatus? Status { get; set; }
    public decimal? MinSize { get; set; }
    public decimal? MaxSize { get; set; }
}

public sealed record ReservationDto(long Id, long PropertyId, DateOnly ReservationDate, DateOnly ExpiryDate, decimal Amount, long? CustomerId, string ReservedForName,
    string? ReservedForContact, Guid ApprovedByUserId, string? ApprovedByName, ReservationStatus Status, DateTime CreatedAtUtc, string? CreatedBy,
    DateTime? ClosedAtUtc, string? ClosedBy, string? CloseReason);

public sealed record PropertyDetailDto(long Id, long ProjectId, string ProjectCode, string ProjectName, ProjectStatus ProjectStatus, string PropertyCode, string PropertyNumber,
    long? BlockId, string? Block, long? SectorId, string? Sector, long? StreetId, string? Street,
    long PropertyTypeId, string TypeName, long CategoryId, string CategoryName, PropertyForm Form, bool RequiresFloor, decimal Size, MeasurementUnit Unit, string? LocationDescription,
    int? FloorNumber, bool IsCorner, bool FacesMainRoad, bool FacesPark, DevelopmentStatus DevelopmentStatus, PropertyStatus Status, string? Remarks,
    IReadOnlyList<DimensionDto> Dimensions, IReadOnlyList<FeatureDto> Features, ReservationDto? ActiveReservation, string RowVersion,
    DateTime CreatedAtUtc, string? CreatedBy, DateTime? ModifiedAtUtc, string? ModifiedBy);

public sealed record CreatePropertyRequest(long ProjectId, string PropertyCode, string PropertyNumber, long? BlockId, long? SectorId, long? StreetId, long PropertyTypeId,
    decimal Size, MeasurementUnit MeasurementUnit, string? LocationDescription, int? FloorNumber, bool IsCorner, bool FacesMainRoad, bool FacesPark,
    DevelopmentStatus DevelopmentStatus, string? Remarks, IReadOnlyList<DimensionDto>? Dimensions, IReadOnlyList<FeatureDto>? Features);

/// <summary>Project and code are fixed once created.</summary>
public sealed record UpdatePropertyRequest(string PropertyNumber, long? BlockId, long? SectorId, long? StreetId, long PropertyTypeId,
    decimal Size, MeasurementUnit MeasurementUnit, string? LocationDescription, int? FloorNumber, bool IsCorner, bool FacesMainRoad, bool FacesPark,
    DevelopmentStatus DevelopmentStatus, string? Remarks, IReadOnlyList<DimensionDto>? Dimensions, IReadOnlyList<FeatureDto>? Features, string? RowVersion);

public sealed record ReservePropertyRequest(DateOnly ReservationDate, DateOnly ExpiryDate, decimal Amount, long? CustomerId, string ReservedForName,
    string? ReservedForContact, Guid ApprovedByUserId, string? RowVersion);

public sealed record ReleaseReservationRequest(string Reason, string? RowVersion);
public sealed record ChangePropertyStatusRequest(PropertyStatus Status, string Reason, string? RowVersion);

public sealed record PropertyStatusHistoryDto(long Id, long PropertyId, string PropertyCode, PropertyStatus? FromStatus, PropertyStatus ToStatus, string Reason,
    DateTime ChangedAtUtc, string? ChangedBy, long? ReservationId);

public sealed class InventoryHistoryRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public PropertyStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record PropertyDocumentDto(long Id, string Title, string FileName, string DocumentType, string ContentType, long SizeBytes, DateTime UploadedAtUtc, string? UploadedBy);

// ---------- reports ----------
public sealed record StatusCountDto(PropertyStatus Status, int Count);
public sealed record InventoryRowDto(string Label, int Total, int Available, int Reserved, int Booked, int Sold, int Other);
public sealed record SizeRowDto(decimal Size, MeasurementUnit Unit, int Total, int Available, int Sold);

public sealed record InventorySummaryDto(long ProjectId, string ProjectCode, string ProjectName, int Total, IReadOnlyList<StatusCountDto> ByStatus,
    IReadOnlyList<InventoryRowDto> ByType, IReadOnlyList<InventoryRowDto> ByBlock);

public sealed record InventoryReportDto(int Total, IReadOnlyList<StatusCountDto> ByStatus, IReadOnlyList<InventoryRowDto> ByProject,
    IReadOnlyList<InventoryRowDto> ByBlock, IReadOnlyList<InventoryRowDto> ByType, IReadOnlyList<SizeRowDto> BySize);

// ---------- validators ----------
internal static class V
{
    public static IRuleBuilderOptions<T, decimal> ValidSize<T>(this IRuleBuilder<T, decimal> r) =>
        r.GreaterThan(0).WithMessage("Size must be greater than zero.").LessThanOrEqualTo(999_999_999m).WithMessage("Size is too large.")
            .Must(a => decimal.Round(a, 4) == a).WithMessage("Size supports at most 4 decimal places.");

    public static IRuleBuilderOptions<T, string> Code<T>(this IRuleBuilder<T, string> r, int max = 30) =>
        r.NotEmpty().MaximumLength(max).Matches("^[A-Za-z0-9][A-Za-z0-9._/-]*$").WithMessage("Use letters, digits, '.', '_', '/' and '-' only.");
}

public class SaveCategoryRequestValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class SavePropertyTypeRequestValidator : AbstractValidator<SavePropertyTypeRequest>
{
    public SavePropertyTypeRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.Form).IsInEnum();
    }
}

public class SaveZoneRequestValidator : AbstractValidator<SaveZoneRequest>
{
    public SaveZoneRequestValidator()
    {
        RuleFor(x => x.Code).Code();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}

public class DimensionDtoValidator : AbstractValidator<DimensionDto>
{
    public DimensionDtoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Value).GreaterThanOrEqualTo(0).LessThanOrEqualTo(999_999_999m).Must(v => decimal.Round(v, 4) == v).WithMessage("At most 4 decimal places.");
        RuleFor(x => x.Unit).MaximumLength(20);
    }
}

public class FeatureDtoValidator : AbstractValidator<FeatureDto>
{
    public FeatureDtoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Value).MaximumLength(200);
    }
}

public class CreatePropertyRequestValidator : AbstractValidator<CreatePropertyRequest>
{
    public CreatePropertyRequestValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.PropertyCode).Code(40);
        RuleFor(x => x.PropertyNumber).NotEmpty().MaximumLength(30);
        RuleFor(x => x.PropertyTypeId).GreaterThan(0);
        RuleFor(x => x.Size).ValidSize();
        RuleFor(x => x.MeasurementUnit).IsInEnum();
        RuleFor(x => x.DevelopmentStatus).IsInEnum();
        RuleFor(x => x.LocationDescription).MaximumLength(500);
        RuleFor(x => x.Remarks).MaximumLength(2000);
        RuleFor(x => x.FloorNumber).InclusiveBetween(-5, 200).When(x => x.FloorNumber.HasValue);
        RuleForEach(x => x.Dimensions!).SetValidator(new DimensionDtoValidator()).When(x => x.Dimensions is not null);
        RuleForEach(x => x.Features!).SetValidator(new FeatureDtoValidator()).When(x => x.Features is not null);
        RuleFor(x => x.Dimensions).Must(d => d!.Select(i => i.Name.Trim().ToLowerInvariant()).Distinct().Count() == d!.Count)
            .When(x => x.Dimensions is not null).WithMessage("Dimension names must be unique.");
        RuleFor(x => x.Features).Must(d => d!.Select(i => i.Name.Trim().ToLowerInvariant()).Distinct().Count() == d!.Count)
            .When(x => x.Features is not null).WithMessage("Feature names must be unique.");
    }
}

public class UpdatePropertyRequestValidator : AbstractValidator<UpdatePropertyRequest>
{
    public UpdatePropertyRequestValidator()
    {
        RuleFor(x => x.PropertyNumber).NotEmpty().MaximumLength(30);
        RuleFor(x => x.PropertyTypeId).GreaterThan(0);
        RuleFor(x => x.Size).ValidSize();
        RuleFor(x => x.MeasurementUnit).IsInEnum();
        RuleFor(x => x.DevelopmentStatus).IsInEnum();
        RuleFor(x => x.LocationDescription).MaximumLength(500);
        RuleFor(x => x.Remarks).MaximumLength(2000);
        RuleFor(x => x.FloorNumber).InclusiveBetween(-5, 200).When(x => x.FloorNumber.HasValue);
        RuleForEach(x => x.Dimensions!).SetValidator(new DimensionDtoValidator()).When(x => x.Dimensions is not null);
        RuleForEach(x => x.Features!).SetValidator(new FeatureDtoValidator()).When(x => x.Features is not null);
        RuleFor(x => x.Dimensions).Must(d => d!.Select(i => i.Name.Trim().ToLowerInvariant()).Distinct().Count() == d!.Count)
            .When(x => x.Dimensions is not null).WithMessage("Dimension names must be unique.");
        RuleFor(x => x.Features).Must(d => d!.Select(i => i.Name.Trim().ToLowerInvariant()).Distinct().Count() == d!.Count)
            .When(x => x.Features is not null).WithMessage("Feature names must be unique.");
    }
}

public class ReservePropertyRequestValidator : AbstractValidator<ReservePropertyRequest>
{
    public ReservePropertyRequestValidator()
    {
        RuleFor(x => x.ExpiryDate).GreaterThanOrEqualTo(x => x.ReservationDate).WithMessage("Expiry date cannot be before the reservation date.");
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0).LessThanOrEqualTo(9_999_999_999_999m).Must(a => decimal.Round(a, 2) == a).WithMessage("Amount supports at most 2 decimal places.");
        RuleFor(x => x.ReservedForName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ReservedForContact).MaximumLength(200);
        RuleFor(x => x.ApprovedByUserId).NotEmpty().WithMessage("An approver is required.");
    }
}

public class ReleaseReservationRequestValidator : AbstractValidator<ReleaseReservationRequest>
{
    public ReleaseReservationRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(1000);
}

public class ChangePropertyStatusRequestValidator : AbstractValidator<ChangePropertyStatusRequest>
{
    public ChangePropertyStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(1000);
    }
}
