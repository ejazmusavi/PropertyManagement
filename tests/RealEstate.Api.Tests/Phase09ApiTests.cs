using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Projects;
using RealEstate.Application.Reporting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase09ApiTests(ApiFactory factory)
{
    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return c;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [MySqlFact]
    public async Task ReportEndpoints_RequireAuthentication_AndPermissions()
    {
        var anon = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/reports", "/api/v1/reports/lookups", "/api/v1/reports/daily-sales", "/api/v1/reports/daily-sales/export?format=pdf", "/api/v1/dashboards/company", "/api/v1/dashboards/projects/1" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);

        var cashier = await ClientAsync(RoleNames.Cashier);                 // no reports.view, no reports.export
        foreach (var url in new[] { "/api/v1/reports/daily-sales", "/api/v1/reports/daily-sales/export?format=pdf", "/api/v1/reports/trial-balance", "/api/v1/dashboards/company", "/api/v1/dashboards/projects/1" })
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(url)).StatusCode);
        var catalog = await JsonAsync(await cashier.GetAsync("/api/v1/reports"));
        Assert.Equal(0, catalog.GetArrayLength());                          // the catalog lists only what the caller may open
    }

    [MySqlFact]
    public async Task Catalog_Reports_Filters_AndExports_Work()
    {
        var admin = await ClientAsync();
        var catalog = await JsonAsync(await admin.GetAsync("/api/v1/reports"));
        var keys = catalog.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        foreach (var k in new[] { "customer-statement", "customer-ledger", "customer-properties", "payment-history", "installment-history", "outstanding-balance", "overdue", "daily-sales", "monthly-sales",
            "project-sales", "block-sales", "category-sales", "salesperson-sales", "agent-commissions", "cancellations", "daily-collection", "monthly-collection", "project-collection", "payment-methods",
            "cashier-collection", "bank-collection", "advance-payments", "trial-balance", "general-ledger", "balance-sheet", "income-statement", "cash-flow", "accounts-receivable", "accounts-payable",
            "project-profitability", "project-expenses", "customer-control", "project-reconciliation" })
            Assert.Contains(k, keys);
        var daily = catalog.EnumerateArray().First(e => e.GetProperty("key").GetString() == "daily-sales");
        var filters = daily.GetProperty("filters").EnumerateArray().Select(f => f.GetString()).ToList();
        foreach (var f in new[] { "From", "To", "Project", "Block", "Customer", "TransactionType", "Status" }) Assert.Contains(f, filters);

        var lookups = await JsonAsync(await admin.GetAsync("/api/v1/reports/lookups"));
        Assert.True(lookups.GetProperty("paymentMethods").GetArrayLength() >= 6);

        // run with query filters
        var report = await JsonAsync(await admin.GetAsync("/api/v1/reports/daily-collection?from=2000-01-01&to=2100-01-01&page=1&pageSize=5"));
        Assert.Equal("Daily collection", report.GetProperty("title").GetString());
        Assert.Equal(5, report.GetProperty("columns").GetArrayLength());
        Assert.Contains(report.GetProperty("filters").EnumerateArray(), f => f.GetProperty("label").GetString() == "From");

        // validation and not found
        var bad = await admin.GetAsync("/api/v1/reports/daily-collection?from=2026-02-01&to=2026-01-01");
        Assert.True(bad.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, bad.StatusCode.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/reports/nope")).StatusCode);
        var needCustomer = await admin.GetAsync("/api/v1/reports/customer-ledger");
        Assert.True(needCustomer.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, needCustomer.StatusCode.ToString());

        // exports
        var pdf = await admin.GetAsync("/api/v1/reports/daily-collection/export?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var xlsx = await admin.GetAsync("/api/v1/reports/trial-balance/export?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Contains("spreadsheetml", xlsx.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".xlsx", xlsx.Content.Headers.ContentDisposition!.FileNameStar ?? xlsx.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/reports/daily-collection/export?format=doc")).StatusCode);

        // financial statement through the same endpoint
        var tb = await JsonAsync(await admin.GetAsync("/api/v1/reports/trial-balance"));
        Assert.Equal("Financial reports", tb.GetProperty("group").GetString());
    }

    [MySqlFact]
    public async Task Dashboards_ReturnFigures_AndProjectAccessIsEnforced()
    {
        var admin = await ClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("D" + Guid.NewGuid().ToString("N")[..10], "Dashboard API", null, "Murree", null, null, null, 40m, MeasurementUnit.Kanal, null, null));
        var pid = (await created.Content.ReadFromJsonAsync<ProjectDetailDto>())!.Id;

        var company = await JsonAsync(await admin.GetAsync("/api/v1/dashboards/company"));
        foreach (var p in new[] { "totalProjects", "totalLandAreaSqFt", "totalSaleableAreaSqFt", "totalInventory", "availableProperties", "soldProperties", "totalBookingValue", "totalCollections",
            "customerReceivables", "overdueAmount", "cashBalance", "bankBalance", "approvedExpenses", "revenue", "profitLoss", "checks", "projects" })
            Assert.True(company.TryGetProperty(p, out _), p);
        Assert.True(company.GetProperty("ledgerAvailable").GetBoolean());
        Assert.True(company.GetProperty("totalProjects").GetInt32() >= 1);

        var project = await JsonAsync(await admin.GetAsync($"/api/v1/dashboards/projects/{pid}"));
        Assert.Equal(pid, project.GetProperty("projectId").GetInt64());
        Assert.True(project.GetProperty("projectArea").GetDecimal() == 40m);
        foreach (var p in new[] { "landDistribution", "inventoryByCategory", "salesValue", "bookingValue", "collections", "outstanding", "overdue", "approvedExpenses", "ledgerProfit", "operationalResult", "checks" })
            Assert.True(project.TryGetProperty(p, out _), p);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/dashboards/projects/999999999")).StatusCode);
        var sales = await ClientAsync(RoleNames.SalesManager);               // has reports.view but is not assigned to the new project
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/api/v1/dashboards/projects/{pid}")).StatusCode);
        var scoped = await JsonAsync(await sales.GetAsync("/api/v1/dashboards/company"));
        Assert.False(scoped.GetProperty("ledgerAvailable").GetBoolean());
        Assert.Equal(0, scoped.GetProperty("totalProjects").GetInt32());     // scoped to the user's (no) projects
        Assert.Equal(JsonValueKind.Null, scoped.GetProperty("revenue").ValueKind);
    }
}
