using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Inventory;

/// <summary>Inventory summaries and reports. Always limited to the projects the caller may access.</summary>
public class InventoryReportService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, PropertyService properties)
{
    private sealed record Row(long ProjectId, long? BlockId, long TypeId, PropertyStatus Status, decimal Size, MeasurementUnit Unit);

    public async Task<InventorySummaryDto> GetProjectSummaryAsync(long projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        await properties.ExpireOverdueAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        var rows = await LoadAsync(projectId, ct);
        var types = await db.PropertyTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var blocks = await db.ProjectBlocks.AsNoTracking().Where(b => b.ProjectId == projectId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        return new InventorySummaryDto(project.Id, project.ProjectCode, project.ProjectName, rows.Count, ByStatus(rows),
            Group(rows, r => types.GetValueOrDefault(r.TypeId, "Unknown")),
            Group(rows, r => r.BlockId is { } b ? blocks.GetValueOrDefault(b, "Unknown") : "No block"));
    }

    public async Task<InventoryReportDto> GetReportAsync(long? projectId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InventoryView, ct);
        if (projectId is { } pid) await access.EnsureAccessAsync(pid, ct);
        await properties.ExpireOverdueAsync(ct);
        var rows = await LoadAsync(projectId, ct);
        var projects = await (await access.ScopeAsync(db.Projects.AsNoTracking(), ct)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode + " – " + p.ProjectName, ct);
        var types = await db.PropertyTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var blocks = await db.ProjectBlocks.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var bySize = rows.GroupBy(r => (r.Size, r.Unit)).OrderBy(g => g.Key.Unit).ThenBy(g => g.Key.Size)
            .Select(g => new SizeRowDto(g.Key.Size, g.Key.Unit, g.Count(), g.Count(x => x.Status == PropertyStatus.Available), g.Count(x => x.Status == PropertyStatus.Sold))).ToList();
        return new InventoryReportDto(rows.Count, ByStatus(rows),
            Group(rows, r => projects.GetValueOrDefault(r.ProjectId, "Unknown")),
            Group(rows, r => (projectId is null ? projects.GetValueOrDefault(r.ProjectId, "") + " / " : "") + (r.BlockId is { } b ? blocks.GetValueOrDefault(b, "Unknown") : "No block")),
            Group(rows, r => types.GetValueOrDefault(r.TypeId, "Unknown")), bySize);
    }

    private async Task<List<Row>> LoadAsync(long? projectId, CancellationToken ct)
    {
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.Properties.AsNoTracking().Where(p => projects.Any(pr => pr.Id == p.ProjectId));
        if (projectId is { } id) q = q.Where(p => p.ProjectId == id);
        return await q.Select(p => new Row(p.ProjectId, p.BlockId, p.PropertyTypeId, p.Status, p.Size, p.MeasurementUnit)).ToListAsync(ct);
    }

    private static IReadOnlyList<StatusCountDto> ByStatus(List<Row> rows) =>
        Enum.GetValues<PropertyStatus>().Select(s => new StatusCountDto(s, rows.Count(r => r.Status == s))).ToList();

    private static IReadOnlyList<InventoryRowDto> Group(List<Row> rows, Func<Row, string> key) =>
        rows.GroupBy(key).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new InventoryRowDto(g.Key, g.Count(),
            g.Count(r => r.Status == PropertyStatus.Available), g.Count(r => r.Status == PropertyStatus.Reserved), g.Count(r => r.Status == PropertyStatus.Booked),
            g.Count(r => r.Status == PropertyStatus.Sold), g.Count(r => r.Status is not (PropertyStatus.Available or PropertyStatus.Reserved or PropertyStatus.Booked or PropertyStatus.Sold)))).ToList();
}
