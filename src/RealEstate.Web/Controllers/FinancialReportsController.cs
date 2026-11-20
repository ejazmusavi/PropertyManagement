using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Accounting;
using RealEstate.Application.Banking;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>
/// Financial reports and statements (spec 8.6). Users without access to all projects get their first project preselected, since company-wide figures need
/// access to all projects.
/// </summary>
[HasPermission(Permissions.AccountingReports)]
public class FinancialReportsController(AccountingReportService reports, ChartOfAccountsService chart, BankingService banking, AccountingSupport support) : InstallmentControllerBase
{
    public IActionResult Index() => View();

    public Task<IActionResult> TrialBalance(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Trial balance", nameof(TrialBalance), f, async () => await reports.TrialBalanceAsync(new ReportFilter(f.From, f.To, f.ProjectId, f.CostCenterId), ct), ct, costCenters: true);

    public Task<IActionResult> GeneralLedger(ReportFilterViewModel f, CancellationToken ct) =>
        Page("General ledger", nameof(GeneralLedger), f, async () => f.AccountId is { } a
            ? await reports.GeneralLedgerAsync(new GeneralLedgerRequest(a, f.From, f.To, f.ProjectId, f.CostCenterId, f.Page, 50), ct) : null, ct, accounts: true, costCenters: true,
            message: f.AccountId is null ? "Choose an account to see its ledger." : null);

    public Task<IActionResult> Cashbook(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Cashbook", nameof(Cashbook), f, async () => f.CashAccountId is { } c ? await reports.CashbookAsync(c, f.From, f.To, f.Page, ct) : null, ct, cash: true, companyOnly: true,
            message: f.CashAccountId is null ? "Choose a cash account." : null);

    public Task<IActionResult> Bankbook(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Bankbook", nameof(Bankbook), f, async () => f.BankAccountId is { } b ? await reports.BankbookAsync(b, f.From, f.To, f.Page, ct) : null, ct, banks: true, companyOnly: true,
            message: f.BankAccountId is null ? "Choose a bank account." : null);

    public Task<IActionResult> JournalRegister(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Journal register", nameof(JournalRegister), f, async () => await reports.JournalRegisterAsync(f.From, f.To, f.Source, f.Page, 20, ct), ct, projectFilter: false);

    public Task<IActionResult> BalanceSheet(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Balance sheet", nameof(BalanceSheet), f, async () => await reports.BalanceSheetAsync(f.AsOf, f.ProjectId, ct), ct);

    public Task<IActionResult> IncomeStatement(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Income statement", nameof(IncomeStatement), f, async () => await reports.IncomeStatementAsync(f.From, f.To, f.ProjectId, ct), ct);

    public Task<IActionResult> CashFlow(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Cash flow statement", nameof(CashFlow), f, async () => await reports.CashFlowAsync(f.From, f.To, f.ProjectId, ct), ct);

    public Task<IActionResult> Receivables(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Accounts receivable", nameof(Receivables), f, async () => await reports.AccountsReceivableAsync(f.AsOf, f.ProjectId, ct), ct);

    public Task<IActionResult> Payables(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Accounts payable", nameof(Payables), f, async () => await reports.AccountsPayableAsync(f.ProjectId, ct), ct);

    public Task<IActionResult> ProjectProfitability(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Project profitability", nameof(ProjectProfitability), f, async () => await reports.ProjectProfitabilityAsync(f.From, f.To, ct), ct, projectFilter: false);

    public Task<IActionResult> ProjectExpenses(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Project expense statement", nameof(ProjectExpenses), f, async () => f.ProjectId is { } p ? await reports.ProjectExpenseStatementAsync(p, f.From, f.To, ct) : null, ct,
            requireProject: true, message: f.ProjectId is null ? "Choose a project." : null);

    public Task<IActionResult> CustomerControl(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Customer control account reconciliation", nameof(CustomerControl), f, async () => await reports.CustomerControlReconciliationAsync(f.AsOf, f.ProjectId, ct), ct);

    public Task<IActionResult> ProjectReconciliation(ReportFilterViewModel f, CancellationToken ct) =>
        Page("Project accounts reconciliation", nameof(ProjectReconciliation), f, async () => await reports.ProjectReconciliationAsync(f.AsOf, ct), ct, projectFilter: false, companyOnly: true);

    private async Task<IActionResult> Page<T>(string title, string action, ReportFilterViewModel f, Func<Task<T?>> load, CancellationToken ct, bool accounts = false, bool costCenters = false,
        bool banks = false, bool cash = false, bool projectFilter = true, bool companyOnly = false, bool requireProject = false, string? message = null) where T : class
    {
        var all = await support.HasAllAccessAsync(ct);
        f.CompanyWideAllowed = all;
        f.Projects = await support.ProjectOptionsAsync(ct);
        if ((projectFilter || requireProject) && !all && f.ProjectId is null && f.Projects.Count > 0) { f.ProjectId = f.Projects[0].Id; if (requireProject) message = null; }
        if (accounts) f.Accounts = await chart.PostableOptionsAsync(null, ct);
        if (costCenters) f.CostCenters = (await chart.ListCostCentersAsync(true, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList();
        if (banks && all) f.BankAccounts = (await banking.ListBankAccountsAsync(false, ct)).Select(b => new OptionDto(b.Id, b.Code, b.Name)).ToList();
        if (cash && all) f.CashAccounts = (await banking.ListCashAccountsAsync(false, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList();
        var vm = new ReportPageViewModel<T> { Title = title, Action = action, Filter = f, Message = message };
        if (companyOnly && !all) vm.Message = "This report covers company-level accounts and needs access to all projects.";
        else if ((projectFilter || requireProject) && !all && f.Projects.Count == 0) vm.Message = "You are not assigned to any project.";
        else if (requireProject && f.ProjectId is null) vm.Message = message;
        else
        {
            try { vm.Report = await load(); }
            catch (Exception ex) when (Soft(ex)) { vm.Message = Message(ex); }
        }
        return View(action, vm);
    }
}
