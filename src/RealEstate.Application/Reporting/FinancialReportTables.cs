using RealEstate.Application.Accounting;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Reporting;

/// <summary>
/// Financial statements (spec 9.6) as report tables. The accounting engine (Phase 8) stays the only source: every figure is produced by <see cref="AccountingReportService"/> from the
/// posted general ledger and merely reshaped here, so the exports and the on-screen statements can never disagree. Permission and project-scope checks are those of that service.
/// </summary>
public class FinancialReportTables(AccountingReportService accounting)
{
    private static ReportColumnDto C(string key, string header, ReportColumnKind kind = ReportColumnKind.Text) => new(key, header, kind);
    private const ReportColumnKind Money = ReportColumnKind.Money, Dt = ReportColumnKind.Date;

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(IEnumerable<object?[]> rows) => rows.Select(r => (IReadOnlyList<object?>)r).ToList();

    private static List<object?[]> Section(StatementSectionDto section) =>
        section.Lines.Select(l => new object?[] { l.Code, new string(' ', Math.Max(0, l.Level) * 2) + l.Name, l.Amount }).Append(new object?[] { "", section.Title + " total", section.Total }).ToList();

    private static ReportBuild Page(IReadOnlyList<ReportColumnDto> cols, List<object?[]> rows, object?[]? totals, ReportFilters f, bool export, IReadOnlyList<string> notes)
    {
        var size = Math.Clamp(f.PageSize <= 0 ? 50 : f.PageSize, 1, ReportLimits.MaxPageSize);
        var page = Math.Max(1, f.Page);
        var shown = export ? rows.Take(ReportLimits.MaxExportRows).ToList() : rows.Skip((page - 1) * size).Take(size).ToList();
        return new ReportBuild(cols, Rows(shown), totals, rows.Count, notes, export && rows.Count > ReportLimits.MaxExportRows);
    }

