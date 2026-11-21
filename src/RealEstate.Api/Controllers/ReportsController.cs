using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Reporting;

namespace RealEstate.Api.Controllers;

/// <summary>Reports (Phase 9): catalog, report data as JSON (filtered, paged) and PDF/Excel exports. Permissions and project/customer scope are enforced by the reporting services.</summary>
[ApiController, Authorize]
[Route("api/v1/reports")]
public class ReportsController(ReportingService reports) : ControllerBase
{
    /// <summary>The reports the caller may open, with the filters each one supports.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReportDefinitionDto>>> List(CancellationToken ct) => Ok(await reports.ListAsync(ct));

    /// <summary>Projects (the caller's), blocks and payment methods for filter pickers.</summary>
    [HttpGet("lookups")]
    public async Task<ActionResult<ReportLookupsDto>> Lookups(CancellationToken ct) => await reports.LookupsAsync(ct);

    [HttpGet("{key}")]
    public async Task<ActionResult<ReportTableDto>> Run(string key, [FromQuery] ReportFilters filters, CancellationToken ct) => await reports.RunAsync(key, filters, ct);

    /// <summary>Exports the whole report (not just a page) as <c>pdf</c> or <c>xlsx</c>. Needs reports.export; every export is audited.</summary>
    [HttpGet("{key}/export")]
    public async Task<IActionResult> Export(string key, [FromQuery] ReportFilters filters, [FromQuery] string format = "pdf", CancellationToken ct = default)
    {
        var f = format.ToLowerInvariant() switch
        {
            "pdf" => ExportFormat.Pdf,
            "xlsx" or "excel" => ExportFormat.Excel,
            _ => throw new RequestValidationException(new Dictionary<string, string[]> { ["format"] = ["Use pdf or xlsx."] }),
        };
        var file = await reports.ExportAsync(key, filters, f, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

/// <summary>Management dashboards (Phase 9): company-wide and per project.</summary>
[ApiController, Authorize]
[Route("api/v1/dashboards")]
public class DashboardsController(DashboardService dashboards) : ControllerBase
{
    [HttpGet("company")]
    public async Task<ActionResult<CompanyDashboardDto>> Company([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        await dashboards.CompanyAsync(new DashboardFilters(from, to), ct);

    [HttpGet("projects/{id:long}")]
    public async Task<ActionResult<ProjectDashboardDto>> Project(long id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        await dashboards.ProjectAsync(id, new DashboardFilters(from, to), ct);
}
