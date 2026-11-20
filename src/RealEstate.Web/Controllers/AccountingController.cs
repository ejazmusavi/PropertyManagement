using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Accounting;
using RealEstate.Application.Banking;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Accounting dashboard, account groups, chart of accounts and cost centers.</summary>
[HasPermission(Permissions.AccountingView)]
public class AccountingController(ChartOfAccountsService chart, AccountingReportService reports, PostingEngineService engine, AccountingSupport support) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(new AccountingDashboardViewModel { Dashboard = await reports.DashboardAsync(engine, ct) });

    // ----- account groups -----
    public async Task<IActionResult> Groups(long? edit, CancellationToken ct)
    {
        var groups = await chart.ListGroupsAsync(ct);
        var vm = new AccountGroupsViewModel { Groups = groups };
        if (edit is { } id && groups.FirstOrDefault(g => g.Id == id) is { } g)
            vm.Form = new AccountGroupFormViewModel
            {
                Id = g.Id, Code = g.Code, Name = g.Name, ParentGroupId = g.ParentGroupId, Classification = g.Classification, CashFlowSection = g.CashFlowSection, SortOrder = g.SortOrder,
                Description = g.Description, IsActive = g.IsActive, IsSystem = g.IsSystem, RowVersion = g.RowVersion,
            };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> SaveGroup([Bind(Prefix = "Form")] AccountGroupFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var r = new SaveAccountGroupRequest(m.Code, m.Name, m.ParentGroupId, m.Classification, m.CashFlowSection, m.SortOrder, m.Description, m.IsActive, m.RowVersion);
                if (m.Id is { } id) await chart.UpdateGroupAsync(id, r, ct); else await chart.CreateGroupAsync(r, ct);
                TempData["Success"] = "Account group saved.";
                return RedirectToAction(nameof(Groups));
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The group was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Groups", new AccountGroupsViewModel { Groups = await chart.ListGroupsAsync(ct), Form = m });
    }

    // ----- chart of accounts -----
    public async Task<IActionResult> Accounts(string? search, long? groupId, AccountNature? nature, bool? active, int page = 1, CancellationToken ct = default)
    {
        var r = new AccountListRequest { Search = search, GroupId = groupId, Nature = nature, Active = active, Page = page, PageSize = 100 };
        var list = await chart.ListAccountsAsync(r, ct);
        return View(new AccountsViewModel
        {
            Accounts = list.Items, Groups = await chart.ListGroupsAsync(ct), Search = search, GroupId = groupId, Nature = nature, Active = active, Page = list.Page, TotalPages = list.TotalPages,
            TotalCount = list.TotalCount,
        });
    }

    public async Task<IActionResult> AccountDetails(long id, CancellationToken ct) => View(await chart.GetAccountAsync(id, ct));

    [HttpGet, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> CreateAccount(long? groupId, long? parentId, CancellationToken ct) =>
        View("AccountForm", await AccountListsAsync(new AccountFormViewModel { AccountGroupId = groupId ?? 0, ParentAccountId = parentId }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> CreateAccount(AccountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var a = await chart.CreateAccountAsync(new SaveAccountRequest(m.Code, m.Name, m.AccountGroupId, m.ParentAccountId, m.AllowManualEntries, m.Description, m.IsActive), ct);
                TempData["Success"] = $"Account {a.Account.Code} created.";
                return RedirectToAction(nameof(AccountDetails), new { id = a.Account.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("AccountForm", await AccountListsAsync(m, ct));
    }

    [HttpGet, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> EditAccount(long id, CancellationToken ct)
    {
        var a = (await chart.GetAccountAsync(id, ct)).Account;
        return View("AccountForm", await AccountListsAsync(new AccountFormViewModel
        {
            Id = a.Id, Code = a.Code, Name = a.Name, AccountGroupId = a.AccountGroupId, ParentAccountId = a.ParentAccountId, AllowManualEntries = a.AllowManualEntries, Description = a.Description,
            IsActive = a.IsActive, RowVersion = a.RowVersion,
        }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> EditAccount(long id, AccountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await chart.UpdateAccountAsync(id, new SaveAccountRequest(m.Code, m.Name, m.AccountGroupId, m.ParentAccountId, m.AllowManualEntries, m.Description, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "Account updated.";
                return RedirectToAction(nameof(AccountDetails), new { id });
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The account was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("AccountForm", await AccountListsAsync(m, ct));
    }

    private async Task<AccountFormViewModel> AccountListsAsync(AccountFormViewModel m, CancellationToken ct)
    {
        m.Groups = (await chart.ListGroupsAsync(ct)).Where(g => g.Depth > 0 && (g.IsActive || g.Id == m.AccountGroupId)).ToList();
        m.Parents = (await chart.AllAccountsAsync(ct)).Where(a => a.Id != m.Id).ToList();
        return m;
    }

    // ----- cost centers -----
    public async Task<IActionResult> CostCenters(long? edit, CancellationToken ct)
    {
        var items = await chart.ListCostCentersAsync(false, ct);
        var vm = new CostCentersViewModel { Items = items, Projects = await support.ProjectOptionsAsync(ct) };
        if (edit is { } id && items.FirstOrDefault(c => c.Id == id) is { } c)
            vm.Form = new CostCenterFormViewModel { Id = c.Id, Code = c.Code, Name = c.Name, ProjectId = c.ProjectId, Description = c.Description, IsActive = c.IsActive, RowVersion = c.RowVersion };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> SaveCostCenter([Bind(Prefix = "Form")] CostCenterFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await chart.SaveCostCenterAsync(m.Id, new SaveCostCenterRequest(m.Code, m.Name, m.ProjectId, m.Description, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "Cost center saved.";
                return RedirectToAction(nameof(CostCenters));
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The cost center was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("CostCenters", new CostCentersViewModel { Items = await chart.ListCostCentersAsync(false, ct), Projects = await support.ProjectOptionsAsync(ct), Form = m });
    }
}

/// <summary>Journal entries: list, approval queue, manual journal create/edit, details with submit/approve/reject/cancel/reverse, fund transfers and cost allocations.</summary>
[HasPermission(Permissions.AccountingView)]
public class JournalsController(JournalService journals, ChartOfAccountsService chart, AccountingSupport support, BankingService banking, ICurrentUser current) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(JournalListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new JournalListViewModel { Filter = filter, Journals = await journals.ListAsync(filter, ct), Projects = await support.ProjectOptionsAsync(ct) });
    }

    [HasPermission(Permissions.AccountingJournalsApprove)]
    public async Task<IActionResult> Approvals(JournalListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20; filter.Status = JournalStatus.PendingApproval;
        return View("Index", new JournalListViewModel
        {
            Filter = filter, Journals = await journals.ListAsync(filter, ct), Projects = await support.ProjectOptionsAsync(ct), Title = "Journal approvals", ApprovalQueue = true, CurrentUser = current.UserName,
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var j = await journals.GetAsync(id, ct);
        return View(new JournalDetailsViewModel
        {
            Journal = j, Today = await support.TodayAsync(ct),
            IsMaker = string.Equals(j.CreatedBy, current.UserName, StringComparison.OrdinalIgnoreCase) || string.Equals(j.SubmittedBy, current.UserName, StringComparison.OrdinalIgnoreCase),
        });
    }

    [HttpGet, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Create(CancellationToken ct) =>
        View("Form", new JournalFormViewModel { EntryDate = await support.TodayAsync(ct), Lookups = await LookupsAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Create(JournalFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var j = await journals.CreateAsync(new CreateJournalRequest(m.EntryDate, m.Narration, m.Reference, Lines(m), m.Submit, m.IdempotencyKey), ct);
                TempData["Success"] = m.Submit ? $"Journal {j.EntryNumber} submitted for approval." : $"Journal {j.EntryNumber} saved as draft.";
                return RedirectToAction(nameof(Details), new { id = j.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Lookups = await LookupsAsync(ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var j = await journals.GetAsync(id, ct);
        if (j.Source != JournalSource.Manual || j.Status is not (JournalStatus.Draft or JournalStatus.Rejected))
        {
            TempData["Error"] = "Only draft or rejected manual journals can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return View("Form", new JournalFormViewModel
        {
            Id = j.Id, EntryDate = j.EntryDate, Narration = j.Narration, Reference = j.Reference, RowVersion = j.RowVersion, Lookups = await LookupsAsync(ct),
            Lines = j.Lines.Select(l => new JournalLineForm { AccountId = l.AccountId, ProjectId = l.ProjectId, CostCenterId = l.CostCenterId, Debit = l.Debit, Credit = l.Credit, Description = l.Description }).ToList(),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Edit(long id, JournalFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var j = await journals.UpdateAsync(id, new UpdateJournalRequest(m.EntryDate, m.Narration, m.Reference, Lines(m), m.RowVersion), ct);
                if (m.Submit) await journals.SubmitAsync(id, new SubmitJournalRequest(j.RowVersion), ct);
                TempData["Success"] = m.Submit ? "Journal updated and submitted for approval." : "Journal updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The journal was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id; m.Lookups = await LookupsAsync(ct);
        return View("Form", m);
    }

    private static IReadOnlyList<JournalLineInput> Lines(JournalFormViewModel m) =>
        m.Lines.Where(l => l.AccountId is not null || l.Debit != 0m || l.Credit != 0m)
            .Select(l => new JournalLineInput(l.AccountId ?? 0, l.Debit, l.Credit, l.ProjectId, l.CostCenterId, l.Description)).ToList();

    private async Task<JournalLookups> LookupsAsync(CancellationToken ct) => new()
    {
        Accounts = await chart.PostableOptionsAsync(null, ct), Projects = await support.ProjectOptionsAsync(ct),
        CostCenters = (await chart.ListCostCentersAsync(true, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList(), CompanyLevelAllowed = await support.HasAllAccessAsync(ct),
    };

    // ----- workflow -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public Task<IActionResult> Submit(long id, string? rowVersion, CancellationToken ct) =>
        Act(id, () => journals.SubmitAsync(id, new SubmitJournalRequest(rowVersion), ct), "Journal submitted for approval.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsApprove)]
    public Task<IActionResult> Approve(long id, string? comments, string? rowVersion, bool postToClosedPeriod, string? closedPeriodReason, CancellationToken ct) =>
        Act(id, () => journals.ApproveAsync(id, new ApproveJournalRequest(comments, rowVersion, postToClosedPeriod, closedPeriodReason), ct), "Journal approved and posted to the general ledger.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsApprove)]
    public Task<IActionResult> Reject(long id, string comments, string? rowVersion, CancellationToken ct) =>
        Act(id, () => journals.RejectAsync(id, new RejectJournalRequest(comments ?? "", rowVersion), ct), "Journal rejected. The maker can edit and resubmit it.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public Task<IActionResult> Cancel(long id, string reason, string? rowVersion, CancellationToken ct) =>
        Act(id, () => journals.CancelAsync(id, new CancelJournalRequest(reason ?? "", rowVersion), ct), "Journal cancelled.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsReverse)]
    public async Task<IActionResult> Reverse(long id, DateOnly? reversalDate, string reason, bool postToClosedPeriod, string? closedPeriodReason, CancellationToken ct)
    {
        try
        {
            var rv = await journals.ReverseAsync(id, new ReverseJournalRequest(reversalDate, reason ?? "", postToClosedPeriod, closedPeriodReason), ct);
            TempData["Success"] = $"Journal reversed by {rv.VoucherNumber}.";
            return RedirectToAction(nameof(Details), new { id = rv.Id });
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> Act(long id, Func<Task<JournalDetailDto>> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- fund transfers -----
    [HttpGet, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Transfer(CancellationToken ct) => View(await TransferListsAsync(new TransferFormViewModel { TransferDate = await support.TodayAsync(ct) }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Transfer(TransferFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var j = await journals.CreateTransferAsync(new CreateTransferRequest(m.FromAccountId, m.ToAccountId, m.Amount, m.TransferDate, m.Narration, m.Reference, m.ProjectId, true, m.IdempotencyKey), ct);
                TempData["Success"] = $"Transfer {j.EntryNumber} submitted for approval.";
                return RedirectToAction(nameof(Details), new { id = j.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await TransferListsAsync(m, ct));
    }

    private async Task<TransferFormViewModel> TransferListsAsync(TransferFormViewModel m, CancellationToken ct)
    {
        var all = await support.HasAllAccessAsync(ct);
        m.CompanyLevelAllowed = all;
        m.Projects = await support.ProjectOptionsAsync(ct);
        if (all)
        {
            var banks = (await banking.ListBankAccountsAsync(true, ct)).Select(b => new OptionDto(b.GlAccountId, b.Code, "Bank: " + b.Name));
            var cash = (await banking.ListCashAccountsAsync(true, ct)).Select(c => new OptionDto(c.GlAccountId, c.Code, "Cash: " + c.Name));
            m.MoneyAccounts = banks.Concat(cash).ToList();
        }
        return m;
    }

    // ----- cost allocations -----
    public async Task<IActionResult> Allocations(CancellationToken ct) => View(new AllocationsViewModel { Items = await journals.ListAllocationsAsync(null, ct) });

    [HttpGet, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Allocate(CancellationToken ct) => View(await AllocationListsAsync(new CostAllocationFormViewModel { AllocationDate = await support.TodayAsync(ct) }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingJournalsManage)]
    public async Task<IActionResult> Allocate(CostAllocationFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var lines = m.Lines.Where(l => l.ProjectId is not null).Select(l => new CostAllocationLineInput(l.ProjectId!.Value, l.Percentage)).ToList();
                var a = await journals.CreateAllocationAsync(new CreateCostAllocationRequest(m.AllocationDate, m.AccountId, m.SourceProjectId, null, m.Amount, m.Basis, lines), ct);
                TempData["Success"] = $"Cost allocation {a.AllocationNumber} submitted for approval.";
                return RedirectToAction(nameof(Details), new { id = a.JournalEntryId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await AllocationListsAsync(m, ct));
    }

    private async Task<CostAllocationFormViewModel> AllocationListsAsync(CostAllocationFormViewModel m, CancellationToken ct)
    {
        m.ExpenseAccounts = (await chart.AllAccountsAsync(ct)).Where(a => a.Nature == AccountNature.Expense && a.IsPostable && a.IsActive).Select(a => new OptionDto(a.Id, a.Code, a.Name)).ToList();
        m.Projects = await support.ProjectOptionsAsync(ct);
        m.CompanyLevelAllowed = await support.HasAllAccessAsync(ct);
        return m;
    }
}

/// <summary>Posting rules (configurable accounting policy with maker-checker approval) and the automatic posting queue.</summary>
[HasPermission(Permissions.AccountingView)]
public class PostingRulesController(PostingRuleService rules, PostingEngineService engine, ChartOfAccountsService chart, AccountingSupport support, ICurrentUser current) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(PostingRuleListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 25;
        return View(new PostingRulesViewModel { Filter = filter, Rules = await rules.ListAsync(filter, ct), Catalog = rules.Catalog() });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var r = await rules.GetAsync(id, ct);
        return View(new PostingRuleDetailsViewModel
        {
            Rule = r, Event = rules.Catalog().First(c => c.EventType == r.EventType),
            IsMaker = string.Equals(r.CreatedBy, current.UserName, StringComparison.OrdinalIgnoreCase) || string.Equals(r.SubmittedBy, current.UserName, StringComparison.OrdinalIgnoreCase),
        });
    }

    [HttpGet, HasPermission(Permissions.AccountingRulesManage)]
    public async Task<IActionResult> Create(AccountingEventType? eventType, CancellationToken ct) =>
        View("Form", await ListsAsync(new PostingRuleFormViewModel { EventType = eventType ?? AccountingEventType.PaymentPosted, EffectiveFrom = await support.TodayAsync(ct) }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesManage)]
    public async Task<IActionResult> Create(PostingRuleFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var r = await rules.CreateAsync(BuildRequest(m), ct);
                TempData["Success"] = "Posting rule saved as draft. Submit it for policy approval.";
                return RedirectToAction(nameof(Details), new { id = r.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", await ListsAsync(m, ct));
    }

    [HttpGet, HasPermission(Permissions.AccountingRulesManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var r = await rules.GetAsync(id, ct);
        if (r.Status is not (PostingRuleStatus.Draft or PostingRuleStatus.Rejected)) { TempData["Error"] = "Only draft or rejected rules can be edited."; return RedirectToAction(nameof(Details), new { id }); }
        return View("Form", await ListsAsync(new PostingRuleFormViewModel
        {
            Id = r.Id, Name = r.Name, EventType = r.EventType, ProjectId = r.ProjectId, ConditionField = r.ConditionField, ConditionValue = r.ConditionValue, Priority = r.Priority,
            EffectiveFrom = r.EffectiveFrom, Description = r.Description, RowVersion = r.RowVersion,
            Lines = r.Lines.Select(l => new RuleLineForm { Side = l.Side, AmountKey = l.AmountKey, AccountId = l.AccountId, AccountKey = l.AccountKey, CostCenterId = l.CostCenterId, Description = l.Description }).ToList(),
        }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesManage)]
    public async Task<IActionResult> Edit(long id, PostingRuleFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await rules.UpdateAsync(id, BuildRequest(m), ct);
                TempData["Success"] = "Posting rule updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The rule was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("Form", await ListsAsync(m, ct));
    }

    private static SavePostingRuleRequest BuildRequest(PostingRuleFormViewModel m) =>
        new(m.Name, m.EventType, m.ProjectId, string.IsNullOrWhiteSpace(m.ConditionField) ? null : m.ConditionField, m.ConditionValue, m.Priority, m.EffectiveFrom, m.Description,
            m.Lines.Where(l => l.AccountId is not null || !string.IsNullOrWhiteSpace(l.AccountKey))
                .Select(l => new PostingRuleLineInput(l.Side, l.AmountKey ?? "", l.AccountId, string.IsNullOrWhiteSpace(l.AccountKey) ? null : l.AccountKey, l.CostCenterId, l.Description)).ToList(),
            m.RowVersion);

    private async Task<PostingRuleFormViewModel> ListsAsync(PostingRuleFormViewModel m, CancellationToken ct)
    {
        m.Catalog = rules.Catalog().Where(c => !c.IsReversal).ToList();
        m.Lookups = new JournalLookups
        {
            Accounts = await chart.PostableOptionsAsync(null, ct), Projects = await support.ProjectOptionsAsync(ct),
            CostCenters = (await chart.ListCostCentersAsync(true, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList(), CompanyLevelAllowed = await support.HasAllAccessAsync(ct),
        };
        return m;
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesManage)]
    public Task<IActionResult> Submit(long id, string? rowVersion, CancellationToken ct) =>
        Act(id, () => rules.SubmitAsync(id, new RuleDecisionRequest(null, rowVersion), ct), "Rule submitted for policy approval.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesApprove)]
    public Task<IActionResult> Approve(long id, string? comments, string? rowVersion, CancellationToken ct) =>
        Act(id, () => rules.ApproveAsync(id, new RuleDecisionRequest(comments, rowVersion), ct), "Rule approved; it now posts matching events.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesApprove)]
    public Task<IActionResult> Reject(long id, string comments, string? rowVersion, CancellationToken ct) =>
        Act(id, () => rules.RejectAsync(id, new RuleDecisionRequest(comments, rowVersion), ct), "Rule rejected.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingRulesApprove)]
    public Task<IActionResult> Retire(long id, string comments, string? rowVersion, CancellationToken ct) =>
        Act(id, () => rules.RetireAsync(id, new RuleDecisionRequest(comments, rowVersion), ct), "Rule retired.");

    private async Task<IActionResult> Act(long id, Func<Task<PostingRuleDto>> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- automatic posting queue -----
    public async Task<IActionResult> Queue(PostingQueueRequest filter, CancellationToken ct)
    {
        filter.PageSize = 25;
        return View(new PostingQueueViewModel
        {
            Filter = filter, Items = await engine.ListAsync(filter, ct), Summary = await engine.SummaryAsync(ct), Projects = await support.ProjectOptionsAsync(ct),
            CanProcess = await support.HasAsync(Permissions.AccountingPostingsProcess, ct) && await support.HasAllAccessAsync(ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<IActionResult> Run(bool retryFailed, CancellationToken ct)
    {
        try
        {
            var r = await engine.RunAsync(retryFailed, null, ct);
            TempData["Success"] = $"Posting run finished: {r.Fed} new events queued, {r.Processed} processed, {r.Posted} posted, {r.Failed} failed, {r.Skipped} not posted.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Queue));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<IActionResult> Retry(long id, CancellationToken ct)
    {
        try
        {
            var r = await engine.RetryAsync(id, ct);
            if (r.Status == AccountingPostingStatus.Posted) TempData["Success"] = $"Event posted ({r.JournalNumber})."; else TempData["Error"] = r.LastError ?? "The event was not posted.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Queue), new { status = AccountingPostingStatus.Failed });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPostingsProcess)]
    public async Task<IActionResult> Skip(long id, string reason, CancellationToken ct)
    {
        try { await engine.SkipAsync(id, new SkipPostingRequest(reason ?? ""), ct); TempData["Success"] = "Event marked as not posted."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Queue), new { status = AccountingPostingStatus.Failed });
    }
}

/// <summary>Financial years, accounting periods and period locking.</summary>
[HasPermission(Permissions.AccountingView)]
public class FiscalYearsController(FiscalPeriodService periods, AccountingSupport support) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var years = await periods.ListAsync(ct);
        var next = years.Count > 0 ? years.Max(y => y.EndDate).AddDays(1) : await SuggestedStartAsync(ct);
        return View(new FiscalYearsViewModel { Years = years, Form = new FiscalYearFormViewModel { StartDate = next, EndDate = next.AddYears(1).AddDays(-1), Name = $"FY {next.Year}-{(next.Year + 1) % 100:D2}" } });
    }

    private async Task<DateOnly> SuggestedStartAsync(CancellationToken ct)
    {
        var today = await support.TodayAsync(ct);
        var month = Math.Clamp(await support.FiscalStartMonthAsync(ct), 1, 12);
        var year = today.Month >= month ? today.Year : today.Year - 1;
        return new DateOnly(year, month, 1);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] FiscalYearFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var y = await periods.CreateAsync(new CreateFiscalYearRequest(m.Name, m.StartDate, m.EndDate), ct);
                TempData["Success"] = $"Fiscal year {y.Name} created with {y.Periods.Count} periods.";
                return RedirectToAction(nameof(Details), new { id = y.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Index", new FiscalYearsViewModel { Years = await periods.ListAsync(ct), Form = m });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) =>
        View(new FiscalYearDetailsViewModel { Year = await periods.GetAsync(id, ct), History = await periods.LockHistoryAsync(id, 1, 100, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<IActionResult> ChangePeriod(long id, long periodId, PeriodLockAction periodAction, string reason, string? rowVersion, CancellationToken ct)
    {
        try { var p = await periods.ChangePeriodAsync(periodId, new PeriodActionRequest(periodAction, reason ?? "", rowVersion), ct); TempData["Success"] = $"Period {p.Name} is now {p.Status.Label()}."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingPeriodsManage)]
    public async Task<IActionResult> Close(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try { await periods.CloseYearAsync(id, new CloseFiscalYearRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Fiscal year closed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Bank and cash accounts and bank reconciliation.</summary>
[HasPermission(Permissions.AccountingView)]
public class BankingController(BankingService banking, ChartOfAccountsService chart, AccountingSupport support) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new MoneyAccountsViewModel { Banks = await banking.ListBankAccountsAsync(false, ct), Cash = await banking.ListCashAccountsAsync(false, ct), CurrencySymbol = await support.CurrencyAsync(ct) });

    [HttpGet, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> BankForm(long? id, CancellationToken ct)
    {
        var m = new BankAccountFormViewModel();
        if (id is { } bid)
        {
            var b = await banking.GetBankAccountAsync(bid, ct);
            m = new BankAccountFormViewModel
            {
                Id = b.Id, Code = b.Code, Name = b.Name, BankName = b.BankName, Branch = b.Branch, AccountTitle = b.AccountTitle, AccountNumber = b.AccountNumber, Iban = b.Iban,
                CurrencyCode = b.CurrencyCode, GlAccountId = b.GlAccountId, ProjectId = b.ProjectId, IsActive = b.IsActive, RowVersion = b.RowVersion,
            };
        }
        return View(await BankListsAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> BankForm(BankAccountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await banking.SaveBankAccountAsync(m.Id, new SaveBankAccountRequest(m.Code, m.Name, m.BankName, m.Branch, m.AccountTitle, m.AccountNumber, m.Iban, m.CurrencyCode, m.GlAccountId, m.ProjectId, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "Bank account saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The bank account was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await BankListsAsync(m, ct));
    }

    private async Task<BankAccountFormViewModel> BankListsAsync(BankAccountFormViewModel m, CancellationToken ct)
    {
        m.LedgerAccounts = await chart.PostableOptionsAsync([AccountClassification.Bank], ct);
        m.Projects = await support.ProjectOptionsAsync(ct);
        return m;
    }

    [HttpGet, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> CashForm(long? id, CancellationToken ct)
    {
        var m = new CashAccountFormViewModel();
        if (id is { } cid && (await banking.ListCashAccountsAsync(false, ct)).FirstOrDefault(c => c.Id == cid) is { } c)
            m = new CashAccountFormViewModel { Id = c.Id, Code = c.Code, Name = c.Name, Custodian = c.Custodian, GlAccountId = c.GlAccountId, ProjectId = c.ProjectId, IsActive = c.IsActive, RowVersion = c.RowVersion };
        return View(await CashListsAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingSetup)]
    public async Task<IActionResult> CashForm(CashAccountFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await banking.SaveCashAccountAsync(m.Id, new SaveCashAccountRequest(m.Code, m.Name, m.Custodian, m.GlAccountId, m.ProjectId, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "Cash account saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The cash account was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await CashListsAsync(m, ct));
    }

    private async Task<CashAccountFormViewModel> CashListsAsync(CashAccountFormViewModel m, CancellationToken ct)
    {
        m.LedgerAccounts = await chart.PostableOptionsAsync([AccountClassification.Cash], ct);
        m.Projects = await support.ProjectOptionsAsync(ct);
        return m;
    }

    // ----- reconciliation -----
    public async Task<IActionResult> Reconcile(long id, CancellationToken ct) => View(new ReconcileViewModel { Workspace = await banking.WorkspaceAsync(id, ct), Today = await support.TodayAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile), RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Import(long id, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a CSV statement file."; return RedirectToAction(nameof(Reconcile), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            var r = await banking.ImportAsync(id, new ImportStatementRequest(BankingService.ParseCsv(s)), ct);
            TempData["Success"] = $"Statement imported: {r.Imported} new lines, {r.Duplicates} duplicates skipped, {r.AutoMatched} matched automatically.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Reconcile), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> AutoMatch(long id, CancellationToken ct) => Act(id, async () => TempData["Success"] = $"{await banking.AutoMatchAsync(id, ct)} lines matched automatically.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> Match(long id, long bankTransactionId, long ledgerEntryId, CancellationToken ct) =>
        Act(id, async () => { await banking.MatchAsync(id, new MatchRequest(bankTransactionId, ledgerEntryId), ct); TempData["Success"] = "Statement line matched."; });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> Unmatch(long id, long transactionId, CancellationToken ct) =>
        Act(id, async () => { await banking.UnmatchAsync(id, transactionId, ct); TempData["Success"] = "Match removed."; });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> Ignore(long id, long transactionId, string reason, CancellationToken ct) =>
        Act(id, async () => { await banking.IgnoreAsync(id, transactionId, new IgnoreTransactionRequest(reason ?? ""), ct); TempData["Success"] = "Statement line ignored."; });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> Start(long id, DateOnly statementDate, decimal statementBalance, string? notes, CancellationToken ct) =>
        Act(id, async () =>
        {
            var r = await banking.StartAsync(id, new StartReconciliationRequest(statementDate, statementBalance, notes), ct);
            TempData[r.Difference == 0m ? "Success" : "Error"] = r.Difference == 0m ? "Reconciliation balances. Complete it to keep it in the history." : $"Difference of {r.Difference:N2} remains.";
        });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.AccountingBankReconcile)]
    public Task<IActionResult> Complete(long id, long reconciliationId, string? rowVersion, CancellationToken ct) =>
        Act(id, async () => { await banking.CompleteAsync(id, reconciliationId, rowVersion, ct); TempData["Success"] = "Reconciliation completed."; });

    private async Task<IActionResult> Act(long id, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Reconcile), new { id });
    }
}