    public async Task<ReportBuild> TrialBalanceAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.TrialBalanceAsync(new ReportFilter(f.From, f.To, f.ProjectId, f.CostCenterId), ct);
        var rows = r.Rows.Select(x => new object?[] { x.Code, x.Name, x.GroupName, x.OpeningDebit, x.OpeningCredit, x.PeriodDebit, x.PeriodCredit, x.ClosingDebit, x.ClosingCredit }).ToList();
        return Page([C("code", "Code"), C("name", "Account"), C("group", "Group"), C("od", "Opening debit", Money), C("oc", "Opening credit", Money), C("pd", "Period debit", Money),
            C("pc", "Period credit", Money), C("cd", "Closing debit", Money), C("cc", "Closing credit", Money)], rows,
            ["Total", "", "", r.TotalOpeningDebit, r.TotalOpeningCredit, r.TotalPeriodDebit, r.TotalPeriodCredit, r.TotalClosingDebit, r.TotalClosingCredit], f, export,
            [r.IsBalanced ? "The trial balance is balanced (debits = credits)." : "WARNING: the trial balance is not balanced."]);
    }

    public async Task<ReportBuild> GeneralLedgerAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var accountId = f.AccountId ?? throw ReportingSupport.Invalid(nameof(f.AccountId), "Choose an account.");
        var pageSize = export ? ReportLimits.MaxPageSize : Math.Clamp(f.PageSize <= 0 ? 50 : f.PageSize, 1, ReportLimits.MaxPageSize);
        var first = await accounting.GeneralLedgerAsync(new GeneralLedgerRequest(accountId, f.From, f.To, f.ProjectId, f.CostCenterId, export ? 1 : Math.Max(1, f.Page), pageSize), ct);
        var lines = first.Lines.Items.ToList();
        var truncated = false;
        if (export)
        {
            var total = first.Lines.TotalCount;
            for (var page = 2; lines.Count < total && lines.Count < ReportLimits.MaxExportRows; page++)
                lines.AddRange((await accounting.GeneralLedgerAsync(new GeneralLedgerRequest(accountId, f.From, f.To, f.ProjectId, f.CostCenterId, page, pageSize), ct)).Lines.Items);
            truncated = total > lines.Count;
        }
        var rows = lines.Select(l => new object?[] { l.EntryDate, l.VoucherNumber, l.Narration, l.Reference ?? "-", l.ProjectCode ?? "-", l.CostCenterCode ?? "-", l.Debit, l.Credit, l.Balance }).ToList();
        return new ReportBuild([C("date", "Date", Dt), C("voucher", "Voucher"), C("narration", "Narration"), C("reference", "Reference"), C("project", "Project"), C("costcenter", "Cost center"),
            C("debit", "Debit", Money), C("credit", "Credit", Money), C("balance", "Balance", Money)], Rows(rows), ["Total", "", "", "", "", "", first.TotalDebit, first.TotalCredit, first.ClosingBalance],
            first.Lines.TotalCount, [$"{first.AccountCode} {first.AccountName}. Opening balance {first.OpeningBalance:N2}; closing balance {first.ClosingBalance:N2}."], truncated);
    }

    public async Task<ReportBuild> BalanceSheetAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.BalanceSheetAsync(f.To, f.ProjectId, ct);
        var rows = new List<object?[]> { new object?[] { "", "ASSETS", null } };
        rows.AddRange(Section(r.Assets));
        rows.Add(["", "LIABILITIES", null]); rows.AddRange(Section(r.Liabilities));
        rows.Add(["", "EQUITY", null]); rows.AddRange(Section(r.Equity));
        rows.Add(["", "Retained earnings (earlier years)", r.RetainedEarningsComputed]);
        rows.Add(["", "Current year profit", r.CurrentYearProfit]);
        rows.Add(["", "Total liabilities and equity", r.TotalLiabilitiesAndEquity]);
        return Page([C("code", "Code"), C("name", "Account"), C("amount", "Amount", Money)], rows, null, f, export,
            [$"As of {r.AsOf:yyyy-MM-dd}.", r.IsBalanced ? "Assets equal liabilities plus equity." : "WARNING: assets differ from liabilities plus equity."]);
    }

    public async Task<ReportBuild> IncomeStatementAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.IncomeStatementAsync(f.From, f.To, f.ProjectId, ct);
        var rows = new List<object?[]> { new object?[] { "", "REVENUE", null } };
        rows.AddRange(Section(r.Revenue));
        rows.Add(["", "EXPENSES", null]); rows.AddRange(Section(r.Expenses));
        rows.Add(["", "Net profit / (loss)", r.NetProfit]);
        return Page([C("code", "Code"), C("name", "Account"), C("amount", "Amount", Money)], rows, null, f, export, [$"Period {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}."]);
    }

    public async Task<ReportBuild> CashFlowAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.CashFlowAsync(f.From, f.To, f.ProjectId, ct);
        var rows = new List<object?[]> { new object?[] { "Opening cash and bank", r.OpeningCash }, new object?[] { "Operating activities", r.Operating }, new object?[] { "Investing activities", r.Investing },
            new object?[] { "Financing activities", r.Financing }, new object?[] { "Net change", r.NetChange }, new object?[] { "Closing cash and bank", r.ClosingCash }, new object?[] { "Closing per ledger", r.ClosingCashPerLedger } };
        rows.AddRange(r.Details.Select(d => new object?[] { "  " + d.Name, d.Amount }));
        return Page([C("item", "Item"), C("amount", "Amount", Money)], rows, null, f, export,
            [$"Period {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}.", r.Reconciles ? "Closing cash equals the ledger balance." : "WARNING: closing cash differs from the ledger balance."]);
    }

    public async Task<ReportBuild> ReceivablesAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.AccountsReceivableAsync(f.To, f.ProjectId, ct);
        var rows = r.Rows.Select(x => new object?[] { x.CustomerName, x.AgreementNumber, x.ProjectCode, x.Debit, x.Credit, x.Balance }).ToList();
        return Page([C("customer", "Customer"), C("agreement", "Agreement"), C("project", "Project"), C("debit", "Debits", Money), C("credit", "Credits", Money), C("balance", "Balance", Money)], rows,
            ["Net balance", "", "", null, null, r.NetBalance], f, export,
            [$"As of {r.AsOf:yyyy-MM-dd}. Receivable {r.TotalReceivable:N2}; advances {r.TotalAdvance:N2}; control account {r.ControlBalance:N2}; difference {r.Difference:N2}."]);
    }

    public async Task<ReportBuild> PayablesAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.AccountsPayableAsync(f.ProjectId, ct);
        var rows = r.Rows.Select(x => new object?[] { x.VendorName, x.OpenExpenses, x.Approved, x.Paid, x.Outstanding, x.Overdue }).ToList();
        return Page([C("vendor", "Vendor"), C("open", "Open expenses", ReportColumnKind.Number), C("approved", "Approved", Money), C("paid", "Paid", Money), C("outstanding", "Outstanding", Money), C("overdue", "Overdue", Money)],
            rows, ["Total", r.Rows.Sum(x => x.OpenExpenses), r.Rows.Sum(x => x.Approved), r.Rows.Sum(x => x.Paid), r.TotalOutstanding, r.Rows.Sum(x => x.Overdue)], f, export,
            [$"As of {r.AsOf:yyyy-MM-dd}. Control account {r.ControlBalance:N2}; difference {r.Difference:N2}. Government payable outstanding {r.GovernmentPayableOutstanding:N2}."]);
    }

    public async Task<ReportBuild> ProjectProfitabilityAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.ProjectProfitabilityAsync(f.From, f.To, ct);
        var rows = r.Rows.Select(x => new object?[] { x.ProjectCode + " " + x.ProjectName, x.Revenue, x.Expenses, x.NetProfit, x.MarginPercent }).ToList();
        return Page([C("project", "Project"), C("revenue", "Revenue", Money), C("expenses", "Expenses", Money), C("net", "Net profit / (loss)", Money), C("margin", "Margin %", ReportColumnKind.Percent)], rows,
            ["Total", r.TotalRevenue, r.TotalExpenses, r.TotalNet, null], f, export, [$"Period {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}. Posted journals only."]);
    }

    public async Task<ReportBuild> ProjectExpensesAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var projectId = f.ProjectId ?? throw ReportingSupport.Invalid(nameof(f.ProjectId), "Choose a project.");
        var r = await accounting.ProjectExpenseStatementAsync(projectId, f.From, f.To, ct);
        var rows = Section(r.ByAccount);
        rows.Add(["", "BY COST CENTER", null]);
        rows.AddRange(r.ByCostCenter.Select(l => new object?[] { l.Code, l.Name, l.Amount }));
        return Page([C("code", "Code"), C("name", "Account / cost center"), C("amount", "Amount", Money)], rows, null, f, export,
            [$"{r.ProjectCode} {r.ProjectName}, {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}. Approved expenses in the expense module: {r.ApprovedExpensesInModule:N2}."]);
    }

    public async Task<ReportBuild> CustomerControlAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.CustomerControlReconciliationAsync(f.To, f.ProjectId, ct);
        var rows = r.Rows.Select(x => new object?[] { x.ProjectCode, x.LedgerBalance, x.ControlBalance, x.Difference, x.UnprocessedEvents, x.Reconciled ? "Reconciled" : "Difference" }).ToList();
        return Page([C("project", "Project"), C("ledger", "Customer ledger", Money), C("control", "Control account", Money), C("difference", "Difference", Money), C("events", "Unposted events", ReportColumnKind.Number), C("status", "Status")],
            rows, ["Total", r.TotalLedger, r.TotalControl, r.TotalDifference, null, r.AllReconciled ? "Reconciled" : "Difference"], f, export, [$"As of {r.AsOf:yyyy-MM-dd}."]);
    }

    public async Task<ReportBuild> ProjectReconciliationAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var r = await accounting.ProjectReconciliationAsync(f.To, ct);
        var rows = r.Rows.Select(x => new object?[] { x.ProjectCode, x.Debit, x.Credit, x.CustomerLedger, x.CustomerControl, x.VendorOutstanding, x.VendorControl, x.ModuleExpenses, x.LedgerExpenses,
            x.Reconciled ? "Reconciled" : "Difference" }).ToList();
        return Page([C("project", "Project"), C("debit", "Debits", Money), C("credit", "Credits", Money), C("cl", "Customer ledger", Money), C("cc", "Customer control", Money), C("vo", "Vendor outstanding", Money),
            C("vc", "Vendor control", Money), C("me", "Expenses (module)", Money), C("le", "Expenses (ledger)", Money), C("status", "Status")], rows, null, f, export,
            [$"As of {r.AsOf:yyyy-MM-dd}. {r.UnprocessedEvents} unposted accounting events."]);
    }
}
