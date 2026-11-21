using System.Linq.Expressions;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Gis;

namespace RealEstate.Application.Gis;

/// <summary>
/// GIS foundation: spatial reference systems, versioned surveyed boundaries (project and property), map layers and map documents. No map is drawn here;
/// the module stores validated, verified survey data so the later map viewer, MySQL spatial import and QGIS exchange can build on it. Boundaries are never
/// edited: a new survey is a new version, and only a boundary verified by someone other than its submitter counts as exact.
/// </summary>
public class GisService(IAppDbContext db, ProjectAccessService access, DocumentUploader uploader, IFileStorage storage, IPermissionService permissions, ICurrentUser user, IAuditService audit,
    TimeProvider clock, IValidator<SaveSrsRequest> srsValidator, IValidator<SubmitBoundaryRequest> boundaryValidator, IValidator<BoundaryDecisionRequest> decisionValidator,
    IValidator<SaveMapLayerRequest> layerValidator)
{
    // ------------------------------------------------------------------------------------------------
    // spatial reference systems
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<SrsDto>> ListSrsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        var q = db.SpatialReferenceSystems.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(s => s.IsActive);
        return await q.OrderBy(s => s.Srid).Select(s => new SrsDto(s.Id, s.Code, s.Srid, s.Name, s.Kind, s.Unit, s.Description, s.IsSystem, s.IsActive, s.RowVersion.ToToken())).ToListAsync(ct);
    }

    public async Task<SrsDto> SaveSrsAsync(long? id, SaveSrsRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        await srsValidator.ValidateOrThrowAsync(r, ct);
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Spatial reference systems are company-wide and need access to all projects.");
        var code = r.Code.Trim().ToUpperInvariant();
        SpatialReferenceSystem s; object? before = null;
        if (id is null)
        {
            if (await db.SpatialReferenceSystems.AnyAsync(x => x.Code == code || x.Srid == r.Srid, ct)) throw Invalid(nameof(r.Code), "A system with this code or SRID already exists.");
            s = new SpatialReferenceSystem { Code = code, Srid = r.Srid, Kind = r.Kind };
            db.SpatialReferenceSystems.Add(s);
        }
        else
        {
            s = await db.SpatialReferenceSystems.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Spatial reference system not found.");
            db.UseRowVersion(s, r.RowVersion);
            if (s.IsSystem && !r.IsActive) throw new DomainException("A standard system cannot be deactivated.");
            if (s.Code != code || s.Srid != r.Srid || s.Kind != r.Kind)
                throw new DomainException("Code, SRID and kind cannot change once created (boundaries refer to them).");
            before = new { s.Name, s.Unit, s.Description, s.IsActive };
        }
        s.Name = r.Name.Trim(); s.Unit = r.Unit.Trim(); s.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); s.IsActive = r.IsActive;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(id is null ? "Srs.Created" : "Srs.Updated", "SpatialReferenceSystem", s.Id.ToString(), before, new { s.Code, s.Srid, s.Name, s.Unit, s.IsActive }, UserId: user.UserId), ct);
        return new SrsDto(s.Id, s.Code, s.Srid, s.Name, s.Kind, s.Unit, s.Description, s.IsSystem, s.IsActive, s.RowVersion.ToToken());
    }

    // ------------------------------------------------------------------------------------------------
    // project boundaries
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<BoundaryDto>> ListProjectBoundariesAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await db.ProjectBoundaries.AsNoTracking().Where(b => b.ProjectId == projectId).OrderByDescending(b => b.VersionNo).Select(ProjectMap(false)).ToListAsync(ct);
    }

    public async Task<BoundaryDto> GetProjectBoundaryAsync(long projectId, long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await db.ProjectBoundaries.AsNoTracking().Where(b => b.ProjectId == projectId && b.Id == id).Select(ProjectMap(true)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Boundary not found.");
    }

    public async Task<BoundaryDto> SubmitProjectBoundaryAsync(long projectId, SubmitBoundaryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await EnsureWritableAsync(projectId, ct);
        var (srs, summary) = await ValidateAsync(r, projectId, ct);
        for (var attempt = 0; ; attempt++)
        {
            var next = (await db.ProjectBoundaries.Where(b => b.ProjectId == projectId).Select(b => (int?)b.VersionNo).MaxAsync(ct) ?? 0) + 1;
            var b = new ProjectBoundary { ProjectId = projectId, VersionNo = next };
            Fill(b, r, srs, summary);
            db.ProjectBoundaries.Add(b);
            try
            {
                await db.SaveChangesAsync(ct);
                await audit.RecordAsync(new AuditEntry("ProjectBoundary.Submitted", "ProjectBoundary", b.Id.ToString(), NewValues: new { projectId, b.VersionNo, b.SurveyReference, b.VertexCount, Srs = srs.Code }, UserId: user.UserId), ct);
                return await GetProjectBoundaryAsync(projectId, b.Id, ct);
            }
            catch (DbUpdateException ex) when (attempt < 3 && PaymentSupport.IsDuplicate(ex))
            {
                db.Entry(b).State = EntityState.Detached;   // another submission took this version number: take the next one
            }
        }
    }

    public async Task<BoundaryDto> DecideProjectBoundaryAsync(long projectId, long id, BoundaryDecisionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisVerify, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await decisionValidator.ValidateOrThrowAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = await db.ProjectBoundaries.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct) ?? throw new NotFoundException("Boundary not found.");
        db.UseRowVersion(b, r.RowVersion);
        EnsureVerifier(b);
        var now = clock.GetUtcNow().UtcDateTime;
        if (r.Approve)
        {
            var current = await db.ProjectBoundaries.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Status == BoundaryStatus.Verified, ct);
            if (current is not null) { current.Move(BoundaryStatus.Superseded); current.ActiveProjectId = null; current.SupersededAtUtc = now; await db.SaveChangesAsync(ct); }
            b.Move(BoundaryStatus.Verified); b.ActiveProjectId = projectId;
        }
        else b.Move(BoundaryStatus.Rejected);
        Decide(b, r, now);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry(r.Approve ? "ProjectBoundary.Verified" : "ProjectBoundary.Rejected", "ProjectBoundary", id.ToString(), new { Status = "Draft" },
            new { Status = b.Status.ToString(), b.VersionNo }, r.Comment?.Trim(), UserId: user.UserId), ct);
        await tx.CommitAsync(ct);
        return await GetProjectBoundaryAsync(projectId, id, ct);
    }

    // ------------------------------------------------------------------------------------------------
    // property boundaries
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<BoundaryDto>> ListPropertyBoundariesAsync(long propertyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        return await db.PropertyBoundaries.AsNoTracking().Where(b => b.PropertyId == propertyId).OrderByDescending(b => b.VersionNo).Select(PropertyMap(false)).ToListAsync(ct);
    }

    public async Task<BoundaryDto> GetPropertyBoundaryAsync(long propertyId, long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        return await db.PropertyBoundaries.AsNoTracking().Where(b => b.PropertyId == propertyId && b.Id == id).Select(PropertyMap(true)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Boundary not found.");
    }

    public async Task<BoundaryDto> SubmitPropertyBoundaryAsync(long propertyId, SubmitBoundaryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        var projectId = await EnsurePropertyAccessAsync(propertyId, ct);
        await EnsureWritableAsync(projectId, ct);
        var (srs, summary) = await ValidateAsync(r, projectId, ct);
        for (var attempt = 0; ; attempt++)
        {
            var next = (await db.PropertyBoundaries.Where(b => b.PropertyId == propertyId).Select(b => (int?)b.VersionNo).MaxAsync(ct) ?? 0) + 1;
            var b = new PropertyBoundary { PropertyId = propertyId, ProjectId = projectId, VersionNo = next };
            Fill(b, r, srs, summary);
            db.PropertyBoundaries.Add(b);
            try
            {
                await db.SaveChangesAsync(ct);
                await audit.RecordAsync(new AuditEntry("PropertyBoundary.Submitted", "PropertyBoundary", b.Id.ToString(), NewValues: new { propertyId, b.VersionNo, b.SurveyReference, b.VertexCount, Srs = srs.Code }, UserId: user.UserId), ct);
                return await GetPropertyBoundaryAsync(propertyId, b.Id, ct);
            }
            catch (DbUpdateException ex) when (attempt < 3 && PaymentSupport.IsDuplicate(ex)) { db.Entry(b).State = EntityState.Detached; }
        }
    }

    public async Task<BoundaryDto> DecidePropertyBoundaryAsync(long propertyId, long id, BoundaryDecisionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisVerify, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        await decisionValidator.ValidateOrThrowAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var b = await db.PropertyBoundaries.FirstOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct) ?? throw new NotFoundException("Boundary not found.");
        db.UseRowVersion(b, r.RowVersion);
        EnsureVerifier(b);
        var now = clock.GetUtcNow().UtcDateTime;
        if (r.Approve)
        {
            var current = await db.PropertyBoundaries.FirstOrDefaultAsync(x => x.PropertyId == propertyId && x.Status == BoundaryStatus.Verified, ct);
            if (current is not null) { current.Move(BoundaryStatus.Superseded); current.ActivePropertyId = null; current.SupersededAtUtc = now; await db.SaveChangesAsync(ct); }
            b.Move(BoundaryStatus.Verified); b.ActivePropertyId = propertyId;
        }
        else b.Move(BoundaryStatus.Rejected);
        Decide(b, r, now);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry(r.Approve ? "PropertyBoundary.Verified" : "PropertyBoundary.Rejected", "PropertyBoundary", id.ToString(), new { Status = "Draft" },
            new { Status = b.Status.ToString(), b.VersionNo }, r.Comment?.Trim(), UserId: user.UserId), ct);
        await tx.CommitAsync(ct);
        return await GetPropertyBoundaryAsync(propertyId, id, ct);
    }

    /// <summary>GeoJSON FeatureCollection of the verified project boundary and verified property boundaries (what a future map viewer or QGIS loads). Each feature keeps its own coordinate system in "srid".</summary>
    public async Task<string> ExportGeoJsonAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var features = new List<string>();
        var pb = await (from b in db.ProjectBoundaries.AsNoTracking()
                        join s in db.SpatialReferenceSystems.AsNoTracking() on b.SrsId equals s.Id
                        where b.ProjectId == projectId && b.Status == BoundaryStatus.Verified
                        select new { b.VersionNo, b.GeometryJson, b.SurveyReference, s.Srid }).FirstOrDefaultAsync(ct);
        if (pb is not null)
            features.Add(Feature(pb.GeometryJson, new Dictionary<string, object?> { ["kind"] = "ProjectBoundary", ["version"] = pb.VersionNo, ["survey"] = pb.SurveyReference, ["srid"] = pb.Srid }));
        var props = await (from b in db.PropertyBoundaries.AsNoTracking()
                           join s in db.SpatialReferenceSystems.AsNoTracking() on b.SrsId equals s.Id
                           join p in db.Properties.AsNoTracking() on b.PropertyId equals p.Id
                           where b.ProjectId == projectId && b.Status == BoundaryStatus.Verified
                           orderby p.PropertyCode
                           select new { p.Id, p.PropertyCode, Status = p.Status.ToString(), b.VersionNo, b.GeometryJson, b.SurveyReference, s.Srid }).ToListAsync(ct);
        features.AddRange(props.Select(p => Feature(p.GeometryJson, new Dictionary<string, object?>
        {
            ["kind"] = "Property", ["propertyId"] = p.Id, ["propertyCode"] = p.PropertyCode, ["status"] = p.Status, ["version"] = p.VersionNo, ["survey"] = p.SurveyReference, ["srid"] = p.Srid,
        })));
        return $"{{\"type\":\"FeatureCollection\",\"features\":[{string.Join(",", features)}]}}";
    }

    private static string Feature(string geometryJson, Dictionary<string, object?> properties)
    {
        using var doc = JsonDocument.Parse(geometryJson);
        var root = doc.RootElement;
        var geometry = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.GetString() == "Feature" && root.TryGetProperty("geometry", out var g) ? g.GetRawText() : root.GetRawText();
        return $"{{\"type\":\"Feature\",\"properties\":{JsonSerializer.Serialize(properties)},\"geometry\":{geometry}}}";
    }

    // ------------------------------------------------------------------------------------------------
    // layers
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<MapLayerDto>> ListLayersAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await db.MapLayers.AsNoTracking().Where(l => l.ProjectId == projectId).OrderBy(l => l.SortOrder).ThenBy(l => l.Code).Select(LayerMap).ToListAsync(ct);
    }

    public async Task<MapLayerDto> GetLayerAsync(long projectId, long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await db.MapLayers.AsNoTracking().Where(l => l.ProjectId == projectId && l.Id == id).Select(LayerMap).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Layer not found.");
    }

    public async Task<MapLayerDto> SaveLayerAsync(long projectId, long? id, SaveMapLayerRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await layerValidator.ValidateOrThrowAsync(r, ct);
        await EnsureWritableAsync(projectId, ct);
        var srs = await db.SpatialReferenceSystems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SrsId && s.IsActive, ct) ?? throw Invalid(nameof(r.SrsId), "Choose an active spatial reference system.");
        if (r.SourceDocumentId is { } did && !await db.MapDocuments.AnyAsync(d => d.Id == did && d.ProjectId == projectId && !d.IsDeleted, ct))
            throw Invalid(nameof(r.SourceDocumentId), "The source document does not belong to this project.");
        var code = r.Code.Trim().ToUpperInvariant();
        MapLayer l; object? before = null;
        if (id is null)
        {
            l = new MapLayer { ProjectId = projectId, Code = code };
            db.MapLayers.Add(l);
        }
        else
        {
            l = await db.MapLayers.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct) ?? throw new NotFoundException("Layer not found.");
            db.UseRowVersion(l, r.RowVersion);
            if (l.Code != code) throw new DomainException("The layer code cannot change.");
            before = new { l.Name, l.Kind, l.SrsId, l.SortOrder, l.IsVisibleByDefault, l.IsActive };
        }
        l.Name = r.Name.Trim(); l.Kind = r.Kind; l.SrsId = srs.Id; l.SortOrder = r.SortOrder; l.IsVisibleByDefault = r.IsVisibleByDefault;
        l.StyleJson = string.IsNullOrWhiteSpace(r.StyleJson) ? null : r.StyleJson.Trim(); l.SourceDocumentId = r.SourceDocumentId;
        l.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); l.IsActive = r.IsActive;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex)) { throw Invalid(nameof(r.Code), "This project already has a layer with that code."); }
        await audit.RecordAsync(new AuditEntry(id is null ? "MapLayer.Created" : "MapLayer.Updated", "MapLayer", l.Id.ToString(), before, new { projectId, l.Code, l.Name, l.Kind, l.SrsId, l.IsActive }, UserId: user.UserId), ct);
        return await GetLayerAsync(projectId, l.Id, ct);
    }

    // ------------------------------------------------------------------------------------------------
    // map documents
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<MapDocumentDto>> ListDocumentsAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await (from d in db.MapDocuments.AsNoTracking()
                      join s in db.SpatialReferenceSystems.AsNoTracking() on d.SrsId equals s.Id into ss
                      from s in ss.DefaultIfEmpty()
                      where d.ProjectId == projectId && !d.IsDeleted
                      orderby d.CreatedAtUtc descending
                      select new MapDocumentDto(d.Id, d.ProjectId, d.Title, d.FileName, d.DocumentType, d.SrsId, s == null ? null : s.Code, d.SurveyReference, d.SurveyDate, d.Description,
                          d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);
    }

    public async Task<MapDocumentDto> UploadDocumentAsync(long projectId, MapDocumentType type, string? title, long? srsId, string? surveyReference, DateOnly? surveyDate, string? description, FileUpload file,
        CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        await access.EnsureAccessAsync(projectId, ct);
        if (!Enum.IsDefined(type)) throw Invalid("DocumentType", "Invalid document type.");
        await EnsureWritableAsync(projectId, ct);
        if (srsId is { } sid && !await db.SpatialReferenceSystems.AnyAsync(s => s.Id == sid && s.IsActive, ct)) throw Invalid("SrsId", "Choose an active spatial reference system.");
        if (surveyDate is { } sd && sd > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(1))) throw Invalid("SurveyDate", "The survey date cannot be in the future.");
        var stored = await uploader.StoreAsync(file, $"gis/{projectId}", ct);
        var doc = new MapDocument
        {
            ProjectId = projectId, DocumentType = type, Title = string.IsNullOrWhiteSpace(title) ? stored.SafeFileName : Cut(title.Trim(), 200)!, SrsId = srsId, SurveyReference = Cut(surveyReference?.Trim(), 100),
            SurveyDate = surveyDate, Description = Cut(description?.Trim(), 1000), FileName = stored.SafeFileName, StorageKey = stored.File.Key, ContentType = stored.ContentType,
            SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.MapDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("MapDocument.Uploaded", "MapDocument", doc.Id.ToString(), NewValues: new { projectId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256, type }, UserId: user.UserId), ct);
        return (await ListDocumentsAsync(projectId, ct)).First(d => d.Id == doc.Id);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long projectId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var d = await db.MapDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long projectId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisManage, ct);
        await access.EnsureAccessAsync(projectId, ct);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw Invalid("Reason", "Give a reason of at least 5 characters.");
        var d = await db.MapDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        if (await db.ProjectBoundaries.AnyAsync(b => b.SourceDocumentId == documentId && b.Status == BoundaryStatus.Verified, ct)
            || await db.PropertyBoundaries.AnyAsync(b => b.SourceDocumentId == documentId && b.Status == BoundaryStatus.Verified, ct))
            throw new DomainException("This document is the source of a verified boundary and cannot be removed.");
        d.MarkDeleted(clock.GetUtcNow().UtcDateTime, user.UserName ?? "unknown", reason.Trim());
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("MapDocument.Removed", "MapDocument", documentId.ToString(), new { projectId, d.Title, d.FileName }, Reason: reason.Trim(), UserId: user.UserId), ct);
    }

    // ------------------------------------------------------------------------------------------------
    // overview
    // ------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<GisSummaryDto>> SummaryAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GisView, ct);
        var projects = await (await access.ScopeAsync(db.Projects.AsNoTracking(), ct)).OrderBy(p => p.ProjectCode).Select(p => new { p.Id, p.ProjectCode, p.ProjectName }).ToListAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var boundaries = await db.ProjectBoundaries.AsNoTracking().Where(b => ids.Contains(b.ProjectId)).Select(b => new { b.ProjectId, b.Status, b.VersionNo }).ToListAsync(ct);
        var props = await db.PropertyBoundaries.AsNoTracking().Where(b => ids.Contains(b.ProjectId) && b.Status == BoundaryStatus.Verified).GroupBy(b => b.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var propTotal = await db.Properties.AsNoTracking().Where(p => ids.Contains(p.ProjectId)).GroupBy(p => p.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var layers = await db.MapLayers.AsNoTracking().Where(l => ids.Contains(l.ProjectId) && l.IsActive).GroupBy(l => l.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var docs = await db.MapDocuments.AsNoTracking().Where(d => ids.Contains(d.ProjectId) && !d.IsDeleted).GroupBy(d => d.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return [.. projects.Select(p =>
        {
            var own = boundaries.Where(b => b.ProjectId == p.Id).ToList();
            var verified = own.FirstOrDefault(b => b.Status == BoundaryStatus.Verified);
            return new GisSummaryDto(p.Id, p.ProjectCode, p.ProjectName, verified is not null, verified?.VersionNo, own.Count(b => b.Status == BoundaryStatus.Draft), props.GetValueOrDefault(p.Id),
                propTotal.GetValueOrDefault(p.Id), layers.GetValueOrDefault(p.Id), docs.GetValueOrDefault(p.Id));
        })];
    }

    // ------------------------------------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------------------------------------
    private async Task EnsureWritableAsync(long projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
    }

    private async Task<(SpatialReferenceSystem Srs, GeoJsonSummary Summary)> ValidateAsync(SubmitBoundaryRequest r, long projectId, CancellationToken ct)
    {
        await boundaryValidator.ValidateOrThrowAsync(r, ct);
        if (r.SurveyDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(1))) throw Invalid(nameof(r.SurveyDate), "The survey date cannot be in the future.");
        var srs = await db.SpatialReferenceSystems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SrsId && s.IsActive, ct) ?? throw Invalid(nameof(r.SrsId), "Choose an active spatial reference system.");
        if (r.SourceDocumentId is { } did && !await db.MapDocuments.AnyAsync(d => d.Id == did && d.ProjectId == projectId && !d.IsDeleted, ct))
            throw Invalid(nameof(r.SourceDocumentId), "The source document does not belong to this project.");
        GeoJsonSummary summary;
        try { summary = GeoJsonBoundary.Parse(r.GeometryJson, srs.Kind); }
        catch (DomainException ex) { throw Invalid(nameof(r.GeometryJson), ex.Message); }
        return (srs, summary);
    }

    private void Fill(BoundaryBase b, SubmitBoundaryRequest r, SpatialReferenceSystem srs, GeoJsonSummary s)
    {
        b.SrsId = srs.Id; b.GeometryType = s.GeometryType; b.GeometryJson = r.GeometryJson.Trim(); b.VertexCount = s.VertexCount;
        b.MinX = s.MinX; b.MinY = s.MinY; b.MaxX = s.MaxX; b.MaxY = s.MaxY;
        b.SurveyReference = r.SurveyReference.Trim(); b.SurveyedBy = r.SurveyedBy.Trim(); b.SurveyDate = r.SurveyDate; b.SourceDocumentId = r.SourceDocumentId;
        b.Remarks = string.IsNullOrWhiteSpace(r.Remarks) ? null : r.Remarks.Trim();
        b.SubmittedByUserId = user.UserId ?? throw new ForbiddenException("Authentication required.");
        b.Status = BoundaryStatus.Draft;
    }

    private void EnsureVerifier(BoundaryBase b)
    {
        if (b.Status != BoundaryStatus.Draft) throw new DomainException("Only a draft boundary can be verified or rejected.");
        if (b.SubmittedByUserId == user.UserId) throw new ForbiddenException("A boundary must be verified by someone other than the person who submitted it.");
    }

    private void Decide(BoundaryBase b, BoundaryDecisionRequest r, DateTime now)
    {
        b.VerifiedByUserId = user.UserId; b.VerifiedBy = user.UserName; b.VerifiedAtUtc = now;
        b.DecisionComment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim();
    }

    private async Task<long> EnsurePropertyAccessAsync(long propertyId, CancellationToken ct)
    {
        var projectId = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => (long?)p.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this property.");
            throw new NotFoundException("Property not found.");
        }
        await access.EnsureAccessAsync(projectId.Value, ct);
        return projectId.Value;
    }

    private Expression<Func<ProjectBoundary, BoundaryDto>> ProjectMap(bool geometry) => b => new BoundaryDto(b.Id, "Project", b.ProjectId,
        db.Projects.Where(p => p.Id == b.ProjectId).Select(p => p.ProjectCode).FirstOrDefault()!, null, null, b.VersionNo, b.SrsId, db.SpatialReferenceSystems.Where(s => s.Id == b.SrsId).Select(s => s.Code).FirstOrDefault()!,
        b.GeometryType, b.VertexCount, b.MinX, b.MinY, b.MaxX, b.MaxY, b.Status, b.SurveyReference, b.SurveyedBy, b.SurveyDate, b.SourceDocumentId, b.Remarks, b.CreatedBy, b.CreatedAtUtc, b.VerifiedBy,
        b.VerifiedAtUtc, b.DecisionComment, b.SupersededAtUtc, Convert.ToBase64String(b.RowVersion), geometry ? b.GeometryJson : null);

    private Expression<Func<PropertyBoundary, BoundaryDto>> PropertyMap(bool geometry) => b => new BoundaryDto(b.Id, "Property", b.ProjectId,
        db.Projects.Where(p => p.Id == b.ProjectId).Select(p => p.ProjectCode).FirstOrDefault()!, b.PropertyId, db.Properties.Where(p => p.Id == b.PropertyId).Select(p => p.PropertyCode).FirstOrDefault(), b.VersionNo,
        b.SrsId, db.SpatialReferenceSystems.Where(s => s.Id == b.SrsId).Select(s => s.Code).FirstOrDefault()!, b.GeometryType, b.VertexCount, b.MinX, b.MinY, b.MaxX, b.MaxY, b.Status, b.SurveyReference,
        b.SurveyedBy, b.SurveyDate, b.SourceDocumentId, b.Remarks, b.CreatedBy, b.CreatedAtUtc, b.VerifiedBy, b.VerifiedAtUtc, b.DecisionComment, b.SupersededAtUtc, Convert.ToBase64String(b.RowVersion),
        geometry ? b.GeometryJson : null);

    private Expression<Func<MapLayer, MapLayerDto>> LayerMap => l => new MapLayerDto(l.Id, l.ProjectId, db.Projects.Where(p => p.Id == l.ProjectId).Select(p => p.ProjectCode).FirstOrDefault()!,
        l.Code, l.Name, l.Kind, l.SrsId, db.SpatialReferenceSystems.Where(s => s.Id == l.SrsId).Select(s => s.Code).FirstOrDefault()!, l.SortOrder, l.IsVisibleByDefault, l.StyleJson, l.SourceDocumentId,
        l.Description, l.IsActive, Convert.ToBase64String(l.RowVersion));

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}
