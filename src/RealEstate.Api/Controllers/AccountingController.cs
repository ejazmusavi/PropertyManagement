using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Accounting;
using RealEstate.Application.Banking;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Double-entry accounting (Phase 8): chart of accounts, journals, posting rules, automatic postings, periods and financial reports.</summary>
[ApiController, Authorize]
[Route("api/v1/accounting")]
public class AccountingController(ChartOfAccountsService chart, JournalService journals, FiscalPeriodService periods, PostingRuleService rules,
    PostingEngineService engine, AccountingReportService reports, BankingService banking) : ControllerBase
{
    // ----- chart of accounts -----
    [HttpGet("accounts"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PagedResult<AccountDto>>> Accounts([FromQuery] AccountListRequest request, CancellationToken ct) => await chart.ListAccountsAsync(request, ct);

    [HttpPost("accounts"), HasPermission(Permissions.AccountingSetup)]
    [ProducesResponseType<AccountDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAccount(SaveAccountRequest request, CancellationToken ct)
    {
        var a = await chart.CreateAccountAsync(request, ct);
        return CreatedAtAction(nameof(Account), new { id = a.Account.Id }, a);
    }

    [HttpGet("accounts/{id:long}"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<AccountDetailDto>> Account(long id, CancellationToken ct) => await chart.GetAccountAsync(id, ct);

    [HttpPut("accounts/{id:long}"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<AccountDetailDto>> UpdateAccount(long id, SaveAccountRequest request, CancellationToken ct) => await chart.UpdateAccountAsync(id, request, ct);

    [HttpGet("account-groups"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<AccountGroupDto>>> Groups(CancellationToken ct) => Ok(await chart.ListGroupsAsync(ct));

    [HttpPost("account-groups"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<AccountGroupDto>> CreateGroup(SaveAccountGroupRequest request, CancellationToken ct) => await chart.CreateGroupAsync(request, ct);

    [HttpPut("account-groups/{id:long}"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<AccountGroupDto>> UpdateGroup(long id, SaveAccountGroupRequest request, CancellationToken ct) => await chart.UpdateGroupAsync(id, request, ct);

    [HttpGet("cost-centers"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<CostCenterDto>>> CostCenters(CancellationToken ct) => Ok(await chart.ListCostCentersAsync(false, ct));

    [HttpPost("cost-centers"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<CostCenterDto>> CreateCostCenter(SaveCostCenterRequest request, CancellationToken ct) => await chart.SaveCostCenterAsync(null, request, ct);

    [HttpPut("cost-centers/{id:long}"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<CostCenterDto>> UpdateCostCenter(long id, SaveCostCenterRequest request, CancellationToken ct) => await chart.SaveCostCenterAsync(id, request, ct);

    // ----- journals -----
    [HttpGet("journals"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PagedResult<JournalListItemDto>>> Journals([FromQuery] JournalListRequest request, CancellationToken ct) => await journals.ListAsync(request, ct);

    [HttpPost("journals"), HasPermission(Permissions.AccountingJournalsManage)]
    [ProducesResponseType<JournalDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateJournal(CreateJournalRequest request, CancellationToken ct)
    {
        var j = await journals.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Journal), new { id = j.Id }, j);
    }

    [HttpGet("journals/{id:long}"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<JournalDetailDto>> Journal(long id, CancellationToken ct) => await journals.GetAsync(id, ct);

    [HttpPut("journals/{id:long}"), HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<ActionResult<JournalDetailDto>> UpdateJournal(long id, UpdateJournalRequest request, CancellationToken ct) => await journals.UpdateAsync(id, request, ct);

    [HttpPost("journals/{id:long}/submit"), HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<ActionResult<JournalDetailDto>> Submit(long id, SubmitJournalRequest request, CancellationToken ct) => await journals.SubmitAsync(id, request, ct);

    [HttpPost("journals/{id:long}/approve"), HasPermission(Permissions.AccountingJournalsApprove)]
    public async Task<ActionResult<JournalDetailDto>> Approve(long id, ApproveJournalRequest request, CancellationToken ct) => await journals.ApproveAsync(id, request, ct);

    [HttpPost("journals/{id:long}/reject"), HasPermission(Permissions.AccountingJournalsApprove)]
    public async Task<ActionResult<JournalDetailDto>> Reject(long id, RejectJournalRequest request, CancellationToken ct) => await journals.RejectAsync(id, request, ct);

    [HttpPost("journals/{id:long}/cancel"), HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<ActionResult<JournalDetailDto>> Cancel(long id, CancelJournalRequest request, CancellationToken ct) => await journals.CancelAsync(id, request, ct);

    [HttpPost("journals/{id:long}/reverse"), HasPermission(Permissions.AccountingJournalsReverse)]
    public async Task<ActionResult<JournalDetailDto>> Reverse(long id, ReverseJournalRequest request, CancellationToken ct) => await journals.ReverseAsync(id, request, ct);

    [HttpPost("transfers"), HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<ActionResult<JournalDetailDto>> Transfer(CreateTransferRequest request, CancellationToken ct) => await journals.CreateTransferAsync(request, ct);

    [HttpGet("cost-allocations"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<CostAllocationDto>>> Allocations(CancellationToken ct) => Ok(await journals.ListAllocationsAsync(null, ct));

    [HttpPost("cost-allocations"), HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<ActionResult<CostAllocationDto>> CreateAllocation(CreateCostAllocationRequest request, CancellationToken ct) => await journals.CreateAllocationAsync(request, ct);

    // ----- fiscal years and periods -----
    [HttpGet("fiscal-years"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<FiscalYearDto>>> FiscalYears(CancellationToken ct) => Ok(await periods.ListAsync(ct));

    [HttpPost("fiscal-years"), HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<ActionResult<FiscalYearDto>> CreateFiscalYear(CreateFiscalYearRequest request, CancellationToken ct) => await periods.CreateAsync(request, ct);

    [HttpPost("fiscal-years/{id:long}/close"), HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<ActionResult<FiscalYearDto>> CloseFiscalYear(long id, CloseFiscalYearRequest request, CancellationToken ct) => await periods.CloseYearAsync(id, request, ct);

    [HttpPost("periods/{id:long}/status"), HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<ActionResult<AccountingPeriodDto>> ChangePeriod(long id, PeriodActionRequest request, CancellationToken ct) => await periods.ChangePeriodAsync(id, request, ct);

    [HttpGet("period-locks"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PagedResult<PeriodLockDto>>> PeriodLocks([FromQuery] long? fiscalYearId, [FromQuery] int page = 1, CancellationToken ct = default) =>
        await periods.LockHistoryAsync(fiscalYearId, page, 50, ct);

    // ----- posting rules and automatic postings -----
    [HttpGet("posting-events"), HasPermission(Permissions.AccountingView)]
    public ActionResult<IReadOnlyList<EventCatalogDto>> EventCatalog() => Ok(rules.Catalog());

    [HttpGet("posting-rules"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PagedResult<PostingRuleDto>>> Rules([FromQuery] PostingRuleListRequest request, CancellationToken ct) => await rules.ListAsync(request, ct);

    [HttpGet("posting-rules/{id:long}"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PostingRuleDto>> Rule(long id, CancellationToken ct) => await rules.GetAsync(id, ct);

    [HttpPost("posting-rules"), HasPermission(Permissions.AccountingRulesManage)]
    public async Task<ActionResult<PostingRuleDto>> CreateRule(SavePostingRuleRequest request, CancellationToken ct) => await rules.CreateAsync(request, ct);

    [HttpPut("posting-rules/{id:long}"), HasPermission(Permissions.AccountingRulesManage)]
    public async Task<ActionResult<PostingRuleDto>> UpdateRule(long id, SavePostingRuleRequest request, CancellationToken ct) => await rules.UpdateAsync(id, request, ct);

    [HttpPost("posting-rules/{id:long}/submit"), HasPermission(Permissions.AccountingRulesManage)]
    public async Task<ActionResult<PostingRuleDto>> SubmitRule(long id, RuleDecisionRequest request, CancellationToken ct) => await rules.SubmitAsync(id, request, ct);

    [HttpPost("posting-rules/{id:long}/approve"), HasPermission(Permissions.AccountingRulesApprove)]
    public async Task<ActionResult<PostingRuleDto>> ApproveRule(long id, RuleDecisionRequest request, CancellationToken ct) => await rules.ApproveAsync(id, request, ct);

    [HttpPost("posting-rules/{id:long}/reject"), HasPermission(Permissions.AccountingRulesApprove)]
    public async Task<ActionResult<PostingRuleDto>> RejectRule(long id, RuleDecisionRequest request, CancellationToken ct) => await rules.RejectAsync(id, request, ct);

    [HttpPost("posting-rules/{id:long}/retire"), HasPermission(Permissions.AccountingRulesApprove)]
    public async Task<ActionResult<PostingRuleDto>> RetireRule(long id, RuleDecisionRequest request, CancellationToken ct) => await rules.RetireAsync(id, request, ct);

    [HttpGet("postings"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<PagedResult<PostingRequestDto>>> Postings([FromQuery] PostingQueueRequest request, CancellationToken ct) => await engine.ListAsync(request, ct);

    [HttpPost("postings/run"), HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<ActionResult<PostingRunResultDto>> RunPostings([FromQuery] bool retryFailed = false, [FromQuery] long? projectId = null, CancellationToken ct = default) =>
        await engine.RunAsync(retryFailed, projectId, ct);

    [HttpPost("postings/{id:long}/retry"), HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<ActionResult<PostingRequestDto>> RetryPosting(long id, CancellationToken ct) => await engine.RetryAsync(id, ct);

    [HttpPost("postings/{id:long}/skip"), HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<ActionResult<PostingRequestDto>> SkipPosting(long id, SkipPostingRequest request, CancellationToken ct) => await engine.SkipAsync(id, request, ct);

    // ----- reports -----
    [HttpGet("trial-balance"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<TrialBalanceDto>> TrialBalance([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] long? projectId, [FromQuery] long? costCenterId, CancellationToken ct) =>
        await reports.TrialBalanceAsync(new ReportFilter(from, to, projectId, costCenterId), ct);

    [HttpGet("general-ledger"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<GeneralLedgerDto>> GeneralLedger([FromQuery] long accountId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] long? projectId,
        [FromQuery] long? costCenterId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        await reports.GeneralLedgerAsync(new GeneralLedgerRequest(accountId, from, to, projectId, costCenterId, page, pageSize), ct);

    [HttpGet("cashbook/{cashAccountId:long}"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<GeneralLedgerDto>> Cashbook(long cashAccountId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, CancellationToken ct = default) =>
        await reports.CashbookAsync(cashAccountId, from, to, page, ct);

    [HttpGet("bankbook/{bankAccountId:long}"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<GeneralLedgerDto>> Bankbook(long bankAccountId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, CancellationToken ct = default) =>
        await reports.BankbookAsync(bankAccountId, from, to, page, ct);

    [HttpGet("journal-register"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<JournalRegisterDto>> JournalRegister([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] JournalSource? source, [FromQuery] int page = 1, CancellationToken ct = default) =>
        await reports.JournalRegisterAsync(from, to, source, page, 20, ct);

    [HttpGet("balance-sheet"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<BalanceSheetDto>> BalanceSheet([FromQuery] DateOnly? asOf, [FromQuery] long? projectId, CancellationToken ct) => await reports.BalanceSheetAsync(asOf, projectId, ct);

    [HttpGet("income-statement"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<IncomeStatementDto>> IncomeStatement([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] long? projectId, CancellationToken ct) =>
        await reports.IncomeStatementAsync(from, to, projectId, ct);

    [HttpGet("cash-flow"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<CashFlowDto>> CashFlow([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] long? projectId, CancellationToken ct) =>
        await reports.CashFlowAsync(from, to, projectId, ct);

    [HttpGet("accounts-receivable"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<AccountsReceivableDto>> Receivables([FromQuery] DateOnly? asOf, [FromQuery] long? projectId, CancellationToken ct) => await reports.AccountsReceivableAsync(asOf, projectId, ct);

    [HttpGet("accounts-payable"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<AccountsPayableDto>> Payables([FromQuery] long? projectId, CancellationToken ct) => await reports.AccountsPayableAsync(projectId, ct);

    [HttpGet("project-profitability"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<ProjectProfitabilityDto>> ProjectProfitability([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        await reports.ProjectProfitabilityAsync(from, to, ct);

    [HttpGet("projects/{projectId:long}/expense-statement"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<ProjectExpenseStatementDto>> ProjectExpenses(long projectId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        await reports.ProjectExpenseStatementAsync(projectId, from, to, ct);

    [HttpGet("customer-control-reconciliation"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<ControlReconciliationDto>> CustomerControl([FromQuery] DateOnly? asOf, [FromQuery] long? projectId, CancellationToken ct) =>
        await reports.CustomerControlReconciliationAsync(asOf, projectId, ct);

    [HttpGet("project-reconciliation"), HasPermission(Permissions.AccountingReports)]
    public async Task<ActionResult<ProjectReconciliationDto>> ProjectReconciliation([FromQuery] DateOnly? asOf, CancellationToken ct) => await reports.ProjectReconciliationAsync(asOf, ct);

    // ----- banking -----
    /// <summary>Bank reconciliation: the workspace of one bank account (unmatched statement lines and book entries, current reconciliation, history),
    /// or the list of bank accounts with their unmatched counts when no account is given.</summary>
    [HttpGet("bank-reconciliation"), HasPermission(Permissions.AccountingView)]
    public async Task<IActionResult> BankReconciliation([FromQuery] long? bankAccountId, CancellationToken ct) =>
        bankAccountId is { } id ? Ok(await banking.WorkspaceAsync(id, ct)) : Ok(await banking.ListBankAccountsAsync(false, ct));

    [HttpGet("bank-reconciliation/history"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<BankReconciliationDto>>> ReconciliationHistory([FromQuery] long? bankAccountId, CancellationToken ct) => Ok(await banking.HistoryAsync(bankAccountId, ct));

    [HttpPost("bank-accounts/{id:long}/statements"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<ImportStatementResultDto>> ImportStatement(long id, ImportStatementRequest request, CancellationToken ct) => await banking.ImportAsync(id, request, ct);

    [HttpPost("bank-accounts/{id:long}/statements/csv"), HasPermission(Permissions.AccountingBankReconcile), RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<ImportStatementResultDto>> ImportStatementCsv(long id, IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return await banking.ImportAsync(id, new ImportStatementRequest(BankingService.ParseCsv(s)), ct);
    }

    [HttpPost("bank-accounts/{id:long}/auto-match"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<int>> AutoMatch(long id, CancellationToken ct) => await banking.AutoMatchAsync(id, ct);

    [HttpPost("bank-accounts/{id:long}/match"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<BankTransactionDto>> Match(long id, MatchRequest request, CancellationToken ct) => await banking.MatchAsync(id, request, ct);

    [HttpPost("bank-accounts/{id:long}/transactions/{transactionId:long}/unmatch"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<BankTransactionDto>> Unmatch(long id, long transactionId, CancellationToken ct) => await banking.UnmatchAsync(id, transactionId, ct);

    [HttpPost("bank-accounts/{id:long}/transactions/{transactionId:long}/ignore"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<BankTransactionDto>> Ignore(long id, long transactionId, IgnoreTransactionRequest request, CancellationToken ct) => await banking.IgnoreAsync(id, transactionId, request, ct);

    [HttpPost("bank-accounts/{id:long}/reconciliations"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<BankReconciliationDto>> StartReconciliation(long id, StartReconciliationRequest request, CancellationToken ct) => await banking.StartAsync(id, request, ct);

    [HttpPost("bank-accounts/{id:long}/reconciliations/{reconciliationId:long}/complete"), HasPermission(Permissions.AccountingBankReconcile)]
    public async Task<ActionResult<BankReconciliationDto>> CompleteReconciliation(long id, long reconciliationId, [FromQuery] string? rowVersion, CancellationToken ct) =>
        await banking.CompleteAsync(id, reconciliationId, rowVersion, ct);

    [HttpGet("bank-accounts"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<BankAccountDto>>> BankAccounts(CancellationToken ct) => Ok(await banking.ListBankAccountsAsync(false, ct));

    [HttpPost("bank-accounts"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<BankAccountDto>> CreateBankAccount(SaveBankAccountRequest request, CancellationToken ct) => await banking.SaveBankAccountAsync(null, request, ct);

    [HttpPut("bank-accounts/{id:long}"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<BankAccountDto>> UpdateBankAccount(long id, SaveBankAccountRequest request, CancellationToken ct) => await banking.SaveBankAccountAsync(id, request, ct);

    [HttpGet("cash-accounts"), HasPermission(Permissions.AccountingView)]
    public async Task<ActionResult<IReadOnlyList<CashAccountDto>>> CashAccounts(CancellationToken ct) => Ok(await banking.ListCashAccountsAsync(false, ct));

    [HttpPost("cash-accounts"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<CashAccountDto>> CreateCashAccount(SaveCashAccountRequest request, CancellationToken ct) => await banking.SaveCashAccountAsync(null, request, ct);

    [HttpPut("cash-accounts/{id:long}"), HasPermission(Permissions.AccountingSetup)]
    public async Task<ActionResult<CashAccountDto>> UpdateCashAccount(long id, SaveCashAccountRequest request, CancellationToken ct) => await banking.SaveCashAccountAsync(id, request, ct);
}
