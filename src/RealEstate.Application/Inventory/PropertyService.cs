using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Inventory;

public class PropertyService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, IUserQueryService users,
    DocumentUploader uploader, IFileStorage storage, TimeProvider clock,
    IValidator<CreatePropertyRequest> createValidator, IValidator<UpdatePropertyRequest> updateValidator, IValidator<ReservePropertyRequest> reserveValidator,
    IValidator<ReleaseReservationRequest> releaseValidator, IValidator<ChangePropertyStatusRequest> statusValidator)
{
    // ---------- time ----------
    /// <summary>Today in the company's time zone (falls back to UTC), used for reservation dates and expiry.</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tz = await db.Companies.AsNoTracking().Select(c => c.TimeZoneId).FirstOrDefaultAsync(ct);
        try { if (!string.IsNullOrWhiteSpace(tz)) now = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(tz)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return DateOnly.FromDateTime(now);
    }

    // ---------- scoping ----------
    private async Task<IQueryable<Property>> ScopedAsync(CancellationToken ct)
    {
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        return db.Properties.AsNoTracking().Where(p => projects.Any(pr => pr.Id == p.ProjectId));
    }

    // ---------- queries ----------
    public async Task<PagedResult<PropertyListItemDto>> ListAsync(PropertyListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await ExpireOverdueAsync(ct);
        return await QueryAsync(r, null, ct);
    }

    /// <summary>Available properties only (reservation expiry is applied first, so expired holds are returned to stock).</summary>
    public async Task<PagedResult<PropertyListItemDto>> ListAvailableAsync(PropertyListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await ExpireOverdueAsync(ct);
        return await QueryAsync(r, PropertyStatus.Available, ct);
    }

    private async Task<PagedResult<PropertyListItemDto>> QueryAsync(PropertyListRequest r, PropertyStatus? forced, CancellationToken ct)
    {
        var q = await ScopedAsync(ct);
        if (r.ProjectId is { } pid) q = q.Where(p => p.ProjectId == pid);
        if (r.BlockId is { } b) q = q.Where(p => p.BlockId == b);
        if (r.SectorId is { } s) q = q.Where(p => p.SectorId == s);
        if (r.StreetId is { } st) q = q.Where(p => p.StreetId == st);
        if (r.TypeId is { } t) q = q.Where(p => p.PropertyTypeId == t);
        if (r.CategoryId is { } c) q = q.Where(p => p.PropertyType!.CategoryId == c);
        if ((forced ?? r.Status) is { } status) q = q.Where(p => p.Status == status);
        if (r.MinSize is { } min) q = q.Where(p => p.Size >= min);
        if (r.MaxSize is { } max) q = q.Where(p => p.Size <= max);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var term = r.Search.Trim();
            q = q.Where(p => p.PropertyCode.Contains(term) || p.PropertyNumber.Contains(term) || (p.LocationDescription != null && p.LocationDescription.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("number", false) => q.OrderBy(p => p.PropertyNumber).ThenBy(p => p.Id),
            ("number", true) => q.OrderByDescending(p => p.PropertyNumber).ThenBy(p => p.Id),
            ("size", false) => q.OrderBy(p => p.Size).ThenBy(p => p.Id),
            ("size", true) => q.OrderByDescending(p => p.Size).ThenBy(p => p.Id),
            ("status", false) => q.OrderBy(p => p.Status).ThenBy(p => p.Id),
            ("status", true) => q.OrderByDescending(p => p.Status).ThenBy(p => p.Id),
            (_, true) => q.OrderByDescending(p => p.PropertyCode),
            _ => q.OrderBy(p => p.PropertyCode),
        };
        var page = await q.Include(p => p.PropertyType).ThenInclude(t => t!.Category).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        var projectCodes = await db.Projects.AsNoTracking().Where(x => page.Select(p => p.ProjectId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ProjectCode, ct);
        var blocks = await db.ProjectBlocks.AsNoTracking().Where(x => page.Select(p => p.BlockId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var sectors = await db.ProjectSectors.AsNoTracking().Where(x => page.Select(p => p.SectorId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var streets = await db.ProjectStreets.AsNoTracking().Where(x => page.Select(p => p.StreetId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var items = page.Select(p => new PropertyListItemDto(p.Id, p.ProjectId, projectCodes.GetValueOrDefault(p.ProjectId, ""), p.PropertyCode, p.PropertyNumber,
            p.BlockId is { } bi ? blocks.GetValueOrDefault(bi) : null, p.SectorId is { } si ? sectors.GetValueOrDefault(si) : null, p.StreetId is { } sti ? streets.GetValueOrDefault(sti) : null,
            p.PropertyType!.Name, p.PropertyType.Category!.Name, p.Size, p.MeasurementUnit, p.Status, p.DevelopmentStatus, p.IsCorner, p.FacesMainRoad, p.FacesPark, p.FloorNumber)).ToList();
        return new PagedResult<PropertyListItemDto>(items, r.Page, r.PageSize, total);
    }

    public async Task<PropertyDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await EnsurePropertyAccessAsync(id, ct);
        await ExpireOverdueAsync(ct, id);
        return await BuildDetailAsync(id, ct);
    }

    private async Task<PropertyDetailDto> BuildDetailAsync(long id, CancellationToken ct)
    {
        var p = await db.Properties.AsNoTracking().Include(x => x.PropertyType).ThenInclude(t => t!.Category).Include(x => x.Dimensions).Include(x => x.Features)
                    .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        var block = p.BlockId is { } b ? await db.ProjectBlocks.AsNoTracking().Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var sector = p.SectorId is { } s ? await db.ProjectSectors.AsNoTracking().Where(x => x.Id == s).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var street = p.StreetId is { } st ? await db.ProjectStreets.AsNoTracking().Where(x => x.Id == st).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var res = await db.PropertyReservations.AsNoTracking().Where(x => x.PropertyId == id && x.Status == ReservationStatus.Active).FirstOrDefaultAsync(ct);
        var t = p.PropertyType!;
        return new PropertyDetailDto(p.Id, p.ProjectId, project.ProjectCode, project.ProjectName, project.Status, p.PropertyCode, p.PropertyNumber,
            p.BlockId, block, p.SectorId, sector, p.StreetId, street, t.Id, t.Name, t.CategoryId, t.Category!.Name, t.Form, t.RequiresFloor, p.Size, p.MeasurementUnit,
            p.LocationDescription, p.FloorNumber, p.IsCorner, p.FacesMainRoad, p.FacesPark, p.DevelopmentStatus, p.Status, p.Remarks,
            p.Dimensions.OrderBy(d => d.Name).Select(d => new DimensionDto(d.Name, d.Value, d.Unit)).ToList(),
            p.Features.OrderBy(f => f.Name).Select(f => new FeatureDto(f.Name, f.Value)).ToList(),
            res is null ? null : ToDto(res), p.RowVersion.ToToken(), p.CreatedAtUtc, p.CreatedBy, p.ModifiedAtUtc, p.ModifiedBy);
    }

    public async Task<IReadOnlyList<ReservationDto>> ListReservationsAsync(long propertyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        return (await db.PropertyReservations.AsNoTracking().Where(r => r.PropertyId == propertyId).OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).ToListAsync(ct))
            .Select(ToDto).ToList();
    }

    public async Task<PagedResult<PropertyStatusHistoryDto>> ListHistoryAsync(InventoryHistoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        if (r.PropertyId is { } one) await EnsurePropertyAccessAsync(one, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.PropertyStatusHistories.AsNoTracking().Where(h => projects.Any(pr => pr.Id == h.Property!.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(h => h.Property!.ProjectId == pid);
        if (r.PropertyId is { } prop) q = q.Where(h => h.PropertyId == prop);
        if (r.Status is { } s) q = q.Where(h => h.ToStatus == s);
        if (r.From is { } from) { var f = from.ToDateTime(TimeOnly.MinValue); q = q.Where(h => h.ChangedAtUtc >= f); }
        if (r.To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(h => h.ChangedAtUtc < t); }
        if (!string.IsNullOrWhiteSpace(r.Search)) { var term = r.Search.Trim(); q = q.Where(h => h.Property!.PropertyCode.Contains(term) || h.Reason.Contains(term)); }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(h => new PropertyStatusHistoryDto(h.Id, h.PropertyId, h.Property!.PropertyCode, h.FromStatus, h.ToStatus, h.Reason, h.ChangedAtUtc, h.ChangedBy, h.ReservationId))
            .ToListAsync(ct);
        return new PagedResult<PropertyStatusHistoryDto>(items, r.Page, r.PageSize, total);
    }

    /// <summary>Active users holding the reserve permission, selectable as approver.</summary>
    public async Task<IReadOnlyList<UserLookupDto>> ListApproversAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryReserve, ct);
        var all = await users.SearchActiveAsync(null, 500, ct);
        var result = new List<UserLookupDto>();
        foreach (var u in all) if (await permissions.HasPermissionAsync(u.Id, Permissions.InventoryReserve, ct)) result.Add(u);
        return result;
    }

    // ---------- commands ----------
    public async Task<PropertyDetailDto> CreateAsync(CreatePropertyRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryManage, ct);
        await access.EnsureAccessAsync(r.ProjectId, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == r.ProjectId, ct);
        if (project.Status is ProjectStatus.Archived or ProjectStatus.Completed) throw new DomainException($"Properties cannot be added while the project is {project.Status}.");
        var code = r.PropertyCode.Trim().ToUpperInvariant();
        if (await db.Properties.AnyAsync(p => p.ProjectId == r.ProjectId && p.PropertyCode == code, ct))
            throw Invalid("PropertyCode", "A property with this code already exists in the project.");
        var type = await ValidateReferencesAsync(r.ProjectId, r.BlockId, r.SectorId, r.StreetId, r.PropertyTypeId, r.FloorNumber, null, ct);
        await EnsureNumberFreeAsync(r.ProjectId, r.PropertyNumber, r.BlockId, r.SectorId, r.StreetId, null, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var p = Property.Create(r.ProjectId, code, r.PropertyNumber, type.Id, r.Size, r.MeasurementUnit);
        Apply(p, r.BlockId, r.SectorId, r.StreetId, r.LocationDescription, r.FloorNumber, r.IsCorner, r.FacesMainRoad, r.FacesPark, r.DevelopmentStatus, r.Remarks);
        SetChildren(p, r.Dimensions, r.Features);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Properties.Add(p);
        db.PropertyStatusHistories.Add(Property.InitialHistory(p, now, user.UserId, user.UserName));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw Invalid("PropertyCode", "A property with this code already exists in the project."); }
        await audit.RecordAsync(new AuditEntry("Property.Created", "Property", p.Id.ToString(), NewValues: Snapshot(p)), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(p.Id, ct);
    }

    public async Task<PropertyDetailDto> UpdateAsync(long id, UpdatePropertyRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryManage, ct);
        await EnsurePropertyAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(r, ct);
        var p = await db.Properties.Include(x => x.Dimensions).Include(x => x.Features).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");

        var number = r.PropertyNumber.Trim();
        var structuralChange = p.Size != r.Size || p.MeasurementUnit != r.MeasurementUnit || p.PropertyTypeId != r.PropertyTypeId || p.PropertyNumber != number
                               || p.BlockId != r.BlockId || p.SectorId != r.SectorId || p.StreetId != r.StreetId || p.FloorNumber != r.FloorNumber;
        if (!p.IsStructureEditable && structuralChange)
            throw new DomainException($"The physical details of a property cannot be changed while it is {p.Status}. Only remarks, features and similar notes can be edited.");
        if (structuralChange)
        {
            await ValidateReferencesAsync(p.ProjectId, r.BlockId, r.SectorId, r.StreetId, r.PropertyTypeId, r.FloorNumber, p, ct);
            await EnsureNumberFreeAsync(p.ProjectId, number, r.BlockId, r.SectorId, r.StreetId, p.Id, ct);
        }
        var before = Snapshot(p);
        db.UseRowVersion(p, r.RowVersion);
        if (structuralChange) { Property.ValidateSize(r.Size); p.PropertyNumber = number; p.PropertyTypeId = r.PropertyTypeId; p.Size = r.Size; p.MeasurementUnit = r.MeasurementUnit; }
        Apply(p, r.BlockId, r.SectorId, r.StreetId, r.LocationDescription, r.FloorNumber, r.IsCorner, r.FacesMainRoad, r.FacesPark, r.DevelopmentStatus, r.Remarks);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        SyncChildren(p, r.Dimensions, r.Features);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Property.Updated", "Property", id.ToString(), before, Snapshot(p)), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    /// <summary>Reserves an available property. Concurrent attempts are rejected by the status check, the row version and the unique active-reservation index.</summary>
    public async Task<PropertyDetailDto> ReserveAsync(long id, ReservePropertyRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryReserve, ct);
        await EnsurePropertyAccessAsync(id, ct);
        await reserveValidator.ValidateOrThrowAsync(r, ct);
        var today = await TodayAsync(ct);
        if (r.ExpiryDate < today) throw Invalid("ExpiryDate", "The expiry date cannot be in the past.");
        if (r.ReservationDate > today) throw Invalid("ReservationDate", "The reservation date cannot be in the future.");
        await ExpireOverdueAsync(ct, id);

        var approver = (await users.LookupAsync([r.ApprovedByUserId], ct)).FirstOrDefault();
        if (approver is null || !approver.IsActive) throw Invalid("ApprovedByUserId", "The approver must be an active user.");
        if (!await permissions.HasPermissionAsync(approver.Id, Permissions.InventoryReserve, ct)) throw Invalid("ApprovedByUserId", "The approver does not hold the reservation permission.");

        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        if (project.Status is ProjectStatus.Archived) throw new DomainException("Archived projects cannot be changed.");
        if (p.Status != PropertyStatus.Available) throw new DomainException($"The property is {p.Status} and cannot be reserved. Only available properties can be reserved.");
        db.UseRowVersion(p, r.RowVersion);

        var now = clock.GetUtcNow().UtcDateTime;
        var reservation = PropertyReservation.Create(id, r.ReservationDate, r.ExpiryDate, r.Amount, r.ReservedForName, r.ReservedForContact, approver.Id, approver.FullName);
        reservation.CustomerId = r.CustomerId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PropertyReservations.Add(reservation);
        await SaveReservationAsync(ct);
        var history = p.ChangeStatus(PropertyStatus.Reserved, $"Reserved for {reservation.ReservedForName} until {reservation.ExpiryDate:yyyy-MM-dd}", now, user.UserId, user.UserName, reservation.Id);
        db.PropertyStatusHistories.Add(history);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Property.Reserved", "Property", id.ToString(), new { Status = "Available" },
            new { Status = "Reserved", reservation.Id, reservation.ReservedForName, reservation.Amount, reservation.ReservationDate, reservation.ExpiryDate, ApprovedBy = approver.FullName }), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<PropertyDetailDto> ReleaseReservationAsync(long id, ReleaseReservationRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryReserve, ct);
        await EnsurePropertyAccessAsync(id, ct);
        await releaseValidator.ValidateOrThrowAsync(r, ct);
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property not found.");
        var res = await db.PropertyReservations.FirstOrDefaultAsync(x => x.PropertyId == id && x.Status == ReservationStatus.Active, ct)
                  ?? throw new DomainException("The property has no active reservation.");
        db.UseRowVersion(p, r.RowVersion);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        res.Close(ReservationStatus.Released, now, user.UserName ?? "unknown", r.Reason.Trim());
        db.PropertyStatusHistories.Add(p.ChangeStatus(PropertyStatus.Available, $"Reservation released: {r.Reason.Trim()}", now, user.UserId, user.UserName, res.Id));
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Property.ReservationReleased", "Property", id.ToString(), new { Status = "Reserved", ReservationId = res.Id }, new { Status = "Available" }, r.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    /// <summary>Controlled manual status change. Reservations go through Reserve/Release; later phases call this for booking, sale and possession steps.</summary>
    public async Task<PropertyDetailDto> ChangeStatusAsync(long id, ChangePropertyStatusRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryStatus, ct);
        await EnsurePropertyAccessAsync(id, ct);
        await statusValidator.ValidateOrThrowAsync(r, ct);
        await ExpireOverdueAsync(ct, id);
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        if (project.Status == ProjectStatus.Archived) throw new DomainException("Archived projects cannot be changed.");
        if (r.Status == PropertyStatus.Reserved) throw new DomainException("Use the reservation action to reserve a property.");
        if (p.Status == PropertyStatus.Reserved && r.Status == PropertyStatus.Available) throw new DomainException("Use the release action to release a reservation.");
        db.UseRowVersion(p, r.RowVersion);
        var from = p.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        if (!Property.CanTransition(from, r.Status)) throw new DomainException($"A property cannot move from {from} to {r.Status}.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        long? reservationId = null;
        if (from == PropertyStatus.Reserved)
        {
            var res = await db.PropertyReservations.FirstOrDefaultAsync(x => x.PropertyId == id && x.Status == ReservationStatus.Active, ct);
            if (res is not null)
            {
                res.Close(r.Status == PropertyStatus.Booked ? ReservationStatus.Converted : ReservationStatus.Released, now, user.UserName ?? "unknown",
                    r.Status == PropertyStatus.Booked ? "Converted to booking" : r.Reason.Trim());
                reservationId = res.Id;
            }
        }
        db.PropertyStatusHistories.Add(p.ChangeStatus(r.Status, r.Reason, now, user.UserId, user.UserName, reservationId));
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Property.StatusChanged", "Property", id.ToString(), new { Status = from.ToString() }, new { Status = r.Status.ToString() }, r.Reason.Trim()), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    /// <summary>Returns properties with overdue reservations to Available (history and audit written). Safe to call repeatedly and concurrently.</summary>
    public async Task<int> ExpireOverdueAsync(CancellationToken ct = default, long? propertyId = null)
    {
        var today = await TodayAsync(ct);
        var q = db.PropertyReservations.Where(r => r.Status == ReservationStatus.Active && r.ExpiryDate < today);
        if (propertyId is { } pid) q = q.Where(r => r.PropertyId == pid);
        var due = await q.OrderBy(r => r.Id).Take(200).ToListAsync(ct);
        var expired = 0;
        foreach (var res in due)
        {
            var p = await db.Properties.FirstAsync(x => x.Id == res.PropertyId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            PropertyStatusHistory? history = null;
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                res.Close(ReservationStatus.Expired, now, "system", "Reservation expired");
                history = p.ChangeStatus(PropertyStatus.Available, $"Reservation expired on {res.ExpiryDate:yyyy-MM-dd}", now, null, "system", res.Id);
                db.PropertyStatusHistories.Add(history);
                await db.SaveChangesAsync(ct);
                await audit.RecordAsync(new AuditEntry("Property.ReservationExpired", "Property", p.Id.ToString(), new { Status = "Reserved", ReservationId = res.Id }, new { Status = "Available" },
                    "Reservation expired", UserName: "system"), ct);
                await tx.CommitAsync(ct);
                expired++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request already handled this property; drop our stale tracked state.
                db.Entry(res).State = EntityState.Detached;
                db.Entry(p).State = EntityState.Detached;
                if (history is not null) db.Entry(history).State = EntityState.Detached;
            }
        }
        return expired;
    }

    // ---------- documents ----------
    public async Task<IReadOnlyList<PropertyDocumentDto>> ListDocumentsAsync(long propertyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        return await db.PropertyDocuments.AsNoTracking().Where(d => d.PropertyId == propertyId && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new PropertyDocumentDto(d.Id, d.Title, d.FileName, d.DocumentType.ToString(), d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);
    }

    public async Task<PropertyDocumentDto> UploadDocumentAsync(long propertyId, PropertyDocumentType type, string? title, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryDocuments, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        if (!Enum.IsDefined(type)) throw Invalid("DocumentType", "Invalid document type.");
        var projectId = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => p.ProjectId).FirstAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
        var stored = await uploader.StoreAsync(file, $"properties/{propertyId}", ct);
        var t = string.IsNullOrWhiteSpace(title) ? stored.SafeFileName : title.Trim();
        var doc = new PropertyDocument
        {
            PropertyId = propertyId, DocumentType = type, Title = t[..Math.Min(t.Length, 200)], FileName = stored.SafeFileName, StorageKey = stored.File.Key,
            ContentType = stored.ContentType, SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.PropertyDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("PropertyDocument.Uploaded", "PropertyDocument", doc.Id.ToString(), NewValues: new { propertyId, doc.Title, doc.FileName, doc.SizeBytes, doc.Sha256 }), ct);
        return new PropertyDocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType.ToString(), doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy);
    }

    public async Task<DownloadedFile> DownloadDocumentAsync(long propertyId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        var d = await db.PropertyDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.PropertyId == propertyId && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteDocumentAsync(long propertyId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryDocuments, ct);
        await EnsurePropertyAccessAsync(propertyId, ct);
        var d = await db.PropertyDocuments.FirstOrDefaultAsync(x => x.Id == documentId && x.PropertyId == propertyId && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(clock.GetUtcNow().UtcDateTime, user.UserName ?? "unknown", reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("PropertyDocument.Removed", "PropertyDocument", d.Id.ToString(), new { propertyId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // ---------- helpers ----------
    private async Task EnsurePropertyAccessAsync(long propertyId, CancellationToken ct)
    {
        var projectId = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId).Select(p => (long?)p.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            // Unknown ids behave like inaccessible ones for non-privileged users so ids cannot be probed.
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this property.");
            throw new NotFoundException("Property not found.");
        }
        await access.EnsureAccessAsync(projectId.Value, ct);
    }

    private async Task<PropertyType> ValidateReferencesAsync(long projectId, long? blockId, long? sectorId, long? streetId, long typeId, int? floor, Property? existing, CancellationToken ct)
    {
        long? exBlock = existing?.BlockId, exSector = existing?.SectorId, exStreet = existing?.StreetId;
        var type = await db.PropertyTypes.AsNoTracking().Include(t => t.Category).FirstOrDefaultAsync(t => t.Id == typeId, ct) ?? throw Invalid("PropertyTypeId", "Property type not found.");
        if ((!type.IsActive || !type.Category!.IsActive) && existing?.PropertyTypeId != typeId) throw Invalid("PropertyTypeId", "The property type is inactive.");
        if (type.RequiresFloor && floor is null) throw Invalid("FloorNumber", $"A floor number is required for {type.Name}.");
        if (blockId is { } b && !await db.ProjectBlocks.AnyAsync(z => z.Id == b && z.ProjectId == projectId && (z.IsActive || exBlock == b), ct)) throw Invalid("BlockId", "The block does not belong to this project or is inactive.");
        if (sectorId is { } s && !await db.ProjectSectors.AnyAsync(z => z.Id == s && z.ProjectId == projectId && (z.IsActive || exSector == s), ct)) throw Invalid("SectorId", "The sector does not belong to this project or is inactive.");
        if (streetId is { } st && !await db.ProjectStreets.AnyAsync(z => z.Id == st && z.ProjectId == projectId && (z.IsActive || exStreet == st), ct)) throw Invalid("StreetId", "The street does not belong to this project or is inactive.");
        return type;
    }

    private async Task EnsureNumberFreeAsync(long projectId, string number, long? blockId, long? sectorId, long? streetId, long? exceptId, CancellationToken ct)
    {
        var n = number.Trim();
        if (await db.Properties.AnyAsync(p => p.ProjectId == projectId && p.Id != exceptId && p.PropertyNumber == n && p.BlockId == blockId && p.SectorId == sectorId && p.StreetId == streetId, ct))
            throw Invalid("PropertyNumber", "Another property already uses this number in the same block, sector and street.");
    }

    private static void Apply(Property p, long? block, long? sector, long? street, string? location, int? floor, bool corner, bool road, bool park, DevelopmentStatus dev, string? remarks)
    {
        p.BlockId = block; p.SectorId = sector; p.StreetId = street; p.LocationDescription = N(location); p.FloorNumber = floor;
        p.IsCorner = corner; p.FacesMainRoad = road; p.FacesPark = park; p.DevelopmentStatus = dev; p.Remarks = N(remarks);
    }

    private static void SetChildren(Property p, IReadOnlyList<DimensionDto>? dims, IReadOnlyList<FeatureDto>? features)
    {
        foreach (var d in dims ?? []) p.Dimensions.Add(new PropertyDimension { Name = d.Name.Trim(), Value = d.Value, Unit = N(d.Unit) });
        foreach (var f in features ?? []) p.Features.Add(new PropertyFeature { Name = f.Name.Trim(), Value = N(f.Value) });
    }

    private void SyncChildren(Property p, IReadOnlyList<DimensionDto>? dims, IReadOnlyList<FeatureDto>? features)
    {
        if (dims is not null)
        {
            var wanted = dims.ToDictionary(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var old in p.Dimensions.ToList())
            {
                if (wanted.TryGetValue(old.Name, out var d)) { old.Value = d.Value; old.Unit = N(d.Unit); wanted.Remove(old.Name); }
                else { p.Dimensions.Remove(old); db.PropertyDimensions.Remove(old); }
            }
            foreach (var d in wanted.Values) p.Dimensions.Add(new PropertyDimension { PropertyId = p.Id, Name = d.Name.Trim(), Value = d.Value, Unit = N(d.Unit) });
        }
        if (features is not null)
        {
            var wanted = features.ToDictionary(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var old in p.Features.ToList())
            {
                if (wanted.TryGetValue(old.Name, out var f)) { old.Value = N(f.Value); wanted.Remove(old.Name); }
                else { p.Features.Remove(old); db.PropertyFeatures.Remove(old); }
            }
            foreach (var f in wanted.Values) p.Features.Add(new PropertyFeature { PropertyId = p.Id, Name = f.Name.Trim(), Value = N(f.Value) });
        }
    }

    private async Task SaveReservationAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw new DomainException("The property already has an active reservation."); }
    }

    private static ReservationDto ToDto(PropertyReservation r) => new(r.Id, r.PropertyId, r.ReservationDate, r.ExpiryDate, r.Amount, r.CustomerId, r.ReservedForName, r.ReservedForContact,
        r.ApprovedByUserId, r.ApprovedByName, r.Status, r.CreatedAtUtc, r.CreatedBy, r.ClosedAtUtc, r.ClosedBy, r.CloseReason);

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private static object Snapshot(Property p) => new
    {
        p.PropertyCode, p.PropertyNumber, p.ProjectId, p.BlockId, p.SectorId, p.StreetId, p.PropertyTypeId, p.Size, Unit = p.MeasurementUnit.ToString(), p.LocationDescription,
        p.FloorNumber, p.IsCorner, p.FacesMainRoad, p.FacesPark, Development = p.DevelopmentStatus.ToString(), Status = p.Status.ToString(), p.Remarks,
        Dimensions = p.Dimensions.Select(d => $"{d.Name}={d.Value}{d.Unit}").OrderBy(x => x).ToArray(), Features = p.Features.Select(f => $"{f.Name}={f.Value}").OrderBy(x => x).ToArray(),
    };
}
