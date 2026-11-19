using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Application.Documents;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.ProjectsView)]
public class ProjectsController(ProjectService projects, ProjectAccessAdminService projectAccess) : Controller
{
    public async Task<IActionResult> Index(string? search, ProjectStatus? status, string? city, string? sortBy, bool desc = false, int page = 1, CancellationToken ct = default)
    {
        var result = await projects.ListAsync(new ProjectListRequest { Page = page, PageSize = 20, Search = search, Status = status, City = city, SortBy = sortBy, Descending = desc }, ct);
        return View(new ProjectListViewModel { Projects = result, Search = search, Status = status, City = city, SortBy = sortBy, Descending = desc, Cities = await projects.ListCitiesAsync(ct) });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) =>
        View(new ProjectDetailsViewModel { Project = await projects.GetAsync(id, ct), Documents = await projects.ListDocumentsAsync(id, ct) });

    [HttpGet, HasPermission(Permissions.ProjectsCreate)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", new ProjectFormViewModel { Managers = await projects.ListManagerCandidatesAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsCreate)]
    public async Task<IActionResult> Create(ProjectFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var created = await projects.CreateAsync(new CreateProjectRequest(m.ProjectCode, m.ProjectName, m.Description, m.City, m.Address, m.StartDate,
                    m.ExpectedCompletionDate, m.InitialArea, m.MeasurementUnit, m.ProjectManagerId, m.ToAddress()), ct);
                TempData["Success"] = "Project created.";
                return RedirectToAction(nameof(Details), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Managers = await projects.ListManagerCandidatesAsync(ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.ProjectsEdit)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var p = await projects.GetAsync(id, ct);
        var a = p.AddressDetails;
        return View("Form", new ProjectFormViewModel
        {
            Id = p.Id, RowVersion = p.RowVersion, ProjectCode = p.ProjectCode, ProjectName = p.ProjectName, Description = p.Description, City = p.City, Address = p.Address,
            StartDate = p.StartDate, ExpectedCompletionDate = p.ExpectedCompletionDate, InitialArea = p.InitialArea, MeasurementUnit = p.MeasurementUnit,
            ProjectManagerId = p.ProjectManagerId, AddressLine = a?.AddressLine, Locality = a?.Locality, Province = a?.Province, Country = a?.Country,
            PostalCode = a?.PostalCode, Latitude = a?.Latitude, Longitude = a?.Longitude, Landmark = a?.Landmark,
            Managers = await projects.ListManagerCandidatesAsync(ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsEdit)]
    public async Task<IActionResult> Edit(long id, ProjectFormViewModel m, CancellationToken ct)
    {
        ModelState.Remove(nameof(m.ProjectCode));
        if (ModelState.IsValid)
        {
            try
            {
                await projects.UpdateAsync(id, new UpdateProjectRequest(m.ProjectName, m.Description, m.City, m.Address, m.StartDate, m.ExpectedCompletionDate,
                    m.ProjectManagerId, m.ToAddress(), m.RowVersion), ct);
                TempData["Success"] = "Project saved.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var current = await projects.GetAsync(id, ct);
        m.Id = id; m.ProjectCode = current.ProjectCode; m.InitialArea = current.InitialArea; m.MeasurementUnit = current.MeasurementUnit;
        m.Managers = await projects.ListManagerCandidatesAsync(ct);
        return View("Form", m);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsStatus)]
    public async Task<IActionResult> ChangeStatus(ChangeStatusViewModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "A reason is required to change the status."; return RedirectToAction(nameof(Details), new { id = m.ProjectId }); }
        try
        {
            await projects.ChangeStatusAsync(m.ProjectId, new ChangeProjectStatusRequest(m.Status, m.Reason, m.RowVersion), ct);
            TempData["Success"] = $"Status changed to {m.Status}.";
        }
        catch (Exception ex) when (ex is Application.Common.RequestValidationException or DomainException && ex is not Application.Common.ForbiddenException && ex is not Application.Common.NotFoundException)
        { TempData["Error"] = ex is Application.Common.RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message; }
        return RedirectToAction(nameof(Details), new { id = m.ProjectId });
    }

    // ----- documents -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsDocuments)]
    public async Task<IActionResult> UploadDocument(long id, IFormFile? file, ProjectDocumentType type, string? title, string? description, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Details), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await projects.UploadDocumentAsync(id, type, title, description, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (ex is DomainException && ex is not Application.Common.ForbiddenException && ex is not Application.Common.NotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> DownloadDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await projects.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await projects.DeleteDocumentAsync(id, documentId, reason, ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (ex is DomainException && ex is not Application.Common.ForbiddenException && ex is not Application.Common.NotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- project access assignment -----
    [HttpGet, HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<IActionResult> Access(long id, string? search, CancellationToken ct) =>
        View(new ProjectAccessViewModel { Project = await projects.GetAsync(id, ct), Assigned = await projectAccess.ListAsync(id, ct), Candidates = await projectAccess.ListCandidatesAsync(id, search, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<IActionResult> AssignAccess(long id, Guid userId, CancellationToken ct)
    {
        try { await projectAccess.AssignAsync(id, userId, ct); TempData["Success"] = "Access granted."; }
        catch (Exception ex) when (ex is DomainException && ex is not Application.Common.ForbiddenException && ex is not Application.Common.NotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Access), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<IActionResult> RevokeAccess(long id, Guid userId, string reason, CancellationToken ct)
    {
        try { await projectAccess.RevokeAsync(id, userId, reason, ct); TempData["Success"] = "Access revoked."; }
        catch (Exception ex) when (ex is DomainException && ex is not Application.Common.ForbiddenException && ex is not Application.Common.NotFoundException)
        { TempData["Error"] = ex is Application.Common.RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message; }
        return RedirectToAction(nameof(Access), new { id });
    }
}
