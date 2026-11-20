using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public partial class Phase06RecoveryWebTests(WebFactory f)
{
    private static string Today => DateTime.UtcNow.AddHours(5).ToString("yyyy-MM-dd");
    private static string Decode(string s) => WebUtility.HtmlDecode(s);
    private static string Rv(string html, string name = "RowVersion") => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static async Task<string> BodyAsync(HttpResponseMessage r) => Decode(await r.Content.ReadAsStringAsync());

    private static async Task<string> GetOkAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
        return Decode(await r.Content.ReadAsStringAsync());
    }

    // ----- test data (through the UI, like Phase04WebTests) -----
    private static async Task<long> CreateProjectWithPriceAsync(HttpClient c)
    {
        var code = "I" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Installments UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var id = long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Projects/Details/(\d+)").Groups[1].Value);
        var rule = await PostAsync(c, $"/Pricing/CreateRule?projectId={id}", "/Pricing/CreateRule", ("ProjectId", id.ToString()), ("Code", "BASE"), ("Name", "Base rate"), ("Kind", "BaseRate"), ("Factor", "Size"),
            ("Scope", "Project"), ("Method", "PerUnit"), ("Value", "1000000"), ("Unit", "Marla"), ("Priority", "0"), ("EffectiveFrom", Today));
        Assert.Equal(HttpStatusCode.Redirect, rule.StatusCode);
        return id;
    }

    private static async Task<long> CreatePropertyAsync(HttpClient c, long projectId)
    {
        var html = await c.GetStringAsync($"/Properties/Create?projectId={projectId}");
        var typeId = Regex.Match(html, "<option value=\"(\\d+)\" data-floor=\"0\"[^>]*>Residential Plot").Groups[1].Value;
        var r = await PostAsync(c, $"/Properties/Create?projectId={projectId}", "/Properties/Create", ("ProjectId", projectId.ToString()), ("PropertyCode", "IN-" + Guid.NewGuid().ToString("N")[..8]), ("PropertyNumber", Random.Shared.Next(1, 100000).ToString()),
            ("PropertyTypeId", typeId), ("Size", "5"), ("MeasurementUnit", "Marla"), ("DevelopmentStatus", "Developed"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Properties/Details/(\d+)").Groups[1].Value);
    }

    private static async Task<long> CreateCustomerAsync(HttpClient c)
    {
        var r = await PostAsync(c, "/Customers/Create", "/Customers/Create", ("FullName", "Installment Customer"), ("IdentityType", "Cnic"),
            ("IdentityNumber", Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString()), ("Mobile", "0300-1234567"), ("Status", "Active"),
            ("Address", "Murree"), ("Addresses[0].AddressType", "Home"), ("Addresses[0].Line1", "House 1"), ("Addresses[0].IsPrimary", "true"),
            ("Nominees[0].FullName", "Nominee One"), ("Nominees[0].Relationship", "Son"), ("Nominees[0].SharePercent", "100"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Customers/Details/(\d+)").Groups[1].Value);
    }

    /// <summary>Project, property, customer and an approved installment booking (5,000,000; 500,000 at booking). Returns (project id, agreement id).</summary>
    private static async Task<(long ProjectId, long AgreementId)> ConfirmedAgreementAsync(HttpClient maker, HttpClient checker)
    {
        var projectId = await CreateProjectWithPriceAsync(maker);
        var propertyId = await CreatePropertyAsync(maker, projectId);
        var customerId = await CreateCustomerAsync(maker);
        var r = await PostAsync(maker, $"/Bookings/Create?projectId={projectId}", "/Bookings/Create", ("ProjectId", projectId.ToString()), ("PaymentMode", "Installment"), ("BookingAmount", "500000"),
            ("SubmitForApproval", "true"), ("IdempotencyKey", Guid.NewGuid().ToString("N")), ("Lines[0].PropertyId", propertyId.ToString()), ("Lines[0].ExtraDiscount", "0"),
            ("Owners[0].CustomerId", customerId.ToString()), ("Owners[0].SharePercent", "100"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var bookingId = long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Bookings/Details/(\d+)").Groups[1].Value);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker, $"/Bookings/Details/{bookingId}", $"/Bookings/Approve/{bookingId}", ("comments", "ok"))).StatusCode);
        var details = await maker.GetStringAsync($"/Bookings/Details/{bookingId}");
        var agreementId = long.Parse(Regex.Match(details, @"/InstallmentAgreements/Details/(\d+)").Groups[1].Value);
        return (projectId, agreementId);
    }

    /// <summary>Creates a standard plan (10% down, 24 monthly on the remainder, 10% possession) through the UI and activates it.</summary>
    private static async Task<long> ActivePlanAsync(HttpClient c, long projectId)
    {
        var code = "STD" + Guid.NewGuid().ToString("N")[..8];
        var created = await PostAsync(c, $"/InstallmentPlans/Create?projectId={projectId}", "/InstallmentPlans/Create", ("ProjectId", projectId.ToString()), ("Code", code), ("Name", "Standard 24 months"), ("PlanType", "StandardProject"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var planId = long.Parse(Regex.Match(created.Headers.Location!.ToString(), @"/InstallmentPlans/Details/(\d+)").Groups[1].Value);

        async Task AddAsync(string chargeName, params (string, string)[] fields)
        {
            var url = $"/InstallmentPlans/AddComponent/{planId}";
            var form = await c.GetStringAsync(url);
            var typeId = Regex.Match(form, $"<option value=\"(\\d+)\"[^>]*>{chargeName}</option>").Groups[1].Value;
            Assert.NotEqual("", typeId);
            var all = new List<(string, string)> { ("ChargeTypeId", typeId), ("RowVersion", Rv(form)), ("Category", "SaleConsideration"), ("GracePeriodDays", "0"), ("MaxWaiverPercent", "0"), ("TaxTreatment", "NotApplicable"), ("TaxRatePercent", "0") };
            all.RemoveAll(x => fields.Any(y => y.Item1 == x.Item1));
            all.AddRange(fields);
            var r = await PostAsync(c, url, $"/InstallmentPlans/AddComponent/{planId}", all.ToArray());
            Assert.True(r.StatusCode == HttpStatusCode.Redirect, "AddComponent " + chargeName + " rv=" + Rv(form) + " failed: " + Strip(await BodyAsync(r)));
        }

        await AddAsync("Down payment", ("Name", "Down payment"), ("AmountBasis", "PercentOfPrice"), ("Percent", "10"), ("Frequency", "OneTime"), ("FirstDueOffsetMonths", "0"));
        await AddAsync("Monthly installment", ("WaiverAllowed", "true"), ("MaxWaiverPercent", "25"), ("Name", "Monthly installment"), ("AmountBasis", "Remainder"), ("Frequency", "Monthly"), ("NumberOfInstallments", "24"), ("FirstDueOffsetMonths", "1"));
        await AddAsync("Possession payment", ("Name", "Possession"), ("AmountBasis", "PercentOfPrice"), ("Percent", "10"), ("Frequency", "OneTime"), ("FirstDueOffsetMonths", "30"));

        var details = await c.GetStringAsync($"/InstallmentPlans/Details/{planId}");
        var activate = await PostAsync(c, $"/InstallmentPlans/Details/{planId}", $"/InstallmentPlans/Activate/{planId}", ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, activate.StatusCode);
        Assert.Contains("text-bg-success\">Active", await c.GetStringAsync($"/InstallmentPlans/Details/{planId}"));
        return planId;
    }

    private static string Strip(string html)
    {
        var i = Math.Max(html.IndexOf("<main", StringComparison.Ordinal), 0);
        var plain = Regex.Replace(html[i..], @"<script.*?</script>|<[^>]+>", " ", RegexOptions.Singleline);
        plain = Regex.Replace(plain, @"\s+", " ");
        return plain[..Math.Min(plain.Length, 1200)];
    }

    private static async Task GenerateScheduleAsync(HttpClient c, long agreementId, long planId)
    {
        var builder = await c.GetStringAsync($"/InstallmentAgreements/Builder/{agreementId}");
        var apply = await PostAsync(c, $"/InstallmentAgreements/Builder/{agreementId}", $"/InstallmentAgreements/ApplyPlan/{agreementId}", ("planId", planId.ToString()), ("startDate", Today), ("rowVersion", Rv(builder, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        var preview = await GetOkAsync(c, $"/InstallmentAgreements/Preview/{agreementId}");
        Dbg.Has(preview, "Generate schedule");
        var generate = await PostAsync(c, $"/InstallmentAgreements/Preview/{agreementId}", $"/InstallmentAgreements/Generate/{agreementId}", ("rowVersion", Rv(preview, "rowVersion")), ("remarks", "initial"));
        Assert.EndsWith($"/InstallmentAgreements/Details/{agreementId}", generate.Headers.Location!.ToString());
    }

    // ----- users, services and data -----
    private sealed record TestUser(Guid Id, string Name, HttpClient Client);

    private async Task<TestUser> NewAdminAsync()
    {
        var email = await f.CreateUserAsync(administrator: true);
        var c = f.NewClient();
        var r = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Password"] = WebFactory.Password, ["__RequestVerificationToken"] = await WebFactory.TokenAsync(c, "/Account/Login") }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using var scope = f.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
        return new TestUser(id, "Web Tester", c);
    }

    /// <summary>Runs Application services as the given user (the services read the user from the HTTP context accessor).</summary>
    private async Task<T> AsAsync<T>(TestUser u, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = f.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Name)], "test");
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = scope.ServiceProvider };
        try { return await action(scope.ServiceProvider); }
        finally { accessor.HttpContext = null; }
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = f.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Agreement with a generated schedule; the first three lines are moved 45 days into the past so they are overdue.</summary>
    private async Task<(long ProjectId, long AgreementId, string AgreementNumber)> OverdueAgreementAsync(HttpClient maker, HttpClient checker, bool backdate = true)
    {
        var (projectId, agreementId) = await ConfirmedAgreementAsync(maker, checker);
        var planId = await ActivePlanAsync(maker, projectId);
        await GenerateScheduleAsync(maker, agreementId, planId);
        var number = await DbAsync(async db => await db.CustomerAgreements.Where(a => a.Id == agreementId).Select(a => a.AgreementNumber).FirstAsync());
        if (backdate)
        {
            var past = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)).AddDays(-45);
            await DbAsync(async db =>
            {
                var scheduleIds = db.PaymentSchedules.Where(s => s.ActiveAgreementId == agreementId).Select(s => s.Id);
                return await db.PaymentScheduleItems.Where(i => scheduleIds.Contains(i.ScheduleId) && i.LineNo <= 3).ExecuteUpdateAsync(s => s.SetProperty(i => i.DueDate, past).SetProperty(i => i.GraceEndDate, past));
            });
        }
        return (projectId, agreementId, number);
    }

    /// <summary>Submits a cash payment as the maker and approves it as the checker (maker-checker). Returns the posted payment.</summary>
    private async Task<PaymentDetailDto> PostedPaymentAsync(TestUser maker, TestUser checker, long agreementId, decimal amount)
    {
        var methodId = await DbAsync(async db => await db.PaymentMethods.Where(m => m.Code == "CASH").Select(m => m.Id).FirstAsync());
        var created = await AsAsync(maker, sp => sp.GetRequiredService<PaymentService>().CreateAsync(new CreatePaymentRequest(agreementId, null, methodId, PaymentSource.Office, amount,
            DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)), null, null, null, null, "web test", AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N"))));
        return await AsAsync(checker, sp => sp.GetRequiredService<PaymentService>().ApproveAsync(created.Id, new ApprovePaymentRequest("ok", null, null, null, null, created.RowVersion)));
    }

    // ----- tests -----
    [MySqlFact]
    public async Task Dashboard_AndReports_Render_WithOverdueData()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (projectId, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client);
        var c = maker.Client;

        var dash = await GetOkAsync(c, $"/Recovery?projectId={projectId}");
        foreach (var t in new[] { "Recovery dashboard", "Overdue amount", "Upcoming", "Defaulters (90+ days)", "Collected this month", "Pending verification", "Pending approval", "Unassigned overdue agreements", "Follow-ups due", "Overdue aging", "Top defaulters", number })
            Dbg.Has(dash, t);
        Assert.Contains("/Payments?status=PendingVerification", dash);
        Assert.Contains("/Payments?status=PendingApproval", dash);
        Assert.Contains($"/Ledgers/Agreement/{agreementId}", dash);

        foreach (var (url, text) in new[]
                 {
                     ($"/Recovery/Upcoming?projectId={projectId}&days=60", "Upcoming dues"), ($"/Recovery/Defaulters?projectId={projectId}&minDaysOverdue=30&minInstallments=1", number),
                     ($"/Recovery/Performance?projectId={projectId}", "Recovery officer performance"), ($"/Recovery/Assignments?projectId={projectId}", "Recovery assignments"),
                     ($"/Recovery/FollowUps?projectId={projectId}", "Follow-up history"), ($"/Recovery/Assign?agreementId={agreementId}", "Recovery officer"),
                     ($"/Recovery/RecordFollowUp?agreementId={agreementId}", "Follow-up date"), ($"/Recovery/Assign?term={number}", number), ($"/Recovery/RecordFollowUp?term={number}", "Select"),
                 })
            Dbg.Has(await GetOkAsync(c, url), text);
    }

    [MySqlFact]
    public async Task Overdue_ListsTheOverdueLine_WithAgingAndActions_AndFilters()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (projectId, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client);
        var html = await GetOkAsync(maker.Client, $"/Recovery/Overdue?projectId={projectId}&search={number}&days=30&sortBy=amount");
        Dbg.Has(html, number); Dbg.Has(html, "Days overdue"); Dbg.Has(html, "Late fee"); Dbg.Has(html, "Total overdue"); Dbg.Has(html, "Unassigned");
        Assert.Contains($"/Ledgers/Agreement/{agreementId}", html);
        Assert.Contains($"/Recovery/RecordFollowUp?agreementId={agreementId}", html);
        Assert.Matches(@"data-label=""Days overdue"" class=""text-end"">4[5-9]<", html);
        // a minimum above the age of the lines hides them and shows the empty state
        Dbg.Has(await GetOkAsync(maker.Client, $"/Recovery/Overdue?projectId={projectId}&search={number}&days=100"), "No overdue installments match the filters");
    }

    [MySqlFact]
    public async Task Assignment_CanBeCreated_Duplicated_AndClosed()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (projectId, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client);
        var c = maker.Client;

        var form = await GetOkAsync(c, $"/Recovery/Assign?agreementId={agreementId}");
        Assert.Contains($"value=\"{checker.Id}\"", form);
        var missing = await PostAsync(c, $"/Recovery/Assign?agreementId={agreementId}", "/Recovery/Assign", ("AgreementId", agreementId.ToString()), ("OfficerUserId", Guid.Empty.ToString()));
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Dbg.Has(await BodyAsync(missing), "Select the recovery officer");

        var ok = await PostAsync(c, $"/Recovery/Assign?agreementId={agreementId}", "/Recovery/Assign", ("AgreementId", agreementId.ToString()), ("OfficerUserId", checker.Id.ToString()), ("Notes", "Call weekly"));
        Assert.True(ok.StatusCode == HttpStatusCode.Redirect, "Assign failed: " + Strip(await BodyAsync(ok)));
        var list = await GetOkAsync(c, $"/Recovery/Assignments?projectId={projectId}&status=Active");
        Dbg.Has(list, number); Dbg.Has(list, "Web Tester"); Dbg.Has(list, "Close assignment");

        var dup = await PostAsync(c, $"/Recovery/Assign?agreementId={agreementId}", "/Recovery/Assign", ("AgreementId", agreementId.ToString()), ("OfficerUserId", checker.Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Dbg.Has(await BodyAsync(dup), "already has an active recovery assignment");

        // the overdue list now shows the officer
        Dbg.Has(await GetOkAsync(c, $"/Recovery/Overdue?projectId={projectId}&search={number}"), "Web Tester");

        var id = Regex.Match(list, @"id=""close(\d+)""").Groups[1].Value;
        var noReason = await PostAsync(c, $"/Recovery/Assignments?projectId={projectId}", $"/Recovery/Close/{id}", ("reason", ""), ("rowVersion", Rv(list, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, noReason.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/Recovery/Assignments?projectId={projectId}&status=Active"), "Close assignment"); // still active
        var closed = await PostAsync(c, $"/Recovery/Assignments?projectId={projectId}", $"/Recovery/Close/{id}", ("reason", "Customer paid in full"), ("rowVersion", Rv(list, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        var after = await GetOkAsync(c, $"/Recovery/Assignments?projectId={projectId}&status=Closed");
        Dbg.Has(after, "Customer paid in full"); Dbg.Has(after, "Closed");
        Assert.DoesNotContain("Close assignment", after);
    }

    [MySqlFact]
    public async Task FollowUp_Promise_IsValidated_Saved_AndListed()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (projectId, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client);
        var c = maker.Client;
        var url = $"/Recovery/RecordFollowUp?agreementId={agreementId}";
        Dbg.Has(await GetOkAsync(c, url), "followUpOutcome");

        var bad = await PostAsync(c, url, "/Recovery/RecordFollowUp", ("AgreementId", agreementId.ToString()), ("FollowUpDate", Today), ("Method", "Call"), ("Outcome", "PromiseToPay"), ("Notes", "Customer will pay on Friday"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badHtml = await BodyAsync(bad);
        Dbg.Has(badHtml, "Enter the promised amount"); Dbg.Has(badHtml, "Enter the promised payment date");

        var promised = DateTime.UtcNow.AddHours(5).AddDays(7).ToString("yyyy-MM-dd");
        var ok = await PostAsync(c, url, "/Recovery/RecordFollowUp", ("AgreementId", agreementId.ToString()), ("FollowUpDate", Today), ("Method", "Call"), ("Outcome", "PromiseToPay"),
            ("PromisedAmount", "250000"), ("PromisedDate", promised), ("NextFollowUpDate", promised), ("Notes", "Customer will pay on Friday"));
        Assert.True(ok.StatusCode == HttpStatusCode.Redirect, "Follow-up failed: " + Strip(await BodyAsync(ok)));
        Assert.Contains($"/Recovery/FollowUps?agreementId={agreementId}", ok.Headers.Location!.ToString());

        var list = await GetOkAsync(c, $"/Recovery/FollowUps?agreementId={agreementId}&outcome=PromiseToPay");
        Dbg.Has(list, number); Dbg.Has(list, "Promise to pay"); Dbg.Has(list, "Customer will pay on Friday"); Dbg.Has(list, "250,000.00"); Dbg.Has(list, promised);
        // a non-promise outcome ignores the promise fields
        var plain = await PostAsync(c, url, "/Recovery/RecordFollowUp", ("AgreementId", agreementId.ToString()), ("FollowUpDate", Today), ("Method", "Visit"), ("Outcome", "NoAnswer"), ("Notes", "Nobody at home"));
        Assert.Equal(HttpStatusCode.Redirect, plain.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/Recovery/FollowUps?search=Nobody"), "Nobody at home");
    }

    [MySqlFact]
    public async Task Receipts_List_Details_AndPdf_FirstIsOriginal_LaterAreDuplicates()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (projectId, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client, backdate: false);
        var payment = await PostedPaymentAsync(maker, checker, agreementId, 100_000m);
        var receipt = payment.Receipt!;
        var c = maker.Client;

        var list = await GetOkAsync(c, $"/Receipts?projectId={projectId}&search={receipt.ReceiptNumber}&from={Today}&to={Today}");
        Dbg.Has(list, receipt.ReceiptNumber); Dbg.Has(list, payment.PaymentNumber); Dbg.Has(list, number);
        Assert.Contains($"/Payments/Details/{payment.Id}", list);
        Dbg.Has(await GetOkAsync(c, "/Receipts?search=NO-SUCH-RECEIPT"), "No receipts found");

        var details = await GetOkAsync(c, $"/Receipts/Details/{receipt.Id}");
        Dbg.Has(details, "Download PDF"); Dbg.Has(details, "DUPLICATE COPY"); Dbg.Has(details, "Never");
        Assert.Contains($"/Payments/Details/{payment.Id}", details);

        async Task<(string Type, string Body)> DownloadAsync()
        {
            var r = await PostAsync(c, $"/Receipts/Details/{receipt.Id}", $"/Receipts/Pdf/{receipt.Id}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (r.Content.Headers.ContentType!.MediaType!, Encoding.Latin1.GetString(await r.Content.ReadAsByteArrayAsync()));
        }
        var first = await DownloadAsync();
        Assert.Equal("application/pdf", first.Type); Assert.StartsWith("%PDF", first.Body);
        Assert.Contains("(ORIGINAL)", first.Body); Assert.Contains(receipt.ReceiptNumber, first.Body);
        var second = await DownloadAsync();
        Assert.StartsWith("%PDF", second.Body); Assert.Contains("(DUPLICATE COPY 1)", second.Body); Assert.DoesNotContain("(ORIGINAL)", second.Body.Replace(receipt.ReceiptNumber + " - ORIGINAL", ""));

        var copies = await DbAsync(async db => await db.PaymentReceipts.Where(x => x.Id == receipt.Id).Select(x => x.CopiesPrinted).FirstAsync());
        Assert.Equal(2, copies);
        Dbg.Has(await GetOkAsync(c, $"/Receipts/Details/{receipt.Id}"), "Copies printed");
    }

    [MySqlFact]
    public async Task Recovery_IsForbidden_WithoutPermission_AndPdfRequiresAntiForgery()
    {
        var plain = await f.LoggedInAsync(administrator: false);
        foreach (var url in new[] { "/Recovery", "/Recovery/Performance", "/Receipts", "/Recovery/Assign", "/Recovery/RecordFollowUp" })
        {
            var r = await plain.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        // a Recovery Officer has no dashboard or performance access but can open the dues lists (scoped to their assignments)
        var officer = await f.LoggedInAsRoleAsync(RoleNames.RecoveryOfficer);
        foreach (var url in new[] { "/Recovery", "/Recovery/Performance" })
        {
            var r = await officer.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        foreach (var url in new[] { "/Recovery/Overdue", "/Recovery/Assignments", "/Recovery/FollowUps", "/Recovery/Defaulters", "/Recovery/Upcoming" })
            Assert.Equal(HttpStatusCode.OK, (await officer.GetAsync(url)).StatusCode);
        var admin = await NewAdminAsync();
        var noToken = await admin.Client.PostAsync("/Receipts/Pdf/1", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }
}
