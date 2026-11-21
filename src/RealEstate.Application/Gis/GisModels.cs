using FluentValidation;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Gis;

public sealed record SrsDto(long Id, string Code, int Srid, string Name, SpatialCoordinateKind Kind, string Unit, string? Description, bool IsSystem, bool IsActive, string RowVersion);

public sealed record SaveSrsRequest(string Code, int Srid, string Name, SpatialCoordinateKind Kind, string Unit, string? Description, bool IsActive, string? RowVersion);

public sealed record BoundaryDto(long Id, string Scope, long ProjectId, string ProjectCode, long? PropertyId, string? PropertyCode, int VersionNo, long SrsId, string SrsCode, string GeometryType,
    int VertexCount, decimal MinX, decimal MinY, decimal MaxX, decimal MaxY, BoundaryStatus Status, string SurveyReference, string SurveyedBy, DateOnly SurveyDate, long? SourceDocumentId,
    string? Remarks, string? SubmittedBy, DateTime CreatedAtUtc, string? VerifiedBy, DateTime? VerifiedAtUtc, string? DecisionComment, DateTime? SupersededAtUtc, string RowVersion, string? GeometryJson);

public sealed record SubmitBoundaryRequest(long SrsId, string GeometryJson, string SurveyReference, string SurveyedBy, DateOnly SurveyDate, long? SourceDocumentId, string? Remarks);

public sealed record BoundaryDecisionRequest(bool Approve, string? Comment, string? RowVersion);

public sealed record MapLayerDto(long Id, long ProjectId, string ProjectCode, string Code, string Name, MapLayerKind Kind, long SrsId, string SrsCode, int SortOrder, bool IsVisibleByDefault,
    string? StyleJson, long? SourceDocumentId, string? Description, bool IsActive, string RowVersion);

public sealed record SaveMapLayerRequest(string Code, string Name, MapLayerKind Kind, long SrsId, int SortOrder, bool IsVisibleByDefault, string? StyleJson, long? SourceDocumentId, string? Description,
    bool IsActive, string? RowVersion);

public sealed record MapDocumentDto(long Id, long ProjectId, string Title, string FileName, MapDocumentType DocumentType, long? SrsId, string? SrsCode, string? SurveyReference, DateOnly? SurveyDate,
    string? Description, string ContentType, long SizeBytes, DateTime CreatedAtUtc, string? CreatedBy);

public sealed record GisSummaryDto(long ProjectId, string ProjectCode, string ProjectName, bool HasVerifiedBoundary, int? VerifiedVersion, int DraftBoundaries, int PropertyBoundariesVerified, int PropertiesTotal,
    int Layers, int Documents);

public class SaveSrsRequestValidator : AbstractValidator<SaveSrsRequest>
{
    public SaveSrsRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9:_.-]+$").WithMessage("Use letters, digits and : _ . - only.");
        RuleFor(x => x.Srid).GreaterThan(0).LessThan(1_000_000);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public class SubmitBoundaryRequestValidator : AbstractValidator<SubmitBoundaryRequest>
{
    public SubmitBoundaryRequestValidator()
    {
        RuleFor(x => x.SrsId).GreaterThan(0);
        RuleFor(x => x.GeometryJson).NotEmpty();
        RuleFor(x => x.SurveyReference).NotEmpty().MaximumLength(100);
        RuleFor(x => x.SurveyedBy).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SurveyDate).Must(d => d != default).WithMessage("The survey date is required.");
        RuleFor(x => x.Remarks).MaximumLength(1000);
    }
}

public class BoundaryDecisionRequestValidator : AbstractValidator<BoundaryDecisionRequest>
{
    public BoundaryDecisionRequestValidator()
    {
        RuleFor(x => x.Comment).MaximumLength(500);
        RuleFor(x => x.Comment).NotEmpty().MinimumLength(5).When(x => !x.Approve).WithMessage("A comment of at least 5 characters is required to reject a boundary.");
    }
}

public class SaveMapLayerRequestValidator : AbstractValidator<SaveMapLayerRequest>
{
    public SaveMapLayerRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Use letters, digits and _ . - only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.SrsId).GreaterThan(0);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.StyleJson).MaximumLength(2000).Must(BeJsonObject).When(x => !string.IsNullOrWhiteSpace(x.StyleJson)).WithMessage("The style must be a JSON object.");
    }

    private static bool BeJsonObject(string? s)
    {
        try { using var d = System.Text.Json.JsonDocument.Parse(s!); return d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
