namespace RealEstate.Application.Reporting;

public enum ReportColumnKind { Text = 1, Money = 2, Number = 3, Date = 4, Percent = 5 }

public sealed record ReportColumnDto(string Key, string Header, ReportColumnKind Kind);

/// <summary>
/// The filters every report understands (spec 9.6). Each report declares which of them it applies (see <see cref="ReportDefinitionDto.Filters"/>); the others are ignored
/// and the screen does not offer them. TransactionType and Status are report-specific vocabularies listed in the definition.
/// </summary>
public sealed class ReportFilters
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public long? ProjectId { get; set; }
    public long? BlockId { get; set; }
    public long? CustomerId { get; set; }
    public string? TransactionType { get; set; }
    public long? PaymentMethodId { get; set; }
    public string? Status { get; set; }
    /// <summary>General ledger report only.</summary>
    public long? AccountId { get; set; }
    public long? CostCenterId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record ReportFilterEchoDto(string Label, string Value);

/// <summary>
/// One generated report. Cells are string, decimal, int, long, DateOnly or null, matching the column kinds. <see cref="Totals"/> covers every row of the report, not just
/// the page. Exports contain all rows (up to <see cref="ReportLimits.MaxExportRows"/>).
/// </summary>
public sealed record ReportTableDto(string Key, string Title, string Group, IReadOnlyList<ReportColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyList<object?>? Totals, int TotalRows, int Page, int PageSize, IReadOnlyList<ReportFilterEchoDto> Filters, IReadOnlyList<string> Notes,
    DateTime GeneratedAtUtc, string GeneratedBy, string CurrencySymbol, bool Truncated)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
}

public static class ReportLimits
{
    public const int MaxExportRows = 20000;
    public const int MaxPageSize = 200;
}

public sealed record ReportOptionDto(string Value, string Label);

public sealed record ReportDefinitionDto(string Key, string Title, string Group, string Description, IReadOnlyList<string> Filters, bool RequiresCustomer, bool RequiresProject,
    bool IsFinancial, IReadOnlyList<ReportOptionDto> TransactionTypes, IReadOnlyList<ReportOptionDto> Statuses)
{
    public bool Uses(string filter) => Filters.Contains(filter, StringComparer.OrdinalIgnoreCase);
}

public static class ReportFilterNames
{
    public const string From = "From", To = "To", Project = "Project", Block = "Block", Customer = "Customer", TransactionType = "TransactionType",
        PaymentMethod = "PaymentMethod", Status = "Status", Account = "Account", CostCenter = "CostCenter";
}

public enum ExportFormat { Pdf = 1, Excel = 2 }

public sealed record ReportExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>Renders a report table as a document. Implemented in Infrastructure without third-party libraries.</summary>
public interface IReportExporter
{
    byte[] ToPdf(ReportTableDto report);
    byte[] ToExcel(ReportTableDto report);
}

// ---------------------------------------------------------------------------------------------------------
// dashboards
// ---------------------------------------------------------------------------------------------------------
public sealed record DashboardFilters(DateOnly? From, DateOnly? To);

public sealed record DashboardCheckDto(string Name, string LeftLabel, decimal Left, string RightLabel, decimal Right, decimal Difference, bool Reconciled, string Note);

public sealed record InventoryStatusCountDto(string Status, int Count);

public sealed record CompanyDashboardDto(
    DateOnly? From, DateOnly To, string CurrencySymbol,
    int TotalProjects, int ActiveProjects,
    decimal TotalLandAreaSqFt, decimal TotalSaleableAreaSqFt,
    int TotalInventory, int AvailableProperties, int ReservedProperties, int BookedProperties, int SoldProperties,
    int BookingsCount, decimal TotalBookingValue, decimal TotalCollections, decimal CustomerReceivables, decimal OverdueAmount,
    decimal ApprovedExpenses, decimal ExpensesPaid,
    bool LedgerAvailable, string? LedgerNote,
    decimal? CashBalance, decimal? BankBalance, decimal? ExpensesPerLedger, decimal? Revenue, decimal? ProfitLoss,
    IReadOnlyList<ProjectSummaryRowDto> Projects, IReadOnlyList<DashboardCheckDto> Checks);

public sealed record ProjectSummaryRowDto(long ProjectId, string ProjectCode, string ProjectName, decimal AreaSqFt, int Inventory, int Sold, int Available,
    decimal BookingValue, decimal Collections, decimal Outstanding, decimal Overdue, decimal Expenses);

public sealed record LandDistributionRowDto(string Category, decimal Area, decimal Percent);

public sealed record InventoryCategoryRowDto(string Category, int Total, int Available, int Reserved, int Booked, int Sold, decimal TotalSize);

public sealed record ProjectDashboardDto(
    long ProjectId, string ProjectCode, string ProjectName, string Status, string City, string AreaUnit, decimal ProjectArea, decimal AreaSqFt,
    DateOnly? From, DateOnly To, string CurrencySymbol,
    IReadOnlyList<LandDistributionRowDto> LandDistribution, IReadOnlyList<InventoryCategoryRowDto> InventoryByCategory, IReadOnlyList<InventoryStatusCountDto> InventoryByStatus,
    int TotalInventory, int SoldProperties, int SalesCount, decimal SalesValue, int BookingsCount, decimal BookingValue, int PendingBookings,
    decimal Collections, decimal Outstanding, decimal Overdue, int OverdueInstallments, int OverdueAgreements,
    decimal ApprovedExpenses, decimal ExpensesPaid, decimal ExpensesOutstanding,
    bool LedgerAvailable, string? LedgerNote, decimal? LedgerRevenue, decimal? LedgerExpenses, decimal? LedgerProfit,
    decimal OperationalResult, IReadOnlyList<DashboardCheckDto> Checks);
