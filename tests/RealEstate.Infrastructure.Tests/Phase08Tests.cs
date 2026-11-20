using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Accounting;
using RealEstate.Application.Administration;
using RealEstate.Application.Banking;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Expenses;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>
/// Phase 8 integration tests against MySQL: chart of accounts, manual journals (maker-checker, reversal, concurrency, idempotency), periods (close/lock/override),
/// posting rules, automatic postings from payments/expenses/ledger, control-account and project reconciliation, statements, transfers, cost allocation,
/// bank reconciliation, access and database constraints.
/// </summary>
[Collection("phase1")]
public class Phase08Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Director, Guid Accountant, Guid Approver, Guid Manager, Guid Outsider, long ProjectId, long OtherProjectId, DateOnly Today);
    private record Accounts(long Cash, long Bank, long Receivable, long Advance, long Sales, long DevRevenue, long Expense, long Vendor, long Gov, long Capital, long BankCharges);

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();
    private void As(Guid id) { fx.Act(id); lock (Names) fx.Actor.UserName = Names[id]; }
    private Guid w0 => Names.Keys.First();

    private async Task<Guid> UserAsync(params string[] roles)
    {
        var id = await fx.CreateUserAsync(roles);
        lock (Names) Names[id] = fx.Actor.UserName!;
        return id;
    }

    private async Task<T> Run<T>(Guid actor, Func<IServiceScope, Task<T>> f)
    {
        As(actor);
        using var s = fx.Scope();
        return await f(s);
    }

    private static async Task<DomainException> Fails(Func<Task> f) => await Assert.ThrowsAnyAsync<DomainException>(f);

    private async Task<World> WorldAsync()
    {
        var admin = await UserAsync(RoleNames.Administrator);
        As(admin);
        long pid, other; DateOnly today;
        using (var s = fx.Scope())
        {
            pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
            other = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        }
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        var approver = await UserAsync(RoleNames.Accountant);
        var manager = await UserAsync(RoleNames.ProjectManager);
        var outsider = await UserAsync(RoleNames.Accountant);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { accountant, approver, manager }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            foreach (var u in new[] { accountant, approver }) await Svc<ProjectAccessAdminService>(s).AssignAsync(other, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
        }
        await YearsAsync(admin, today);
        return new World(admin, director, accountant, approver, manager, outsider, pid, other, today);
    }

    /// <summary>July-June fiscal years from two years back to next year (created once per database).</summary>
    private async Task YearsAsync(Guid admin, DateOnly today)
    {
        for (var y = today.Year - 2; y <= today.Year + 1; y++)
        {
            var start = new DateOnly(y, 7, 1);
            await Run(admin, async s =>
            {
                if (await Svc<AppDbContext>(s).FiscalYears.AnyAsync(f => f.StartDate == start)) return 0;
                await Svc<FiscalPeriodService>(s).CreateAsync(new CreateFiscalYearRequest($"FY{y}-{(y + 1) % 100:D2}", start, new DateOnly(y + 1, 6, 30)));
                return 1;
            });
        }
    }

    private async Task<long> GroupAsync(string code) => await Run(w0, async s => await Svc<AppDbContext>(s).AccountGroups.Where(g => g.Code == code).Select(g => g.Id).FirstAsync());

    private async Task<long> AccountAsync(Guid actor, string group, string name, bool manual = true, long? parent = null)
    {
        var gid = await GroupAsync(group);
        var code = "T" + Interlocked.Increment(ref counter) + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return (await Run(actor, s => Svc<ChartOfAccountsService>(s).CreateAccountAsync(new SaveAccountRequest(code, name, gid, parent, manual)))).Account.Id;
    }

    private async Task<Accounts> AccountsAsync(World w) => new(
        await AccountAsync(w.Accountant, "CASH", "Cash in hand"), await AccountAsync(w.Accountant, "BANK", "Bank current"),
        await AccountAsync(w.Accountant, "CUSTOMER_RECEIVABLES", "Customer receivables", manual: false), await AccountAsync(w.Accountant, "CUSTOMER_ADVANCES", "Customer advances", manual: false),
        await AccountAsync(w.Accountant, "PROPERTY_SALES_REVENUE", "Plot sales"), await AccountAsync(w.Accountant, "DEVELOPMENT_REVENUE", "Development charges income"),
        await AccountAsync(w.Accountant, "OPERATING_EXPENSES", "Office rent"), await AccountAsync(w.Accountant, "VENDOR_PAYABLES", "Trade creditors"),
        await AccountAsync(w.Accountant, "GOVERNMENT_PAYABLES", "Government fees payable"), await AccountAsync(w.Accountant, "CAPITAL", "Owner capital"),
        await AccountAsync(w.Accountant, "ADMINISTRATIVE_EXPENSES", "Bank charges"));

    private static JournalLineInput Dr(long account, decimal amount, long? project) => new(account, amount, 0m, project);
    private static JournalLineInput Cr(long account, decimal amount, long? project) => new(account, 0m, amount, project);

    /// <summary>Creates, submits and approves (posts) a manual journal: maker = accountant, checker = approver.</summary>
    private async Task<JournalDetailDto> PostedJournalAsync(World w, DateOnly date, params JournalLineInput[] lines)
    {
        var j = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(date, "Test journal", "REF-" + Guid.NewGuid().ToString("N")[..6], lines, Submit: true)));
        return await Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest("ok", j.RowVersion)));
    }

    /// <summary>Creates a posting rule as the accountant and approves it as the director (policy approval).</summary>
    private async Task<PostingRuleDto> RuleAsync(World w, AccountingEventType type, long? projectId, string? field, string? value, params PostingRuleLineInput[] lines)
    {
        var r = await Run(w.Accountant, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest($"{type} {value}".Trim(), type, projectId, field, value, 100, new DateOnly(2000, 1, 1), null, lines)));
        r = await Run(w.Accountant, s => Svc<PostingRuleService>(s).SubmitAsync(r.Id, new RuleDecisionRequest(null, r.RowVersion)));
        return await Run(w.Director, s => Svc<PostingRuleService>(s).ApproveAsync(r.Id, new RuleDecisionRequest("approved policy", r.RowVersion)));
    }

    private static PostingRuleLineInput D(long account, string amount = "Amount") => new(EntrySide.Debit, amount, account, null);
    private static PostingRuleLineInput C(long account, string amount = "Amount") => new(EntrySide.Credit, amount, account, null);

    private async Task<PostingRunResultDto> PostEventsAsync(World w, long? projectId = null, bool retry = false) =>
        await Run(w.Director, s => Svc<PostingEngineService>(s).RunAsync(retry, projectId ?? w.ProjectId));

    // ===================================================================================== seed data and chart of accounts
    [MySqlFact]
    public async Task Seeds_AccountGroupHierarchy_OfSpec82()
    {
        var w = await WorldAsync();
        var groups = await Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).ListGroupsAsync());
        foreach (var root in new[] { "Assets", "Liabilities", "Equity", "Revenue", "Expenses" }) Assert.Contains(groups, g => g.Name == root && g.Depth == 0 && g.IsSystem);
        foreach (var n in new[] { "Cash", "Bank", "Customer receivables", "Land inventory", "Property inventory", "Fixed assets", "Other assets", "Customer advances", "Government payables",
                     "Vendor payables", "Tax liabilities", "Other liabilities", "Capital", "Retained earnings", "Reserves", "Property sales revenue", "Development-related revenue", "Other income",
                     "Operating expenses", "Construction expenses", "Marketing", "Salaries", "Administrative expenses", "Other expenses" })
            Assert.Contains(groups, g => g.Name == n && g.Depth == 1 && g.IsSystem);
        Assert.Equal(AccountNature.Liability, groups.Single(g => g.Code == "CUSTOMER_ADVANCES").Nature);
        Assert.Empty(await Run(w.Admin, async s => await Svc<AppDbContext>(s).PostingRules.Where(r => r.CreatedBy == null).ToListAsync())); // no seeded policy
    }

    [MySqlFact]
    public async Task ChartOfAccounts_Hierarchy_Validation_AndProtection()
    {
        var w = await WorldAsync();
        var parent = await AccountAsync(w.Accountant, "OPERATING_EXPENSES", "Utilities (parent)");
        var child = await AccountAsync(w.Accountant, "OPERATING_EXPENSES", "Electricity", parent: parent);
        var cash = await AccountAsync(w.Accountant, "CASH", "Petty cash");
        var all = await Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).AllAccountsAsync());
        Assert.False(all.Single(a => a.Id == parent).IsPostable);
        Assert.True(all.Single(a => a.Id == child).IsPostable);
        Assert.Equal(1, all.Single(a => a.Id == child).Depth);

        // a parent account takes no postings
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "x", null, [Dr(parent, 10m, w.ProjectId), Cr(cash, 10m, w.ProjectId)]))));
        Assert.Contains("sub-accounts", string.Join(" ", ex.Errors.SelectMany(e => e.Value)));
        // classification must match the nature; duplicate codes are rejected; the parent must have the same nature
        var assets = await GroupAsync("ASSETS"); var cashG = await GroupAsync("CASH");
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).CreateGroupAsync(
            new SaveAccountGroupRequest("BADGRP" + counter++, "Bad", assets, AccountClassification.Salaries, CashFlowSection.Operating, 1, null))));
        var code = all.Single(a => a.Id == cash).Code;
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).CreateAccountAsync(new SaveAccountRequest(code, "Dup", all.Single(a => a.Id == cash).AccountGroupId, null))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, async s => await Svc<ChartOfAccountsService>(s).CreateAccountAsync(
            new SaveAccountRequest("X" + Guid.NewGuid().ToString("N")[..8], "Wrong parent", cashG, parent))));

        // an account with a balance cannot be deactivated; project managers cannot change the chart
        var capital = await AccountAsync(w.Accountant, "CAPITAL", "Capital");
        await PostedJournalAsync(w, w.Today, Dr(cash, 1_000m, w.ProjectId), Cr(capital, 1_000m, w.ProjectId));
        var detail = await Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).GetAccountAsync(cash));
        Assert.Equal(1_000m, detail.Balance);
        await Fails(() => Run(w.Accountant, s => Svc<ChartOfAccountsService>(s).UpdateAccountAsync(cash,
            new SaveAccountRequest(detail.Account.Code, detail.Account.Name, detail.Account.AccountGroupId, null, true, null, false, detail.Account.RowVersion))));
        var cashGroup = await GroupAsync("CASH");
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<ChartOfAccountsService>(s).CreateAccountAsync(new SaveAccountRequest("PM" + counter++, "x", cashGroup, null))));
    }

    // ===================================================================================== manual journals
    [MySqlFact]
    public async Task ManualJournal_MakerChecker_Posting_Voucher_AndImmutability()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        // unbalanced journals are rejected
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(
            new CreateJournalRequest(w.Today, "x", null, [Dr(a.Cash, 100m, w.ProjectId), Cr(a.Capital, 90m, w.ProjectId)]))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(
            new CreateJournalRequest(w.Today, "x", null, [new JournalLineInput(a.Cash, 100m, 100m, w.ProjectId), Cr(a.Capital, 0m, w.ProjectId)]))));

        var j = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Capital introduced", "DEP-1", [Dr(a.Cash, 250_000m, w.ProjectId), Cr(a.Capital, 250_000m, w.ProjectId)])));
        Assert.Equal(JournalStatus.Draft, j.Status);
        Assert.StartsWith("JE-", j.EntryNumber);
        Assert.Null(j.VoucherNumber);
        j = await Run(w.Accountant, s => Svc<JournalService>(s).UpdateAsync(j.Id, new UpdateJournalRequest(w.Today, "Capital introduced (edited)", "DEP-1",
            [Dr(a.Cash, 300_000m, w.ProjectId), Cr(a.Capital, 300_000m, w.ProjectId)], j.RowVersion)));
        j = await Run(w.Accountant, s => Svc<JournalService>(s).SubmitAsync(j.Id, new SubmitJournalRequest(j.RowVersion)));
        Assert.Equal(JournalStatus.PendingApproval, j.Status);

        // the maker cannot approve; the checker posts
        var self = await Fails(() => Run(w.Accountant, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, j.RowVersion))));
        Assert.Contains("different user", self.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, j.RowVersion))));
        var posted = await Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest("checked", j.RowVersion)));
        Assert.Equal(JournalStatus.Posted, posted.Status);
        Assert.StartsWith("JV", posted.VoucherNumber);
        Assert.Equal(300_000m, posted.TotalDebit);
        Assert.Equal(posted.TotalDebit, posted.TotalCredit);
        var gl = await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.Where(e => e.JournalEntryId == j.Id).ToListAsync());
        Assert.Equal(2, gl.Count);
        Assert.All(gl, e => Assert.Equal(posted.VoucherNumber, e.VoucherNumber));
        Assert.Equal(gl.Sum(e => e.Debit), gl.Sum(e => e.Credit));

        // posted journals can neither be edited, cancelled, approved again nor deleted (no delete path exists)
        await Fails(() => Run(w.Accountant, s => Svc<JournalService>(s).UpdateAsync(j.Id, new UpdateJournalRequest(w.Today, "x", null, [Dr(a.Cash, 1m, w.ProjectId), Cr(a.Capital, 1m, w.ProjectId)], null))));
        await Fails(() => Run(w.Accountant, s => Svc<JournalService>(s).CancelAsync(j.Id, new CancelJournalRequest("not allowed", null))));
        var again = await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null))));
        Assert.Contains("already posted", again.Message);

        // rejection and resubmission
        var j2 = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Wrong", null, [Dr(a.Cash, 5m, w.ProjectId), Cr(a.Capital, 5m, w.ProjectId)], Submit: true)));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Approver, s => Svc<JournalService>(s).RejectAsync(j2.Id, new RejectJournalRequest("no", j2.RowVersion))));
        j2 = await Run(w.Approver, s => Svc<JournalService>(s).RejectAsync(j2.Id, new RejectJournalRequest("Wrong account used", j2.RowVersion)));
        Assert.Equal(JournalStatus.Rejected, j2.Status);
        j2 = await Run(w.Accountant, s => Svc<JournalService>(s).CancelAsync(j2.Id, new CancelJournalRequest("Entered by mistake", j2.RowVersion)));
        Assert.Equal(JournalStatus.Cancelled, j2.Status);

        // the trial balance for the project balances and shows the posting
        var tb = await Run(w.Accountant, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(null, w.Today, w.ProjectId, null)));
        Assert.True(tb.IsBalanced);
        Assert.Equal(300_000m, tb.Rows.Single(r => r.AccountId == a.Cash).ClosingDebit);
        Assert.Equal(300_000m, tb.Rows.Single(r => r.AccountId == a.Capital).ClosingCredit);

        // audit trail
        var actions = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.Where(l => l.EntityType == "JournalEntry" && l.EntityId == j.Id.ToString()).Select(l => l.Action).ToListAsync());
        Assert.Contains("Journal.Created", actions); Assert.Contains("Journal.Updated", actions); Assert.Contains("Journal.Submitted", actions); Assert.Contains("Journal.Posted", actions);
    }

    [MySqlFact]
    public async Task Reversal_PostsMirrorJournal_Once_AndNetsToZero()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var j = await PostedJournalAsync(w, w.Today, Dr(a.Expense, 12_000m, w.ProjectId), Cr(a.Cash, 12_000m, w.ProjectId));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).ReverseAsync(j.Id, new ReverseJournalRequest(null, "no"))));
        await Fails(() => Run(w.Accountant, s => Svc<JournalService>(s).ReverseAsync(j.Id, new ReverseJournalRequest(w.Today.AddDays(-400), "Before the original"))));
        var rv = await Run(w.Accountant, s => Svc<JournalService>(s).ReverseAsync(j.Id, new ReverseJournalRequest(w.Today, "Posted to the wrong project")));
        Assert.Equal(JournalSource.Reversal, rv.Source);
        Assert.StartsWith("RV", rv.VoucherNumber);
        Assert.Equal(j.Id, rv.ReversalOfJournalId);
        Assert.Equal(12_000m, rv.Lines.Single(l => l.AccountId == a.Cash).Debit);
        Assert.Equal(12_000m, rv.Lines.Single(l => l.AccountId == a.Expense).Credit);
        var original = await Run(w.Accountant, s => Svc<JournalService>(s).GetAsync(j.Id));
        Assert.Equal(rv.Id, original.ReversedByJournalId);
        Assert.Equal(JournalStatus.Posted, original.Status); // history kept, never deleted
        var again = await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ReverseAsync(j.Id, new ReverseJournalRequest(w.Today, "Second reversal"))));
        Assert.Contains("already been reversed", again.Message);
        await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ReverseAsync(rv.Id, new ReverseJournalRequest(w.Today, "Reverse the reversal"))));
        var tb = await Run(w.Accountant, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(null, w.Today, w.ProjectId, null)));
        Assert.DoesNotContain(tb.Rows, r => r.AccountId == a.Expense && (r.ClosingDebit != 0m || r.ClosingCredit != 0m));
    }

    [MySqlFact]
    public async Task ConcurrentApprovals_PostOnce_AndIdempotentCreation()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var j = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Race", null, [Dr(a.Cash, 7m, w.ProjectId), Cr(a.Capital, 7m, w.ProjectId)], Submit: true)));
        // The fixture has one current user, so concurrency is exercised with four scopes (own DbContexts) acting as the same approver.
        As(w.Approver);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var s = fx.Scope();
            try { await Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null)); return true; }
            catch (DomainException) { return false; }
        })));
        Assert.Equal(1, results.Count(x => x));
        var gl = await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.CountAsync(e => e.JournalEntryId == j.Id));
        Assert.Equal(2, gl);
        var vouchers = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AccountingVouchers.CountAsync(v => v.JournalEntryId == j.Id));
        Assert.Equal(1, vouchers);

        var key = Guid.NewGuid().ToString("N");
        var first = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Idem", null, [Dr(a.Cash, 3m, w.ProjectId), Cr(a.Capital, 3m, w.ProjectId)], false, key)));
        var second = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Idem", null, [Dr(a.Cash, 3m, w.ProjectId), Cr(a.Capital, 3m, w.ProjectId)], false, key)));
        Assert.Equal(first.Id, second.Id);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Approver, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "Idem", null, [Dr(a.Cash, 3m, w.ProjectId), Cr(a.Capital, 3m, w.ProjectId)], false, key))));
    }

    // ===================================================================================== periods
    [MySqlFact]
    public async Task ClosedPeriods_RejectUnauthorizedPostings_LockedRejectAll_ReopenAllows()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var y = 1990 + Interlocked.Increment(ref counter) % 20; // a private fiscal year in the past
        var start = new DateOnly(y, 7, 1);
        var year = await Run(w.Director, async s =>
            await Svc<AppDbContext>(s).FiscalYears.AnyAsync(f => f.StartDate == start)
                ? await Svc<FiscalPeriodService>(s).GetAsync(await Svc<AppDbContext>(s).FiscalYears.Where(f => f.StartDate == start).Select(f => f.Id).FirstAsync())
                : await Svc<FiscalPeriodService>(s).CreateAsync(new CreateFiscalYearRequest($"T{y}", start, new DateOnly(y + 1, 6, 30))));
        Assert.Equal(12, year.Periods.Count);
        await Fails(() => Run(w.Director, s => Svc<FiscalPeriodService>(s).CreateAsync(new CreateFiscalYearRequest($"O{y}", start.AddMonths(3), start.AddMonths(15).AddDays(-1)))));
        var period = year.Periods[0];
        var date = period.StartDate.AddDays(9);

        var j = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(date, "Late entry", null, [Dr(a.Expense, 50m, w.ProjectId), Cr(a.Cash, 50m, w.ProjectId)], Submit: true)));
        // close the period (accountant may manage periods), with a reason kept in the history
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Close, "x"))));
        var closed = await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Close, "Month-end close")));
        Assert.Equal(AccountingPeriodStatus.Closed, closed.Status);

        // a closed period rejects normal approval, and the override needs permission plus a reason
        var plain = await Fails(() => Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null))));
        Assert.Contains("closed", plain.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null, true, "Audit adjustment"))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Director, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null, true, null))));

        // a period cannot be locked while journals dated in it wait for approval; a locked period rejects even the override
        Assert.Contains("waiting for approval", (await Fails(() => Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Lock, "Audit completed"))))).Message);
        await Run(w.Approver, s => Svc<JournalService>(s).RejectAsync(j.Id, new RejectJournalRequest("Hold until the audit is done", null)));
        await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Lock, "Audit completed")));
        await Run(w.Accountant, s => Svc<JournalService>(s).SubmitAsync(j.Id, new SubmitJournalRequest(null)));
        var locked = await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest(null, null, true, "Audit adjustment"))));
        Assert.Contains("locked", locked.Message);
        await Fails(() => Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Close, "Cannot close a locked period"))));

        // reopen to closed state is not a transition; reopen to open, close again, then an authorized override posts with its reason
        await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Reopen, "Correction approved by CFO")));
        await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Close, "Closed again")));
        var posted = await Run(w.Director, s => Svc<JournalService>(s).ApproveAsync(j.Id, new ApproveJournalRequest("ok", null, true, "Audit adjustment approved")));
        Assert.Equal(JournalStatus.Posted, posted.Status);
        Assert.Equal("Audit adjustment approved", posted.ClosedPeriodReason);
        Assert.Equal(period.Name, posted.PeriodName);

        var history = await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).LockHistoryAsync(year.Id));
        Assert.True(history.Items.Count(h => h.PeriodId == period.Id) >= 4);
        Assert.Contains(history.Items, h => h.Action == PeriodLockAction.Lock && h.Reason == "Audit completed");

        // closing the year: all periods must be closed or locked first; afterwards nothing posts into it
        await Fails(() => Run(w.Accountant, s => Svc<FiscalPeriodService>(s).CloseYearAsync(year.Id, new CloseFiscalYearRequest("Year end close"))));
        foreach (var p in year.Periods.Skip(1)) await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(p.Id, new PeriodActionRequest(PeriodLockAction.Lock, "Year end lock")));
        var closedYear = await Run(w.Accountant, s => Svc<FiscalPeriodService>(s).CloseYearAsync(year.Id, new CloseFiscalYearRequest("Year end close")));
        Assert.Equal(FiscalYearStatus.Closed, closedYear.Status);
        var j2 = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(date, "After close", null, [Dr(a.Expense, 1m, w.ProjectId), Cr(a.Cash, 1m, w.ProjectId)], Submit: true)));
        var rejected = await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ApproveAsync(j2.Id, new ApproveJournalRequest(null, null, true, "Override attempt"))));
        Assert.Contains("closed", rejected.Message);
        await Fails(() => Run(w.Accountant, s => Svc<FiscalPeriodService>(s).ChangePeriodAsync(period.Id, new PeriodActionRequest(PeriodLockAction.Reopen, "Reopen after year close"))));
    }

    // ===================================================================================== posting rules and automatic journals
    [MySqlFact]
    public async Task PostingRules_RequireSecondUserApproval_AndAreImmutableOnceActive()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        // invalid definitions
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest("Bad", AccountingEventType.ExpenseApproved, w.ProjectId,
            null, null, 100, w.Today, null, [D(a.Expense, "Discount"), C(a.Vendor)]))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest("Rev", AccountingEventType.PaymentReversed, w.ProjectId,
            null, null, 100, w.Today, null, [D(a.Expense), C(a.Vendor)]))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest("Cond", AccountingEventType.ExpenseApproved, w.ProjectId,
            "Colour", "Red", 100, w.Today, null, [D(a.Expense), C(a.Vendor)]))));

        var r = await Run(w.Accountant, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest("Expense accrual", AccountingEventType.ExpenseApproved, w.ProjectId, null, null, 100, w.Today, null,
            [D(a.Expense), C(a.Vendor)])));
        Assert.Equal(PostingRuleStatus.Draft, r.Status);
        r = await Run(w.Accountant, s => Svc<PostingRuleService>(s).SubmitAsync(r.Id, new RuleDecisionRequest(null, r.RowVersion)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Approver, s => Svc<PostingRuleService>(s).ApproveAsync(r.Id, new RuleDecisionRequest(null, r.RowVersion)))); // accountants do not approve policy
        r = await Run(w.Director, s => Svc<PostingRuleService>(s).ApproveAsync(r.Id, new RuleDecisionRequest("Approved by director", r.RowVersion)));
        Assert.Equal(PostingRuleStatus.Active, r.Status);
        await Fails(() => Run(w.Accountant, s => Svc<PostingRuleService>(s).UpdateAsync(r.Id, new SavePostingRuleRequest("Changed", AccountingEventType.ExpenseApproved, w.ProjectId, null, null, 100, w.Today, null,
            [D(a.Expense), C(a.Cash)], r.RowVersion))));

        // the director cannot approve a rule the director created
        var own = await Run(w.Director, s => Svc<PostingRuleService>(s).CreateAsync(new SavePostingRuleRequest("Own", AccountingEventType.ExpensePaid, w.ProjectId, null, null, 100, w.Today, null, [D(a.Vendor), C(a.Cash)])));
        own = await Run(w.Director, s => Svc<PostingRuleService>(s).SubmitAsync(own.Id, new RuleDecisionRequest(null, own.RowVersion)));
        Assert.Contains("different user", (await Fails(() => Run(w.Director, s => Svc<PostingRuleService>(s).ApproveAsync(own.Id, new RuleDecisionRequest(null, own.RowVersion))))).Message);
        own = await Run(w.Admin, s => Svc<PostingRuleService>(s).RejectAsync(own.Id, new RuleDecisionRequest("Use the bank account", own.RowVersion)));
        Assert.Equal(PostingRuleStatus.Rejected, own.Status);

        var retired = await Run(w.Director, s => Svc<PostingRuleService>(s).RetireAsync(r.Id, new RuleDecisionRequest("Replaced by new policy", r.RowVersion)));
        Assert.Equal(PostingRuleStatus.Retired, retired.Status);
        Assert.Contains(await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.Where(l => l.EntityType == "PostingRule" && l.EntityId == r.Id.ToString()).Select(l => l.Action).ToListAsync()),
            x => x == "PostingRule.Approved");
    }

    // ----- payment / receivable flow helpers (Phase 6 data) -----
    private async Task<long> CustomerAsync(World w) => await Run(w.Admin, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
        [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed installment booking (5,000,000; down payment 10% due today-<paramref name="monthsBack"/>m, 24 monthly, development charges 100,000 at month 12).</summary>
    private async Task<long> AgreementAsync(World w, int monthsBack)
    {
        await Run(w.Admin, async s =>
        {
            if (await Svc<AppDbContext>(s).PricingRules.AnyAsync(r => r.ProjectId == w.ProjectId)) return 0;
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(w.ProjectId, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, w.Today, null, null));
            return 1;
        });
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"AC-{Guid.NewGuid():N}"[..14], "Z" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
        var c = await CustomerAsync(w);
        var b = await Run(w.Admin, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, PaymentMode.Installment, [new BookingPropertyInput(propertyId, 0m, null)],
            [new OwnerInput(c, 100m)], null, 500_000m, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        var agreementId = b.AgreementId!.Value;
        var types = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ChargeTypes.ToDictionaryAsync(t => t.Code, t => t.Id));
        PlanComponentInput Comp(string type, string name, AmountBasis basis, decimal? amount, decimal? pct, ScheduleFrequency f, int? count, int offset, ChargeCategory cat = ChargeCategory.SaleConsideration) =>
            new(types[type], name, cat, basis, amount, pct, f, count, null, offset, 0, null, null, 5, null, true, 50m, TaxTreatment.NotApplicable, 0m, null, null);
        var code = "AQ" + Interlocked.Increment(ref counter);
        var plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, code, "Plan " + code, null, InstallmentPlanType.StandardProject,
            null, null, null,
            [
                Comp("DOWN_PAYMENT", "Down payment", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 0),
                Comp("MONTHLY", "Monthly installment", AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, 1),
                Comp("POSSESSION", "Possession", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 30),
                Comp("DEVELOPMENT", "Development charges", AmountBasis.FixedAmount, 100_000m, null, ScheduleFrequency.OneTime, null, 12, ChargeCategory.DevelopmentCharges),
            ])));
        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(plan.RowVersion)));
        var ag = await Run(w.Admin, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(plan.Id, null, null)));
        await Run(w.Admin, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null)));
        if (monthsBack > 0)
            await Run(w.Admin, async s =>
            {
                var db = Svc<AppDbContext>(s);
                var items = await (from i in db.PaymentScheduleItems join sc in db.PaymentSchedules on i.ScheduleId equals sc.Id where sc.AgreementId == agreementId select i).ToListAsync();
                foreach (var i in items) { i.DueDate = i.DueDate.AddMonths(-monthsBack); i.GraceEndDate = i.GraceEndDate.AddMonths(-monthsBack); }
                return await db.SaveChangesAsync();
            });
        return agreementId;
    }

    private async Task<PaymentDetailDto> PayAsync(World w, long agreementId, decimal amount, string method = "CASH")
    {
        var methodId = await Run(w.Admin, async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == method).Select(m => m.Id).FirstAsync());
        var p = await Run(w.Admin, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(agreementId, null, methodId, PaymentSource.Office, amount, w.Today, method == "CASH" ? null : "TRX-" + counter++,
            null, null, null, null, AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N"))));
        if (p.Status == PaymentStatus.PendingVerification)
            p = await Run(w.Approver, s => Svc<PaymentService>(s).VerifyAsync(p.Id, new VerifyPaymentRequest(true, "Funds received", null, w.Today, p.RowVersion)));
        return await Run(w.Approver, s => Svc<PaymentService>(s).ApproveAsync(p.Id, new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion)));
    }

    [MySqlFact]
    public async Task CustomerFlow_AutomaticJournals_ControlAccountsReconcile_AndReversalsPost()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        await RuleAsync(w, AccountingEventType.ReceivableRecognized, w.ProjectId, "ChargeCategory", "SaleConsideration", D(a.Receivable), C(a.Sales));
        await RuleAsync(w, AccountingEventType.ReceivableRecognized, w.ProjectId, "ChargeCategory", "DevelopmentCharges", D(a.Receivable), C(a.DevRevenue));
        await RuleAsync(w, AccountingEventType.ReceivableDerecognized, w.ProjectId, null, null, D(a.Sales), C(a.Receivable));
        await RuleAsync(w, AccountingEventType.PaymentPosted, w.ProjectId, "Method", "Cash", D(a.Cash), C(a.Receivable));
        await RuleAsync(w, AccountingEventType.PaymentPosted, w.ProjectId, null, null, D(a.Bank), C(a.Receivable));

        var agreementId = await AgreementAsync(w, monthsBack: 13);
        await Run(w.Accountant, s => Svc<LedgerService>(s).GetAgreementLedgerAsync(agreementId)); // posts the receivables that fell due
        var cash = await PayAsync(w, agreementId, 700_000m);
        var bank = await PayAsync(w, agreementId, 162_500m, "BANK_TRANSFER");

        var run = await PostEventsAsync(w);
        Assert.True(run.Posted >= 4, $"posted {run.Posted}, failed {run.Failed}");
        var events = await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.ProjectId, PageSize = 100 }));
        Assert.DoesNotContain(events.Items, e => e.EventType is AccountingEventType.ReceivableRecognized or AccountingEventType.PaymentPosted && e.Status != AccountingPostingStatus.Posted);

        // the cash payment journal: Dr cash / Cr receivables, voucher AJ, linked to the payment and the rule
        var payEvent = events.Items.Single(e => e.EventType == AccountingEventType.PaymentPosted && e.SourceId == cash.Id);
        var pj = await Run(w.Accountant, s => Svc<JournalService>(s).GetAsync(payEvent.JournalEntryId!.Value));
        Assert.Equal(JournalSource.Automatic, pj.Source);
        Assert.StartsWith("AJ", pj.VoucherNumber);
        Assert.Equal(700_000m, pj.Lines.Single(l => l.AccountId == a.Cash).Debit);
        Assert.Equal(700_000m, pj.Lines.Single(l => l.AccountId == a.Receivable).Credit);
        Assert.All(pj.Lines, l => Assert.Equal(w.ProjectId, l.ProjectId));
        Assert.Equal("Payment", pj.SourceType);
        Assert.Equal(cash.Id, pj.SourceId);
        var bj = await Run(w.Accountant, async s => await Svc<JournalService>(s).GetAsync(events.Items.Single(e => e.EventType == AccountingEventType.PaymentPosted && e.SourceId == bank.Id).JournalEntryId!.Value));
        Assert.Equal(162_500m, bj.Lines.Single(l => l.AccountId == a.Bank).Debit);
        // development charges were recognized as development revenue (condition on the charge category)
        Assert.True(await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.AnyAsync(e => e.AccountId == a.DevRevenue && e.Credit == 100_000m)));

        // every automatic journal balances; the customer control account reconciles with the customer ledger
        var unbalanced = await Run(w.Admin, async s => await Svc<AppDbContext>(s).JournalEntries.Where(j => j.Status == JournalStatus.Posted && j.TotalDebit != j.TotalCredit).CountAsync());
        Assert.Equal(0, unbalanced);
        var rec = await Run(w.Accountant, s => Svc<AccountingReportService>(s).CustomerControlReconciliationAsync(w.Today, w.ProjectId));
        var row = rec.Rows.Single();
        Assert.True(row.Reconciled, $"ledger {row.LedgerBalance} control {row.ControlBalance}");
        Assert.Equal(0, row.UnprocessedEvents);
        var ar = await Run(w.Accountant, s => Svc<AccountingReportService>(s).AccountsReceivableAsync(w.Today, w.ProjectId));
        Assert.Equal(0m, ar.Difference);
        Assert.Equal(row.LedgerBalance, ar.NetBalance);

        // reversing the payment posts the reversal journal automatically and the accounts still reconcile
        await Run(w.Admin, s => Svc<PaymentService>(s).ReverseAsync(bank.Id, new ReversePaymentRequest("Transfer recalled by bank", null)));
        await PostEventsAsync(w);
        var revEvent = (await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.ProjectId, EventType = AccountingEventType.PaymentReversed }))).Items.Single();
        Assert.Equal(AccountingPostingStatus.Posted, revEvent.Status);
        var rj = await Run(w.Accountant, s => Svc<JournalService>(s).GetAsync(revEvent.JournalEntryId!.Value));
        Assert.Equal(JournalSource.Reversal, rj.Source);
        Assert.Equal(bj.Id, rj.ReversalOfJournalId);
        Assert.Equal(162_500m, rj.Lines.Single(l => l.AccountId == a.Bank).Credit);
        rec = await Run(w.Accountant, s => Svc<AccountingReportService>(s).CustomerControlReconciliationAsync(w.Today, w.ProjectId));
        Assert.True(rec.AllReconciled);
        // automatic journals cannot be reversed by hand
        await Fails(() => Run(w.Director, s => Svc<JournalService>(s).ReverseAsync(pj.Id, new ReverseJournalRequest(w.Today, "Manual reversal attempt"))));

        // the booking confirmation has no approved rule: it fails with the reason and can be marked as not posted (policy decision)
        var booking = (await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.ProjectId, EventType = AccountingEventType.BookingConfirmed }))).Items.Single();
        Assert.Equal(AccountingPostingStatus.Failed, booking.Status);
        Assert.Contains("No active, approved posting rule", booking.LastError);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<PostingEngineService>(s).SkipAsync(booking.Id, new SkipPostingRequest("Sale recognized per installment")))); // needs all projects
        var skipped = await Run(w.Director, s => Svc<PostingEngineService>(s).SkipAsync(booking.Id, new SkipPostingRequest("Policy: sale recognized as installments fall due")));
        Assert.Equal(AccountingPostingStatus.Skipped, skipped.Status);
        await Fails(() => Run(w.Director, s => Svc<PostingEngineService>(s).SkipAsync(booking.Id, new SkipPostingRequest("Twice is not allowed"))));

        // statements for the project are consistent
        var bs = await Run(w.Accountant, s => Svc<AccountingReportService>(s).BalanceSheetAsync(w.Today, w.ProjectId));
        Assert.True(bs.IsBalanced, $"assets {bs.Assets.Total} vs {bs.TotalLiabilitiesAndEquity}");
        var cf = await Run(w.Accountant, s => Svc<AccountingReportService>(s).CashFlowAsync(new DateOnly(w.Today.Year - 3, 1, 1), w.Today, w.ProjectId));
        Assert.True(cf.Reconciles);
        Assert.Equal(700_000m, cf.ClosingCash);
        Assert.Equal(700_000m, cf.Operating);
        var pnl = await Run(w.Accountant, s => Svc<AccountingReportService>(s).IncomeStatementAsync(new DateOnly(w.Today.Year - 3, 1, 1), w.Today, w.ProjectId));
        Assert.Equal(pnl.Revenue.Total, pnl.NetProfit);
        Assert.Equal(bs.RetainedEarningsComputed + bs.CurrentYearProfit, pnl.NetProfit);
    }

    [MySqlFact]
    public async Task ExpenseFlow_PayablesAndProjectAccountsReconcile_FailuresRetry()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var cat = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ExpenseCategories.Where(c => c.Code == "OFFICE_RENT").Select(c => c.Id).FirstAsync());
        var salaries = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ExpenseCategories.Where(c => c.Code == "SALARIES").Select(c => c.Id).FirstAsync());
        var salaryAccount = await AccountAsync(w.Accountant, "SALARIES", "Staff salaries");
        await RuleAsync(w, AccountingEventType.ExpenseApproved, w.ProjectId, null, null, D(a.Expense), C(a.Vendor));
        await RuleAsync(w, AccountingEventType.ExpenseApproved, w.ProjectId, "CategoryId", salaries.ToString(), D(salaryAccount), C(a.Vendor));
        await RuleAsync(w, AccountingEventType.ExpensePaid, w.ProjectId, null, null, D(a.Vendor), C(a.Cash));

        async Task<ExpenseDetailDto> ApprovedExpense(long category, decimal amount)
        {
            var e = await Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, category, null, "Rent " + Guid.NewGuid().ToString("N")[..6], w.Today, amount, null, null, null, Guid.NewGuid().ToString("N"))));
            await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Invoice, null, new FileUpload("bill.pdf", "application/pdf", 5, new MemoryStream([1, 2, 3, 4, 5]))));
            e = await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null)));
            return await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest("ok", null)));
        }
        var rent = await ApprovedExpense(cat, 80_000m);
        var salary = await ApprovedExpense(salaries, 50_000m);
        var methodId = await Run(w.Admin, async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == "CASH").Select(m => m.Id).FirstAsync());
        await Run(w.Accountant, s => Svc<ExpenseService>(s).PayAsync(rent.Id, new PayExpenseRequest(30_000m, methodId, w.Today, null, null, Guid.NewGuid().ToString("N"))));
        await PostEventsAsync(w);

        // salaries went to the salary account (category condition), rent to the default expense account
        Assert.Equal(50_000m, await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.Where(e => e.AccountId == salaryAccount).SumAsync(e => e.Debit)));
        Assert.Equal(80_000m, await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.Where(e => e.AccountId == a.Expense).SumAsync(e => e.Debit)));
        var ap = await Run(w.Accountant, s => Svc<AccountingReportService>(s).AccountsPayableAsync(w.ProjectId));
        Assert.Equal(100_000m, ap.TotalOutstanding);
        Assert.Equal(0m, ap.Difference);
        var statement = await Run(w.Accountant, s => Svc<AccountingReportService>(s).ProjectExpenseStatementAsync(w.ProjectId, w.Today.AddDays(-1), w.Today));
        Assert.Equal(130_000m, statement.Total);
        Assert.Equal(statement.ApprovedExpensesInModule, statement.Total);
        var projectRec = await Run(w.Director, s => Svc<AccountingReportService>(s).ProjectReconciliationAsync(w.Today));
        var mine = projectRec.Rows.Single(r => r.ProjectId == w.ProjectId);
        Assert.True(mine.Reconciled);
        Assert.Equal(mine.ModuleExpenses, mine.LedgerExpenses);
        Assert.True(projectRec.TotalsAgree);
        var profit = await Run(w.Accountant, s => Svc<AccountingReportService>(s).ProjectProfitabilityAsync(w.Today.AddDays(-1), w.Today));
        Assert.Equal(-130_000m, profit.Rows.Single(r => r.ProjectId == w.ProjectId).NetProfit);

        // cancelling an approved expense reverses its journal
        var other = await ApprovedExpense(cat, 5_000m);
        await PostEventsAsync(w);
        await Run(w.Accountant, s => Svc<ExpenseService>(s).CancelAsync(other.Id, new CancelExpenseRequest("Duplicate bill", null)));
        await PostEventsAsync(w);
        var cancelEvent = (await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.ProjectId, EventType = AccountingEventType.ExpenseCancelled }))).Items.Single();
        Assert.Equal(AccountingPostingStatus.Posted, cancelEvent.Status);
        Assert.Equal(80_000m, await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.Where(e => e.AccountId == a.Expense).SumAsync(e => e.Debit - e.Credit)));

        // an event with no matching rule (other project) fails and is retried once a rule is approved
        var otherProjectExpense = await Run(w.Director, async s =>
        {
            var e = await Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.OtherProjectId, cat, null, "Other project rent", w.Today, 1_000m, null, null, null, Guid.NewGuid().ToString("N")));
            return e;
        });
        await Run(w.Director, s => Svc<ExpenseService>(s).UploadAttachmentAsync(otherProjectExpense.Id, ExpenseDocumentType.Invoice, null, new FileUpload("b.pdf", "application/pdf", 5, new MemoryStream([1, 2, 3, 4, 5]))));
        await Run(w.Director, s => Svc<ExpenseService>(s).SubmitAsync(otherProjectExpense.Id, new SubmitExpenseRequest(null)));
        await Run(w.Approver, s => Svc<ExpenseService>(s).ApproveAsync(otherProjectExpense.Id, new ApproveExpenseRequest("ok", null)));
        await PostEventsAsync(w, w.OtherProjectId);
        var failed = (await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.OtherProjectId, EventType = AccountingEventType.ExpenseApproved }))).Items.Single();
        Assert.Equal(AccountingPostingStatus.Failed, failed.Status);
        await RuleAsync(w, AccountingEventType.ExpenseApproved, w.OtherProjectId, null, null, D(a.Expense), C(a.Vendor));
        var retried = await Run(w.Director, s => Svc<PostingEngineService>(s).RetryAsync(failed.Id));
        Assert.Equal(AccountingPostingStatus.Posted, retried.Status);
        Assert.Equal(2, retried.Attempts);
    }

    [MySqlFact]
    public async Task AmbiguousRules_AndLockedPeriods_FailEventsWithoutPosting()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        await RuleAsync(w, AccountingEventType.ExpenseApproved, w.ProjectId, null, null, D(a.Expense), C(a.Vendor));
        await RuleAsync(w, AccountingEventType.ExpenseApproved, w.ProjectId, null, null, D(a.Expense), C(a.Cash));
        var cat = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ExpenseCategories.Where(c => c.Code == "UTILITIES").Select(c => c.Id).FirstAsync());
        var e = await Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "Gas", w.Today, 2_000m, null, null, null, Guid.NewGuid().ToString("N"))));
        await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Invoice, null, new FileUpload("b.pdf", "application/pdf", 5, new MemoryStream([1, 2, 3, 4, 5]))));
        await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null)));
        await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest("ok", null)));
        await PostEventsAsync(w);
        var ev = (await Run(w.Accountant, s => Svc<PostingEngineService>(s).ListAsync(new PostingQueueRequest { ProjectId = w.ProjectId, EventType = AccountingEventType.ExpenseApproved }))).Items.Single();
        Assert.Equal(AccountingPostingStatus.Failed, ev.Status);
        Assert.Contains("same priority", ev.LastError);
        Assert.Null(ev.JournalEntryId);
        Assert.Equal(0, await Run(w.Admin, async s => await Svc<AppDbContext>(s).JournalEntries.CountAsync(j => j.PostingRequestId == ev.Id)));
    }

    // ===================================================================================== banking
    [MySqlFact]
    public async Task Transfers_BankStatementImport_Matching_AndReconciliation()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var bank = await Run(w.Director, s => Svc<BankingService>(s).SaveBankAccountAsync(null, new SaveBankAccountRequest("B" + Guid.NewGuid().ToString("N")[..8], "HBL current", "HBL", "Murree",
            "Company", "0012345678", null, "PKR", a.Bank, null)));
        var cashAcc = await Run(w.Director, s => Svc<BankingService>(s).SaveCashAccountAsync(null, new SaveCashAccountRequest("C" + Guid.NewGuid().ToString("N")[..8], "Office cash", "Cashier", a.Cash, null)));
        // a ledger account of the wrong class is rejected
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Director, s => Svc<BankingService>(s).SaveBankAccountAsync(null, new SaveBankAccountRequest("B" + Guid.NewGuid().ToString("N")[..8], "x", "x", null,
            "x", "1", null, "PKR", a.Cash, null))));

        // opening capital into cash, then a transfer cash -> bank built from the approved transfer rule (maker-checker)
        await PostedJournalAsync(w, w.Today, Dr(a.Cash, 500_000m, w.ProjectId), Cr(a.Capital, 500_000m, w.ProjectId));
        var noRule = await Fails(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateTransferAsync(new CreateTransferRequest(a.Cash, a.Bank, 200_000m, w.Today, "Deposit", "DS-1", w.ProjectId))));
        Assert.Contains("posting rule", noRule.Message);
        await RuleAsync(w, AccountingEventType.BankTransfer, w.ProjectId, null, null, new PostingRuleLineInput(EntrySide.Debit, "Amount", null, "ToAccount"), new PostingRuleLineInput(EntrySide.Credit, "Amount", null, "FromAccount"));
        var t = await Run(w.Accountant, s => Svc<JournalService>(s).CreateTransferAsync(new CreateTransferRequest(a.Cash, a.Bank, 200_000m, w.Today, "Cash deposited", "DS-1", w.ProjectId)));
        Assert.Equal(JournalStatus.PendingApproval, t.Status);
        t = await Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(t.Id, new ApproveJournalRequest(null, t.RowVersion)));
        Assert.StartsWith("TR", t.VoucherNumber);
        var bankbook = await Run(w.Director, s => Svc<AccountingReportService>(s).BankbookAsync(bank.Id, null, w.Today));
        Assert.Equal(200_000m, bankbook.ClosingBalance);
        var cashbook = await Run(w.Director, s => Svc<AccountingReportService>(s).CashbookAsync(cashAcc.Id, null, w.Today));
        Assert.Equal(300_000m, cashbook.ClosingBalance);
        var cf = await Run(w.Accountant, s => Svc<AccountingReportService>(s).CashFlowAsync(w.Today, w.Today, w.ProjectId));
        Assert.Equal(500_000m, cf.Financing); // capital; the transfer between cash and bank adds nothing
        Assert.True(cf.Reconciles);

        // statement import: the deposit and a bank charge; re-import detects duplicates; the deposit auto-matches
        var lines = new List<StatementLineInput> { new(w.Today, "Cash deposit DS-1", "DS-1", 200_000m, 0m), new(w.Today, "Bank charges", null, 0m, 350m) };
        var imp = await Run(w.Director, s => Svc<BankingService>(s).ImportAsync(bank.Id, new ImportStatementRequest(lines)));
        Assert.Equal(2, imp.Imported); Assert.Equal(1, imp.AutoMatched);
        var again = await Run(w.Director, s => Svc<BankingService>(s).ImportAsync(bank.Id, new ImportStatementRequest(lines)));
        Assert.Equal(0, again.Imported); Assert.Equal(2, again.Duplicates);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<BankingService>(s).ImportAsync(bank.Id, new ImportStatementRequest(lines))));

        // reconciliation: statement balance 199,650 differs by the unrecorded charge until it is booked and matched
        var rec = await Run(w.Director, s => Svc<BankingService>(s).StartAsync(bank.Id, new StartReconciliationRequest(w.Today, 199_650m, null)));
        Assert.Equal(200_000m, rec.BookBalance);
        Assert.Equal(-350m, rec.UnmatchedStatementNet);
        Assert.Equal(0m, rec.Difference); // explained by the unmatched statement line
        var ws = await Run(w.Director, s => Svc<BankingService>(s).WorkspaceAsync(bank.Id));
        var charge = ws.UnmatchedStatement.Single();
        var cj = await PostedJournalAsync(w, w.Today, Dr(a.BankCharges, 350m, w.ProjectId), Cr(a.Bank, 350m, w.ProjectId));
        var bookEntry = await Run(w.Admin, async s => await Svc<AppDbContext>(s).GeneralLedgerEntries.Where(e => e.JournalEntryId == cj.Id && e.AccountId == a.Bank).Select(e => e.Id).FirstAsync());
        await Fails(() => Run(w.Director, s => Svc<BankingService>(s).MatchAsync(bank.Id, new MatchRequest(ws.RecentlyMatched.Single().Id, bookEntry))));
        var m = await Run(w.Director, s => Svc<BankingService>(s).MatchAsync(bank.Id, new MatchRequest(charge.Id, bookEntry)));
        Assert.Equal(BankTransactionStatus.Matched, m.Status);
        rec = await Run(w.Director, s => Svc<BankingService>(s).StartAsync(bank.Id, new StartReconciliationRequest(w.Today, 199_650m, "June statement")));
        Assert.Equal(199_650m, rec.BookBalance);
        Assert.Equal(0m, rec.UnclearedBookNet);
        Assert.Equal(0m, rec.Difference);
        var done = await Run(w.Director, s => Svc<BankingService>(s).CompleteAsync(bank.Id, rec.Id, rec.RowVersion));
        Assert.Equal(BankReconciliationStatus.Completed, done.Status);
        await Fails(() => Run(w.Director, s => Svc<BankingService>(s).UnmatchAsync(bank.Id, charge.Id)));
        var history = await Run(w.Director, s => Svc<BankingService>(s).HistoryAsync(bank.Id));
        Assert.Contains(history, h => h.Id == rec.Id && h.Status == BankReconciliationStatus.Completed);
        await Fails(() => Run(w.Director, s => Svc<BankingService>(s).StartAsync(bank.Id, new StartReconciliationRequest(w.Today, 1m, null))));
    }

    [MySqlFact]
    public void CsvStatement_IsParsed_WithQuotedFields()
    {
        var csv = "Date,Description,Reference,Deposit,Withdrawal,Balance\n2026-06-01,\"Deposit, branch 2\",DS-9,\"1,000.50\",,1000.50\n02/06/2026,Charges,,,25,975.50\n";
        var lines = BankingService.ParseCsv(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(2, lines.Count);
        Assert.Equal("Deposit, branch 2", lines[0].Description);
        Assert.Equal(1000.50m, lines[0].Deposit);
        Assert.Equal(new DateOnly(2026, 6, 2), lines[1].TransactionDate);
        Assert.Equal(25m, lines[1].Withdrawal);
        Assert.Throws<RequestValidationException>(() => BankingService.ParseCsv(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("x,y\n"))));
    }

    // ===================================================================================== project accounting
    [MySqlFact]
    public async Task CostAllocation_SplitsByPercentage_AfterApproval()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        await PostedJournalAsync(w, w.Today, Dr(a.Expense, 90_000m, w.ProjectId), Cr(a.Cash, 90_000m, w.ProjectId));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAllocationAsync(new CreateCostAllocationRequest(w.Today, a.Expense, w.ProjectId, null, 90_000m,
            "Floor area", [new CostAllocationLineInput(w.OtherProjectId, 60m)]))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAllocationAsync(new CreateCostAllocationRequest(w.Today, a.Cash, w.ProjectId, null, 90_000m,
            "Floor area", [new CostAllocationLineInput(w.OtherProjectId, 100m)]))));
        var alloc = await Run(w.Accountant, s => Svc<JournalService>(s).CreateAllocationAsync(new CreateCostAllocationRequest(w.Today, a.Expense, w.ProjectId, null, 30_000m,
            "Shared site office by plot count", [new CostAllocationLineInput(w.OtherProjectId, 100m)])));
        Assert.Equal(CostAllocationStatus.PendingApproval, alloc.Status);
        Assert.StartsWith("PCA-", alloc.AllocationNumber);
        var j = await Run(w.Approver, s => Svc<JournalService>(s).ApproveAsync(alloc.JournalEntryId, new ApproveJournalRequest(null, null)));
        Assert.StartsWith("CA", j.VoucherNumber);
        Assert.Equal(CostAllocationStatus.Posted, (await Run(w.Accountant, s => Svc<JournalService>(s).GetAllocationAsync(alloc.Id))).Status);
        var source = await Run(w.Accountant, s => Svc<AccountingReportService>(s).ProjectExpenseStatementAsync(w.ProjectId, w.Today, w.Today));
        var target = await Run(w.Accountant, s => Svc<AccountingReportService>(s).ProjectExpenseStatementAsync(w.OtherProjectId, w.Today, w.Today));
        Assert.Equal(60_000m, source.Total);
        Assert.Equal(30_000m, target.Total);
        Assert.Equal(new[] { 0.5m }, Domain.Accounting.AccountingMath.Split(1m, [50m, 50m]).Take(1));
    }

    // ===================================================================================== access and constraints
    [MySqlFact]
    public async Task Access_IsProjectScoped_AndCompanyWideNeedsAllProjects()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var j = await PostedJournalAsync(w, w.Today, Dr(a.Cash, 10m, w.ProjectId), Cr(a.Capital, 10m, w.ProjectId));
        // outsider: not assigned to the project
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(null, w.Today, w.ProjectId, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<JournalService>(s).GetAsync(j.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "x", null, [Dr(a.Cash, 1m, w.ProjectId), Cr(a.Capital, 1m, w.ProjectId)]))));
        var list = await Run(w.Outsider, s => Svc<JournalService>(s).ListAsync(new JournalListRequest { PageSize = 100 }));
        Assert.DoesNotContain(list.Items, x => x.Id == j.Id);
        // company-wide figures and company-level lines need access to all projects
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(null, w.Today, null, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "x", null, [Dr(a.Cash, 1m, null), Cr(a.Capital, 1m, null)]))));
        var company = await Run(w.Director, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(null, w.Today, null, null)));
        Assert.True(company.IsBalanced);
        // the automatic posting run covers all projects
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<PostingEngineService>(s).RunAsync()));
        // project managers see project reports but cannot journalize
        Assert.NotNull(await Run(w.Manager, s => Svc<AccountingReportService>(s).IncomeStatementAsync(null, w.Today, w.ProjectId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "x", null, [Dr(a.Cash, 1m, w.ProjectId), Cr(a.Capital, 1m, w.ProjectId)]))));
        // control accounts need the control permission for manual journals
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<JournalService>(s).CreateAsync(new CreateJournalRequest(w.Today, "x", null, [Dr(a.Receivable, 1m, w.ProjectId), Cr(a.Capital, 1m, w.ProjectId)]))));
        Assert.Contains("control account", string.Join(" ", ex.Errors.SelectMany(e => e.Value)));
    }

    [MySqlFact]
    public async Task Database_EnforcesBalancedPostedJournals_OneSidedLines_AndUniqueVouchers()
    {
        var w = await WorldAsync();
        var a = await AccountsAsync(w);
        var j = await PostedJournalAsync(w, w.Today, Dr(a.Cash, 10m, w.ProjectId), Cr(a.Capital, 10m, w.ProjectId));
        await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE JournalEntries SET TotalCredit = TotalCredit + 1 WHERE Id = {0}", j.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE GeneralLedgerEntries SET Credit = 5 WHERE JournalEntryId = {0} AND Debit > 0", j.Id));
            var v = await db.AccountingVouchers.FirstAsync(x => x.JournalEntryId == j.Id);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO AccountingVouchers (VoucherType, FiscalYearId, SequenceNo, VoucherNumber, JournalEntryId, IssuedAtUtc, IssuedBy) VALUES ({0},{1},{2},{3},{4},UTC_TIMESTAMP(),'x')",
                v.VoucherType.ToString(), v.FiscalYearId, v.SequenceNo, v.VoucherNumber + "X", j.Id));
            var line = await db.JournalEntryLines.FirstAsync(l => l.JournalEntryId == j.Id);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO GeneralLedgerEntries (JournalEntryId, JournalEntryLineId, AccountId, EntryDate, FiscalYearId, PeriodId, Debit, Credit, VoucherNumber, Source, Narration, PostedAtUtc) " +
                "SELECT JournalEntryId, JournalEntryLineId, AccountId, EntryDate, FiscalYearId, PeriodId, Debit, Credit, VoucherNumber, Source, Narration, PostedAtUtc FROM GeneralLedgerEntries WHERE JournalEntryLineId = {0}", line.Id));
            return 0;
        });
    }
}
