using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class BookingsController(BookingService bookings, PropertyService properties, ProjectService projects, CustomerService customers) : Controller
{
    // ----- list / approvals -----
    [HasPermission(Permissions.BookingsView)]
    public async Task<IActionResult> Index(BookingListRequest filter, bool desc = false, CancellationToken ct = default)
    {
        filter.PageSize = 20;
        filter.Descending = desc;
        return View(new BookingListViewModel
        {
            Filter = filter, Descending = desc, Bookings = await bookings.ListAsync(filter, ct),
            Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items,
        });
    }

    [HasPermission(Permissions.BookingsApprove)]
    public async Task<IActionResult> Approvals(int page = 1, CancellationToken ct = default)
    {
        var list = await bookings.ListAsync(new BookingListRequest { Status = BookingStatus.PendingApproval, Page = page, PageSize = 20, SortBy = "date", Descending = true }, ct);
        return View(new BookingApprovalsViewModel { Bookings = list, CurrentUserName = (await bookings.GetSettingsAsync(ct)).UserName });
    }

    [HasPermission(Permissions.BookingsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var s = await bookings.GetSettingsAsync(ct);
        return View(new BookingDetailsViewModel { Booking = await bookings.GetAsync(id, ct), CurrentUserId = s.UserId, CurrentUserName = s.UserName });
    }

    // ----- wizard -----
    [HttpGet, HasPermission(Permissions.BookingsCreate)]
    public async Task<IActionResult> Create(long? projectId, long? propertyId, string? q, CancellationToken ct)
    {
        if (projectId is null && propertyId is { } pid0) projectId = (await properties.GetAsync(pid0, ct)).ProjectId;
        if (projectId is null) return View("ChooseProject", (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items);
        var m = new BookingWizardViewModel { ProjectId = projectId.Value, Q = q };
        if (propertyId is { } pid) m.Lines.Add(new WizardLine { PropertyId = pid });
        return View(await FillAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsCreate)]
    public async Task<IActionResult> Create(BookingWizardViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var created = await bookings.CreateAsync(m.ToRequest(), ct);
                TempData["Success"] = created.Status == BookingStatus.PendingApproval ? $"Booking {created.BookingNumber} submitted for approval." : $"Booking {created.BookingNumber} created as a draft.";
                return RedirectToAction(nameof(Details), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await FillAsync(m, ct));
    }

    [HasPermission(Permissions.BookingsCreate)]
    public async Task<IActionResult> Quote(long propertyId, long? customerId, CancellationToken ct)
    {
        try { return Json(await bookings.QuoteAsync(propertyId, customerId, ct)); }
        catch (Exception ex) when (ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException) { return BadRequest(new { error = Message(ex) }); }
    }

    /// <summary>Typeahead for customer pickers (booking wizard, transfer, relationships).</summary>
    public async Task<IActionResult> CustomerLookup(string? term, bool activeOnly = true, CancellationToken ct = default) => Json(await customers.LookupAsync(term, activeOnly, ct));

    // ----- workflow -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsCreate)]
    public async Task<IActionResult> Submit(long id, string? rowVersion, CancellationToken ct)
    {
        try { await bookings.SubmitAsync(id, new SubmitBookingRequest(rowVersion), ct); TempData["Success"] = "Booking submitted for approval."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsApprove)]
    public async Task<IActionResult> Approve(long id, string? comments, string? rowVersion, string? returnTo, CancellationToken ct)
    {
        try { var b = await bookings.ApproveAsync(id, new ApproveBookingRequest(comments, rowVersion), ct); TempData["Success"] = $"Booking {b.BookingNumber} confirmed; agreement {b.AgreementNumber} generated."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return returnTo == "approvals" ? RedirectToAction(nameof(Approvals)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsApprove)]
    public async Task<IActionResult> Reject(long id, string comments, string? rowVersion, string? returnTo, CancellationToken ct)
    {
        try { await bookings.RejectAsync(id, new RejectBookingRequest(comments ?? "", rowVersion), ct); TempData["Success"] = "Booking rejected; the property was released."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return returnTo == "approvals" ? RedirectToAction(nameof(Approvals)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsApprove)]
    public async Task<IActionResult> DecideRefund(long id, long refundId, bool approve, string? comments, string? rowVersion, CancellationToken ct)
    {
        try { await bookings.DecideRefundAsync(id, refundId, new DecideRefundRequest(approve, comments, rowVersion), ct); TempData["Success"] = approve ? "Refund approved." : "Refund rejected."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- cancellation -----
    [HttpGet, HasPermission(Permissions.BookingsCancel)]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var b = await bookings.GetAsync(id, ct);
        return View(new CancelBookingViewModel { Booking = b, RowVersion = b.RowVersion, CanRefund = (await bookings.GetSettingsAsync(ct)).CanRefund });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsCancel)]
    public async Task<IActionResult> Cancel(long id, CancelBookingViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await bookings.CancelAsync(id, new CancelBookingRequest(m.Reason, m.RequestRefund ? m.RefundAmount : null, m.RefundReason, m.RowVersion), ct);
                TempData["Success"] = "Booking cancelled and the property released.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Booking = await bookings.GetAsync(id, ct);
        m.CanRefund = (await bookings.GetSettingsAsync(ct)).CanRefund;
        return View(m);
    }

    // ----- transfer / ownership change -----
    [HttpGet, HasPermission(Permissions.BookingsTransfer)]
    public async Task<IActionResult> Transfer(long id, CancellationToken ct)
    {
        var b = await bookings.GetAsync(id, ct);
        return View(new TransferBookingViewModel
        {
            Booking = b, RowVersion = b.RowVersion,
            Owners = b.Customers.Where(c => c.IsActive).Select(c => new WizardOwner { CustomerId = c.CustomerId, SharePercent = c.SharePercent, CustomerLabel = $"{c.CustomerCode} – {c.FullName}" }).ToList(),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.BookingsTransfer)]
    public async Task<IActionResult> Transfer(long id, TransferBookingViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var b = await bookings.TransferAsync(id, new TransferBookingRequest(m.Reason, m.Owners.Select(o => new OwnerInput(o.CustomerId, o.SharePercent)).ToList(), m.RowVersion), ct);
                TempData["Success"] = $"Ownership updated; agreement version {b.AgreementVersion} generated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Booking = await bookings.GetAsync(id, ct);
        return View(m);
    }

    // ----- agreement -----
    [HasPermission(Permissions.BookingsView)]
    public async Task<IActionResult> Agreement(long id, int? version, CancellationToken ct) =>
        View(new AgreementViewModel { Agreement = await bookings.GetAgreementAsync(id, version, ct), Booking = await bookings.GetAsync(id, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsDocuments)]
    public async Task<IActionResult> UploadAgreementDocument(long id, IFormFile? file, AgreementDocumentType type, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Agreement), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await bookings.UploadAgreementDocumentAsync(id, type, title, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Agreement), new { id });
    }

    [HasPermission(Permissions.BookingsView)]
    public async Task<IActionResult> DownloadAgreementDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await bookings.DownloadAgreementDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsDocuments)]
    public async Task<IActionResult> DeleteAgreementDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await bookings.DeleteAgreementDocumentAsync(id, documentId, reason ?? "", ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Agreement), new { id });
    }

    // ----- helpers -----
    private async Task<BookingWizardViewModel> FillAsync(BookingWizardViewModel m, CancellationToken ct)
    {
        var project = await projects.GetAsync(m.ProjectId, ct);
        m.ProjectName = $"{project.ProjectCode} – {project.ProjectName}";
        var list = (await properties.ListAvailableAsync(new PropertyListRequest { ProjectId = m.ProjectId, Search = m.Q, PageSize = 100 }, ct)).Items.ToList();
        foreach (var line in m.Lines.Where(l => list.All(p => p.Id != l.PropertyId)))
        {
            var p = await properties.GetAsync(line.PropertyId, ct);
            if (p.ProjectId == m.ProjectId && p.Status is PropertyStatus.Available or PropertyStatus.Reserved)
                list.Insert(0, new PropertyListItemDto(p.Id, p.ProjectId, p.ProjectCode, p.PropertyCode, p.PropertyNumber, p.Block, p.Sector, p.Street, p.TypeName, p.CategoryName, p.Size, p.Unit, p.Status,
                    p.DevelopmentStatus, p.IsCorner, p.FacesMainRoad, p.FacesPark, p.FloorNumber));
        }
        m.Available = list;
        var s = await bookings.GetSettingsAsync(ct);
        m.CanDiscount = s.CanDiscount; m.CanAuthorizeBalance = s.CanAuthorizeBalance; m.CurrencySymbol = s.CurrencySymbol; m.DecimalPlaces = s.DecimalPlaces;
        return m;
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}
