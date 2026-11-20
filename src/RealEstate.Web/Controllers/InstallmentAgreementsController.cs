using RealEstate.Infrastructure.Identity;
using RealEstate.Application.Common;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Installments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class InstallmentAgreementsController(AgreementScheduleService schedules, InstallmentPlanService plans, InstallmentSetupService setup, ProjectService projects) : InstallmentControllerBase
{
    // ----- list and details -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Index(InstallmentAgreementListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new AgreementListViewModel { Filter = filter, Agreements = await schedules.ListAsync(filter, ct), Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items });
    }

    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var a = await schedules.GetAsync(id, ct);
        return View(new AgreementDetailsViewModel { Agreement = a, Schedule = a.ScheduleGenerated ? await schedules.GetScheduleAsync(id, null, ct) : null });
    }

    // ----- schedule builder -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Builder(long id, CancellationToken ct)
    {
        var a = await schedules.GetAsync(id, ct);
        return View(new BuilderViewModel
        {
            Agreement = a, Preview = await schedules.PreviewAsync(id, ct),
            Plans = a.CanEditCharges ? await plans.ApplicablePlansAsync(id, ct) : [],
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> ApplyPlan(long id, long planId, DateOnly? startDate, string? rowVersion, CancellationToken ct)
    {
        try { await schedules.ApplyPlanAsync(id, new ApplyPlanRequest(planId, startDate, rowVersion), ct); TempData["Success"] = "Plan applied. Review the charges and the preview, then generate the schedule."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Builder), new { id });
    }

    [HttpGet, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> AddCharge(long id, CancellationToken ct)
    {
        var a = await schedules.GetAsync(id, ct);
        return View("ChargeForm", await FillAsync(new AgreementChargeFormViewModel { AgreementId = id, AgreementNumber = a.AgreementNumber, CurrencySymbol = a.CurrencySymbol, RowVersion = a.RowVersion,
            FirstDueDate = a.ScheduleStartDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)) }, a.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> AddCharge(long id, AgreementChargeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await schedules.AddChargeAsync(id, new SaveAgreementChargeRequest(m.ToInput(), m.RowVersion), ct);
                TempData["Success"] = "Charge added.";
                return RedirectToAction(nameof(Builder), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var a = await schedules.GetAsync(id, ct);
        m.AgreementId = id; m.AgreementNumber = a.AgreementNumber; m.CurrencySymbol = a.CurrencySymbol;
        return View("ChargeForm", await FillAsync(m, a.ProjectId, ct));
    }

    [HttpGet, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> EditCharge(long id, long chargeId, CancellationToken ct)
    {
        var a = await schedules.GetAsync(id, ct);
        var c = a.Charges.FirstOrDefault(x => x.Id == chargeId) ?? throw new NotFoundException("Charge not found.");
        return View("ChargeForm", await FillAsync(AgreementChargeFormViewModel.From(id, a.AgreementNumber, c, a.RowVersion, a.CurrencySymbol), a.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> EditCharge(long id, long chargeId, AgreementChargeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await schedules.UpdateChargeAsync(id, chargeId, new SaveAgreementChargeRequest(m.ToInput(), m.RowVersion), ct);
                TempData["Success"] = "Charge updated.";
                return RedirectToAction(nameof(Builder), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var a = await schedules.GetAsync(id, ct);
        m.AgreementId = id; m.ChargeId = chargeId; m.AgreementNumber = a.AgreementNumber; m.CurrencySymbol = a.CurrencySymbol;
        return View("ChargeForm", await FillAsync(m, a.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> RemoveCharge(long id, long chargeId, string? rowVersion, CancellationToken ct)
    {
        try { await schedules.RemoveChargeAsync(id, chargeId, new AgreementRowVersionRequest(rowVersion), ct); TempData["Success"] = "Charge removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Builder), new { id });
    }

    // ----- preview and generation -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Preview(long id, CancellationToken ct) =>
        View(new PreviewViewModel { Agreement = await schedules.GetAsync(id, ct), Preview = await schedules.PreviewAsync(id, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<IActionResult> Generate(long id, string? rowVersion, string? remarks, CancellationToken ct)
    {
        try
        {
            var s = await schedules.GenerateAsync(id, new GenerateScheduleRequest(rowVersion, remarks), ct);
            TempData["Success"] = $"Schedule version {s.VersionNo} generated with {s.ItemCount} installments.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Preview), new { id });
    }

    // ----- history -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> History(long id, int? version, CancellationToken ct)
    {
        var h = await schedules.GetHistoryAsync(id, ct);
        var s = h.Schedules.Count == 0 ? null : await schedules.GetScheduleAsync(id, version, ct);
        return View(new HistoryViewModel { History = h, Schedule = s, CurrencySymbol = s?.CurrencySymbol ?? (await schedules.GetAsync(id, ct)).CurrencySymbol });
    }

    private async Task<AgreementChargeFormViewModel> FillAsync(AgreementChargeFormViewModel m, long projectId, CancellationToken ct)
    {
        m.ChargeTypes = await setup.ListChargeTypesAsync(true, ct);
        m.LateFeeRules = (await setup.ListLateFeeRulesAsync(new LateFeeRuleListRequest { ProjectId = projectId, IsActive = true, PageSize = 100 }, ct)).Items;
        return m;
    }
}
