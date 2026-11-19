using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Projects;

public class ProjectService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit,
    IUserQueryService users, DocumentUploader uploader, IFileStorage storage, TimeProvider clock,
    IValidator<CreateProjectRequest> createValidator, IValidator<UpdateProjectRequest> updateValidator,
    IValidator<ChangeProjectStatusRequest> statusValidator)
{
    // ---------- queries ----------
    public async Task<PagedResult<ProjectListItemDto>> ListAsync(ProjectListRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        var query = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        if (request.Status is { } s) query = query.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(request.City)) { var c = request.City.Trim(); query = query.Where(p => p.City == c); }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            query = query.Where(p => p.ProjectCode.Contains(t) || p.ProjectName.Contains(t) || p.City.Contains(t));
        }
        var total = await query.CountAsync(ct);
        query = (request.SortBy?.ToLowerInvariant(), request.Descending) switch
        {
            ("code", false) => query.OrderBy(p => p.ProjectCode),
            ("code", true) => query.OrderByDescending(p => p.ProjectCode),
            ("city", false) => query.OrderBy(p => p.City),
            ("city", true) => query.OrderByDescending(p => p.City),
            ("area", false) => query.OrderBy(p => p.CurrentArea),
            ("area", true) => query.OrderByDescending(p => p.CurrentArea),
            ("status", false) => query.OrderBy(p => p.Status),
            ("status", true) => query.OrderByDescending(p => p.Status),
            (_, true) => query.OrderByDescending(p => p.ProjectName),
            _ => query.OrderBy(p => p.ProjectName),
        };
        var page = await query.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        var names = await ManagerNamesAsync(page.Select(p => p.ProjectManagerId), ct);
        var items = page.Select(p => new ProjectListItemDto(p.Id, p.ProjectCode, p.ProjectName, p.City, p.Status, p.InitialArea, p.CurrentArea,
            p.MeasurementUnit, p.ProjectManagerId, p.ProjectManagerId is { } m ? names.GetValueOrDefault(m) : null, p.StartDate, p.ExpectedCompletionDate)).ToList();
        return new PagedResult<ProjectListItemDto>(items, request.Page, request.PageSize, total);
    }

    public async Task<IReadOnlyList<string>> ListCitiesAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        var q = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        return await q.Select(p => p.City).Distinct().OrderBy(c => c).ToListAsync(ct);
    }

    public async Task<ProjectDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        await access.EnsureAccessAsync(id, ct);
        var p = await db.Projects.AsNoTracking().Include(x => x.AddressDetails).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Project not found.");
        var status = await db.ProjectStatusHistories.AsNoTracking().Where(h => h.ProjectId == id).OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id)
            .Select(h => new StatusHistoryDto(h.FromStatus, h.ToStatus, h.Reason, h.ChangedAtUtc, h.ChangedBy)).ToListAsync(ct);
        var mgr = await db.ProjectManagerAssignments.AsNoTracking().Where(a => a.ProjectId == id).OrderByDescending(a => a.AssignedFromUtc).ToListAsync(ct);
        var names = await ManagerNamesAsync(mgr.Select(a => (Guid?)a.UserId).Append(p.ProjectManagerId), ct);
        var history = mgr.Select(a => new ManagerHistoryDto(a.UserId, names.GetValueOrDefault(a.UserId), a.AssignedFromUtc, a.AssignedToUtc, a.AssignedBy, a.Reason)).ToList();
        var a2 = p.AddressDetails;
        return new ProjectDetailDto(p.Id, p.ProjectCode, p.ProjectName, p.Description, p.City, p.Address, p.StartDate, p.ExpectedCompletionDate,
            p.InitialArea, p.CurrentArea, p.MeasurementUnit, p.Status, p.ProjectManagerId, p.ProjectManagerId is { } m ? names.GetValueOrDefault(m) : null,
            a2 is null ? null : new ProjectAddressDto(a2.AddressLine, a2.Locality, a2.Province, a2.Country, a2.PostalCode, a2.Latitude, a2.Longitude, a2.Landmark),
            p.RowVersion.ToToken(), p.CreatedAtUtc, p.CreatedBy, p.ModifiedAtUtc, p.ModifiedBy, status, history);
    }

    /// <summary>Active users selectable as project manager.</summary>
    public async Task<IReadOnlyList<UserLookupDto>> ListManagerCandidatesAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        return await users.SearchActiveAsync(null, 500, ct);
    }

    // ---------- commands ----------
    public async Task<ProjectDetailDto> CreateAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsCreate, ct);
        await createValidator.ValidateOrThrowAsync(request, ct);
        var code = request.ProjectCode.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.ProjectCode == code, ct))
            throw Invalid(nameof(request.ProjectCode), "A project with this code already exists.");
        if (request.ProjectManagerId is { } mid) await RequireActiveUserAsync(mid, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var actor = user.UserName;
        var project = Project.Create(code, request.ProjectName, request.Description, request.City, request.Address,
            request.StartDate, request.ExpectedCompletionDate, request.InitialArea, request.MeasurementUnit, request.ProjectManagerId);
        project.AddressDetails = MapAddress(request.AddressDetails);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Projects.Add(project);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw Invalid(nameof(request.ProjectCode), "A project with this code already exists."); }

        db.ProjectStatusHistories.Add(new ProjectStatusHistory
        {
            ProjectId = project.Id, FromStatus = null, ToStatus = ProjectStatus.Planning, Reason = "Project created",
            ChangedAtUtc = now, ChangedByUserId = user.UserId, ChangedBy = actor,
        });
        db.ProjectAreaHistories.Add(new ProjectAreaHistory
        {
            ProjectId = project.Id, ChangedAtUtc = now, EffectiveDate = DateOnly.FromDateTime(now), PreviousArea = 0,
            Change = project.InitialArea, NewArea = project.InitialArea, Source = AreaChangeSource.Initial,
            Reason = "Initial project area", ChangedByUserId = user.UserId, ChangedBy = actor,
        });
        foreach (var cat in LandRules.ClassifiedCategories.Append(LandCategory.Unclassified))
        {
            var area = cat == LandCategory.Unclassified ? project.InitialArea : 0m;
            db.LandDistributions.Add(new LandDistribution { ProjectId = project.Id, Category = cat, Area = area });
            db.LandDistributionHistories.Add(new LandDistributionHistory
            {
                ProjectId = project.Id, Category = cat, PreviousArea = 0, NewArea = area, Reason = "Initial distribution",
                ChangedAtUtc = now, ChangedByUserId = user.UserId, ChangedBy = actor,
            });
        }
        if (request.ProjectManagerId is { } manager) AddManagerAssignment(project.Id, manager, now, actor, "Initial manager");
        await EnsureAccessRowAsync(project.Id, user.UserId, ct);
        if (request.ProjectManagerId is { } m2) await EnsureAccessRowAsync(project.Id, m2, ct);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry("Project.Created", "Project", project.Id.ToString(), NewValues: Snapshot(project)), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(project.Id, ct);
    }

    public async Task<ProjectDetailDto> UpdateAsync(long id, UpdateProjectRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsEdit, ct);
        await access.EnsureAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(request, ct);
        var project = await db.Projects.Include(p => p.AddressDetails).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project not found.");
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be edited.");
        if (request.ProjectManagerId is { } mid && mid != project.ProjectManagerId) await RequireActiveUserAsync(mid, ct);

        var before = Snapshot(project);
        db.UseRowVersion(project, request.RowVersion);
        project.UpdateDetails(request.ProjectName, request.Description, request.City, request.Address, request.StartDate, request.ExpectedCompletionDate);
        UpsertAddress(project, request.AddressDetails);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.ProjectManagerId != project.ProjectManagerId) await ChangeManagerAsync(project, request.ProjectManagerId, "Changed via project update", ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Project.Updated", "Project", id.ToString(), before, Snapshot(project)), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ProjectDetailDto> ChangeStatusAsync(long id, ChangeProjectStatusRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsStatus, ct);
        await access.EnsureAccessAsync(id, ct);
        await statusValidator.ValidateOrThrowAsync(request, ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project not found.");
        db.UseRowVersion(project, request.RowVersion);
        var from = project.Status;
        var history = project.ChangeStatus(request.Status, request.Reason, clock.GetUtcNow().UtcDateTime, user.UserId, user.UserName);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ProjectStatusHistories.Add(history);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Project.StatusChanged", "Project", id.ToString(), new { Status = from.ToString() }, new { Status = request.Status.ToString() }, request.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- documents ----------
    public async Task<IReadOnlyList<DocumentDto>> ListDocumentsAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return await db.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == projectId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new DocumentDto(d.Id, d.Title, d.FileName, d.DocumentType.ToString(), d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy, d.Description, null))
            .ToListAsync(ct);
    }

    public async Task<DocumentDto> UploadDocumentAsync(long projectId, ProjectDocumentType type, string? title, string? description, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsDocuments, ct);
        await access.EnsureAccessAsync(projectId, ct);
        if (!Enum.IsDefined(type)) throw Invalid("DocumentType", "Invalid document type.");
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
        var stored = await uploader.StoreAsync(file, $"projects/{projectId}", ct);
        var doc = new ProjectDocument
        {
            ProjectId = projectId, DocumentType = type, Title = string.IsNullOrWhiteSpace(title) ? stored.SafeFileName : title.Trim()[..Math.Min(title.Trim().Length, 200)],
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), FileName = stored.SafeFileName, StorageKey = stored.File.Key,
            ContentType = stored.ContentType, SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ProjectDocument.Uploaded", "ProjectDocument", doc.Id.ToString(), NewValues: new { projectId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256 }), ct);
        return new DocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType.ToString(), doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy, doc.Description, null);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long projectId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var d = await db.ProjectDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long projectId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ProjectsDocuments, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var d = await db.ProjectDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(clock.GetUtcNow().UtcDateTime, user.UserName ?? "unknown", reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ProjectDocument.Removed", "ProjectDocument", d.Id.ToString(), new { projectId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // ---------- helpers ----------
    private async Task ChangeManagerAsync(Project project, Guid? newManager, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.ProjectManagerAssignments.Where(a => a.ProjectId == project.Id && a.AssignedToUtc == null).ToListAsync(ct);
        foreach (var a in open) a.AssignedToUtc = now;
        if (newManager is { } m)
        {
            AddManagerAssignment(project.Id, m, now, user.UserName, reason);
            await EnsureAccessRowAsync(project.Id, m, ct);
        }
        project.SetManager(newManager);
    }

    private void AddManagerAssignment(long projectId, Guid userId, DateTime now, string? by, string reason) =>
        db.ProjectManagerAssignments.Add(new ProjectManagerAssignment { ProjectId = projectId, UserId = userId, AssignedFromUtc = now, AssignedBy = by, Reason = reason });

    private async Task EnsureAccessRowAsync(long projectId, Guid? userId, CancellationToken ct)
    {
        if (userId is not { } uid) return;
        var row = await db.UserProjects.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.UserId == uid, ct);
        if (row is null) db.UserProjects.Add(new UserProject { ProjectId = projectId, UserId = uid, IsActive = true });
        else row.IsActive = true;
    }

    private async Task RequireActiveUserAsync(Guid id, CancellationToken ct)
    {
        var found = (await users.LookupAsync([id], ct)).FirstOrDefault();
        if (found is null || !found.IsActive) throw Invalid("ProjectManagerId", "The selected project manager is not an active user.");
    }

    private async Task<Dictionary<Guid, string>> ManagerNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return set.Count == 0 ? [] : (await users.LookupAsync(set, ct)).ToDictionary(u => u.Id, u => u.FullName);
    }

    private static ProjectAddress? MapAddress(ProjectAddressDto? a) => a is null ? null : new ProjectAddress
    {
        AddressLine = N(a.AddressLine), Locality = N(a.Locality), Province = N(a.Province), Country = N(a.Country),
        PostalCode = N(a.PostalCode), Latitude = a.Latitude, Longitude = a.Longitude, Landmark = N(a.Landmark),
    };

    private void UpsertAddress(Project p, ProjectAddressDto? dto)
    {
        if (dto is null) return;
        if (p.AddressDetails is null) { var created = MapAddress(dto)!; created.ProjectId = p.Id; p.AddressDetails = created; db.ProjectAddresses.Add(created); return; }
        var a = p.AddressDetails;
        a.AddressLine = N(dto.AddressLine); a.Locality = N(dto.Locality); a.Province = N(dto.Province); a.Country = N(dto.Country);
        a.PostalCode = N(dto.PostalCode); a.Latitude = dto.Latitude; a.Longitude = dto.Longitude; a.Landmark = N(dto.Landmark);
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private static object Snapshot(Project p) => new
    {
        p.ProjectCode, p.ProjectName, p.Description, p.City, p.Address, p.StartDate, p.ExpectedCompletionDate,
        p.InitialArea, p.CurrentArea, Unit = p.MeasurementUnit.ToString(), Status = p.Status.ToString(), p.ProjectManagerId,
    };
}
