using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.LandManagement;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.LandView)]
public class LandController(LandService land) : Controller
{
    public async Task<IActionResult> Index(long projectId, CancellationToken ct) => View(await land.GetOverviewAsync(projectId, ct));

    // ----- parcels -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> AddParcel(AddParcelViewModel m, CancellationToken ct)
    {
        try
        {
            await land.AddParcelAsync(m.ProjectId, new AddLandParcelRequest(m.ParcelCode, m.Description, m.Area, m.TitleReference, m.Location), ct);
            TempData["Success"] = "Parcel added.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Index), new { projectId = m.ProjectId });
    }

    // ----- acquisitions -----
    [HttpGet, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> Acquire(long projectId, CancellationToken ct)
    {
        var o = await land.GetOverviewAsync(projectId, ct);
        return View(new AcquireLandViewModel { ProjectId = projectId, ProjectName = o.ProjectName, Unit = o.MeasurementUnit, RowVersion = o.RowVersion });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> Acquire(AcquireLandViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var docs = await ToUploadsAsync(m.Document, LandDocumentType.Agreement, "Acquisition document");
                await land.AcquireAsync(m.ProjectId, new AcquireLandRequest(m.AcquisitionDate, m.SellerName, m.Area, m.Cost, m.ReferenceNumber, m.Notes, m.ParcelCode, m.RowVersion), docs, ct);
                TempData["Success"] = "Land acquisition recorded and project area updated.";
                return RedirectToAction(nameof(Index), new { projectId = m.ProjectId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var o = await land.GetOverviewAsync(m.ProjectId, ct);
        m.ProjectName = o.ProjectName; m.Unit = o.MeasurementUnit; m.RowVersion = o.RowVersion;
        return View(m);
    }

    // ----- adjustments -----
    [HttpGet, HasPermission(Permissions.LandAdjust)]
    public async Task<IActionResult> Adjust(long projectId, CancellationToken ct) => View(await BuildAdjustAsync(projectId, null, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandAdjust)]
    public async Task<IActionResult> Adjust(AdjustLandViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var docs = await ToUploadsAsync(m.Document, LandDocumentType.AdjustmentSupport, "Adjustment support");
                await land.AdjustAsync(m.ProjectId, new AdjustLandRequest(m.AdjustmentDate, m.Type, m.Area, m.Reason, m.SupportingReference, m.ApprovedByUserId!.Value, m.RowVersion), docs, ct);
                TempData["Success"] = "Area adjustment recorded.";
                return RedirectToAction(nameof(History), new { projectId = m.ProjectId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await BuildAdjustAsync(m.ProjectId, m, ct));
    }

    // ----- distribution -----
    [HttpGet, HasPermission(Permissions.LandView)]
    public async Task<IActionResult> Distribution(long projectId, CancellationToken ct) => View(await BuildDistributionAsync(projectId, null, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> Distribution(DistributionViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var lines = LandRules.ClassifiedCategories.Select(c => new DistributionInput(c, m.Areas.GetValueOrDefault(c))).ToList();
                await land.SetDistributionAsync(m.ProjectId, new SetLandDistributionRequest(lines, m.Reason, m.DistributionVersion), ct);
                TempData["Success"] = "Land distribution saved.";
                return RedirectToAction(nameof(Distribution), new { projectId = m.ProjectId });
            }
            catch (ConcurrencyException)
            {
                // Someone else changed the distribution (or the project area): show the current values instead of the stale edit.
                var fresh = await BuildDistributionAsync(m.ProjectId, null, ct);
                fresh.Reason = m.Reason;
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, "The distribution was changed by another user while you were editing. The current values are shown; review them and save again.");
                return View(fresh);
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await BuildDistributionAsync(m.ProjectId, m, ct));
    }

    // ----- history -----
    public async Task<IActionResult> History(long projectId, int page = 1, int dpage = 1, CancellationToken ct = default) =>
        View(new AreaHistoryViewModel
        {
            Overview = await land.GetOverviewAsync(projectId, ct),
            History = await land.GetAreaHistoryAsync(projectId, new PagedRequest { Page = page, PageSize = 20 }, ct),
            DistributionHistory = await land.GetDistributionHistoryAsync(projectId, new PagedRequest { Page = dpage, PageSize = 20 }, ct),
        });

    // ----- documents -----
    public async Task<IActionResult> Documents(long projectId, CancellationToken ct) =>
        View(new LandDocumentsViewModel { Overview = await land.GetOverviewAsync(projectId, ct), Documents = await land.ListDocumentsAsync(projectId, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> UploadDocument(long projectId, IFormFile? file, LandDocumentType type, string? title, long? parcelId, long? acquisitionId, long? adjustmentId, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Documents), new { projectId }); }
        try
        {
            await using var s = file.OpenReadStream();
            await land.UploadDocumentAsync(projectId, new LandDocumentUpload(new FileUpload(file.FileName, file.ContentType, file.Length, s), type, title), parcelId, acquisitionId, adjustmentId, ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Documents), new { projectId });
    }

    public async Task<IActionResult> DownloadDocument(long projectId, long documentId, CancellationToken ct)
    {
        var f = await land.DownloadDocumentAsync(projectId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> DeleteDocument(long projectId, long documentId, string reason, CancellationToken ct)
    {
        try { await land.DeleteDocumentAsync(projectId, documentId, reason, ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Documents), new { projectId });
    }

    // ----- helpers -----
    private async Task<AdjustLandViewModel> BuildAdjustAsync(long projectId, AdjustLandViewModel? posted, CancellationToken ct)
    {
        var o = await land.GetOverviewAsync(projectId, ct);
        var vm = posted ?? new AdjustLandViewModel();
        vm.ProjectId = projectId; vm.ProjectName = o.ProjectName; vm.Unit = o.MeasurementUnit; vm.CurrentArea = o.TotalProjectArea; vm.RowVersion = o.RowVersion;
        vm.Approvers = await land.ListApproversAsync(ct);
        return vm;
    }

    private async Task<DistributionViewModel> BuildDistributionAsync(long projectId, DistributionViewModel? posted, CancellationToken ct)
    {
        var o = await land.GetOverviewAsync(projectId, ct);
        // A posted (re-displayed) form keeps the version it was based on, so a stale edit can never be saved by simply resubmitting.
        var vm = posted ?? new DistributionViewModel
        {
            Areas = o.Distribution.Where(d => d.Category != LandCategory.Unclassified).ToDictionary(d => d.Category, d => d.Area),
            DistributionVersion = o.DistributionVersion,
        };
        vm.ProjectId = projectId; vm.ProjectName = o.ProjectName; vm.Unit = o.MeasurementUnit; vm.TotalArea = o.TotalProjectArea; vm.Unclassified = o.UnclassifiedArea;
        return vm;
    }

    private static async Task<IReadOnlyList<LandDocumentUpload>?> ToUploadsAsync(IFormFile? file, LandDocumentType type, string title)
    {
        if (file is null || file.Length == 0) return null;
        // Copy to memory so the stream outlives the request body handling inside the service transaction.
        var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        return [new LandDocumentUpload(new FileUpload(file.FileName, file.ContentType, file.Length, ms), type, title)];
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}
