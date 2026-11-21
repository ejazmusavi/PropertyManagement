using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Documents;
using RealEstate.Application.Gis;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>GIS foundation screens: projects overview, boundaries (submit, verify), layers, map documents, coordinate systems. No map is drawn; the screens manage the verified survey data a future map will use.</summary>
public class GisController(GisService gis, ProjectService projects, PropertyService properties) : InstallmentControllerBase
{
    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new GisIndexViewModel { Projects = await gis.SummaryAsync(ct) });

    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> Project(long id, CancellationToken ct) => View(new GisProjectViewModel
    {
        Project = await projects.GetAsync(id, ct), Boundaries = await gis.ListProjectBoundariesAsync(id, ct), Layers = await gis.ListLayersAsync(id, ct),
        Documents = await gis.ListDocumentsAsync(id, ct), Systems = await gis.ListSrsAsync(true, ct),
    });

    // ----- boundaries -----
    [HttpGet, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> SubmitBoundary(long projectId, long? propertyId, CancellationToken ct) => View(await NewBoundaryFormAsync(projectId, propertyId, new BoundaryFormViewModel(), ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> SubmitBoundary(long projectId, long? propertyId, BoundaryFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var req = new SubmitBoundaryRequest(m.SrsId, m.GeometryJson ?? "", m.SurveyReference ?? "", m.SurveyedBy ?? "", m.SurveyDate ?? default, m.SourceDocumentId, m.Remarks);
                var b = propertyId is { } pid ? await gis.SubmitPropertyBoundaryAsync(pid, req, ct) : await gis.SubmitProjectBoundaryAsync(projectId, req, ct);
                TempData["Success"] = $"Boundary version {b.VersionNo} was submitted as a draft. A different user must verify it before it counts as exact.";
                return RedirectToAction(nameof(BoundaryDetails), new { projectId, propertyId, id = b.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await NewBoundaryFormAsync(projectId, propertyId, m, ct));
    }

    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> BoundaryDetails(long projectId, long? propertyId, long id, CancellationToken ct)
    {
        var b = propertyId is { } pid ? await gis.GetPropertyBoundaryAsync(pid, id, ct) : await gis.GetProjectBoundaryAsync(projectId, id, ct);
        var history = propertyId is { } p2 ? await gis.ListPropertyBoundariesAsync(p2, ct) : await gis.ListProjectBoundariesAsync(projectId, ct);
        return View(new BoundaryDetailsViewModel { Boundary = b, History = history });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisVerify)]
    public async Task<IActionResult> DecideBoundary(long projectId, long? propertyId, long id, bool approve, string? comment, string? rowVersion, CancellationToken ct)
    {
        try
        {
            var req = new BoundaryDecisionRequest(approve, comment, rowVersion);
            if (propertyId is { } pid) await gis.DecidePropertyBoundaryAsync(pid, id, req, ct); else await gis.DecideProjectBoundaryAsync(projectId, id, req, ct);
            TempData["Success"] = approve ? "The boundary was verified and is now the boundary in force." : "The boundary was rejected.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(BoundaryDetails), new { projectId, propertyId, id });
    }

    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> PropertyBoundaries(long propertyId, CancellationToken ct)
    {
        var p = await properties.GetAsync(propertyId, ct);
        return View(new PropertyBoundariesViewModel { PropertyId = propertyId, PropertyCode = p.PropertyCode, ProjectId = p.ProjectId, Boundaries = await gis.ListPropertyBoundariesAsync(propertyId, ct) });
    }

    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> ExportGeoJson(long projectId, CancellationToken ct) =>
        File(System.Text.Encoding.UTF8.GetBytes(await gis.ExportGeoJsonAsync(projectId, ct)), "application/geo+json", $"project-{projectId}-boundaries.geojson");

    // ----- layers -----
    [HttpGet, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> LayerForm(long projectId, long? id, CancellationToken ct)
    {
        var m = new LayerFormViewModel { ProjectId = projectId };
        if (id is { } i)
        {
            var l = await gis.GetLayerAsync(projectId, i, ct);
            m.Id = l.Id; m.Code = l.Code; m.Name = l.Name; m.Kind = l.Kind; m.SrsId = l.SrsId; m.SortOrder = l.SortOrder; m.IsVisibleByDefault = l.IsVisibleByDefault;
            m.StyleJson = l.StyleJson; m.SourceDocumentId = l.SourceDocumentId; m.Description = l.Description; m.IsActive = l.IsActive; m.RowVersion = l.RowVersion;
        }
        return View(await FillLayerAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> LayerForm(long projectId, LayerFormViewModel m, CancellationToken ct)
    {
        m.ProjectId = projectId;
        if (ModelState.IsValid)
        {
            try
            {
                await gis.SaveLayerAsync(projectId, m.Id, new SaveMapLayerRequest(m.Code ?? "", m.Name ?? "", m.Kind, m.SrsId, m.SortOrder, m.IsVisibleByDefault, m.StyleJson, m.SourceDocumentId, m.Description, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "The layer was saved.";
                return RedirectToAction(nameof(Project), new { id = projectId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await FillLayerAsync(m, ct));
    }

    // ----- documents -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> UploadDocument(long projectId, IFormFile? file, MapDocumentType type, string? title, long? srsId, string? surveyReference, DateOnly? surveyDate, string? description, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Project), new { id = projectId }); }
        try
        {
            await using var s = file.OpenReadStream();
            await gis.UploadDocumentAsync(projectId, type, title, srsId, surveyReference, surveyDate, description, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Map document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Project), new { id = projectId });
    }

    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> DownloadDocument(long projectId, long documentId, CancellationToken ct)
    {
        var f = await gis.DownloadDocumentAsync(projectId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> DeleteDocument(long projectId, long documentId, string? reason, CancellationToken ct)
    {
        try { await gis.DeleteDocumentAsync(projectId, documentId, reason ?? "", ct); TempData["Success"] = "The document was removed (history kept)."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Project), new { id = projectId });
    }

    // ----- coordinate systems -----
    [HasPermission(Permissions.GisView)]
    public async Task<IActionResult> Systems(CancellationToken ct) => View(new SrsListViewModel { Systems = await gis.ListSrsAsync(false, ct) });

    [HttpGet, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> SrsForm(long? id, CancellationToken ct)
    {
        var m = new SrsFormViewModel();
        if (id is { } i)
        {
            var s = (await gis.ListSrsAsync(false, ct)).FirstOrDefault(x => x.Id == i) ?? throw new Application.Common.NotFoundException("Spatial reference system not found.");
            m.Id = s.Id; m.Code = s.Code; m.Srid = s.Srid; m.Name = s.Name; m.Kind = s.Kind; m.Unit = s.Unit; m.Description = s.Description; m.IsActive = s.IsActive; m.IsSystem = s.IsSystem; m.RowVersion = s.RowVersion;
        }
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> SrsForm(SrsFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await gis.SaveSrsAsync(m.Id, new SaveSrsRequest(m.Code ?? "", m.Srid, m.Name ?? "", m.Kind, m.Unit ?? "", m.Description, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "The coordinate system was saved.";
                return RedirectToAction(nameof(Systems));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(m);
    }

    private async Task<BoundaryFormViewModel> NewBoundaryFormAsync(long projectId, long? propertyId, BoundaryFormViewModel m, CancellationToken ct)
    {
        m.ProjectId = projectId; m.PropertyId = propertyId;
        m.ProjectCode = (await projects.GetAsync(projectId, ct)).ProjectCode;
        if (propertyId is { } pid) m.PropertyCode = (await properties.GetAsync(pid, ct)).PropertyCode;
        m.Systems = await gis.ListSrsAsync(true, ct);
        m.Documents = await gis.ListDocumentsAsync(projectId, ct);
        return m;
    }

    private async Task<LayerFormViewModel> FillLayerAsync(LayerFormViewModel m, CancellationToken ct)
    {
        m.ProjectCode = (await projects.GetAsync(m.ProjectId, ct)).ProjectCode;
        m.Systems = await gis.ListSrsAsync(true, ct);
        m.Documents = await gis.ListDocumentsAsync(m.ProjectId, ct);
        return m;
    }
}
