using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Reporting;

public sealed record ReportLookupsDto(IReadOnlyList<ProjectLookupDto> Projects, IReadOnlyList<BlockLookupDto> Blocks, IReadOnlyList<ReportOptionDto> PaymentMethods);
public sealed record ProjectLookupDto(long Id, string Code, string Name);
public sealed record BlockLookupDto(long Id, long ProjectId, string Code, string Name);

/// <summary>
/// The report catalog, runner and exporter shared by the MVC screens and the API. Operational reports need <c>reports.view</c>; financial statements need <c>accounting.reports</c>
/// (enforced again by the accounting engine); exports additionally need <c>reports.export</c> and are audited (who, when, report, filters, row count).
/// </summary>
public class ReportingService(IAppDbContext db, ReportingSupport s, OperationalReportService operational, FinancialReportTables financial, IReportExporter exporter, IAuditService audit)
{
    private delegate Task<ReportBuild> Builder(ReportFilters f, bool export, CancellationToken ct);
    private sealed record Entry(ReportDefinitionDto Def, Builder Build);

    private const string F = ReportFilterNames.From, T = ReportFilterNames.To, P = ReportFilterNames.Project, B = ReportFilterNames.Block, Cu = ReportFilterNames.Customer,
        Tt = ReportFilterNames.TransactionType, Pm = ReportFilterNames.PaymentMethod, St = ReportFilterNames.Status, Ac = ReportFilterNames.Account, Cc = ReportFilterNames.CostCenter;

