using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.PricingView)]
public class PricingController(PricingRuleService rules, PricingCalculationService calculator, PropertyPricingService prices, PricingApprovalService approvals,
    PropertySetupService setup, ProjectService projects, ICurrentUser currentUser) : Controller
{
    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectsAsync(CancellationToken ct) => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;

    // ================= rules =================
    public async Task<IActionResult> Rules(PricingRuleListRequest filter, bool desc = false, CancellationToken ct = default)
    {
        filter.PageSize = 20; filter.Descending = desc;
        return View(new RulesListViewModel { Filter = filter, Rules = await rules.ListRulesAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    [HttpGet, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> CreateRule(long? projectId, CancellationToken ct)
    {
        if (projectId is null) return View("ChooseProject", new ChooseProjectModel("Create pricing rule", nameof(CreateRule), await ProjectsAsync(ct)));
        return View("RuleForm", await FillAsync(new RuleFormViewModel { ProjectId = projectId.Value, EffectiveFrom = DateOnly.FromDateTime(DateTime.Today) }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> CreateRule(RuleFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (m.Value is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["Value"] = ["Enter the rate, amount or percentage."] });
                if (m.EffectiveFrom is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["EffectiveFrom"] = ["Choose the start date."] });
                var created = await rules.CreateRuleAsync(new CreatePricingRuleRequest(m.ProjectId, m.Code, m.Name, m.Description, m.Kind, m.Factor, m.Scope, m.BlockId, m.StreetId, m.CategoryId, m.PropertyId,
                    m.CustomerId, m.CustomerReference, m.Method, m.Value.Value, m.Unit, m.Priority, m.EffectiveFrom.Value, m.EffectiveTo, m.ToConditions()), ct);
                TempData["Success"] = created.Status == PricingRuleStatus.PendingApproval ? "Rule saved. Customer-negotiated pricing is active only after approval." : "Pricing rule created.";
                return RedirectToAction(nameof(RuleDetails), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("RuleForm", await FillAsync(m, ct));
    }

    public async Task<IActionResult> RuleDetails(long id, CancellationToken ct) => View(await rules.GetRuleAsync(id, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> UpdateRule(long id, string name, string? description, string? rowVersion, CancellationToken ct)
    {
        try { await rules.UpdateRuleDetailsAsync(id, new UpdateRuleDetailsRequest(name ?? "", description, rowVersion), ct); TempData["Success"] = "Rule saved."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(RuleDetails), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> RetireRule(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await rules.RetireRuleAsync(id, new RetireRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Rule retired. Past calculations are unaffected."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(RuleDetails), new { id });
    }

    // ================= rate cards =================
    public async Task<IActionResult> RateCards(long? projectId, DateOnly? asOf, CancellationToken ct)
    {
        var list = await ProjectsAsync(ct);
        var selected = projectId ?? list.FirstOrDefault()?.Id;
        return View(new RateCardViewModel { ProjectId = selected, AsOf = asOf, Projects = list, Card = selected is { } id ? await rules.GetRateCardAsync(id, asOf, ct) : null });
    }

    // ================= discounts =================
    public async Task<IActionResult> Discounts(DiscountListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new DiscountsListViewModel { Filter = filter, Discounts = await rules.ListDiscountsAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    [HttpGet, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> CreateDiscount(long? projectId, CancellationToken ct)
    {
        if (projectId is null) return View("ChooseProject", new ChooseProjectModel("Create discount", nameof(CreateDiscount), await ProjectsAsync(ct)));
        return View("DiscountForm", await FillAsync(new DiscountFormViewModel { ProjectId = projectId.Value, EffectiveFrom = DateOnly.FromDateTime(DateTime.Today) }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> CreateDiscount(DiscountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (m.Value is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["Value"] = ["Enter the discount value."] });
                if (m.EffectiveFrom is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["EffectiveFrom"] = ["Choose the start date."] });
                var created = await rules.CreateDiscountAsync(new CreateDiscountRuleRequest(m.ProjectId, m.Code, m.Name, m.Description, m.Scope, m.BlockId, m.StreetId, m.CategoryId, m.PropertyId,
                    m.Method, m.Value.Value, m.IsStackable, m.Priority, m.EffectiveFrom.Value, m.EffectiveTo), ct);
                TempData["Success"] = "Discount created.";
                return RedirectToAction(nameof(DiscountDetails), new { id = created.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("DiscountForm", await FillAsync(m, ct));
    }

    public async Task<IActionResult> DiscountDetails(long id, CancellationToken ct) => View(await rules.GetDiscountAsync(id, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> UpdateDiscount(long id, string name, string? description, string? rowVersion, CancellationToken ct)
    {
        try { await rules.UpdateDiscountDetailsAsync(id, new UpdateRuleDetailsRequest(name ?? "", description, rowVersion), ct); TempData["Success"] = "Discount saved."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(DiscountDetails), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> RetireDiscount(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await rules.RetireDiscountAsync(id, new RetireRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Discount retired."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(DiscountDetails), new { id });
    }

    // ================= calculator =================
    public async Task<IActionResult> Calculator(long? projectId, long? propertyId, long? customerId, DateOnly? asOf, CancellationToken ct)
    {
        var vm = new CalculatorViewModel { ProjectId = projectId, PropertyId = propertyId, CustomerId = customerId, AsOf = asOf, Projects = await ProjectsAsync(ct) };
        vm.ProjectId ??= vm.Projects.FirstOrDefault()?.Id;
        if (vm.ProjectId is { } pid) vm.Properties = (await prices.ListPropertyPricesAsync(new PropertyPriceListRequest { ProjectId = pid, PageSize = 100 }, ct)).Items;
        if (propertyId is { } prop)
        {
            try { vm.Result = await calculator.CalculateAsync(new CalculatePriceRequest(prop, customerId, asOf), ct); }
            catch (Exception ex) when (Soft(ex)) { vm.Error = Message(ex); }
        }
        return View(vm);
    }

    // ================= property pricing =================
    public async Task<IActionResult> Properties(PropertyPriceListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new PropertyPricesViewModel { Filter = filter, Prices = await prices.ListPropertyPricesAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    public async Task<IActionResult> Property(long id, long? customerId, DateOnly? asOf, CancellationToken ct) => View(await BuildPropertyAsync(id, customerId, asOf, null, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> Publish(long id, string? reason, CancellationToken ct)
    {
        try { var o = await prices.PublishAsync(id, new PublishPriceRequest(reason), ct); TempData["Success"] = o.Changed ? "Price published." : "The published price is already up to date."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Property), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingManage)]
    public async Task<IActionResult> PublishProject(long projectId, string? reason, CancellationToken ct)
    {
        try
        {
            var r = await prices.PublishProjectAsync(projectId, new PublishPriceRequest(reason), ct);
            TempData["Success"] = $"Published {r.Published}, unchanged {r.Unchanged}, frozen (booked/sold) {r.Frozen}, skipped {r.Skipped.Count}.";
            if (r.Skipped.Count > 0) TempData["Error"] = "Not priced: " + string.Join("; ", r.Skipped.Take(5).Select(s => $"{s.PropertyCode} – {s.Reason}")) + (r.Skipped.Count > 5 ? $" (+{r.Skipped.Count - 5} more)" : "");
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Properties), new { projectId });
    }

    // ================= history =================
    public async Task<IActionResult> History(PriceHistoryRequest filter, CancellationToken ct)
    {
        filter.PageSize = 30;
        return View(new PriceHistoryViewModel { Filter = filter, History = await prices.ListHistoryAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    // ================= approvals =================
    public async Task<IActionResult> Approvals(ApprovalListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new ApprovalsViewModel { Filter = filter, Approvals = await approvals.ListAsync(filter, ct), Projects = await ProjectsAsync(ct) });
    }

    public async Task<IActionResult> ApprovalDetails(long id, CancellationToken ct) =>
        View(new ApprovalDetailsViewModel { Approval = await approvals.GetAsync(id, ct), CurrentUserId = currentUser.UserId ?? Guid.Empty });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingRequest)]
    public async Task<IActionResult> RequestApproval(ApprovalFormViewModel m, CancellationToken ct)
    {
        try
        {
            if (m.Value is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["Value"] = ["Enter the price, amount or percentage."] });
            var a = await approvals.RequestAsync(new RequestPricingApprovalRequest(m.Type, m.PropertyId, m.CustomerId, m.CustomerReference, m.AdjustmentKind, m.Method, m.Value.Value,
                m.EffectiveFrom, m.EffectiveTo, m.Reason ?? ""), ct);
            TempData["Success"] = "Request submitted for approval.";
            return RedirectToAction(nameof(ApprovalDetails), new { id = a.Id });
        }
        catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        return View("Property", await BuildPropertyAsync(m.PropertyId, null, null, m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingApprove)]
    public async Task<IActionResult> Decide(long id, bool approve, string? comment, string? rowVersion, CancellationToken ct)
    {
        try { await approvals.DecideAsync(id, new DecidePricingApprovalRequest(approve, comment, rowVersion), ct); TempData["Success"] = approve ? "Request approved." : "Request rejected."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(ApprovalDetails), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelApproval(long id, string? comment, string? rowVersion, CancellationToken ct)
    {
        try { await approvals.CancelAsync(id, new CancelPricingApprovalRequest(comment, rowVersion), ct); TempData["Success"] = "Request cancelled."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(ApprovalDetails), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PricingApprove)]
    public async Task<IActionResult> RevokeAdjustment(long id, long propertyId, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await prices.RevokeAdjustmentAsync(id, new RetireRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Adjustment revoked."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Property), new { id = propertyId });
    }

    // ================= helpers =================
    private async Task<PropertyPriceViewModel> BuildPropertyAsync(long id, long? customerId, DateOnly? asOf, ApprovalFormViewModel? posted, CancellationToken ct)
    {
        var view = await prices.GetPriceAsync(id, customerId, asOf, ct);
        var form = posted ?? new ApprovalFormViewModel();
        form.PropertyId = id;
        return new PropertyPriceViewModel { View = view, CustomerId = customerId, AsOf = asOf, Today = DateOnly.FromDateTime(DateTime.Today), Request = form };
    }

    private async Task<RuleFormViewModel> FillAsync(RuleFormViewModel m, CancellationToken ct)
    {
        var project = await projects.GetAsync(m.ProjectId, ct);
        m.ProjectName = $"{project.ProjectCode} – {project.ProjectName}";
        m.Types = await setup.ListTypesAsync(true, ct);
        m.Blocks = await setup.ListZonesAsync(ZoneKind.Block, m.ProjectId, false, ct);
        m.Sectors = await setup.ListZonesAsync(ZoneKind.Sector, m.ProjectId, false, ct);
        m.Streets = await setup.ListZonesAsync(ZoneKind.Street, m.ProjectId, false, ct);
        m.Categories = await setup.ListCategoriesAsync(ct);
        m.Properties = (await prices.ListPropertyPricesAsync(new PropertyPriceListRequest { ProjectId = m.ProjectId, PageSize = 100 }, ct)).Items;
        return m;
    }

    private async Task<DiscountFormViewModel> FillAsync(DiscountFormViewModel m, CancellationToken ct)
    {
        var project = await projects.GetAsync(m.ProjectId, ct);
        m.ProjectName = $"{project.ProjectCode} – {project.ProjectName}";
        m.Blocks = await setup.ListZonesAsync(ZoneKind.Block, m.ProjectId, false, ct);
        m.Streets = await setup.ListZonesAsync(ZoneKind.Street, m.ProjectId, false, ct);
        m.Categories = await setup.ListCategoriesAsync(ct);
        m.Properties = (await prices.ListPropertyPricesAsync(new PropertyPriceListRequest { ProjectId = m.ProjectId, PageSize = 100 }, ct)).Items;
        return m;
    }

    private static bool Soft(Exception ex) => ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException;
    private static string Message(Exception ex) => ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
}

public sealed record ChooseProjectModel(string Title, string Action, IReadOnlyList<ProjectListItemDto> Projects);
