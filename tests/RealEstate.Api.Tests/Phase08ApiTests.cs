using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Accounting;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase08ApiTests(ApiFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return c;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<long> ProjectAsync(HttpClient c) => (await Ok<ProjectDetailDto>(await c.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("X" + Guid.NewGuid().ToString("N")[..10],
        "Accounting API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null)))).Id;

    private async Task<long> GroupAsync(string code)
    {
        using var s = factory.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<AppDbContext>().AccountGroups.Where(g => g.Code == code).Select(g => g.Id).FirstAsync();
    }

    private async Task<long> AccountAsync(HttpClient c, string group, string name)
    {
        var r = await c.PostAsJsonAsync("/api/v1/accounting/accounts", new SaveAccountRequest("A" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), name, await GroupAsync(group), null));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<AccountDetailDto>())!.Account.Id;
    }

    private static async Task EnsureYearAsync(HttpClient c)
    {
        var years = await Ok<List<FiscalYearDto>>(await c.GetAsync("/api/v1/accounting/fiscal-years"));
        if (years.Any(y => y.StartDate <= Today && y.EndDate >= Today)) return;
        var start = new DateOnly(Today.Month >= 7 ? Today.Year : Today.Year - 1, 7, 1);
        await Ok<FiscalYearDto>(await c.PostAsJsonAsync("/api/v1/accounting/fiscal-years", new CreateFiscalYearRequest($"API FY {start.Year}", start, start.AddYears(1).AddDays(-1))));
    }

    [MySqlFact]
    public async Task Phase8Endpoints_RequireAuthentication_AndPermissions()
    {
        var anon = factory.CreateClient();
        string[] gets = ["/api/v1/accounting/accounts", "/api/v1/accounting/trial-balance", "/api/v1/accounting/general-ledger?accountId=1", "/api/v1/accounting/balance-sheet",
            "/api/v1/accounting/income-statement", "/api/v1/accounting/cash-flow", "/api/v1/accounting/bank-reconciliation"];
        foreach (var url in gets) Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/accounting/accounts", "/api/v1/accounting/journals", "/api/v1/accounting/journals/1/approve", "/api/v1/accounting/journals/1/reverse" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(url, new { })).StatusCode);
        var cashier = await ClientAsync(RoleNames.Cashier);
        foreach (var url in gets) Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/accounting/journals", new CreateJournalRequest(Today, "x", null, []))).StatusCode);
    }

    [MySqlFact]
    public async Task Accounts_Journals_Approval_Reversal_AndReports_EndToEnd()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        await EnsureYearAsync(maker);
        var projectId = await ProjectAsync(maker);
        var cash = await AccountAsync(maker, "CASH", "API cash");
        var capital = await AccountAsync(maker, "CAPITAL", "API capital");
        var expense = await AccountAsync(maker, "OPERATING_EXPENSES", "API rent");

        // GET accounts (search) and validation
        var accounts = await Ok<PagedResult<AccountDto>>(await maker.GetAsync("/api/v1/accounting/accounts?search=API%20cash"));
        Assert.Contains(accounts.Items, a => a.Id == cash && a.IsPostable);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/accounting/accounts", new SaveAccountRequest("", "", 0, null))).StatusCode);

        // POST journals: unbalanced 400, balanced 201 (submitted)
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/accounting/journals", new CreateJournalRequest(Today, "Bad", null,
            [new JournalLineInput(cash, 100m, 0m, projectId), new JournalLineInput(capital, 0m, 50m, projectId)]))).StatusCode);
        var r = await maker.PostAsJsonAsync("/api/v1/accounting/journals", new CreateJournalRequest(Today, "API capital", "API-1",
            [new JournalLineInput(cash, 500_000m, 0m, projectId), new JournalLineInput(capital, 0m, 500_000m, projectId)], Submit: true, IdempotencyKey: Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = (await r.Content.ReadFromJsonAsync<JournalDetailDto>())!;
        Assert.Equal(JournalStatus.PendingApproval, j.Status);

        // maker-checker: the maker gets 422 with Problem Details; the checker posts
        var self = await maker.PostAsJsonAsync($"/api/v1/accounting/journals/{j.Id}/approve", new ApproveJournalRequest(null, j.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("different user", (await self.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var posted = await Ok<JournalDetailDto>(await checker.PostAsJsonAsync($"/api/v1/accounting/journals/{j.Id}/approve", new ApproveJournalRequest("ok", j.RowVersion)));
        Assert.Equal(JournalStatus.Posted, posted.Status);
        Assert.NotNull(posted.VoucherNumber);

        var exp = await Ok<JournalDetailDto>(await maker.PostAsJsonAsync("/api/v1/accounting/journals", new CreateJournalRequest(Today, "API rent", null,
            [new JournalLineInput(expense, 40_000m, 0m, projectId), new JournalLineInput(cash, 0m, 40_000m, projectId)], Submit: true)));
        await Ok<JournalDetailDto>(await checker.PostAsJsonAsync($"/api/v1/accounting/journals/{exp.Id}/approve", new ApproveJournalRequest(null, exp.RowVersion)));

        // reports
        var tb = await Ok<TrialBalanceDto>(await maker.GetAsync($"/api/v1/accounting/trial-balance?projectId={projectId}&to={Today:yyyy-MM-dd}"));
        Assert.True(tb.IsBalanced);
        Assert.Equal(460_000m, tb.Rows.Single(x => x.AccountId == cash).ClosingDebit);
        var gl = await Ok<GeneralLedgerDto>(await maker.GetAsync($"/api/v1/accounting/general-ledger?accountId={cash}&projectId={projectId}&to={Today:yyyy-MM-dd}"));
        Assert.Equal(2, gl.Lines.TotalCount);
        Assert.Equal(460_000m, gl.ClosingBalance);
        var bs = await Ok<BalanceSheetDto>(await maker.GetAsync($"/api/v1/accounting/balance-sheet?projectId={projectId}&asOf={Today:yyyy-MM-dd}"));
        Assert.True(bs.IsBalanced);
        Assert.Equal(460_000m, bs.Assets.Total);
        var pl = await Ok<IncomeStatementDto>(await maker.GetAsync($"/api/v1/accounting/income-statement?projectId={projectId}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Equal(-40_000m, pl.NetProfit);
        var cf = await Ok<CashFlowDto>(await maker.GetAsync($"/api/v1/accounting/cash-flow?projectId={projectId}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Equal(500_000m, cf.Financing);
        Assert.Equal(-40_000m, cf.Operating);
        Assert.True(cf.Reconciles);
        Assert.Equal(HttpStatusCode.OK, (await maker.GetAsync("/api/v1/accounting/bank-reconciliation")).StatusCode);

        // reverse: 400 without reason, then RV journal; second reversal 422
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync($"/api/v1/accounting/journals/{exp.Id}/reverse", new ReverseJournalRequest(Today, ""))).StatusCode);
        var rv = await Ok<JournalDetailDto>(await maker.PostAsJsonAsync($"/api/v1/accounting/journals/{exp.Id}/reverse", new ReverseJournalRequest(Today, "Rent belongs to next month")));
        Assert.Equal(JournalSource.Reversal, rv.Source);
        Assert.Equal(exp.Id, rv.ReversalOfJournalId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/accounting/journals/{exp.Id}/reverse", new ReverseJournalRequest(Today, "Twice is refused"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/api/v1/accounting/journals/999999999")).StatusCode);
        pl = await Ok<IncomeStatementDto>(await maker.GetAsync($"/api/v1/accounting/income-statement?projectId={projectId}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Equal(0m, pl.NetProfit);
    }

    [MySqlFact]
    public async Task PostingRules_AndPostingRun_ThroughTheApi()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        await EnsureYearAsync(maker);
        var projectId = await ProjectAsync(maker);
        var expense = await AccountAsync(maker, "OPERATING_EXPENSES", "API rule expense");
        var vendor = await AccountAsync(maker, "VENDOR_PAYABLES", "API rule vendor");
        var catalog = await Ok<List<EventCatalogDto>>(await maker.GetAsync("/api/v1/accounting/posting-events"));
        Assert.Contains(catalog, c => c.EventType == AccountingEventType.ExpenseApproved && c.AmountKeys.Contains("Amount"));
        Assert.Contains(catalog, c => c.EventType == AccountingEventType.PaymentReversed && c.IsReversal);
        var rule = await Ok<PostingRuleDto>(await maker.PostAsJsonAsync("/api/v1/accounting/posting-rules", new SavePostingRuleRequest("API accrual", AccountingEventType.ExpenseApproved, projectId, null, null, 100, Today, null,
            [new PostingRuleLineInput(EntrySide.Debit, "Amount", expense, null), new PostingRuleLineInput(EntrySide.Credit, "Amount", vendor, null)])));
        rule = await Ok<PostingRuleDto>(await maker.PostAsJsonAsync($"/api/v1/accounting/posting-rules/{rule.Id}/submit", new RuleDecisionRequest(null, rule.RowVersion)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/accounting/posting-rules/{rule.Id}/approve", new RuleDecisionRequest(null, rule.RowVersion))).StatusCode);
        rule = await Ok<PostingRuleDto>(await checker.PostAsJsonAsync($"/api/v1/accounting/posting-rules/{rule.Id}/approve", new RuleDecisionRequest("approved", rule.RowVersion)));
        Assert.Equal(PostingRuleStatus.Active, rule.Status);
        var run = await Ok<PostingRunResultDto>(await maker.PostAsync($"/api/v1/accounting/postings/run?projectId={projectId}", null));
        Assert.True(run.Processed >= 0);
        var queue = await Ok<PagedResult<PostingRequestDto>>(await maker.GetAsync("/api/v1/accounting/postings?status=Failed"));
        Assert.All(queue.Items, q => Assert.Equal(AccountingPostingStatus.Failed, q.Status));
    }
}