    private static IReadOnlyList<ReportOptionDto> Opts<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => new ReportOptionDto(v.ToString(), Label(v.ToString()))).ToList();

    private static string Label(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static readonly IReadOnlyList<ReportOptionDto> None = [];

    private static ReportDefinitionDto Def(string key, string title, string group, string description, string[] filters, bool customer = false, bool project = false, bool financial = false,
        IReadOnlyList<ReportOptionDto>? types = null, IReadOnlyList<ReportOptionDto>? statuses = null) =>
        new(key, title, group, description, filters, customer, project, financial, types ?? None, statuses ?? None);

    private IReadOnlyList<Entry> BuildCatalog()
    {
        var ledger = Opts<LedgerEntryType>(); var mode = Opts<PaymentMode>(); var source = Opts<PaymentSource>();
        var booking = Opts<BookingStatus>(); var payment = Opts<PaymentStatus>(); var item = Opts<ScheduleItemStatus>(); var expense = Opts<ExpenseStatus>();
        const string customers = "Customer reports", sales = "Sales reports", collections = "Collection reports", fin = "Financial reports";
        return
        [
            new(Def("customer-statement", "Customer statement", customers, "Opening balance, debits, credits and closing balance of each agreement of a customer.", [F, T, P, B, Cu, Tt], customer: true, types: ledger), operational.CustomerStatementAsync),
            new(Def("customer-ledger", "Customer ledger", customers, "Every ledger entry of a customer with a running balance.", [F, T, P, B, Cu, Tt], customer: true, types: ledger), operational.CustomerLedgerAsync),
            new(Def("customer-properties", "Customer property list", customers, "Properties held by each customer, with ownership share and price.", [F, T, P, B, Cu, St], statuses: booking), operational.CustomerPropertiesAsync),
            new(Def("payment-history", "Payment history", customers, "Payments of customers in every status.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.PaymentHistoryAsync),
            new(Def("installment-history", "Installment history", customers, "Installment lines of active schedules with paid, waived and outstanding amounts.", [F, T, P, B, Cu, St], statuses: item), operational.InstallmentHistoryAsync),
            new(Def("outstanding-balance", "Outstanding balance", customers, "Due, overdue and not-yet-due balance of every agreement as of a date.", [T, P, B, Cu]), operational.OutstandingBalanceAsync),
            new(Def("overdue", "Overdue report", customers, "Overdue installment lines with days overdue and aging.", [F, T, P, B, Cu]), operational.OverdueAsync),

            new(Def("daily-sales", "Daily sales", sales, "Sales by booking date.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.DailySalesReportAsync),
            new(Def("monthly-sales", "Monthly sales", sales, "Sales by month.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.MonthlySalesReportAsync),
            new(Def("project-sales", "Project-wise sales", sales, "Sales by project.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.ProjectSalesReportAsync),
            new(Def("block-sales", "Block-wise sales", sales, "Properties sold and their value by project block.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.BlockSalesReportAsync),
            new(Def("category-sales", "Sales by property category", sales, "Properties sold and their value by category and type.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.CategorySalesReportAsync),
            new(Def("salesperson-sales", "Sales by salesperson", sales, "Sales by the user who created the booking.", [F, T, P, B, Cu, Tt, St], types: mode, statuses: booking), operational.SalespersonSalesReportAsync),
            new(Def("agent-commissions", "Agent commissions", sales, "Commission expenses by agent (vendor).", [F, T, P, St], statuses: expense), operational.AgentCommissionsAsync),
            new(Def("cancellations", "Cancellation report", sales, "Cancelled bookings with reasons and refunds.", [F, T, P, B, Cu, Tt], types: mode), operational.CancellationsAsync),

            new(Def("daily-collection", "Daily collection", collections, "Posted payments by payment date.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.DailyCollectionAsync),
            new(Def("monthly-collection", "Monthly collection", collections, "Posted payments by month.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.MonthlyCollectionAsync),
            new(Def("project-collection", "Project collection", collections, "Posted payments by project.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.ProjectCollectionAsync),
            new(Def("payment-methods", "Payment method summary", collections, "Posted payments by payment method.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.PaymentMethodSummaryAsync),
            new(Def("cashier-collection", "Cashier collection", collections, "Posted payments by the user who recorded them.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.CashierCollectionAsync),
            new(Def("bank-collection", "Bank collection", collections, "Non-cash collections by bank.", [F, T, P, B, Cu, Pm, St, Tt], types: source, statuses: payment), operational.BankCollectionAsync),
            new(Def("advance-payments", "Advance payment report", collections, "Money paid ahead of the due date or held as advance, per agreement.", [F, T, P, B, Cu]), operational.AdvancePaymentsAsync),

            new(Def("trial-balance", "Trial balance", fin, "Opening, period and closing balances of every account (general ledger).", [F, T, P, Cc], financial: true), financial.TrialBalanceAsync),
            new(Def("general-ledger", "General ledger", fin, "Entries of one account with a running balance.", [F, T, P, Ac, Cc], financial: true), financial.GeneralLedgerAsync),
            new(Def("balance-sheet", "Balance sheet", fin, "Assets, liabilities and equity as of a date.", [T, P], financial: true), financial.BalanceSheetAsync),
            new(Def("income-statement", "Income statement", fin, "Revenue, expenses and net profit for a period.", [F, T, P], financial: true), financial.IncomeStatementAsync),
            new(Def("cash-flow", "Cash flow statement", fin, "Cash and bank movements by activity.", [F, T, P], financial: true), financial.CashFlowAsync),
            new(Def("accounts-receivable", "Accounts receivable", fin, "Customer receivables by agreement with control-account check.", [T, P], financial: true), financial.ReceivablesAsync),
            new(Def("accounts-payable", "Accounts payable", fin, "Vendor payables with control-account check.", [P], financial: true), financial.PayablesAsync),
            new(Def("project-profitability", "Project profitability", fin, "Revenue, expenses and profit per project from posted journals.", [F, T], financial: true), financial.ProjectProfitabilityAsync),
            new(Def("project-expenses", "Project expense statement", fin, "Expenses of one project by account and cost center.", [F, T, P], project: true, financial: true), financial.ProjectExpensesAsync),
            new(Def("customer-control", "Customer control reconciliation", fin, "Customer ledgers against the receivable control accounts.", [T, P], financial: true), financial.CustomerControlAsync),
            new(Def("project-reconciliation", "Project accounts reconciliation", fin, "Project ledgers against the expense module and sub-ledgers.", [T], financial: true), financial.ProjectReconciliationAsync),
        ];
    }

    private IReadOnlyList<Entry>? built;
    private IReadOnlyList<Entry> Catalog => built ??= BuildCatalog();

    /// <summary>The reports the caller may open (operational need reports.view, financial need accounting.reports).</summary>
    public async Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct = default)
    {
        var view = await s.HasAsync(Permissions.ReportsView, ct);
        var fin = await s.HasAsync(Permissions.AccountingReports, ct);
        return Catalog.Select(e => e.Def).Where(d => d.IsFinancial ? fin : view).ToList();
    }

    public async Task<ReportDefinitionDto> GetDefinitionAsync(string key, CancellationToken ct = default)
    {
        var e = Find(key);
        await s.EnsureAsync(e.Def.IsFinancial ? Permissions.AccountingReports : Permissions.ReportsView, ct);
        return e.Def;
    }

    private Entry Find(string key) => Catalog.FirstOrDefault(e => string.Equals(e.Def.Key, key, StringComparison.OrdinalIgnoreCase)) ?? throw new NotFoundException("Report not found.");

    private static void Validate(ReportFilters f)
    {
        if (f.From is { } from && f.To is { } to && from > to) throw ReportingSupport.Invalid(nameof(f.From), "The start date must not be after the end date.");
    }

    private async Task<ReportTableDto> BuildAsync(Entry e, ReportFilters f, bool export, CancellationToken ct)
    {
        await s.EnsureAsync(e.Def.IsFinancial ? Permissions.AccountingReports : Permissions.ReportsView, ct);
        Validate(f);
        var today = await s.TodayAsync(ct);
        var b = await e.Build(f, export, ct);
        var size = Math.Clamp(f.PageSize <= 0 ? 50 : f.PageSize, 1, ReportLimits.MaxPageSize);
        var notes = b.Notes.ToList();
        if (b.Truncated) notes.Add($"The export is limited to the first {ReportLimits.MaxExportRows:N0} rows; narrow the filters to see the rest.");
        return new ReportTableDto(e.Def.Key, e.Def.Title, e.Def.Group, b.Columns, b.Rows, b.Totals, b.TotalRows, export ? 1 : Math.Max(1, f.Page), export ? Math.Max(1, b.Rows.Count) : size,
            await s.EchoAsync(e.Def, f, e.Def.Uses(ReportFilterNames.To) ? today : null, ct), notes, s.Now, s.Actor, await s.CurrencyAsync(ct), b.Truncated);
    }

    public async Task<ReportTableDto> RunAsync(string key, ReportFilters filters, CancellationToken ct = default) => await BuildAsync(Find(key), filters, false, ct);

    public async Task<ReportExportFile> ExportAsync(string key, ReportFilters filters, ExportFormat format, CancellationToken ct = default)
    {
        var e = Find(key);
        await s.EnsureAsync(Permissions.ReportsExport, ct);
        var report = await BuildAsync(e, filters, true, ct);
        var bytes = format == ExportFormat.Pdf ? exporter.ToPdf(report) : exporter.ToExcel(report);
        await audit.RecordAsync(new AuditEntry("ReportExported", "Report", e.Def.Key, NewValues: new
        {
            Format = format.ToString(), Rows = report.Rows.Count, Filters = report.Filters.Select(x => $"{x.Label}: {x.Value}").ToList(), report.GeneratedAtUtc,
        }), ct);
        var stamp = s.Now.ToString("yyyyMMdd-HHmm");
        return format == ExportFormat.Pdf
            ? new ReportExportFile(bytes, "application/pdf", $"{e.Def.Key}-{stamp}.pdf")
            : new ReportExportFile(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{e.Def.Key}-{stamp}.xlsx");
    }

    public Task<bool> CanExportAsync(CancellationToken ct = default) => s.HasAsync(Permissions.ReportsExport, ct);

    /// <summary>Projects (caller's), blocks and payment methods for the filter forms.</summary>
    public async Task<ReportLookupsDto> LookupsAsync(CancellationToken ct = default)
    {
        var projects = await s.ProjectOptionsAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var blocks = await db.ProjectBlocks.AsNoTracking().Where(b => ids.Contains(b.ProjectId)).OrderBy(b => b.Code).Select(b => new BlockLookupDto(b.Id, b.ProjectId, b.Code, b.Name)).ToListAsync(ct);
        var methods = await db.PaymentMethods.AsNoTracking().OrderBy(m => m.Name).Select(m => new ReportOptionDto(m.Id.ToString(), m.Name)).ToListAsync(ct);
        return new ReportLookupsDto(projects.Select(p => new ProjectLookupDto(p.Id, p.ProjectCode, p.ProjectName)).ToList(), blocks, methods);
    }
}
