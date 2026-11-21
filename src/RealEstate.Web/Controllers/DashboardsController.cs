using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Reporting;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Company and project dashboards (Phase 9). All figures are calculated by <see cref="DashboardService"/> from the business data and the general ledger.</summary>
[HasPermission(Permissions.ReportsView)]
public class DashboardsController(DashboardService dashboards) : InstallmentControllerBase
{
    public async Task<IActionResult> Company(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        try { return View(new DashboardViewModel<CompanyDashboardDto> { Data = await dashboards.CompanyAsync(new DashboardFilters(from, to), ct), From = from, To = to }); }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); return RedirectToAction(nameof(Company)); }
    }

    public async Task<IActionResult> Project(long id, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        try { return View(new DashboardViewModel<ProjectDashboardDto> { Data = await dashboards.ProjectAsync(id, new DashboardFilters(from, to), ct), From = from, To = to }); }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); return RedirectToAction(nameof(Project), new { id }); }
    }
}
