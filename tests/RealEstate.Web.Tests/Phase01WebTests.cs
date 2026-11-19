using System.Net;
using System.Text.RegularExpressions;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class Phase01WebTests(WebFactory f)
{
    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static async Task<string> DistributionVersionAsync(HttpClient c, long projectId)
    {
        var html = await c.GetStringAsync($"/Land/Distribution?projectId={projectId}");
        var m = Regex.Match(html, "name=\"DistributionVersion\" value=\"([^\"]+)\"");
        Assert.True(m.Success, "Distribution form must carry its version.");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    private static async Task<long> CreateProjectAsync(HttpClient c, string code, string area = "100")
    {
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create",
            ("ProjectCode", code), ("ProjectName", "UI Project " + code), ("City", "Rawalpindi"), ("InitialArea", area), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Projects/Details/(\d+)").Groups[1].Value);
    }

    [MySqlFact]
    public async Task Admin_CanOpenEveryPhase1Screen()
    {
        var c = await f.LoggedInAsync(administrator: true);
        foreach (var (url, text) in new[]
                 {
                     ("/Company", "Company profile"), ("/Projects", "Projects"), ("/Projects/Create", "New project"), ("/Users", "Users"), ("/Users/Create", "New user"),
                     ("/Roles", "Roles"), ("/Roles/Create", "New role"), ("/Permissions", "Permission matrix"),
                 })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
            Assert.Contains(text, await r.Content.ReadAsStringAsync());
        }
        var roles = await c.GetStringAsync("/Roles");
        foreach (var role in new[] { "Director", "Project Manager", "Sales Manager", "Accountant", "Cashier", "Recovery Officer", "Inventory Manager", "Customer Support" })
            Assert.Contains(role, roles);
    }

    [MySqlFact]
    public async Task Project_Create_Details_Land_Screens_AndAcquisitionUpdatesArea()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var id = await CreateProjectAsync(c, "UI" + Guid.NewGuid().ToString("N")[..8]);

        var details = await c.GetStringAsync($"/Projects/Details/{id}");
        Assert.Contains("UI Project", details);
        Assert.Contains("Land management", details);
        Assert.Contains("Status history", details);

        foreach (var action in new[] { "Index", "Distribution", "History", "Documents", "Acquire", "Adjust" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/Land/{action}?projectId={id}")).StatusCode);

        var acq = await PostAsync(c, $"/Land/Acquire?projectId={id}", "/Land/Acquire", ("ProjectId", id.ToString()), ("AcquisitionDate", DateTime.Today.ToString("yyyy-MM-dd")),
            ("SellerName", "Seller"), ("Area", "25.5"), ("RowVersion", ""));
        Assert.Equal(HttpStatusCode.Redirect, acq.StatusCode);
        var land = await c.GetStringAsync($"/Land/Index?projectId={id}");
        Assert.Contains("125.5000", land);

        var firstVersion = await DistributionVersionAsync(c, id);
        var dist = await PostAsync(c, $"/Land/Distribution?projectId={id}", "/Land/Distribution", ("ProjectId", id.ToString()), ("DistributionVersion", firstVersion),
            ("Areas[Residential]", "60"), ("Areas[Roads]", "20"), ("Reason", "Plan"));
        Assert.Equal(HttpStatusCode.Redirect, dist.StatusCode);
        Assert.Contains("45.5000", await c.GetStringAsync($"/Land/Distribution?projectId={id}")); // unclassified remainder

        // A form loaded before the save above is stale: rejected, current values shown, saved values kept.
        var stale = await PostAsync(c, $"/Land/Distribution?projectId={id}", "/Land/Distribution", ("ProjectId", id.ToString()), ("DistributionVersion", firstVersion),
            ("Areas[Residential]", "10"), ("Reason", "Stale edit"));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Contains("changed by another user", staleHtml);
        Assert.Contains("45.5000", staleHtml);

        var over = await PostAsync(c, $"/Land/Distribution?projectId={id}", "/Land/Distribution", ("ProjectId", id.ToString()), ("DistributionVersion", await DistributionVersionAsync(c, id)),
            ("Areas[Residential]", "500"), ("Reason", "Too much"));
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        Assert.Contains("exceeds the project area", await over.Content.ReadAsStringAsync());
        Assert.Contains("Acquisition", await c.GetStringAsync($"/Land/History?projectId={id}"));
    }

    [MySqlFact]
    public async Task Project_Form_ShowsValidationErrors_AndDuplicateCode()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var bad = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", ""), ("ProjectName", ""), ("City", ""), ("InitialArea", "0"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("text-danger", await bad.Content.ReadAsStringAsync());
        var code = "DUP" + Guid.NewGuid().ToString("N")[..6];
        await CreateProjectAsync(c, code);
        var dup = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Again"), ("City", "X"), ("InitialArea", "5"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task Cashier_CannotOpenAdminScreens_AndSeesNoUnassignedProjects()
    {
        var admin = await f.LoggedInAsync(administrator: true);
        var code = "SECX" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var id = await CreateProjectAsync(admin, code);
        var c = await f.LoggedInAsRoleAsync("Cashier");
        foreach (var url in new[] { "/Roles", "/Permissions", "/Users", "/Users/Create", "/Company", "/Projects/Create", $"/Projects/Details/{id}", $"/Land/Index?projectId={id}", $"/Projects/Access/{id}" })
        {
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        var list = await c.GetStringAsync("/Projects");
        Assert.DoesNotContain(code, list);
        Assert.DoesNotContain("href=\"/Roles\"", await c.GetStringAsync("/"));
    }

    [MySqlFact]
    public async Task Role_Create_AppearsInList_AndPermissionMatrixSaves()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var name = "Auditor" + Guid.NewGuid().ToString("N")[..5];
        var r = await PostAsync(c, "/Roles/Create", "/Roles/Create", ("Name", name), ("Description", "Read only"), ("PermissionCodes", "projects.view"), ("PermissionCodes", "land.view"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains(name, await c.GetStringAsync("/Roles"));
        var matrix = await c.GetStringAsync("/Permissions");
        var roleId = Regex.Match(matrix, @"name=""grant"" value=""([0-9a-f-]{36})\|projects\.view""[^>]*aria-label=""" + name).Groups[1].Value;
        Assert.NotEmpty(roleId);
        var save = await PostAsync(c, "/Permissions", "/Permissions", ("grant", $"{roleId}|projects.view"), ("grant", $"{roleId}|company.view"));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
    }

    [MySqlFact]
    public async Task User_Create_FromForm_ShowsInList()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var email = $"ui{Guid.NewGuid():N}@example.com";
        var r = await PostAsync(c, "/Users/Create", "/Users/Create", ("Email", email), ("FullName", "Form User"), ("Password", WebFactory.Password), ("IsActive", "true"), ("Roles", "Cashier"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var list = await c.GetStringAsync($"/Users?search={email}");
        Assert.Contains("Form User", list);
        Assert.Contains("Cashier", list);
        var weak = await PostAsync(c, "/Users/Create", "/Users/Create", ("Email", $"w{Guid.NewGuid():N}@example.com"), ("FullName", "Weak"), ("Password", "weakpassword12"));
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
    }

    [MySqlFact]
    public async Task Company_Save_Persists()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var page = await c.GetStringAsync("/Company");
        var rv = Regex.Match(page, @"name=""RowVersion""[^>]*value=""([^""]*)""").Groups[1].Value;
        var r = await PostAsync(c, "/Company", "/Company", ("Name", "Serene Estates"), ("CurrencyCode", "PKR"), ("CurrencySymbol", "Rs"), ("CurrencyDecimalPlaces", "2"), ("RoundingMode", "Nearest"),
            ("FiscalYearStartMonth", "7"), ("TimeZoneId", "Asia/Karachi"), ("MaxUploadSizeMb", "10"), ("AllowedFileExtensions", "pdf,png"), ("RowVersion", System.Net.WebUtility.HtmlDecode(rv)));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Serene Estates", await c.GetStringAsync("/Company"));
    }
}
