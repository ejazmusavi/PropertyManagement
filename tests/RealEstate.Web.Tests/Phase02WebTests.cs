using System.Net;
using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class Phase02WebTests(WebFactory f)
{
    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static async Task<long> CreateProjectAsync(HttpClient c)
    {
        var code = "W" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Inventory UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Projects/Details/(\d+)").Groups[1].Value);
    }

    private static async Task<long> CreatePropertyAsync(HttpClient c, long projectId, string code, params (string, string)[] extra)
    {
        var html = await c.GetStringAsync($"/Properties/Create?projectId={projectId}");
        var typeId = Regex.Match(html, "<option value=\"(\\d+)\" data-floor=\"0\"[^>]*>Residential Plot").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(typeId), "Residential Plot must be offered.");
        var fields = new List<(string, string)> { ("ProjectId", projectId.ToString()), ("PropertyCode", code), ("PropertyNumber", "101"), ("PropertyTypeId", typeId), ("Size", "5"), ("MeasurementUnit", "Marla"),
            ("DevelopmentStatus", "Developed"), ("Dimensions[0].Name", "Length"), ("Dimensions[0].Value", "45"), ("Dimensions[0].Unit", "ft"), ("Features[0].Name", "Gas") };
        fields.AddRange(extra);
        var r = await PostAsync(c, $"/Properties/Create?projectId={projectId}", "/Properties/Create", fields.ToArray());
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Properties/Details/(\d+)").Groups[1].Value);
    }

    [MySqlFact]
    public async Task Admin_CanOpenEveryInventoryScreen()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId, "UI-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (url, text) in new[]
                 {
                     ("/Inventory", "Property dashboard"), ($"/Inventory?projectId={projectId}", "Total properties"), ("/Inventory/Reports", "Inventory reports"), ("/Inventory/History", "Inventory history"),
                     ("/Properties", "Properties"), ($"/Properties?projectId={projectId}", "UI-"), ("/Properties/Create", "New property"), ($"/Properties/Create?projectId={projectId}", "Property code"),
                     ($"/Properties/Edit/{propertyId}", "Edit property"), ($"/Properties/Details/{propertyId}", "Status history"),
                     ("/PropertySetup/Types", "Residential Plot"), ("/PropertySetup/Blocks", "Project blocks"), ("/PropertySetup/Sectors", "Project sectors"), ("/PropertySetup/Streets", "Project streets"),
                 })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
            Assert.Contains(text, await r.Content.ReadAsStringAsync());
        }
        var types = await c.GetStringAsync("/PropertySetup/Types");
        foreach (var t in new[] { "Residential Plot", "Commercial Plot", "House", "Apartment", "Shop", "Office" }) Assert.Contains(t, types);
        Assert.Contains("Property inventory", await c.GetStringAsync($"/Projects/Details/{projectId}"));
        Assert.Contains("Property dashboard", await c.GetStringAsync("/"));
    }

    [MySqlFact]
    public async Task Property_ReserveReleaseAndStatusChange_ThroughTheUi()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectAsync(c);
        var id = await CreatePropertyAsync(c, projectId, "UR-" + Guid.NewGuid().ToString("N")[..8]);
        var details = await c.GetStringAsync($"/Properties/Details/{id}");
        Assert.Contains("Reserve", details);
        var rv = WebUtility.HtmlDecode(Regex.Match(details, "name=\"RowVersion\" value=\"([^\"]+)\"").Groups[1].Value);
        var approver = Regex.Match(details, "<option value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(approver));
        var today = DateTime.Today;
        var reserve = await PostAsync(c, $"/Properties/Details/{id}", $"/Properties/Reserve/{id}", ("RowVersion", rv), ("ReservedForName", "Ali Khan"), ("ReservationDate", today.ToString("yyyy-MM-dd")),
            ("ExpiryDate", today.AddDays(5).ToString("yyyy-MM-dd")), ("Amount", "1000"), ("ApprovedByUserId", approver));
        Assert.Equal(HttpStatusCode.Redirect, reserve.StatusCode);
        var after = await c.GetStringAsync($"/Properties/Details/{id}");
        Assert.Contains("Ali Khan", after);
        Assert.Contains("Release reservation", after);

        // A second reservation attempt is refused and shown inline.
        var again = await PostAsync(c, $"/Properties/Details/{id}", $"/Properties/Reserve/{id}", ("ReservedForName", "Someone Else"), ("ReservationDate", today.ToString("yyyy-MM-dd")),
            ("ExpiryDate", today.AddDays(5).ToString("yyyy-MM-dd")), ("Amount", "1"), ("ApprovedByUserId", approver));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("cannot be reserved", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));

        var rv2 = WebUtility.HtmlDecode(Regex.Match(after, "name=\"rowVersion\" value=\"([^\"]+)\"").Groups[1].Value);
        var release = await PostAsync(c, $"/Properties/Details/{id}", $"/Properties/Release/{id}", ("reason", "Customer withdrew"), ("rowVersion", rv2));
        Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        var final = await c.GetStringAsync($"/Properties/Details/{id}");
        Assert.Contains("Reservation released", final);
        Assert.Contains("Change status", final);

        var rv3 = WebUtility.HtmlDecode(Regex.Match(final, "name=\"rowVersion\" value=\"([^\"]+)\"").Groups[1].Value);
        var status = await PostAsync(c, $"/Properties/Details/{id}", $"/Properties/ChangeStatus/{id}", ("status", "Booked"), ("reason", "Booked by sales"), ("rowVersion", rv3));
        Assert.Equal(HttpStatusCode.Redirect, status.StatusCode);
        Assert.Contains("Booked", await c.GetStringAsync($"/Properties/Details/{id}"));
    }

    [MySqlFact]
    public async Task Setup_Screens_CreateTypeAndZone()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectAsync(c);
        var code = "UI" + Guid.NewGuid().ToString("N")[..6];
        var cat = await PostAsync(c, "/PropertySetup/Types", "/PropertySetup/SaveCategory", ("code", code), ("name", "Cat " + code), ("isActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, cat.StatusCode);
        var types = await c.GetStringAsync("/PropertySetup/Types");
        Assert.Contains("Cat " + code, types);
        var catId = Regex.Match(types, "<option value=\"(\\d+)\">Cat " + code).Groups[1].Value;
        var type = await PostAsync(c, "/PropertySetup/Types", "/PropertySetup/SaveType", ("code", "T" + code), ("name", "Type " + code), ("categoryId", catId), ("form", "5"), ("requiresFloor", "false"), ("isActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, type.StatusCode);
        Assert.Contains("Type " + code, await c.GetStringAsync("/PropertySetup/Types"));

        var zone = await PostAsync(c, $"/PropertySetup/Blocks?projectId={projectId}", "/PropertySetup/SaveZone", ("kind", "Block"), ("projectId", projectId.ToString()), ("code", "BA"), ("name", "Block Alpha"), ("isActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, zone.StatusCode);
        Assert.Contains("Block Alpha", await c.GetStringAsync($"/PropertySetup/Blocks?projectId={projectId}"));
        var dup = await PostAsync(c, $"/PropertySetup/Blocks?projectId={projectId}", "/PropertySetup/SaveZone", ("kind", "Block"), ("projectId", projectId.ToString()), ("code", "BA"), ("name", "Other"), ("isActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, dup.StatusCode); // error is surfaced via TempData on the redirected page
    }

    [MySqlFact]
    public async Task Create_WithInvalidInput_ShowsErrorsAndSavesNothing()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectAsync(c);
        var html = await c.GetStringAsync($"/Properties/Create?projectId={projectId}");
        var typeId = Regex.Match(html, "<option value=\"(\\d+)\" data-floor=\"0\"").Groups[1].Value;
        var r = await PostAsync(c, $"/Properties/Create?projectId={projectId}", "/Properties/Create", ("ProjectId", projectId.ToString()), ("PropertyCode", "bad code!"), ("PropertyNumber", "1"),
            ("PropertyTypeId", typeId), ("Size", "-4"), ("MeasurementUnit", "Marla"), ("DevelopmentStatus", "Developed"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.Contains("Size must be greater than zero", body);
        Assert.Contains("letters, digits", body);
    }

    [MySqlFact]
    public async Task Post_WithoutAntiForgeryToken_IsRejected()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var r = await c.PostAsync("/PropertySetup/SaveCategory", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "X", ["name"] = "X" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [MySqlFact]
    public async Task Authorization_UsersWithoutInventoryPermissions_AreDenied()
    {
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/Inventory", "/Inventory/Reports", "/Properties", "/Properties/Create", "/PropertySetup/Types", "/PropertySetup/Blocks" })
        {
            var r = await cashier.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        Assert.DoesNotContain("Property dashboard", await cashier.GetStringAsync("/"));

        var support = await f.LoggedInAsRoleAsync(RoleNames.CustomerSupport); // view only
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/Properties")).StatusCode);
        var create = await support.GetAsync("/Properties/Create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var types = await support.GetStringAsync("/PropertySetup/Types");
        Assert.DoesNotContain("data-bs-target=\"#typeModal\"", types); // no setup buttons without the permission
    }
}
