using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.LandManagement;

/// <summary>Land parcels, acquisitions, adjustments and distribution. Every project-area change is a controlled transaction with history.</summary>
public class LandService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit,
    IUserQueryService users, DocumentUploader uploader, IFileStorage storage, TimeProvider clock,
    IValidator<AddLandParcelRequest> parcelValidator, IValidator<AcquireLandRequest> acquireValidator,
    IValidator<AdjustLandRequest> adjustValidator, IValidator<SetLandDistributionRequest> distributionValidator)
{
    // ---------- queries ----------
    public async Task<LandOverviewDto> GetOverviewAsync(long projectId, CancellationToken ct = default)
    {
        await RequireViewAsync(projectId, ct);
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        var dist = await db.LandDistributions.AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(ct);
        var parcels = await db.LandParcels.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.ParcelCode)
            .Select(x => new LandParcelDto(x.Id, x.ParcelCode, x.Description, x.ParcelType, x.Area, x.TitleReference, x.Location)).ToListAsync(ct);
        var acq = await db.LandAcquisitions.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.AcquisitionDate).ThenByDescending(x => x.Id)
            .Select(x => new LandAcquisitionDto(x.Id, x.AcquisitionDate, x.SellerName, x.Area, x.Cost, x.ReferenceNumber, x.Notes, x.Parcel != null ? x.Parcel.ParcelCode : null)).ToListAsync(ct);
        var adj = await db.LandAdjustments.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.AdjustmentDate).ThenByDescending(x => x.Id)
            .Select(x => new LandAdjustmentDto(x.Id, x.AdjustmentDate, x.Type, x.PreviousArea, x.AdjustmentArea, x.NewArea, x.Reason, x.SupportingReference, x.ApprovedByName, x.ApprovedAtUtc)).ToListAsync(ct);

        var acquired = acq.Sum(a => a.Area);
        var added = adj.Where(a => a.Type == LandAdjustmentType.Addition).Sum(a => a.AdjustmentArea);
        var reduced = adj.Where(a => a.Type == LandAdjustmentType.Reduction).Sum(a => -a.AdjustmentArea);
        decimal Get(LandCategory c) => dist.FirstOrDefault(d => d.Category == c)?.Area ?? 0m;
        var lines = LandRules.ClassifiedCategories.Append(LandCategory.Unclassified)
            .Select(c => new DistributionLineDto(c, Get(c), p.CurrentArea == 0 ? 0 : Math.Round(Get(c) / p.CurrentArea * 100m, 2))).ToList();
        var classified = LandRules.ClassifiedCategories.Sum(Get);
        var reconciled = p.InitialArea + acquired + added - reduced == p.CurrentArea && classified + Get(LandCategory.Unclassified) == p.CurrentArea;
        return new LandOverviewDto(p.Id, p.ProjectCode, p.ProjectName, p.MeasurementUnit, p.Status, p.InitialArea, acquired, added, reduced, p.CurrentArea,
            Get(LandCategory.Residential) + Get(LandCategory.Commercial), classified, Get(LandCategory.Unclassified), reconciled, p.RowVersion.ToToken(),
            DistributionVersion(dist), lines, parcels, acq, adj);
    }

    public async Task<PagedResult<AreaHistoryDto>> GetAreaHistoryAsync(long projectId, PagedRequest request, CancellationToken ct = default)
    {
        await RequireViewAsync(projectId, ct);
        var q = db.ProjectAreaHistories.AsNoTracking().Where(h => h.ProjectId == projectId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(h => new AreaHistoryDto(h.Id, h.ChangedAtUtc, h.EffectiveDate, h.PreviousArea, h.Change, h.NewArea, h.Source, h.Reason, h.ChangedBy,
                h.Adjustment != null ? h.Adjustment.Id : (long?)null, h.Acquisition != null ? h.Acquisition.Id : (long?)null,
                h.Adjustment != null ? h.Adjustment.SupportingReference : null, h.Adjustment != null ? h.Adjustment.ApprovedByName : null)).ToListAsync(ct);
        return new PagedResult<AreaHistoryDto>(items, request.Page, request.PageSize, total);
    }

    public async Task<PagedResult<DistributionHistoryDto>> GetDistributionHistoryAsync(long projectId, PagedRequest request, CancellationToken ct = default)
    {
        await RequireViewAsync(projectId, ct);
        var q = db.LandDistributionHistories.AsNoTracking().Where(h => h.ProjectId == projectId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id).Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(h => new DistributionHistoryDto(h.Category, h.PreviousArea, h.NewArea, h.Reason, h.ChangedAtUtc, h.ChangedBy)).ToListAsync(ct);
        return new PagedResult<DistributionHistoryDto>(items, request.Page, request.PageSize, total);
    }

    /// <summary>Active users who may approve land adjustments (used to populate the approver list).</summary>
    public async Task<IReadOnlyList<UserLookupDto>> ListApproversAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.LandAdjust, ct);
        var candidates = await users.SearchActiveAsync(null, 500, ct);
        var result = new List<UserLookupDto>();
        foreach (var c in candidates)
            if (await permissions.HasPermissionAsync(c.Id, Permissions.LandApprove, ct)) result.Add(c);
        return result;
    }

    // ---------- commands ----------
    public async Task<LandParcelDto> AddParcelAsync(long projectId, AddLandParcelRequest request, CancellationToken ct = default)
    {
        var project = await BeginManageAsync(projectId, Permissions.LandManage, ct);
        await parcelValidator.ValidateOrThrowAsync(request, ct);
        var code = request.ParcelCode.Trim().ToUpperInvariant();
        if (await db.LandParcels.AnyAsync(x => x.ProjectId == projectId && x.ParcelCode == code, ct))
            throw Invalid(nameof(request.ParcelCode), "A parcel with this code already exists in the project.");
        var originals = await db.LandParcels.Where(x => x.ProjectId == projectId && x.ParcelType == LandParcelType.Original).SumAsync(x => (decimal?)x.Area, ct) ?? 0m;
        if (originals + request.Area > project.InitialArea)
            throw Invalid(nameof(request.Area), $"Original parcels ({originals + request.Area:0.####}) cannot exceed the project's original area ({project.InitialArea:0.####}).");
        var parcel = new LandParcel
        {
            ProjectId = projectId, ParcelCode = code, Description = N(request.Description), ParcelType = LandParcelType.Original,
            Area = request.Area, TitleReference = N(request.TitleReference), Location = N(request.Location),
        };
        db.LandParcels.Add(parcel);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("LandParcel.Created", "LandParcel", parcel.Id.ToString(), NewValues: new { projectId, parcel.ParcelCode, parcel.Area }), ct);
        return new LandParcelDto(parcel.Id, parcel.ParcelCode, parcel.Description, parcel.ParcelType, parcel.Area, parcel.TitleReference, parcel.Location);
    }

    public async Task<LandAcquisitionDto> AcquireAsync(long projectId, AcquireLandRequest request, IReadOnlyList<LandDocumentUpload>? documents = null, CancellationToken ct = default)
    {
        var project = await BeginManageAsync(projectId, Permissions.LandManage, ct);
        await acquireValidator.ValidateOrThrowAsync(request, ct);
        db.UseRowVersion(project, request.RowVersion);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dist = await LoadDistributionAsync(project, now, ct);
        var previous = project.CurrentArea;
        project.ApplyAreaChange(request.Area);

        var code = N(request.ParcelCode)?.ToUpperInvariant()
                   ?? $"ACQ-{await db.LandAcquisitions.CountAsync(a => a.ProjectId == projectId, ct) + 1:000}";
        if (await db.LandParcels.AnyAsync(x => x.ProjectId == projectId && x.ParcelCode == code, ct))
            throw Invalid(nameof(request.ParcelCode), "A parcel with this code already exists in the project.");
        var parcel = new LandParcel
        {
            ProjectId = projectId, ParcelCode = code, ParcelType = LandParcelType.Acquired, Area = request.Area,
            Description = $"Acquired from {request.SellerName.Trim()}", TitleReference = N(request.ReferenceNumber),
        };
        var acquisition = new LandAcquisition
        {
            ProjectId = projectId, Parcel = parcel, AcquisitionDate = request.AcquisitionDate, SellerName = request.SellerName.Trim(), Area = request.Area,
            Cost = request.Cost, ReferenceNumber = N(request.ReferenceNumber), Notes = N(request.Notes),
        };
        db.LandAcquisitions.Add(acquisition);
        db.ProjectAreaHistories.Add(new ProjectAreaHistory
        {
            ProjectId = projectId, ChangedAtUtc = now, EffectiveDate = request.AcquisitionDate, PreviousArea = previous, Change = request.Area,
            NewArea = project.CurrentArea, Source = AreaChangeSource.Acquisition, Acquisition = acquisition,
            Reason = $"Land acquired from {acquisition.SellerName}", ChangedByUserId = user.UserId, ChangedBy = user.UserName,
        });
        SyncUnclassified(project, dist, "Project area changed: land acquisition", now);
        await AttachDocumentsAsync(projectId, documents, parcel, acquisition, null, ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Land.Acquired", "LandAcquisition", acquisition.Id.ToString(),
            new { projectId, PreviousArea = previous }, new { acquisition.Area, acquisition.SellerName, NewArea = project.CurrentArea, acquisition.Cost }), ct);
        await tx.CommitAsync(ct);
        return new LandAcquisitionDto(acquisition.Id, acquisition.AcquisitionDate, acquisition.SellerName, acquisition.Area, acquisition.Cost, acquisition.ReferenceNumber, acquisition.Notes, parcel.ParcelCode);
    }

    public async Task<LandAdjustmentDto> AdjustAsync(long projectId, AdjustLandRequest request, IReadOnlyList<LandDocumentUpload>? documents = null, CancellationToken ct = default)
    {
        var project = await BeginManageAsync(projectId, Permissions.LandAdjust, ct);
        await adjustValidator.ValidateOrThrowAsync(request, ct);
        var approver = (await users.LookupAsync([request.ApprovedByUserId], ct)).FirstOrDefault();
        if (approver is null || !approver.IsActive || !await permissions.HasPermissionAsync(approver.Id, Permissions.LandApprove, ct))
            throw Invalid(nameof(request.ApprovedByUserId), "The approver must be an active user who may approve land adjustments.");
        db.UseRowVersion(project, request.RowVersion);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dist = await LoadDistributionAsync(project, now, ct);
        var previous = project.CurrentArea;
        var delta = request.Type == LandAdjustmentType.Addition ? request.Area : -request.Area;
        var classified = dist.Where(d => d.Category != LandCategory.Unclassified).Sum(d => d.Area);
        if (previous + delta < classified)
            throw Invalid(nameof(request.Area), $"The area cannot be reduced below the land already distributed ({classified:0.####}). Reduce the distribution first.");
        project.ApplyAreaChange(delta);

        var adjustment = new LandAdjustment
        {
            ProjectId = projectId, AdjustmentDate = request.AdjustmentDate, Type = request.Type, PreviousArea = previous, AdjustmentArea = delta,
            NewArea = project.CurrentArea, Reason = request.Reason.Trim(), SupportingReference = request.SupportingReference.Trim(),
            ApprovedByUserId = approver.Id, ApprovedByName = approver.FullName, ApprovedAtUtc = now,
        };
        db.LandAdjustments.Add(adjustment);
        db.ProjectAreaHistories.Add(new ProjectAreaHistory
        {
            ProjectId = projectId, ChangedAtUtc = now, EffectiveDate = request.AdjustmentDate, PreviousArea = previous, Change = delta, NewArea = project.CurrentArea,
            Source = AreaChangeSource.Adjustment, Adjustment = adjustment, Reason = adjustment.Reason, ChangedByUserId = user.UserId, ChangedBy = user.UserName,
        });
        SyncUnclassified(project, dist, $"Project area changed: land {request.Type.ToString().ToLowerInvariant()}", now);
        await AttachDocumentsAsync(projectId, documents, null, null, adjustment, ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Land.Adjusted", "LandAdjustment", adjustment.Id.ToString(),
            new { projectId, PreviousArea = previous }, new { Type = request.Type.ToString(), Adjustment = delta, NewArea = project.CurrentArea, adjustment.SupportingReference, ApprovedBy = approver.FullName },
            adjustment.Reason), ct);
        await tx.CommitAsync(ct);
        return new LandAdjustmentDto(adjustment.Id, adjustment.AdjustmentDate, adjustment.Type, adjustment.PreviousArea, adjustment.AdjustmentArea, adjustment.NewArea,
            adjustment.Reason, adjustment.SupportingReference, adjustment.ApprovedByName, adjustment.ApprovedAtUtc);
    }

    public async Task<LandOverviewDto> SetDistributionAsync(long projectId, SetLandDistributionRequest request, CancellationToken ct = default)
    {
        var project = await BeginManageAsync(projectId, Permissions.LandManage, ct);
        await distributionValidator.ValidateOrThrowAsync(request, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dist = await LoadDistributionAsync(project, now, ct);
        // Optimistic concurrency: the edit must be based on the distribution as it is now. Rows written below are also
        // guarded by their own RowVersion, so a save racing this check fails instead of silently overwriting.
        if (!string.Equals(DistributionVersion(dist), request.DistributionVersion.Trim(), StringComparison.Ordinal))
            throw new ConcurrencyException();
        var requested = request.Lines.ToDictionary(l => l.Category, l => l.Area);
        var classified = LandRules.ClassifiedCategories.Select(c => requested.TryGetValue(c, out var a) ? a : dist.First(d => d.Category == c).Area).ToList();
        var unclassified = LandRules.ComputeUnclassified(project.CurrentArea, classified); // throws when it exceeds the project area

        var before = dist.ToDictionary(d => d.Category.ToString(), d => d.Area);
        foreach (var (cat, area) in LandRules.ClassifiedCategories.Zip(classified)) Apply(dist.First(d => d.Category == cat), area, request.Reason.Trim(), now);
        Apply(dist.First(d => d.Category == LandCategory.Unclassified), unclassified, request.Reason.Trim(), now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Land.DistributionChanged", "Project", projectId.ToString(), before,
            dist.ToDictionary(d => d.Category.ToString(), d => d.Area), request.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await GetOverviewAsync(projectId, ct);
    }

    // ---------- documents ----------
    public async Task<IReadOnlyList<LandDocumentDto>> ListDocumentsAsync(long projectId, CancellationToken ct = default)
    {
        await RequireViewAsync(projectId, ct);
        return await db.LandDocuments.AsNoTracking().Where(d => d.ProjectId == projectId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new LandDocumentDto(d.Id, d.Title, d.FileName, d.DocumentType, d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy, d.LandParcelId, d.LandAcquisitionId, d.LandAdjustmentId))
            .ToListAsync(ct);
    }

    public async Task<LandDocumentDto> UploadDocumentAsync(long projectId, LandDocumentUpload upload, long? parcelId, long? acquisitionId, long? adjustmentId, CancellationToken ct = default)
    {
        await BeginManageAsync(projectId, Permissions.LandManage, ct);
        if (parcelId is { } pid && !await db.LandParcels.AnyAsync(x => x.Id == pid && x.ProjectId == projectId, ct)) throw new NotFoundException("Parcel not found.");
        if (acquisitionId is { } aid && !await db.LandAcquisitions.AnyAsync(x => x.Id == aid && x.ProjectId == projectId, ct)) throw new NotFoundException("Acquisition not found.");
        if (adjustmentId is { } jid && !await db.LandAdjustments.AnyAsync(x => x.Id == jid && x.ProjectId == projectId, ct)) throw new NotFoundException("Adjustment not found.");
        var doc = await BuildDocumentAsync(projectId, upload, ct);
        doc.LandParcelId = parcelId; doc.LandAcquisitionId = acquisitionId; doc.LandAdjustmentId = adjustmentId;
        db.LandDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("LandDocument.Uploaded", "LandDocument", doc.Id.ToString(), NewValues: new { projectId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256 }), ct);
        return new LandDocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType, doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy, parcelId, acquisitionId, adjustmentId);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long projectId, long documentId, CancellationToken ct = default)
    {
        await RequireViewAsync(projectId, ct);
        var d = await db.LandDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long projectId, long documentId, string reason, CancellationToken ct = default)
    {
        await BeginManageAsync(projectId, Permissions.LandManage, ct);
        var d = await db.LandDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(clock.GetUtcNow().UtcDateTime, user.UserName ?? "unknown", reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("LandDocument.Removed", "LandDocument", d.Id.ToString(), new { projectId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // ---------- helpers ----------
    private async Task RequireViewAsync(long projectId, CancellationToken ct)
    {
        await permissions.EnsureAsync(user, Permissions.LandView, ct);
        await access.EnsureAccessAsync(projectId, ct);
    }

    private async Task<Project> BeginManageAsync(long projectId, string permission, CancellationToken ct)
    {
        await permissions.EnsureAsync(user, permission, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        if (!project.AllowsLandChanges) throw new DomainException($"Land cannot be changed while the project is {project.Status}.");
        return project;
    }

    private async Task<List<LandDistribution>> LoadDistributionAsync(Project project, DateTime now, CancellationToken ct)
    {
        var rows = await db.LandDistributions.Where(d => d.ProjectId == project.Id).ToListAsync(ct);
        foreach (var cat in LandRules.ClassifiedCategories.Append(LandCategory.Unclassified).Where(c => rows.All(r => r.Category != c)))
        {
            var row = new LandDistribution { ProjectId = project.Id, Category = cat, Area = 0 };
            db.LandDistributions.Add(row);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Opaque version of a project's distribution: a hash of every persisted row's RowVersion. Any change to any line,
    /// including the unclassified remainder after an area change, produces a new value.</summary>
    private static string DistributionVersion(IEnumerable<LandDistribution> rows)
    {
        var raw = string.Join(";", rows.Where(r => r.Id != 0).OrderBy(r => r.Category).Select(r => $"{(int)r.Category}:{r.Id}:{r.RowVersion.ToToken()}"));
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>After a project-area change the unclassified remainder absorbs the difference (and is recorded in the distribution history).</summary>
    private void SyncUnclassified(Project project, List<LandDistribution> dist, string reason, DateTime now)
    {
        var classified = dist.Where(d => d.Category != LandCategory.Unclassified).Sum(d => d.Area);
        Apply(dist.First(d => d.Category == LandCategory.Unclassified), LandRules.ComputeUnclassified(project.CurrentArea, [classified]), reason, now);
    }

    private void Apply(LandDistribution row, decimal newArea, string reason, DateTime now)
    {
        if (row.Area == newArea && row.Id != 0) return;
        db.LandDistributionHistories.Add(new LandDistributionHistory
        {
            ProjectId = row.ProjectId, Category = row.Category, PreviousArea = row.Area, NewArea = newArea, Reason = reason,
            ChangedAtUtc = now, ChangedByUserId = user.UserId, ChangedBy = user.UserName,
        });
        row.Area = newArea;
    }

    private async Task AttachDocumentsAsync(long projectId, IReadOnlyList<LandDocumentUpload>? uploads, LandParcel? parcel, LandAcquisition? acquisition, LandAdjustment? adjustment, CancellationToken ct)
    {
        if (uploads is null) return;
        foreach (var u in uploads)
        {
            var doc = await BuildDocumentAsync(projectId, u, ct);
            db.LandDocuments.Add(doc);
            doc.Parcel = parcel; doc.Acquisition = acquisition; doc.Adjustment = adjustment;
        }
    }


    private async Task<LandDocument> BuildDocumentAsync(long projectId, LandDocumentUpload upload, CancellationToken ct)
    {
        if (!Enum.IsDefined(upload.Type)) throw Invalid("DocumentType", "Invalid document type.");
        var stored = await uploader.StoreAsync(upload.File, $"land/{projectId}", ct);
        var title = string.IsNullOrWhiteSpace(upload.Title) ? stored.SafeFileName : upload.Title.Trim();
        return new LandDocument
        {
            ProjectId = projectId, DocumentType = upload.Type, Title = title[..Math.Min(title.Length, 200)], FileName = stored.SafeFileName,
            StorageKey = stored.File.Key, ContentType = stored.ContentType, SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
