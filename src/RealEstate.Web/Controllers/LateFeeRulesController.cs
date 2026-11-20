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

public class LateFeeRulesController(InstallmentSetupService setup, ProjectService projects) : InstallmentControllerBase
{
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Index(LateFeeRuleListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new LateFeeListViewModel { Filter = filter, Rules = await setup.ListLateFeeRulesAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Create() => View("Form", new LateFeeFormViewModel { Projects = await ProjectsAsync(default) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Create(LateFeeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await setup.CreateLateFeeRuleAsync(ToRequest(m), ct);
                TempData["Success"] = "Late fee rule created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Projects = await ProjectsAsync(ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var r = await setup.GetLateFeeRuleAsync(id, ct);
        return View("Form", new LateFeeFormViewModel
        {
            Id = r.Id, RowVersion = r.RowVersion, Code = r.Code, Name = r.Name, ProjectId = r.ProjectId, Method = r.Method, Value = r.Value, MaxAmount = r.MaxAmount,
            MaxPercentOfInstallment = r.MaxPercentOfInstallment, Description = r.Description, IsActive = r.IsActive, IsInUse = r.IsInUse, Projects = await ProjectsAsync(ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Edit(long id, LateFeeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await setup.UpdateLateFeeRuleAsync(id, ToRequest(m), ct);
                TempData["Success"] = "Late fee rule updated.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        m.Projects = await ProjectsAsync(ct);
        return View("Form", m);
    }

    /// <summary>Late fee calculator (spec 5.9): shows the fee a rule would charge for the entered installment.</summary>
    [HttpGet, HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Calculator(long? ruleId, decimal? installmentAmount, decimal? outstanding, DateOnly? graceEndDate, DateOnly? asOf, CancellationToken ct)
    {
        var vm = new LateFeeCalculatorViewModel
        {
            Rules = (await setup.ListLateFeeRulesAsync(new LateFeeRuleListRequest { PageSize = 100 }, ct)).Items,
            RuleId = ruleId, InstallmentAmount = installmentAmount, Outstanding = outstanding, GraceEndDate = graceEndDate, AsOf = asOf,
        };
        if (ruleId is not null && installmentAmount is not null && graceEndDate is not null && asOf is not null)
        {
            try { vm.Result = await setup.PreviewLateFeeAsync(new LateFeePreviewRequest(ruleId.Value, installmentAmount.Value, outstanding ?? installmentAmount.Value, graceEndDate.Value, asOf.Value), ct); }
            catch (Exception ex) when (Soft(ex)) { vm.Error = Message(ex); }
        }
        return View(vm);
    }

    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectsAsync(CancellationToken ct) => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;

    private static SaveLateFeeRuleRequest ToRequest(LateFeeFormViewModel m) =>
        new(m.Code, m.Name, m.ProjectId, m.Method, m.Value ?? 0m, m.MaxAmount, m.MaxPercentOfInstallment, m.Description, m.IsActive, m.RowVersion);
}
