using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.LandManagement;

public sealed record DistributionLineDto(LandCategory Category, decimal Area, decimal Percentage);
public sealed record LandParcelDto(long Id, string ParcelCode, string? Description, LandParcelType ParcelType, decimal Area, string? TitleReference, string? Location);
public sealed record LandAcquisitionDto(long Id, DateOnly AcquisitionDate, string SellerName, decimal Area, decimal? Cost, string? ReferenceNumber, string? Notes, string? ParcelCode);
public sealed record LandAdjustmentDto(long Id, DateOnly AdjustmentDate, LandAdjustmentType Type, decimal PreviousArea, decimal AdjustmentArea, decimal NewArea,
    string Reason, string SupportingReference, string ApprovedByName, DateTime ApprovedAtUtc);

public sealed record LandOverviewDto(long ProjectId, string ProjectCode, string ProjectName, MeasurementUnit MeasurementUnit, ProjectStatus Status,
    decimal OriginalArea, decimal AcquiredArea, decimal AddedArea, decimal ReducedArea, decimal TotalProjectArea,
    decimal SaleableArea, decimal ClassifiedArea, decimal UnclassifiedArea, bool IsReconciled, string RowVersion,
    string DistributionVersion, IReadOnlyList<DistributionLineDto> Distribution, IReadOnlyList<LandParcelDto> Parcels,
    IReadOnlyList<LandAcquisitionDto> Acquisitions, IReadOnlyList<LandAdjustmentDto> Adjustments);

public sealed record AreaHistoryDto(long Id, DateTime ChangedAtUtc, DateOnly EffectiveDate, decimal PreviousArea, decimal Change, decimal NewArea,
    AreaChangeSource Source, string Reason, string? ChangedBy, long? AdjustmentId, long? AcquisitionId, string? SupportingReference, string? ApprovedBy);

public sealed record DistributionHistoryDto(LandCategory Category, decimal PreviousArea, decimal NewArea, string Reason, DateTime ChangedAtUtc, string? ChangedBy);

public sealed record LandDocumentDto(long Id, string Title, string FileName, LandDocumentType DocumentType, string ContentType, long SizeBytes,
    DateTime UploadedAtUtc, string? UploadedBy, long? ParcelId, long? AcquisitionId, long? AdjustmentId);

public sealed record LandDocumentUpload(FileUpload File, LandDocumentType Type, string? Title);

public sealed record AddLandParcelRequest(string ParcelCode, string? Description, decimal Area, string? TitleReference, string? Location);

public sealed record AcquireLandRequest(DateOnly AcquisitionDate, string SellerName, decimal Area, decimal? Cost, string? ReferenceNumber,
    string? Notes, string? ParcelCode, string? RowVersion);

public sealed record AdjustLandRequest(DateOnly AdjustmentDate, LandAdjustmentType Type, decimal Area, string Reason, string SupportingReference,
    Guid ApprovedByUserId, string? RowVersion);

public sealed record DistributionInput(LandCategory Category, decimal Area);
/// <summary><c>DistributionVersion</c> is the <c>LandOverviewDto.DistributionVersion</c> the edit was based on; a stale value is rejected.</summary>
public sealed record SetLandDistributionRequest(IReadOnlyList<DistributionInput> Lines, string Reason, string DistributionVersion);

public class AddLandParcelRequestValidator : AbstractValidator<AddLandParcelRequest>
{
    public AddLandParcelRequestValidator()
    {
        RuleFor(x => x.ParcelCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Area).ValidArea();
        RuleFor(x => x.TitleReference).MaximumLength(200);
        RuleFor(x => x.Location).MaximumLength(300);
    }
}

public class AcquireLandRequestValidator : AbstractValidator<AcquireLandRequest>
{
    public AcquireLandRequestValidator()
    {
        RuleFor(x => x.AcquisitionDate).Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))).WithMessage("The acquisition date cannot be in the future.");
        RuleFor(x => x.SellerName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Area).ValidArea();
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, false).When(x => x.Cost.HasValue);
        RuleFor(x => x.ReferenceNumber).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.ParcelCode).MaximumLength(50);
    }
}

public class AdjustLandRequestValidator : AbstractValidator<AdjustLandRequest>
{
    public AdjustLandRequestValidator()
    {
        RuleFor(x => x.AdjustmentDate).Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))).WithMessage("The adjustment date cannot be in the future.");
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Area).ValidArea();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
        RuleFor(x => x.SupportingReference).NotEmpty().WithMessage("Supporting documents are required: enter the document reference.").MaximumLength(300);
        RuleFor(x => x.ApprovedByUserId).NotEmpty().WithMessage("An approver is required.");
    }
}

public class SetLandDistributionRequestValidator : AbstractValidator<SetLandDistributionRequest>
{
    public SetLandDistributionRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(1000);
        RuleFor(x => x.DistributionVersion).NotEmpty().WithMessage("The distribution version is required. Reload the distribution and try again.").MaximumLength(100);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(i => i.Category).Must(c => c != LandCategory.Unclassified && Enum.IsDefined(c)).WithMessage("Unclassified area is calculated automatically.");
            l.RuleFor(i => i.Area).GreaterThanOrEqualTo(0).Must(a => decimal.Round(a, 4) == a).WithMessage("Area must be zero or more with at most 4 decimals.");
        });
        RuleFor(x => x.Lines).Must(ls => ls.Select(l => l.Category).Distinct().Count() == ls.Count).WithMessage("Each category may appear only once.");
    }
}
