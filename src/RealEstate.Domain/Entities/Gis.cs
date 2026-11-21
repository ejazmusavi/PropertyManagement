using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>A coordinate system (EPSG code) that boundaries, layers and map documents refer to. System rows are seeded and cannot be removed.</summary>
public class SpatialReferenceSystem : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public int Srid { get; set; }
    public string Name { get; set; } = default!;
    public SpatialCoordinateKind Kind { get; set; }
    public string Unit { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Shared shape of a surveyed outline. A boundary is a numbered version: new surveys add a version, the Verified one in force is superseded, never edited.
/// Only a Verified boundary counts as exact (it needs the survey reference, surveyor and date, and a verifier other than the submitter).
/// The geometry is stored as GeoJSON text; a MySQL spatial column can be derived from it later.
/// </summary>
public abstract class BoundaryBase : AuditableEntity<long>
{
    public int VersionNo { get; set; }
    public long SrsId { get; set; }
    public string GeometryType { get; set; } = default!;
    public string GeometryJson { get; set; } = default!;
    public int VertexCount { get; set; }
    public decimal MinX { get; set; }
    public decimal MinY { get; set; }
    public decimal MaxX { get; set; }
    public decimal MaxY { get; set; }
    public BoundaryStatus Status { get; set; } = BoundaryStatus.Draft;
    public string SurveyReference { get; set; } = default!;
    public string SurveyedBy { get; set; } = default!;
    public DateOnly SurveyDate { get; set; }
    public long? SourceDocumentId { get; set; }
    public string? Remarks { get; set; }
    public Guid SubmittedByUserId { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public DateTime? SupersededAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public static bool CanTransition(BoundaryStatus from, BoundaryStatus to) => (from, to) switch
    {
        (BoundaryStatus.Draft, BoundaryStatus.Verified) => true,
        (BoundaryStatus.Draft, BoundaryStatus.Rejected) => true,
        (BoundaryStatus.Verified, BoundaryStatus.Superseded) => true,
        _ => false,
    };

    public void Move(BoundaryStatus to)
    {
        if (!CanTransition(Status, to)) throw new DomainException($"A boundary cannot move from {Status} to {to}.");
        Status = to;
    }
}

public class ProjectBoundary : BoundaryBase
{
    public long ProjectId { get; set; }
    /// <summary>Set to ProjectId while the boundary is Verified (the one in force); null otherwise. Unique, so a project has at most one.</summary>
    public long? ActiveProjectId { get; set; }
}

public class PropertyBoundary : BoundaryBase
{
    public long PropertyId { get; set; }
    /// <summary>The property's project, kept for project-level access checks.</summary>
    public long ProjectId { get; set; }
    public long? ActivePropertyId { get; set; }
}

/// <summary>A named group of map features of a project (boundary, blocks, streets, plots...), prepared for the future map viewer.</summary>
public class MapLayer : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public MapLayerKind Kind { get; set; }
    public long SrsId { get; set; }
    public int SortOrder { get; set; }
    public bool IsVisibleByDefault { get; set; } = true;
    /// <summary>Optional style hints for the map viewer (JSON object).</summary>
    public string? StyleJson { get; set; }
    public long? SourceDocumentId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A survey plan, GeoJSON export, shapefile, CAD or imagery file of a project, stored outside the web root.</summary>
public class MapDocument : StoredDocument
{
    public long ProjectId { get; set; }
    public MapDocumentType DocumentType { get; set; }
    public long? SrsId { get; set; }
    public string? SurveyReference { get; set; }
    public DateOnly? SurveyDate { get; set; }
    public string? Description { get; set; }
}
