using RealEstate.Infrastructure.Identity;
using RealEstate.Application.Common;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class InstallmentPlansController(InstallmentPlanService plans, InstallmentSetupService setup, ProjectService projects, PropertyService properties, PropertySetupService propertySetup) : InstallmentControllerBase
{
    // ----- list -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Index(InstallmentPlanListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new PlanListViewModel { Filter = filter, Plans = await plans.ListAsync(filter, ct), Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items });
    }

    // ----- create / edit header -----
    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Create(long? projectId, CancellationToken ct)
    {
        if (projectId is null) return View("ChooseProject", (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items);
        return View("Form", await FillAsync(new PlanFormViewModel { ProjectId = projectId.Value }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Create(PlanFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var p = await plans.CreateAsync(new CreateInstallmentPlanRequest(m.ProjectId, m.Code, m.Name, m.Description, m.PlanType,
                    m.PlanType == InstallmentPlanType.PropertyCategory ? m.PropertyCategoryId : null,
                    m.PlanType is InstallmentPlanType.PropertySpecific or InstallmentPlanType.CustomerSpecific ? m.PropertyId : null,
                    m.PlanType == InstallmentPlanType.CustomerSpecific ? m.CustomerId : null, null), ct);
                TempData["Success"] = $"Plan {p.Code} created as a draft. Add its components, then activate it.";
                return RedirectToAction(nameof(Details), new { id = p.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", await FillAsync(m, ct));
    }

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        return View("Form", await FillAsync(new PlanFormViewModel
        {
            Id = p.Id, RowVersion = p.RowVersion, ProjectId = p.ProjectId, Code = p.Code, Name = p.Name, Description = p.Description, PlanType = p.PlanType,
            PropertyCategoryId = p.PropertyCategoryId, PropertyId = p.PropertyId, CustomerId = p.CustomerId, CustomerLabel = p.CustomerName,
        }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Edit(long id, PlanFormViewModel m, CancellationToken ct)
    {
        ModelState.Remove(nameof(m.Code));
        if (ModelState.IsValid)
        {
            try
            {
                await plans.UpdateAsync(id, new UpdateInstallmentPlanRequest(m.Name, m.Description,
                    m.PlanType == InstallmentPlanType.PropertyCategory ? m.PropertyCategoryId : null,
                    m.PlanType is InstallmentPlanType.PropertySpecific or InstallmentPlanType.CustomerSpecific ? m.PropertyId : null,
                    m.PlanType == InstallmentPlanType.CustomerSpecific ? m.CustomerId : null, m.RowVersion), ct);
                TempData["Success"] = "Plan updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("Form", await FillAsync(m, ct));
    }

    // ----- details, preview, lifecycle -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> Details(long id, decimal? price, decimal? bookingAmount, DateOnly? startDate, bool preview = false, CancellationToken ct = default)
    {
        var vm = new PlanDetailsViewModel { Plan = await plans.GetAsync(id, ct), PreviewStartDate = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)) };
        if (price is not null) vm.PreviewPrice = price.Value;
        if (bookingAmount is not null) vm.PreviewBookingAmount = bookingAmount.Value;
        if (preview)
        {
            vm.Previewed = true;
            try { vm.Preview = await plans.PreviewAsync(id, new PlanPreviewRequest(vm.PreviewPrice, vm.PreviewBookingAmount, vm.PreviewStartDate), ct); }
            catch (Exception ex) when (Soft(ex)) { vm.PreviewError = Message(ex); }
        }
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Activate(long id, string? rowVersion, CancellationToken ct)
    {
        try { var p = await plans.ActivateAsync(id, new PlanRowVersionRequest(rowVersion), ct); TempData["Success"] = $"Plan {p.Code} activated."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Retire(long id, string? reason, string? rowVersion, CancellationToken ct)
    {
        try { var p = await plans.RetireAsync(id, new RetirePlanRequest(reason ?? "", rowVersion), ct); TempData["Success"] = $"Plan {p.Code} retired."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> Copy(long id, string? code, string? name, CancellationToken ct)
    {
        try
        {
            var p = await plans.CopyAsync(id, new CopyPlanRequest(code ?? "", name ?? ""), ct);
            TempData["Success"] = $"Copied to draft plan {p.Code}.";
            return RedirectToAction(nameof(Details), new { id = p.Id });
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- components -----
    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> AddComponent(long id, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        return View("ComponentForm", await FillComponentAsync(new PlanComponentFormViewModel { PlanId = id, PlanName = p.Name, RowVersion = p.RowVersion, GracePeriodDays = 0 }, p.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> AddComponent(long id, PlanComponentFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await plans.AddComponentAsync(id, new PlanComponentRequest(m.ToInput(), m.RowVersion), ct);
                TempData["Success"] = "Component added.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var p = await plans.GetAsync(id, ct);
        m.PlanId = id; m.PlanName = p.Name;
        return View("ComponentForm", await FillComponentAsync(m, p.ProjectId, ct));
    }

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> EditComponent(long id, long componentId, CancellationToken ct)
    {
        var p = await plans.GetAsync(id, ct);
        var c = p.Components.FirstOrDefault(x => x.Id == componentId) ?? throw new NotFoundException("Component not found.");
        return View("ComponentForm", await FillComponentAsync(PlanComponentFormViewModel.From(id, p.Name, c, p.RowVersion), p.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> EditComponent(long id, long componentId, PlanComponentFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await plans.UpdateComponentAsync(id, componentId, new PlanComponentRequest(m.ToInput(), m.RowVersion), ct);
                TempData["Success"] = "Component updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var p = await plans.GetAsync(id, ct);
        m.PlanId = id; m.PlanName = p.Name; m.ComponentId = componentId;
        return View("ComponentForm", await FillComponentAsync(m, p.ProjectId, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> RemoveComponent(long id, long componentId, string? rowVersion, CancellationToken ct)
    {
        try { await plans.RemoveComponentAsync(id, componentId, new PlanRowVersionRequest(rowVersion), ct); TempData["Success"] = "Component removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- charge types -----
    [HasPermission(Permissions.InstallmentsView)]
    public async Task<IActionResult> ChargeTypes(CancellationToken ct) => View(await setup.ListChargeTypesAsync(false, ct));

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public IActionResult CreateChargeType() => View("ChargeTypeForm", new ChargeTypeFormViewModel());

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> CreateChargeType(ChargeTypeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await setup.CreateChargeTypeAsync(new SaveChargeTypeRequest(m.Code, m.Name, m.Kind, m.DefaultCategory, m.DefaultFrequency, m.Description, m.IsActive), ct);
                TempData["Success"] = "Charge type created.";
                return RedirectToAction(nameof(ChargeTypes));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("ChargeTypeForm", m);
    }

    [HttpGet, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> EditChargeType(long id, CancellationToken ct)
    {
        var t = (await setup.ListChargeTypesAsync(false, ct)).FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Charge type not found.");
        return View("ChargeTypeForm", new ChargeTypeFormViewModel { Id = t.Id, IsSystem = t.IsSystem, Code = t.Code, Name = t.Name, Kind = t.Kind, DefaultCategory = t.DefaultCategory, DefaultFrequency = t.DefaultFrequency, Description = t.Description, IsActive = t.IsActive });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.InstallmentsManage)]
    public async Task<IActionResult> EditChargeType(long id, ChargeTypeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await setup.UpdateChargeTypeAsync(id, new SaveChargeTypeRequest(m.Code, m.Name, m.Kind, m.DefaultCategory, m.DefaultFrequency, m.Description, m.IsActive), ct);
                TempData["Success"] = "Charge type updated.";
                return RedirectToAction(nameof(ChargeTypes));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("ChargeTypeForm", m);
    }

    // ----- helpers -----
    private async Task<PlanFormViewModel> FillAsync(PlanFormViewModel m, CancellationToken ct)
    {
        var project = await projects.GetAsync(m.ProjectId, ct);
        m.ProjectName = $"{project.ProjectCode} – {project.ProjectName}";
        m.Categories = await OrAsync<IReadOnlyList<CategoryDto>>(async () => (await propertySetup.ListCategoriesAsync(ct)).Where(c => c.IsActive).ToList(), []);
        m.Properties = await OrAsync<IReadOnlyList<PropertyListItemDto>>(async () => (await properties.ListAsync(new PropertyListRequest { ProjectId = m.ProjectId, PageSize = 100 }, ct)).Items, []);
        return m;
    }

    private async Task<PlanComponentFormViewModel> FillComponentAsync(PlanComponentFormViewModel m, long projectId, CancellationToken ct)
    {
        m.ChargeTypes = await setup.ListChargeTypesAsync(true, ct);
        m.LateFeeRules = (await setup.ListLateFeeRulesAsync(new LateFeeRuleListRequest { ProjectId = projectId, IsActive = true, PageSize = 100 }, ct)).Items;
        return m;
    }
}
