using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Accounting;
using RealEstate.Application.Common;
using RealEstate.Application.Reporting;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Report catalog, filtered report screens and PDF/Excel exports (Phase 9). Thin: filters in, report table out; permissions and scope live in the services.</summary>
public class ReportsController(ReportingService reports, ChartOfAccountsService chart) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(new ReportCatalogViewModel { Reports = await reports.ListAsync(ct) });

    public async Task<IActionResult> Run(string key, ReportFilters filters, string? customerLabel, bool show = false, CancellationToken ct = default)
    {
        var def = await reports.GetDefinitionAsync(key, ct);
        var vm = await BuildAsync(def, filters, customerLabel, ct);
        // The first visit shows the filter form only (a customer or project may be required); "Show" or any filter runs the report.
        var run = show || Request.Query.Keys.Any(k => !string.Equals(k, "key", StringComparison.OrdinalIgnoreCase));
        if (run)
        {
            try
            {
                vm.Report = await reports.RunAsync(key, filters, ct);
                if (vm.Report.Filters.FirstOrDefault(f => f.Label == "Customer") is { } c && string.IsNullOrEmpty(vm.CustomerLabel)) vm.CustomerLabel = c.Value;
            }
            catch (Exception ex) when (Soft(ex)) { vm.Message = Message(ex); }
        }
        return View(vm);
    }

    public async Task<IActionResult> Export(string key, ReportFilters filters, string format = "pdf", CancellationToken ct = default)
    {
        var f = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Excel : ExportFormat.Pdf;
        try
        {
            var file = await reports.ExportAsync(key, filters, f, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (Exception ex) when (Soft(ex))
        {
            TempData["Error"] = Message(ex);
            return RedirectToAction(nameof(Run), new { key, filters.From, filters.To, filters.ProjectId, filters.BlockId, filters.CustomerId, filters.TransactionType, filters.PaymentMethodId, filters.Status, filters.AccountId, filters.CostCenterId });
        }
    }

    private async Task<ReportRunViewModel> BuildAsync(ReportDefinitionDto def, ReportFilters filters, string? customerLabel, CancellationToken ct)
    {
        var vm = new ReportRunViewModel { Definition = def, Filters = filters, CustomerLabel = customerLabel, Lookups = await reports.LookupsAsync(ct) };
        if (def.Uses(ReportFilterNames.Account)) vm.Accounts = await OrAsync(() => chart.PostableOptionsAsync(null, ct), []);
        if (def.Uses(ReportFilterNames.CostCenter)) vm.CostCenters = await OrAsync(async () => (await chart.ListCostCentersAsync(true, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList() as IReadOnlyList<OptionDto>, []);
        vm.CanExport = await reports.CanExportAsync(ct);
        return vm;
    }
}
