using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Gateway;
using RealEstate.Application.Portal;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Office-side management of the customer application: customer logins, announcements, notifications and payment instructions.</summary>
public class CustomerAppController(CustomerAccountService accounts, CustomerService customers, PortalContentService content, ProjectService projects) : InstallmentControllerBase
{
    // ----- customer login -----
    [HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> Account(long id, CancellationToken ct) => View(await BuildAccountAsync(id, new PortalAccountFormViewModel(), ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> CreateAccount(long id, PortalAccountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await accounts.CreateAsync(id, new CreateCustomerAccountRequest(m.Email ?? "", m.Password ?? ""), ct);
                TempData["Success"] = "The customer application login was created. Hand the password to the customer; they can change it in the app.";
                return RedirectToAction(nameof(Account), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Password = null;
        return View(nameof(Account), await BuildAccountAsync(id, m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> DisableAccount(long id, string? reason, string? rowVersion, CancellationToken ct)
    {
        try { await accounts.DisableAsync(id, new DisableCustomerAccountRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "The login was disabled."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Account), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> EnableAccount(long id, string? rowVersion, CancellationToken ct)
    {
        try { await accounts.EnableAsync(id, rowVersion, ct); TempData["Success"] = "The login was enabled."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Account), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> ResetAccountPassword(long id, string? newPassword, CancellationToken ct)
    {
        try { await accounts.ResetPasswordAsync(id, new ResetCustomerPasswordRequest(newPassword ?? ""), ct); TempData["Success"] = "The password was changed and the customer's sessions were ended."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Account), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> SendNotification(long id, string? title, string? message, CancellationToken ct)
    {
        try { await content.SendNotificationAsync(new CreateNotificationRequest(id, title ?? "", message ?? ""), ct); TempData["Success"] = "The notification was sent."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Account), new { id });
    }

    private async Task<PortalAccountPageViewModel> BuildAccountAsync(long id, PortalAccountFormViewModel form, CancellationToken ct)
    {
        var customer = await customers.GetAsync(id, ct);
        return new PortalAccountPageViewModel { Customer = customer, Account = await accounts.GetAsync(id, ct), Form = form };
    }

    // ----- announcements -----
    [HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> Announcements(CancellationToken ct) => View(await content.ListAnnouncementsAsync(ct));

    [HttpGet, HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> AnnouncementForm(long? id, CancellationToken ct)
    {
        var m = new AnnouncementFormViewModel { Projects = await ProjectListAsync(ct), IsActive = true };
        if (id is { } i)
        {
            var a = await content.GetAnnouncementAsync(i, ct);
            m.Id = a.Id; m.Title = a.Title; m.Body = a.Body; m.ProjectId = a.ProjectId; m.PublishFrom = a.PublishFromUtc; m.ExpiresAt = a.ExpiresAtUtc; m.IsActive = a.IsActive; m.RowVersion = a.RowVersion;
        }
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> AnnouncementForm(AnnouncementFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await content.SaveAnnouncementAsync(m.Id, new SaveAnnouncementRequest(m.Title ?? "", m.Body ?? "", m.ProjectId, m.PublishFrom, m.ExpiresAt, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "The announcement was saved.";
                return RedirectToAction(nameof(Announcements));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Projects = await ProjectListAsync(ct);
        return View(m);
    }

    // ----- payment instructions -----
    [HttpGet, HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> Instructions(CancellationToken ct) => View(new InstructionsViewModel { Note = (await content.GetInstructionsAsync(ct)).Note });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> Instructions(InstructionsViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try { await content.SaveInstructionsAsync(new PaymentInstructionsNoteDto(m.Note), ct); TempData["Success"] = "The payment instructions were saved."; return RedirectToAction(nameof(Instructions)); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(m);
    }

    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectListAsync(CancellationToken ct) =>
        (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;
}

/// <summary>Support desk for requests raised by customers in the application.</summary>
public class SupportController(SupportDeskService desk) : InstallmentControllerBase
{
    [HasPermission(Permissions.SupportView)]
    public async Task<IActionResult> Index(SupportListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new SupportListViewModel { Filter = filter, Result = await desk.ListAsync(filter, ct) });
    }

    [HasPermission(Permissions.SupportView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(new SupportDetailsViewModel { Detail = await desk.GetAsync(id, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.SupportManage)]
    public async Task<IActionResult> Reply(long id, string? message, SupportRequestStatus? status, string? rowVersion, CancellationToken ct)
    {
        try { await desk.ReplyAsync(id, new SupportStaffReplyRequest(message ?? "", status, rowVersion), ct); TempData["Success"] = "The reply was sent to the customer."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.SupportManage)]
    public async Task<IActionResult> SetStatus(long id, SupportRequestStatus status, string? rowVersion, CancellationToken ct)
    {
        try { await desk.SetStatusAsync(id, new SupportStatusRequest(status, rowVersion), ct); TempData["Success"] = $"The request is now {status}."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Online payment (gateway) transactions: monitoring, reconciliation, refunds and the callback log.</summary>
public class GatewayController(GatewayQueryService query, GatewayReconciliationService reconciliation, GatewayRefundService refunds) : InstallmentControllerBase
{
    [HasPermission(Permissions.GatewayView)]
    public async Task<IActionResult> Index(GatewayListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new GatewayListViewModel { Filter = filter, Result = await query.ListAsync(filter, ct) });
    }

    [HasPermission(Permissions.GatewayView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(await query.GetAsync(id, ct));

    [HasPermission(Permissions.GatewayView)]
    public async Task<IActionResult> Webhooks(WebhookLogListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new WebhookListViewModel { Filter = filter, Result = await query.WebhooksAsync(filter, ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GatewayManage)]
    public async Task<IActionResult> Reconcile(CancellationToken ct)
    {
        try
        {
            var r = await reconciliation.ReconcileAsync(ct);
            TempData["Success"] = $"Reconciliation checked {r.Checked} open transaction(s); {r.Changed} changed.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GatewayManage)]
    public async Task<IActionResult> ReconcileOne(long id, CancellationToken ct)
    {
        try { var r = await reconciliation.ReconcileOneAsync(id, ct); TempData["Success"] = r.Note; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GatewayManage)]
    public async Task<IActionResult> Refund(long id, decimal amount, string? reason, string? rowVersion, CancellationToken ct)
    {
        try { await refunds.RefundAsync(id, new GatewayRefundRequestDto(amount, reason ?? "", rowVersion), ct); TempData["Success"] = "The refund was accepted by the provider."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Authorized download of the proof files customers attached to a payment.</summary>
public class PaymentProofsController(PaymentProofAdminService proofs) : Controller
{
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Download(long paymentId, long documentId, CancellationToken ct)
    {
        var f = await proofs.DownloadAsync(paymentId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }
}
