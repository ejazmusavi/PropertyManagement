using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 8 accounting screens (rendered-HTML tests through the MVC pipeline with anti-forgery tokens and two administrators for maker-checker).</summary>
[Collection("web")]
public class Phase08WebTests(WebFactory f)
{
    private static string Today => DateTime.UtcNow.AddHours(5).ToString("yyyy-MM-dd");
    private static string Decode(string s) => WebUtility.HtmlDecode(s);
    private static string Rv(string html, string name = "RowVersion") => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static long IdFrom(HttpResponseMessage r, string pattern) => long.Parse(Regex.Match(r.Headers.Location!.ToString(), pattern).Groups[1].Value);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static async Task<string> GetOkAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
        return Decode(await r.Content.ReadAsStringAsync());
    }

    private static async Task<string> FollowAsync(HttpClient c, HttpResponseMessage r)
    {
        if (r.StatusCode != HttpStatusCode.Redirect)
        {
            var text = Regex.Replace(Decode(await r.Content.ReadAsStringAsync()), "<[^>]+>", " ");
            Assert.Fail($"expected redirect, got {r.StatusCode}: {text[..Math.Min(text.Length, 600)]}");
        }
        return await GetOkAsync(c, r.Headers.Location!.ToString());
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = f.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<long> GroupAsync(string code) => await DbAsync(async db => await db.AccountGroups.Where(g => g.Code == code).Select(g => g.Id).FirstAsync());

    private async Task<long> AccountAsync(HttpClient c, string group, string name, bool manual = true)
    {
        var code = "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var r = await PostAsync(c, "/Accounting/CreateAccount", "/Accounting/CreateAccount", ("Code", code), ("Name", name), ("AccountGroupId", (await GroupAsync(group)).ToString()),
            ("AllowManualEntries", manual ? "true" : "false"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return IdFrom(r, @"/Accounting/AccountDetails/(\d+)");
    }

    /// <summary>Makes sure a fiscal year covers today (through the screen, once per database).</summary>
    private async Task EnsureYearAsync(HttpClient c)
    {
        var today = DateOnly.Parse(Today);
        if (await DbAsync(db => db.FiscalYears.AnyAsync(y => y.StartDate <= today && y.EndDate >= today))) return;
        var start = new DateOnly(today.Month >= 7 ? today.Year : today.Year - 1, 7, 1);
        var r = await PostAsync(c, "/FiscalYears", "/FiscalYears/Create", ("Form.Name", $"FY {start.Year}"), ("Form.StartDate", start.ToString("yyyy-MM-dd")), ("Form.EndDate", start.AddYears(1).AddDays(-1).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    private async Task<long> ProjectAsync(HttpClient c)
    {
        var code = "A" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Accounting UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return IdFrom(r, @"/Projects/Details/(\d+)");
    }

    [MySqlFact]
    public async Task ChartOfAccounts_Journals_MakerChecker_Reversal_AndReports_WorkThroughTheUi()
    {
        var maker = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        await EnsureYearAsync(maker);
        var projectId = await ProjectAsync(maker);

        // dashboard, groups and chart of accounts
        var dash = await GetOkAsync(maker, "/Accounting");
        foreach (var t in new[] { "Accounting", "Journals awaiting approval", "Automatic postings pending", "approved posting rules" }) Dbg.Has(dash, t);
        var groups = await GetOkAsync(maker, "/Accounting/Groups");
        foreach (var t in new[] { "Assets", "Customer receivables", "Customer advances", "Government payables", "Retained earnings", "Development-related revenue", "Construction expenses", "New group" }) Dbg.Has(groups, t);
        var bad = await PostAsync(maker, "/Accounting/CreateAccount", "/Accounting/CreateAccount", ("Code", ""), ("Name", ""), ("AccountGroupId", "0"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(Decode(await bad.Content.ReadAsStringAsync()), "Choose an account group");
        var cash = await AccountAsync(maker, "CASH", "UI cash in hand");
        var capital = await AccountAsync(maker, "CAPITAL", "UI owner capital");
        var accounts = await GetOkAsync(maker, "/Accounting/Accounts?search=UI");
        Dbg.Has(accounts, "UI cash in hand"); Dbg.Has(accounts, "UI owner capital");
        Dbg.Has(await GetOkAsync(maker, $"/Accounting/AccountDetails/{cash}"), "Takes postings");
        var cc = await PostAsync(maker, "/Accounting/CostCenters", "/Accounting/SaveCostCenter", ("Form.Code", "CC" + Guid.NewGuid().ToString("N")[..6]), ("Form.Name", "Site office"), ("Form.ProjectId", projectId.ToString()), ("Form.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, cc.StatusCode);
        Dbg.Has(await GetOkAsync(maker, "/Accounting/CostCenters"), "Site office");

        // manual journal: unbalanced is refused; balanced draft; submit; maker cannot approve; checker posts
        var form = await GetOkAsync(maker, "/Journals/Create");
        Dbg.Has(form, "Save and submit for approval"); Dbg.Has(form, "UI cash in hand");
        var unbalanced = await PostAsync(maker, "/Journals/Create", "/Journals/Create", ("EntryDate", Today), ("Narration", "Capital"), ("IdempotencyKey", Guid.NewGuid().ToString("N")),
            ("Lines[0].AccountId", cash.ToString()), ("Lines[0].ProjectId", projectId.ToString()), ("Lines[0].Debit", "1000"), ("Lines[1].AccountId", capital.ToString()), ("Lines[1].ProjectId", projectId.ToString()), ("Lines[1].Credit", "900"));
        Assert.Equal(HttpStatusCode.OK, unbalanced.StatusCode);
        Dbg.Has(Decode(await unbalanced.Content.ReadAsStringAsync()), "Total debit must equal total credit");
        var created = await PostAsync(maker, "/Journals/Create", "/Journals/Create", ("EntryDate", Today), ("Narration", "Capital introduced"), ("Reference", "DEP-77"), ("IdempotencyKey", Guid.NewGuid().ToString("N")), ("Submit", "true"),
            ("Lines[0].AccountId", cash.ToString()), ("Lines[0].ProjectId", projectId.ToString()), ("Lines[0].Debit", "250000"), ("Lines[1].AccountId", capital.ToString()), ("Lines[1].ProjectId", projectId.ToString()), ("Lines[1].Credit", "250000"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = IdFrom(created, @"/Journals/Details/(\d+)");
        var details = await GetOkAsync(maker, $"/Journals/Details/{id}");
        Dbg.Has(details, "Pending approval"); Dbg.Has(details, "another user must approve");
        Assert.DoesNotContain("Approve and post</button>", details);
        var queue = await GetOkAsync(checker, "/Journals/Approvals");
        Dbg.Has(queue, "Journal approvals"); Dbg.Has(queue, "Capital introduced");
        var checkerView = await GetOkAsync(checker, $"/Journals/Details/{id}");
        var approved = await PostAsync(checker, $"/Journals/Details/{id}", $"/Journals/Approve/{id}", ("comments", "ok"), ("rowVersion", Rv(checkerView, "rowVersion")));
        details = await FollowAsync(checker, approved);
        Dbg.Has(details, "Posted"); Dbg.Has(details, "JV"); Dbg.Has(details, "balanced");

        // ledgers and statements
        var tb = await GetOkAsync(maker, $"/FinancialReports/TrialBalance?projectId={projectId}&to={Today}");
        Dbg.Has(tb, "UI cash in hand"); Dbg.Has(tb, "250,000.00"); Dbg.Has(tb, "Debits equal credits");
        var gl = await GetOkAsync(maker, $"/FinancialReports/GeneralLedger?accountId={cash}&projectId={projectId}&to={Today}");
        Dbg.Has(gl, "Closing balance"); Dbg.Has(gl, "Capital introduced");
        Dbg.Has(await GetOkAsync(maker, $"/FinancialReports/BalanceSheet?projectId={projectId}&asOf={Today}"), "Assets = liabilities + equity");
        Dbg.Has(await GetOkAsync(maker, $"/FinancialReports/CashFlow?projectId={projectId}&from={Today}&to={Today}"), "agrees");
        foreach (var url in new[] { "/FinancialReports", $"/FinancialReports/IncomeStatement?projectId={projectId}", "/FinancialReports/JournalRegister", $"/FinancialReports/Receivables?projectId={projectId}",
                     $"/FinancialReports/Payables?projectId={projectId}", "/FinancialReports/ProjectProfitability", $"/FinancialReports/ProjectExpenses?projectId={projectId}",
                     $"/FinancialReports/CustomerControl?projectId={projectId}", "/FinancialReports/ProjectReconciliation", "/FinancialReports/Cashbook", "/FinancialReports/Bankbook" })
            await GetOkAsync(maker, url);

        // reversal from the screen
        var reversed = await PostAsync(maker, $"/Journals/Details/{id}", $"/Journals/Reverse/{id}", ("reversalDate", Today), ("reason", "Entered against the wrong project"));
        var rv = await FollowAsync(maker, reversed);
        Dbg.Has(rv, "Reversal"); Dbg.Has(rv, "RV");
        Dbg.Has(await GetOkAsync(maker, $"/Journals/Details/{id}"), "Reversed by");
        Dbg.Has(await GetOkAsync(maker, "/Journals?search=Capital"), "reversed");
    }

    [MySqlFact]
    public async Task PostingRules_Queue_Periods_Banking_AndAuthorization_WorkThroughTheUi()
    {
        var maker = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        await EnsureYearAsync(maker);
        var cashGl = await AccountAsync(maker, "CASH", "UI rules cash");
        var bankGl = await AccountAsync(maker, "BANK", "UI rules bank");
        var expense = await AccountAsync(maker, "OPERATING_EXPENSES", "UI rent expense");
        var vendor = await AccountAsync(maker, "VENDOR_PAYABLES", "UI creditors");

        // posting rule: draft -> submit -> second admin approves
        var form = await GetOkAsync(maker, "/PostingRules/Create?eventType=ExpenseApproved");
        Dbg.Has(form, "postingCatalog"); Dbg.Has(form, "Expense / vendor invoice approved");
        var created = await PostAsync(maker, "/PostingRules/Create", "/PostingRules/Create", ("Name", "UI expense accrual"), ("EventType", "ExpenseApproved"), ("Priority", "100"), ("EffectiveFrom", Today),
            ("Lines[0].Side", "Debit"), ("Lines[0].AmountKey", "Amount"), ("Lines[0].AccountId", expense.ToString()), ("Lines[1].Side", "Credit"), ("Lines[1].AmountKey", "Amount"), ("Lines[1].AccountId", vendor.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var ruleId = IdFrom(created, @"/PostingRules/Details/(\d+)");
        var rule = await GetOkAsync(maker, $"/PostingRules/Details/{ruleId}");
        Dbg.Has(rule, "Draft"); Dbg.Has(rule, "Submit for policy approval");
        var submitted = await FollowAsync(maker, await PostAsync(maker, $"/PostingRules/Details/{ruleId}", $"/PostingRules/Submit/{ruleId}", ("rowVersion", Rv(rule, "rowVersion"))));
        Dbg.Has(submitted, "another user must approve");
        var view = await GetOkAsync(checker, $"/PostingRules/Details/{ruleId}");
        var active = await FollowAsync(checker, await PostAsync(checker, $"/PostingRules/Details/{ruleId}", $"/PostingRules/Approve/{ruleId}", ("comments", "Policy approved"), ("rowVersion", Rv(view, "rowVersion"))));
        Dbg.Has(active, "Active");
        Dbg.Has(await GetOkAsync(maker, "/PostingRules"), "UI expense accrual");

        // automatic posting queue
        var q = await GetOkAsync(maker, "/PostingRules/Queue");
        Dbg.Has(q, "Automatic postings"); Dbg.Has(q, "Post pending events");
        var run = await FollowAsync(maker, await PostAsync(maker, "/PostingRules/Queue", "/PostingRules/Run"));
        Dbg.Has(run, "Posting run finished");

        // financial years and period locking (a private past year)
        var y = 1980 + Random.Shared.Next(0, 9);
        if (!await DbAsync(db => db.FiscalYears.AnyAsync(x => x.StartDate == new DateOnly(y, 1, 1))))
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(maker, "/FiscalYears", "/FiscalYears/Create", ("Form.Name", $"UI{y}"), ("Form.StartDate", $"{y}-01-01"), ("Form.EndDate", $"{y}-12-31"))).StatusCode);
        var yearId = await DbAsync(db => db.FiscalYears.Where(x => x.StartDate == new DateOnly(y, 1, 1)).Select(x => x.Id).FirstAsync());
        var yearPage = await GetOkAsync(maker, $"/FiscalYears/Details/{yearId}");
        Dbg.Has(yearPage, "Accounting periods and period locking"); Dbg.Has(yearPage, $"Jan {y}");
        var period = await DbAsync(db => db.AccountingPeriods.Where(p => p.FiscalYearId == yearId && p.PeriodNo == 1).FirstAsync());
        if (period.Status == RealEstate.Domain.Enums.AccountingPeriodStatus.Open)
        {
            var changed = await FollowAsync(maker, await PostAsync(maker, $"/FiscalYears/Details/{yearId}", $"/FiscalYears/ChangePeriod/{yearId}", ("periodId", period.Id.ToString()), ("periodAction", "Close"),
                ("reason", "Month-end close from UI"), ("rowVersion", Convert.ToBase64String(period.RowVersion))));
            Dbg.Has(changed, "is now Closed"); Dbg.Has(changed, "Month-end close from UI");
        }
        Dbg.Has(await GetOkAsync(maker, "/FiscalYears"), $"UI{y}");

        // bank and cash accounts, statement import and reconciliation
        var bankForm = await GetOkAsync(maker, "/Banking/BankForm");
        Dbg.Has(bankForm, "UI rules bank");
        var savedBank = await PostAsync(maker, "/Banking/BankForm", "/Banking/BankForm", ("Code", "B" + Guid.NewGuid().ToString("N")[..8]), ("Name", "UI HBL"), ("BankName", "HBL"), ("AccountTitle", "Company"),
            ("AccountNumber", "001122"), ("CurrencyCode", "PKR"), ("GlAccountId", bankGl.ToString()), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, savedBank.StatusCode);
        var savedCash = await PostAsync(maker, "/Banking/CashForm", "/Banking/CashForm", ("Code", "C" + Guid.NewGuid().ToString("N")[..8]), ("Name", "UI petty cash"), ("GlAccountId", cashGl.ToString()), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, savedCash.StatusCode);
        var banks = await GetOkAsync(maker, "/Banking");
        Dbg.Has(banks, "UI HBL"); Dbg.Has(banks, "UI petty cash");
        var bankId = await DbAsync(db => db.BankAccounts.Where(b => b.GlAccountId == bankGl).Select(b => b.Id).FirstAsync());
        Dbg.Has(await GetOkAsync(maker, $"/Banking/Reconcile/{bankId}"), "Import bank statement");
        var content = new MultipartFormDataContent { { new StringContent(await WebFactory.TokenAsync(maker, $"/Banking/Reconcile/{bankId}")), "__RequestVerificationToken" } };
        var csv = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"Date,Description,Reference,Deposit,Withdrawal\n{Today},Bank charges,,,120\n"));
        csv.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(csv, "file", "statement.csv");
        var imported = await FollowAsync(maker, await maker.PostAsync($"/Banking/Import/{bankId}", content));
        Dbg.Has(imported, "1 new lines"); Dbg.Has(imported, "Bank charges");
        var started = await FollowAsync(maker, await PostAsync(maker, $"/Banking/Reconcile/{bankId}", $"/Banking/Start/{bankId}", ("statementDate", Today), ("statementBalance", "-120")));
        Dbg.Has(started, "Reconciliation in progress"); Dbg.Has(started, "Complete");

        // transfer screen lists the money accounts
        Dbg.Has(await GetOkAsync(maker, "/Journals/Transfer"), "Bank: UI HBL");
        Dbg.Has(await GetOkAsync(maker, "/Journals/Allocate"), "Target projects");
        Dbg.Has(await GetOkAsync(maker, "/Journals/Allocations"), "Project cost allocations");

        // authorization: no accounting permissions -> access denied; project managers read reports only
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/Accounting", "/Journals", "/PostingRules", "/FinancialReports/TrialBalance", "/Banking", "/FiscalYears" })
            Assert.Contains("AccessDenied", (await cashier.GetAsync(url)).Headers.Location!.ToString());
        var pm = await f.LoggedInAsRoleAsync(RoleNames.ProjectManager);
        Assert.Equal(HttpStatusCode.OK, (await pm.GetAsync("/FinancialReports/IncomeStatement")).StatusCode);
        foreach (var url in new[] { "/Journals/Create", "/PostingRules/Create", "/Accounting/CreateAccount", "/Journals/Approvals" })
            Assert.Contains("AccessDenied", (await pm.GetAsync(url)).Headers.Location!.ToString());
        // state-changing posts without an anti-forgery token are rejected
        foreach (var url in new[] { "/Journals/Create", "/Journals/Approve/1", "/Journals/Reverse/1", "/PostingRules/Create", "/PostingRules/Approve/1", "/PostingRules/Run", "/Accounting/CreateAccount",
                     "/FiscalYears/Create", "/FiscalYears/ChangePeriod/1", "/Banking/BankForm", "/Banking/Start/1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/Journals/Details/999999999")).StatusCode);
    }
}
