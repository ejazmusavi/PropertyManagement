using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Inventory;

/// <summary>Property categories, property types and project blocks/sectors/streets.</summary>
public class PropertySetupService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit,
    IValidator<SaveCategoryRequest> categoryValidator, IValidator<SavePropertyTypeRequest> typeValidator, IValidator<SaveZoneRequest> zoneValidator)
{
    // ---------- categories ----------
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        var counts = await db.PropertyTypes.AsNoTracking().GroupBy(t => t.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var items = await db.PropertyCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        return items.Select(c => new CategoryDto(c.Id, c.Code, c.Name, c.Description, c.IsSystem, c.IsActive, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await categoryValidator.ValidateOrThrowAsync(r, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        if (await db.PropertyCategories.AnyAsync(c => c.Code == code, ct)) throw Invalid("Code", "A category with this code already exists.");
        if (await db.PropertyCategories.AnyAsync(c => c.Name == name, ct)) throw Invalid("Name", "A category with this name already exists.");
        var c = new PropertyCategory { Code = code, Name = name, Description = N(r.Description), IsActive = true };
        db.PropertyCategories.Add(c);
        await SaveUniqueAsync("Code", "A category with this code or name already exists.", ct);
        await audit.RecordAsync(new AuditEntry("PropertyCategory.Created", "PropertyCategory", c.Id.ToString(), NewValues: new { c.Code, c.Name }), ct);
        return new CategoryDto(c.Id, c.Code, c.Name, c.Description, false, true, 0);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(long id, SaveCategoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await categoryValidator.ValidateOrThrowAsync(r, ct);
        var c = await db.PropertyCategories.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Category not found.");
        var name = r.Name.Trim();
        if (await db.PropertyCategories.AnyAsync(x => x.Id != id && x.Name == name, ct)) throw Invalid("Name", "A category with this name already exists.");
        if (c.IsSystem && !r.IsActive) throw new DomainException("System categories cannot be deactivated.");
        var before = new { c.Name, c.Description, c.IsActive };
        c.Name = name; c.Description = N(r.Description); c.IsActive = r.IsActive;
        await SaveUniqueAsync("Name", "A category with this name already exists.", ct);
        await audit.RecordAsync(new AuditEntry("PropertyCategory.Updated", "PropertyCategory", id.ToString(), before, new { c.Name, c.Description, c.IsActive }), ct);
        return (await ListCategoriesAsync(ct)).First(x => x.Id == id);
    }

    // ---------- types ----------
    public async Task<IReadOnlyList<PropertyTypeDto>> ListTypesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        var q = db.PropertyTypes.AsNoTracking().Include(t => t.Category).AsQueryable();
        if (activeOnly) q = q.Where(t => t.IsActive && t.Category!.IsActive);
        return await q.OrderBy(t => t.Category!.Name).ThenBy(t => t.Name)
            .Select(t => new PropertyTypeDto(t.Id, t.Code, t.Name, t.CategoryId, t.Category!.Name, t.Form, t.RequiresFloor, t.IsSystem, t.IsActive)).ToListAsync(ct);
    }

    public async Task<PropertyTypeDto> CreateTypeAsync(SavePropertyTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await typeValidator.ValidateOrThrowAsync(r, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        var cat = await db.PropertyCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId, ct) ?? throw Invalid("CategoryId", "Category not found.");
        if (!cat.IsActive) throw Invalid("CategoryId", "The category is inactive.");
        if (await db.PropertyTypes.AnyAsync(t => t.Code == code, ct)) throw Invalid("Code", "A property type with this code already exists.");
        if (await db.PropertyTypes.AnyAsync(t => t.Name == name, ct)) throw Invalid("Name", "A property type with this name already exists.");
        var t = new PropertyType { Code = code, Name = name, CategoryId = cat.Id, Form = r.Form, RequiresFloor = r.RequiresFloor, IsActive = true };
        db.PropertyTypes.Add(t);
        await SaveUniqueAsync("Code", "A property type with this code or name already exists.", ct);
        await audit.RecordAsync(new AuditEntry("PropertyType.Created", "PropertyType", t.Id.ToString(), NewValues: new { t.Code, t.Name, Category = cat.Name, Form = t.Form.ToString() }), ct);
        return new PropertyTypeDto(t.Id, t.Code, t.Name, cat.Id, cat.Name, t.Form, t.RequiresFloor, false, true);
    }

    public async Task<PropertyTypeDto> UpdateTypeAsync(long id, SavePropertyTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await typeValidator.ValidateOrThrowAsync(r, ct);
        var t = await db.PropertyTypes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Property type not found.");
        var name = r.Name.Trim();
        if (await db.PropertyTypes.AnyAsync(x => x.Id != id && x.Name == name, ct)) throw Invalid("Name", "A property type with this name already exists.");
        var cat = await db.PropertyCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId, ct) ?? throw Invalid("CategoryId", "Category not found.");
        if (t.IsSystem && (!r.IsActive || r.Form != t.Form || r.CategoryId != t.CategoryId)) throw new DomainException("A system property type can only be renamed or have its floor requirement changed.");
        if (await db.Properties.AnyAsync(p => p.PropertyTypeId == id, ct) && (r.Form != t.Form || r.CategoryId != t.CategoryId))
            throw new DomainException("The category and form cannot change once properties use this type.");
        var before = new { t.Name, t.CategoryId, Form = t.Form.ToString(), t.RequiresFloor, t.IsActive };
        t.Name = name; t.CategoryId = cat.Id; t.Form = r.Form; t.RequiresFloor = r.RequiresFloor; t.IsActive = r.IsActive;
        await SaveUniqueAsync("Name", "A property type with this name already exists.", ct);
        await audit.RecordAsync(new AuditEntry("PropertyType.Updated", "PropertyType", id.ToString(), before, new { t.Name, t.CategoryId, Form = t.Form.ToString(), t.RequiresFloor, t.IsActive }), ct);
        return new PropertyTypeDto(t.Id, t.Code, t.Name, cat.Id, cat.Name, t.Form, t.RequiresFloor, t.IsSystem, t.IsActive);
    }

    // ---------- blocks / sectors / streets ----------
    public async Task<IReadOnlyList<ZoneDto>> ListZonesAsync(ZoneKind kind, long projectId, bool activeOnly = false, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        return kind switch
        {
            ZoneKind.Block => await Project(db.ProjectBlocks, projectId, activeOnly, p => p.BlockId, ct),
            ZoneKind.Sector => await Project(db.ProjectSectors, projectId, activeOnly, p => p.SectorId, ct),
            ZoneKind.Street => await Project(db.ProjectStreets, projectId, activeOnly, p => p.StreetId, ct),
            _ => throw new DomainException("Unknown zone kind."),
        };
    }

    private async Task<IReadOnlyList<ZoneDto>> Project<T>(DbSet<T> set, long projectId, bool activeOnly, System.Linq.Expressions.Expression<Func<Property, long?>> key, CancellationToken ct) where T : ProjectZone
    {
        var q = set.AsNoTracking().Where(z => z.ProjectId == projectId);
        if (activeOnly) q = q.Where(z => z.IsActive);
        var zones = await q.OrderBy(z => z.Code).ToListAsync(ct);
        var counts = await db.Properties.AsNoTracking().Where(p => p.ProjectId == projectId).GroupBy(key).Select(g => new { g.Key, N = g.Count() })
            .Where(g => g.Key != null).ToDictionaryAsync(g => g.Key!.Value, g => g.N, ct);
        return zones.Select(z => new ZoneDto(z.Id, z.ProjectId, z.Code, z.Name, z.IsActive, counts.GetValueOrDefault(z.Id))).ToList();
    }

    public async Task<ZoneDto> CreateZoneAsync(ZoneKind kind, long projectId, SaveZoneRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await zoneValidator.ValidateOrThrowAsync(r, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        ProjectZone z = kind switch { ZoneKind.Block => new ProjectBlock(), ZoneKind.Sector => new ProjectSector(), ZoneKind.Street => new ProjectStreet(), _ => throw new DomainException("Unknown zone kind.") };
        if (await ExistsAsync(kind, projectId, null, code, name, ct)) throw Invalid("Code", $"A {kind.ToString().ToLowerInvariant()} with this code or name already exists in the project.");
        z.ProjectId = projectId; z.Code = code; z.Name = name; z.IsActive = true;
        switch (z) { case ProjectBlock b: db.ProjectBlocks.Add(b); break; case ProjectSector s: db.ProjectSectors.Add(s); break; case ProjectStreet st: db.ProjectStreets.Add(st); break; }
        await SaveUniqueAsync("Code", $"A {kind.ToString().ToLowerInvariant()} with this code or name already exists in the project.", ct);
        await audit.RecordAsync(new AuditEntry($"Project{kind}.Created", $"Project{kind}", z.Id.ToString(), NewValues: new { projectId, z.Code, z.Name }), ct);
        return new ZoneDto(z.Id, projectId, z.Code, z.Name, true, 0);
    }

    public async Task<ZoneDto> UpdateZoneAsync(ZoneKind kind, long projectId, long id, SaveZoneRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventorySetup, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await zoneValidator.ValidateOrThrowAsync(r, ct);
        ProjectZone z = (kind switch
        {
            ZoneKind.Block => await db.ProjectBlocks.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct) as ProjectZone,
            ZoneKind.Sector => await db.ProjectSectors.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct),
            ZoneKind.Street => await db.ProjectStreets.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct),
            _ => null,
        }) ?? throw new NotFoundException($"{kind} not found.");
        var name = r.Name.Trim();
        if (await ExistsAsync(kind, projectId, id, z.Code, name, ct)) throw Invalid("Name", $"A {kind.ToString().ToLowerInvariant()} with this name already exists in the project.");
        var before = new { z.Name, z.IsActive };
        z.Name = name; z.IsActive = r.IsActive;
        await SaveUniqueAsync("Name", $"A {kind.ToString().ToLowerInvariant()} with this name already exists in the project.", ct);
        await audit.RecordAsync(new AuditEntry($"Project{kind}.Updated", $"Project{kind}", id.ToString(), before, new { z.Name, z.IsActive }), ct);
        return (await ListZonesAsync(kind, projectId, false, ct)).First(x => x.Id == id);
    }

    private Task<bool> ExistsAsync(ZoneKind kind, long projectId, long? exceptId, string code, string name, CancellationToken ct) => kind switch
    {
        ZoneKind.Block => db.ProjectBlocks.AnyAsync(z => z.ProjectId == projectId && z.Id != exceptId && (z.Code == code || z.Name == name), ct),
        ZoneKind.Sector => db.ProjectSectors.AnyAsync(z => z.ProjectId == projectId && z.Id != exceptId && (z.Code == code || z.Name == name), ct),
        _ => db.ProjectStreets.AnyAsync(z => z.ProjectId == projectId && z.Id != exceptId && (z.Code == code || z.Name == name), ct),
    };

    private async Task SaveUniqueAsync(string field, string message, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Invalid(field, message); }
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
