using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.InventoryView)]
public class PropertiesController(PropertyService properties, PropertySetupService setup, ProjectService projects) : Controller
{
    public async Task<IActionResult> Index(PropertyListRequest filter, bool desc = false, CancellationToken ct = default)
    {
        filter.PageSize = 20;
        filter.Descending = desc;
        var vm = new PropertyListViewModel
        {
            Filter = filter, Descending = desc, Properties = await properties.ListAsync(filter, ct),
            Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items, Types = await setup.ListTypesAsync(false, ct),
            Blocks = filter.ProjectId is { } pid ? await setup.ListZonesAsync(ZoneKind.Block, pid, false, ct) : [],
        };
        return View(vm);
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(await BuildDetailsAsync(id, null, ct));

    // ----- create / edit -----
    [HttpGet, HasPermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Create(long? projectId, CancellationToken ct)
    {
        if (projectId is null)
            return View("ChooseProject", (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items);
        return View("Form", await FillLookupsAsync(new PropertyFormViewModel { ProjectId = projectId.Value }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Create(PropertyFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var created = await properties.CreateAsync(new CreatePropertyRequest(m.ProjectId, m.PropertyCode, m.PropertyNumber, m.BlockId, m.SectorId, m.StreetId, m.PropertyTypeId,
                    m.Size, m.MeasurementUnit, m.LocationDescription, m.FloorNumber, m.IsCorner, m.FacesMainRoad, m.FacesPark, m.DevelopmentStatus, m.Remarks, m.ToDimensions(), m.ToFeatures()), ct);
                TempData["Success"] = "Property created.";
                return RedirectToAction(nameof(Details), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", await FillLookupsAsync(m, ct));
    }

    [HttpGet, HasPermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var p = await properties.GetAsync(id, ct);
        var m = new PropertyFormViewModel
        {
            Id = p.Id, RowVersion = p.RowVersion, ProjectId = p.ProjectId, CurrentStatus = p.Status, PropertyCode = p.PropertyCode, PropertyNumber = p.PropertyNumber,
            BlockId = p.BlockId, SectorId = p.SectorId, StreetId = p.StreetId, PropertyTypeId = p.PropertyTypeId, Size = p.Size, MeasurementUnit = p.Unit,
            LocationDescription = p.LocationDescription, FloorNumber = p.FloorNumber, IsCorner = p.IsCorner, FacesMainRoad = p.FacesMainRoad, FacesPark = p.FacesPark,
            DevelopmentStatus = p.DevelopmentStatus, Remarks = p.Remarks,
            Dimensions = p.Dimensions.Select(d => new DimensionInput { Name = d.Name, Value = d.Value, Unit = d.Unit }).ToList(),
            Features = p.Features.Select(f => new FeatureInput { Name = f.Name, Value = f.Value }).ToList(),
        };
        return View("Form", await FillLookupsAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Edit(long id, PropertyFormViewModel m, CancellationToken ct)
    {
        m.Id = id;
        if (ModelState.IsValid)
        {
            try
            {
                await properties.UpdateAsync(id, new UpdatePropertyRequest(m.PropertyNumber, m.BlockId, m.SectorId, m.StreetId, m.PropertyTypeId, m.Size, m.MeasurementUnit,
                    m.LocationDescription, m.FloorNumber, m.IsCorner, m.FacesMainRoad, m.FacesPark, m.DevelopmentStatus, m.Remarks, m.ToDimensions(), m.ToFeatures(), m.RowVersion), ct);
                TempData["Success"] = "Property saved.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", await FillLookupsAsync(m, ct));
    }

    // ----- reservation & status -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryReserve)]
    public async Task<IActionResult> Reserve(long id, ReserveViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid && m.ApprovedByUserId is { } approver)
        {
            try
            {
                await properties.ReserveAsync(id, new ReservePropertyRequest(m.ReservationDate, m.ExpiryDate, m.Amount, null, m.ReservedForName, m.ReservedForContact, approver, m.RowVersion), ct);
                TempData["Success"] = "Property reserved.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        else if (m.ApprovedByUserId is null) ModelState.AddModelError(nameof(m.ApprovedByUserId), "Choose the approver.");
        return View("Details", await BuildDetailsAsync(id, m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryReserve)]
    public async Task<IActionResult> Release(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await properties.ReleaseReservationAsync(id, new ReleaseReservationRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Reservation released."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryStatus)]
    public async Task<IActionResult> ChangeStatus(long id, PropertyStatus status, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await properties.ChangeStatusAsync(id, new ChangePropertyStatusRequest(status, reason ?? "", rowVersion), ct); TempData["Success"] = $"Status changed to {status}."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- documents -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryDocuments)]
    public async Task<IActionResult> UploadDocument(long id, IFormFile? file, PropertyDocumentType type, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Details), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await properties.UploadDocumentAsync(id, type, title, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> DownloadDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await properties.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InventoryDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await properties.DeleteDocumentAsync(id, documentId, reason ?? "", ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- helpers -----
    private async Task<PropertyDetailsViewModel> BuildDetailsAsync(long id, ReserveViewModel? posted, CancellationToken ct)
    {
        var p = await properties.GetAsync(id, ct);
        var today = await properties.TodayAsync(ct);
        var vm = new PropertyDetailsViewModel
        {
            Property = p, Today = today, Reservations = await properties.ListReservationsAsync(id, ct), Documents = await properties.ListDocumentsAsync(id, ct),
            History = await properties.ListHistoryAsync(new InventoryHistoryRequest { PropertyId = id, PageSize = 50 }, ct),
        };
        vm.Reserve = posted ?? new ReserveViewModel { ReservationDate = today, ExpiryDate = today.AddDays(7) };
        vm.Reserve.RowVersion = p.RowVersion;
        try { vm.Approvers = await properties.ListApproversAsync(ct); } catch (ForbiddenException) { vm.Approvers = []; }
        return vm;
    }

    private async Task<PropertyFormViewModel> FillLookupsAsync(PropertyFormViewModel m, CancellationToken ct)
    {
        var project = await projects.GetAsync(m.ProjectId, ct);
        m.ProjectName = $"{project.ProjectCode} – {project.ProjectName}";
        m.Types = await setup.ListTypesAsync(false, ct);
        m.Blocks = await setup.ListZonesAsync(ZoneKind.Block, m.ProjectId, false, ct);
        m.Sectors = await setup.ListZonesAsync(ZoneKind.Sector, m.ProjectId, false, ct);
        m.Streets = await setup.ListZonesAsync(ZoneKind.Street, m.ProjectId, false, ct);
        if (m.Id is not null && m.CurrentStatus is { } s) m.StructureLocked = s is not (PropertyStatus.Available or PropertyStatus.Reserved or PropertyStatus.Cancelled);
        return m;
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}
