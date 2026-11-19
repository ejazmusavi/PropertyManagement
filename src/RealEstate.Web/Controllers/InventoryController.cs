using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Property dashboard, inventory reports and the project-wide inventory history.</summary>
[HasPermission(Permissions.InventoryView)]
public class InventoryController(InventoryReportService reports, PropertyService properties, ProjectService projects) : Controller
{
    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectsAsync(CancellationToken ct) => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;

    public async Task<IActionResult> Index(long? projectId, CancellationToken ct)
    {
        var list = await ProjectsAsync(ct);
        var selected = projectId ?? list.FirstOrDefault()?.Id;
        return View(new InventoryDashboardViewModel { Projects = list, Summary = selected is { } id ? await reports.GetProjectSummaryAsync(id, ct) : null });
    }

    public async Task<IActionResult> Reports(long? projectId, CancellationToken ct) =>
        View(new InventoryReportsViewModel { ProjectId = projectId, Projects = await ProjectsAsync(ct), Report = await reports.GetReportAsync(projectId, ct) });

    public async Task<IActionResult> History(InventoryHistoryRequest filter, CancellationToken ct)
    {
        filter.PageSize = 30;
        return View(new InventoryHistoryViewModel { Filter = filter, History = await properties.ListHistoryAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryReserve)]
    public async Task<IActionResult> ExpireReservations(long? projectId, CancellationToken ct)
    {
        var n = await properties.ExpireOverdueAsync(ct);
        TempData["Success"] = n == 0 ? "No overdue reservations." : $"{n} overdue reservation(s) returned to available stock.";
        return RedirectToAction(nameof(Index), new { projectId });
    }
}

public class PropertySetupController(PropertySetupService setup, ProjectService projects) : Controller
{
    // ----- categories & types -----
    [HasPermission(Permissions.InventoryView)]
    public async Task<IActionResult> Types(CancellationToken ct) =>
        View(new TypesViewModel { Categories = await setup.ListCategoriesAsync(ct), Types = await setup.ListTypesAsync(false, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventorySetup)]
    public async Task<IActionResult> SaveCategory(long? id, string code, string name, string? description, bool isActive = true, CancellationToken ct = default)
    {
        try
        {
            var req = new SaveCategoryRequest(code ?? "", name ?? "", description, isActive);
            if (id is { } existing) await setup.UpdateCategoryAsync(existing, req, ct); else await setup.CreateCategoryAsync(req, ct);
            TempData["Success"] = "Category saved.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Types));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventorySetup)]
    public async Task<IActionResult> SaveType(long? id, string code, string name, long categoryId, PropertyForm form, bool requiresFloor, bool isActive = true, CancellationToken ct = default)
    {
        try
        {
            var req = new SavePropertyTypeRequest(code ?? "", name ?? "", categoryId, form, requiresFloor, isActive);
            if (id is { } existing) await setup.UpdateTypeAsync(existing, req, ct); else await setup.CreateTypeAsync(req, ct);
            TempData["Success"] = "Property type saved.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Types));
    }

    // ----- blocks / sectors / streets -----
    [HasPermission(Permissions.InventoryView)] public Task<IActionResult> Blocks(long? projectId, CancellationToken ct) => ZonesAsync(ZoneKind.Block, projectId, ct);
    [HasPermission(Permissions.InventoryView)] public Task<IActionResult> Sectors(long? projectId, CancellationToken ct) => ZonesAsync(ZoneKind.Sector, projectId, ct);
    [HasPermission(Permissions.InventoryView)] public Task<IActionResult> Streets(long? projectId, CancellationToken ct) => ZonesAsync(ZoneKind.Street, projectId, ct);

    private async Task<IActionResult> ZonesAsync(ZoneKind kind, long? projectId, CancellationToken ct)
    {
        var list = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;
        var selected = projectId ?? list.FirstOrDefault()?.Id ?? 0;
        return View("Zones", new ZonesViewModel { Kind = kind, ProjectId = selected, Projects = list, Zones = selected == 0 ? [] : await setup.ListZonesAsync(kind, selected, false, ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventorySetup)]
    public async Task<IActionResult> SaveZone(ZoneKind kind, long projectId, long? id, string code, string name, bool isActive = true, CancellationToken ct = default)
    {
        try
        {
            var req = new SaveZoneRequest(code ?? "", name ?? "", isActive);
            if (id is { } existing) await setup.UpdateZoneAsync(kind, projectId, existing, req, ct); else await setup.CreateZoneAsync(kind, projectId, req, ct);
            TempData["Success"] = $"{kind} saved.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(kind switch { ZoneKind.Block => nameof(Blocks), ZoneKind.Sector => nameof(Sectors), _ => nameof(Streets) }, new { projectId });
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}
