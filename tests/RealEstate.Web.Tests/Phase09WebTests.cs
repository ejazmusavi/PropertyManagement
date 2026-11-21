using System.Net;
using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 9 screens: dashboards, report catalog, filtered report pages, exports, permissions (rendered-HTML tests through the MVC pipeline).</summary>
[Collection("web")]
public class Phase09WebTests(WebFactory f)
{
    private static string Decode(string s) => WebUtility.HtmlDecode(s);

    private static async Task<string> GetOkAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
        return Decode(await r.Content.ReadAsStringAsync());
    }

    private static async Task<long> ProjectAsync(HttpClient c)
    {
        var code = "R" + Guid.NewGuid().ToString("N")[..9];
        var data = new Dictionary<string, string> { ["ProjectCode"] = code, ["ProjectName"] = "Reports UI " + code, ["City"] = "Murree", ["InitialArea"] = "50", ["MeasurementUnit"] = "Kanal",
            ["__RequestVerificationToken"] = await WebFactory.TokenAsync(c, "/Projects/Create") };
        var r = await c.PostAsync("/Projects/Create", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Projects/Details/(\d+)").Groups[1].Value);
    }

    [MySqlFact]
    public async Task Dashboards_Catalog_Reports_AndExports_WorkThroughTheUi()
    {
        var admin = await f.LoggedInAsync(administrator: true);
        var pid = await ProjectAsync(admin);

        // navigation
        var home = await GetOkAsync(admin, "/");
        Dbg.Has(home, "Management dashboard"); Dbg.Has(home, "All reports");

        // company dashboard (spec 9.1) and project dashboard (9.2)
        var company = await GetOkAsync(admin, "/Dashboards/Company");
        foreach (var t in new[] { "Company dashboard", "Total projects", "Total land area", "Total saleable area", "Total inventory", "Available", "Sold", "Total booking value", "Total collections",
            "Customer receivables", "Overdue amount", "Cash balance", "Bank balance", "Expenses", "Revenue", "Profit / (loss)", "Reconciliation with financial records", "Collections (all time)" })
            Dbg.Has(company, t);
        Dbg.Has(await GetOkAsync(admin, "/Dashboards/Company?from=2026-01-01&to=2026-12-31"), "From 2026-01-01");
        var project = await GetOkAsync(admin, $"/Dashboards/Project/{pid}");
        foreach (var t in new[] { "Project area", "Land distribution", "Property inventory", "Sales", "Collections", "Outstanding", "Overdue installments", "Project expenses", "Project profitability" })
            Dbg.Has(project, t);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Dashboards/Project/999999999")).StatusCode);

        // catalog groups every report
        var catalog = await GetOkAsync(admin, "/Reports");
        foreach (var t in new[] { "Customer reports", "Sales reports", "Collection reports", "Financial reports", "Customer statement", "Customer ledger", "Customer property list", "Payment history", "Installment history",
            "Outstanding balance", "Overdue report", "Daily sales", "Monthly sales", "Project-wise sales", "Block-wise sales", "Sales by property category", "Sales by salesperson", "Agent commissions",
            "Cancellation report", "Daily collection", "Monthly collection", "Project collection", "Payment method summary", "Cashier collection", "Bank collection", "Advance payment report", "Trial balance",
            "Balance sheet", "Income statement", "Cash flow statement" })
            Dbg.Has(catalog, t);

        // filter form first, then the report, with every supported filter present
        var form = await GetOkAsync(admin, "/Reports/Run?key=daily-sales");
        Dbg.Has(form, "Set the filters and press Show."); Dbg.Has(form, "Block"); Dbg.Has(form, "Customer"); Dbg.Has(form, "Sale type"); Dbg.Has(form, "Status"); Dbg.Has(form, "Project");
        var collection = await GetOkAsync(admin, "/Reports/Run?key=daily-collection");
        Dbg.Has(collection, "Payment method"); Dbg.Has(collection, "Cash");
        var empty = await GetOkAsync(admin, $"/Reports/Run?key=daily-sales&show=true&projectId={pid}");
        Dbg.Has(empty, "No data matches the filters."); Dbg.Has(empty, "Generated");
        var shown = await GetOkAsync(admin, $"/Reports/Run?key=trial-balance&show=true&projectId={pid}");
        Dbg.Has(shown, "Trial balance"); Dbg.Has(shown, "Export PDF"); Dbg.Has(shown, "Export Excel");
        Dbg.Has(await GetOkAsync(admin, "/Reports/Run?key=customer-ledger&show=true"), "Choose a customer.");
        Dbg.Has(await GetOkAsync(admin, "/Reports/Run?key=project-expenses&show=true"), "Choose a project.");
        Dbg.Has(await GetOkAsync(admin, "/Reports/Run?key=general-ledger&show=true"), "Choose an account.");
        Dbg.Has(await GetOkAsync(admin, "/Reports/Run?key=daily-sales&show=true&from=2026-02-01&to=2026-01-01"), "start date must not be after the end date");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Reports/Run?key=nope")).StatusCode);

        // exports (all rows, PDF and Excel)
        var pdf = await admin.GetAsync($"/Reports/Export?key=daily-collection&format=pdf&projectId={pid}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var xlsx = await admin.GetAsync($"/Reports/Export?key=trial-balance&format=xlsx&projectId={pid}");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Contains("spreadsheetml", xlsx.Content.Headers.ContentType!.MediaType);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]));
        // a failed export (missing required filter) returns to the filter page with the message
        var bad = await admin.GetAsync("/Reports/Export?key=customer-ledger&format=pdf");
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
    }

    [MySqlFact]
    public async Task ReportScreens_AreRestrictedByPermission()
    {
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);       // no reports.view
        foreach (var url in new[] { "/Dashboards/Company", "/Dashboards/Project/1" })
        {
            var r = await cashier.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        Dbg.Has(await GetOkAsync(cashier, "/Reports"), "You do not have access to any report.");
        var denied = await cashier.GetAsync("/Reports/Run?key=daily-sales&show=true");
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await cashier.GetAsync("/Reports/Export?key=daily-collection&format=pdf")).StatusCode);

        var sales = await f.LoggedInAsRoleAsync(RoleNames.SalesManager);    // reports.view + reports.export, no accounting.reports
        Dbg.Has(await GetOkAsync(sales, "/Dashboards/Company"), "Company dashboard");
        Dbg.Has(await GetOkAsync(sales, "/Dashboards/Company"), "need the financial reports permission");
        var catalog = await GetOkAsync(sales, "/Reports");
        Dbg.Has(catalog, "Daily sales"); Assert.DoesNotContain("Trial balance", catalog);
        Assert.NotEqual(HttpStatusCode.OK, (await sales.GetAsync("/Reports/Run?key=trial-balance&show=true")).StatusCode);
        var ok = await sales.GetAsync("/Reports/Export?key=daily-sales&format=xlsx");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
