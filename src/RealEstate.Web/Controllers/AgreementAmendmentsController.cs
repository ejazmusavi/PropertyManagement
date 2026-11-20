using RealEstate.Infrastructure.Identity;
using RealEstate.Application.Common;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Installments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class AgreementAmendmentsController(AgreementAmendmentService amendments, AgreementScheduleService schedules, InstallmentSetupService setup, ProjectService projects) : InstallmentControllerBase
{
    // ----- queue (agreement-wide) -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Index(AmendmentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new AmendmentQueueViewModel { Filter = filter, Amendments = await amendments.ListAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    /// <summary>Pending amendments awaiting a decision.</summary>
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Pending(AmendmentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20; filter.Status = AmendmentStatus.Pending;
        return View("Index", new AmendmentQueueViewModel { Filter = filter, PendingOnly = true, Amendments = await amendments.ListAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    // ----- request -----
    [HttpGet, HasPermission(Permissions.AgreementsAmend)]
    public async Task<IActionResult> Create(long id, AmendmentType? type, CancellationToken ct) =>
        View(await FillAsync(new AmendmentFormViewModel { AgreementId = id, Type = type ?? AmendmentType.Deferment }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsAmend)]
    public async Task<IActionResult> Create(long id, AmendmentFormViewModel m, CancellationToken ct)
    {
        // the nested additional-charge fields are only relevant for that amendment type
        if (m.Type != AmendmentType.AdditionalCharge) foreach (var k in ModelState.Keys.Where(k => k.StartsWith("Charge.")).ToList()) ModelState.Remove(k);
        if (ModelState.IsValid)
        {
            try
            {
                var created = await amendments.CreateAsync(id, m.ToRequest(), ct);
                TempData["Success"] = "Amendment submitted for approval by another user.";
                return RedirectToAction(nameof(Details), new { id, amendmentId = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.AgreementId = id;
        return View(await FillAsync(m, ct));
    }

    // ----- details and decisions -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Details(long id, long amendmentId, CancellationToken ct) =>
        View(new AmendmentDetailsViewModel { Amendment = await amendments.GetAsync(id, amendmentId, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsAmendApprove)]
    public async Task<IActionResult> Approve(long id, long amendmentId, string? comments, string? rowVersion, string? returnTo, CancellationToken ct)
    {
        try { var a = await amendments.ApproveAsync(id, amendmentId, new DecideAmendmentRequest(comments, rowVersion), ct); TempData["Success"] = a.ResultScheduleVersion is { } v ? $"Amendment approved; schedule version {v} is now active." : "Amendment approved."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return Back(returnTo, id, amendmentId);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsAmendApprove)]
    public async Task<IActionResult> Reject(long id, long amendmentId, string? comments, string? rowVersion, string? returnTo, CancellationToken ct)
    {
        try { await amendments.RejectAsync(id, amendmentId, new RejectAmendmentRequest(comments ?? "", rowVersion), ct); TempData["Success"] = "Amendment rejected."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return Back(returnTo, id, amendmentId);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AgreementsAmend)]
    public async Task<IActionResult> Cancel(long id, long amendmentId, string? comments, string? rowVersion, string? returnTo, CancellationToken ct)
    {
        try { await amendments.CancelAsync(id, amendmentId, new DecideAmendmentRequest(comments, rowVersion), ct); TempData["Success"] = "Amendment cancelled."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return Back(returnTo, id, amendmentId);
    }

    // ----- waivers -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Waivers(WaiverStatus? status, long? projectId, int page = 1, CancellationToken ct = default) =>
        View(new WaiverListViewModel { Status = status, ProjectId = projectId, Waivers = await amendments.ListWaiversAsync(status, projectId, page, 20, ct), Projects = await ProjectsAsync(ct) });

    // ----- helpers -----
    private IActionResult Back(string? returnTo, long id, long amendmentId) =>
        returnTo == "pending" ? RedirectToAction(nameof(Pending)) : returnTo == "waivers" ? RedirectToAction(nameof(Waivers)) : RedirectToAction(nameof(Details), new { id, amendmentId });

    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectsAsync(CancellationToken ct) => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;

    private async Task<AmendmentFormViewModel> FillAsync(AmendmentFormViewModel m, CancellationToken ct)
    {
        var a = await schedules.GetAsync(m.AgreementId, ct);
        m.AgreementNumber = a.AgreementNumber; m.CurrencySymbol = a.CurrencySymbol;
        m.Charges = a.Charges.Where(c => c.Status == AgreementChargeStatus.Active).ToList();
        if (a.ScheduleGenerated) m.Lines = (await schedules.GetScheduleAsync(m.AgreementId, null, ct)).Items.Where(i => i.Id is not null).ToList();
        m.ChargeTypes = await setup.ListChargeTypesAsync(true, ct);
        m.LateFeeRules = (await setup.ListLateFeeRulesAsync(new LateFeeRuleListRequest { ProjectId = a.ProjectId, IsActive = true, PageSize = 100 }, ct)).Items;
        m.Charge.ChargeTypes = m.ChargeTypes; m.Charge.LateFeeRules = m.LateFeeRules; m.Charge.CurrencySymbol = a.CurrencySymbol;
        return m;
    }
}
