using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Projects;

public sealed record ProjectAddressDto(string? AddressLine, string? Locality, string? Province, string? Country, string? PostalCode,
    decimal? Latitude, decimal? Longitude, string? Landmark);

public sealed record ProjectListItemDto(long Id, string ProjectCode, string ProjectName, string City, ProjectStatus Status,
    decimal InitialArea, decimal CurrentArea, MeasurementUnit MeasurementUnit, Guid? ProjectManagerId, string? ProjectManagerName,
    DateOnly? StartDate, DateOnly? ExpectedCompletionDate);

public sealed class ProjectListRequest : PagedRequest
{
    public ProjectStatus? Status { get; set; }
    public string? City { get; set; }
}

public sealed record StatusHistoryDto(ProjectStatus? FromStatus, ProjectStatus ToStatus, string Reason, DateTime ChangedAtUtc, string? ChangedBy);
public sealed record ManagerHistoryDto(Guid UserId, string? ManagerName, DateTime AssignedFromUtc, DateTime? AssignedToUtc, string? AssignedBy, string? Reason);

public sealed record ProjectDetailDto(long Id, string ProjectCode, string ProjectName, string? Description, string City, string? Address,
    DateOnly? StartDate, DateOnly? ExpectedCompletionDate, decimal InitialArea, decimal CurrentArea, MeasurementUnit MeasurementUnit,
    ProjectStatus Status, Guid? ProjectManagerId, string? ProjectManagerName, ProjectAddressDto? AddressDetails, string RowVersion,
    DateTime CreatedAtUtc, string? CreatedBy, DateTime? ModifiedAtUtc, string? ModifiedBy,
    IReadOnlyList<StatusHistoryDto> StatusHistory, IReadOnlyList<ManagerHistoryDto> ManagerHistory);

public sealed record CreateProjectRequest(string ProjectCode, string ProjectName, string? Description, string City, string? Address,
    DateOnly? StartDate, DateOnly? ExpectedCompletionDate, decimal InitialArea, MeasurementUnit MeasurementUnit,
    Guid? ProjectManagerId, ProjectAddressDto? AddressDetails);

/// <summary>Area and code are intentionally absent: areas change only through land acquisitions/adjustments.</summary>
public sealed record UpdateProjectRequest(string ProjectName, string? Description, string City, string? Address,
    DateOnly? StartDate, DateOnly? ExpectedCompletionDate, Guid? ProjectManagerId, ProjectAddressDto? AddressDetails, string? RowVersion);

public sealed record ChangeProjectStatusRequest(ProjectStatus Status, string Reason, string? RowVersion);

public sealed record DocumentDto(long Id, string Title, string FileName, string DocumentType, string ContentType, long SizeBytes,
    DateTime UploadedAtUtc, string? UploadedBy, string? Description, long? RelatedId);

public class ProjectAddressDtoValidator : AbstractValidator<ProjectAddressDto>
{
    public ProjectAddressDtoValidator()
    {
        RuleFor(x => x.AddressLine).MaximumLength(500);
        RuleFor(x => x.Locality).MaximumLength(150);
        RuleFor(x => x.Province).MaximumLength(100);
        RuleFor(x => x.Country).MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.Landmark).MaximumLength(250);
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m).When(x => x.Longitude.HasValue);
    }
}

public class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.ProjectCode).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9][A-Za-z0-9._-]*$")
            .WithMessage("Project code may contain letters, digits, '.', '_' and '-'.");
        RuleFor(x => x.ProjectName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.InitialArea).ValidArea();
        RuleFor(x => x.MeasurementUnit).IsInEnum();
        RuleFor(x => x.ExpectedCompletionDate).GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.StartDate.HasValue && x.ExpectedCompletionDate.HasValue).WithMessage("Expected completion cannot be before the start date.");
        RuleFor(x => x.AddressDetails!).SetValidator(new ProjectAddressDtoValidator()).When(x => x.AddressDetails is not null);
    }
}

public class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.ProjectName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.ExpectedCompletionDate).GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.StartDate.HasValue && x.ExpectedCompletionDate.HasValue).WithMessage("Expected completion cannot be before the start date.");
        RuleFor(x => x.AddressDetails!).SetValidator(new ProjectAddressDtoValidator()).When(x => x.AddressDetails is not null);
    }
}

public class ChangeProjectStatusRequestValidator : AbstractValidator<ChangeProjectStatusRequest>
{
    public ChangeProjectStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(1000);
    }
}
