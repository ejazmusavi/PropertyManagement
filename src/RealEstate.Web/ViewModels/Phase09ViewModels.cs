using RealEstate.Application.Accounting;
using RealEstate.Application.Reporting;

namespace RealEstate.Web.ViewModels;

public class ReportCatalogViewModel
{
    public IReadOnlyList<ReportDefinitionDto> Reports { get; set; } = [];
}

public class ReportRunViewModel
{
    public ReportDefinitionDto Definition { get; set; } = default!;
    public ReportFilters Filters { get; set; } = new();
    public string? CustomerLabel { get; set; }
    public ReportLookupsDto Lookups { get; set; } = new([], [], []);
    public IReadOnlyList<OptionDto> Accounts { get; set; } = [];
    public IReadOnlyList<OptionDto> CostCenters { get; set; } = [];
    public ReportTableDto? Report { get; set; }
    public string? Message { get; set; }
    public bool CanExport { get; set; }
}

public class DashboardViewModel<T> where T : class
{
    public T Data { get; set; } = default!;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}
