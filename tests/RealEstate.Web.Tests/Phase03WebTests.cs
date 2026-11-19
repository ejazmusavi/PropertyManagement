using System.Net;
using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class Phase03WebTests(WebFactory f)
{
    private static string Today => DateTime.UtcNow.AddHours(5).ToString("yyyy-MM-dd");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static string Decode(string s) => WebUtility.HtmlDecode(s);

    private static async Task<long> CreateProjectAsync(HttpClient c)
    {
        var code = "V" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Pricing UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Projects/Details/(\d+)").Groups[1].Value);
    }

    private static async Task<long> CreatePropertyAsync(HttpClient c, long projectId, string code)
    {
        var html = await c.GetStringAsync($"/Properties/Create?projectId={projectId}");
        var typeId = Regex.Match(html, "<option value=\"(\\d+)\" data-floor=\"0\"[^>]*>Residential Plot").Groups[1].Value;
        var r = await PostAsync(c, $"/Properties/Create?projectId={projectId}", "/Properties/Create", ("ProjectId", projectId.ToString()), ("PropertyCode", code), ("PropertyNumber", "1"),
            ("PropertyTypeId", typeId), ("Size", "5"), ("MeasurementUnit", "Marla"), ("DevelopmentStatus", "Developed"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Properties/Details/(\d+)").Groups[1].Value);
    }

    private static (string, string)[] BaseRuleForm(long projectId, params (string, string)[] over)
    {
        var d = new Dictionary<string, string>
        {
            ["ProjectId"] = projectId.ToString(), ["Code"] = "BASE", ["Name"] = "Base rate", ["Kind"] = "BaseRate", ["Factor"] = "Size", ["Scope"] = "Project", ["Method"] = "PerUnit",
            ["Value"] = "1000000", ["Unit"] = "Marla", ["Priority"] = "0", ["EffectiveFrom"] = Today,
        };
        foreach (var (k, v) in over) d[k] = v;
        return d.Select(x => (x.Key, x.Value)).ToArray();
    }

    [MySqlFact]
    public async Task Admin_CanOpenEveryPricingScreen()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId, "PU-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (url, text) in new[]
                 {
                     ("/Pricing/Rules", "Pricing rules"), ($"/Pricing/Rules?projectId={projectId}", "No pricing rules found"), ("/Pricing/CreateRule", "Create pricing rule"),
                     ($"/Pricing/CreateRule?projectId={projectId}", "New pricing rule"), ("/Pricing/RateCards", "Rate cards"), ($"/Pricing/RateCards?projectId={projectId}", "No base rate configured"),
                     ("/Pricing/Discounts", "Discounts"), ($"/Pricing/CreateDiscount?projectId={projectId}", "New discount"), ("/Pricing/Calculator", "Price calculator"),
                     ($"/Pricing/Calculator?projectId={projectId}&propertyId={propertyId}", "No base rate applies"), ("/Pricing/Properties", "Property pricing"),
                     ($"/Pricing/Properties?projectId={projectId}", "Publish prices for project"), ($"/Pricing/Property/{propertyId}", "Published price"), ("/Pricing/History", "Price history"),
                     ("/Pricing/Approvals", "Pricing approvals"),
                 })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
            Assert.Contains(text, Decode(await r.Content.ReadAsStringAsync()));
        }
        Assert.Contains("Pricing engine", await c.GetStringAsync("/"));
        Assert.Contains("/Pricing/Property/" + propertyId, await c.GetStringAsync($"/Properties/Details/{propertyId}"));
    }

    [MySqlFact]
    public async Task Rules_Calculator_Publish_Discount_AndApprovalFlow_ThroughTheUi()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var director = await f.LoggedInAsRoleAsync(RoleNames.Director);
        var projectId = await CreateProjectAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId, "PF-" + Guid.NewGuid().ToString("N")[..8]);

        var bad = await PostAsync(c, $"/Pricing/CreateRule?projectId={projectId}", "/Pricing/CreateRule", BaseRuleForm(projectId, ("Value", "0")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("must be greater than", Decode(await bad.Content.ReadAsStringAsync()));
        var pastDate = await PostAsync(c, $"/Pricing/CreateRule?projectId={projectId}", "/Pricing/CreateRule", BaseRuleForm(projectId, ("EffectiveFrom", "2020-01-01")));
        Assert.Contains("cannot start in the past", Decode(await pastDate.Content.ReadAsStringAsync()));

        var created = await PostAsync(c, $"/Pricing/CreateRule?projectId={projectId}", "/Pricing/CreateRule", BaseRuleForm(projectId));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var ruleId = Regex.Match(created.Headers.Location!.ToString(), @"/Pricing/RuleDetails/(\d+)").Groups[1].Value;
        var details = await c.GetStringAsync($"/Pricing/RuleDetails/{ruleId}");
        Assert.Contains("Base rate", details);
        Assert.Contains("Retire rule", details);
        Assert.Contains("BASE", await c.GetStringAsync($"/Pricing/RateCards?projectId={projectId}"));

        var corner = await PostAsync(c, $"/Pricing/CreateRule?projectId={projectId}", "/Pricing/CreateRule", BaseRuleForm(projectId, ("Code", "CORNER"), ("Name", "Corner"), ("Kind", "Premium"), ("Factor", "Corner"), ("Method", "Percentage"), ("Value", "10"), ("Unit", "")));
        Assert.Equal(HttpStatusCode.Redirect, corner.StatusCode);

        var disc = await PostAsync(c, $"/Pricing/CreateDiscount?projectId={projectId}", "/Pricing/CreateDiscount", ("ProjectId", projectId.ToString()), ("Code", "EARLY"), ("Name", "Early bird"), ("Scope", "Project"),
            ("Method", "Percentage"), ("Value", "5"), ("Priority", "0"), ("EffectiveFrom", Today));
        Assert.Equal(HttpStatusCode.Redirect, disc.StatusCode);
        Assert.Contains("Early bird", await c.GetStringAsync($"/Pricing/Discounts?projectId={projectId}"));

        var calc = Decode(await c.GetStringAsync($"/Pricing/Calculator?projectId={projectId}&propertyId={propertyId}"));
        Assert.Contains("5,000,000.00", calc);
        Assert.Contains("Applied pricing rules", calc);

        var propPage = $"/Pricing/Property/{propertyId}";
        var pub = await PostAsync(c, propPage, $"/Pricing/Publish/{propertyId}", ("reason", "go live"));
        Assert.Equal(HttpStatusCode.Redirect, pub.StatusCode);
        var page = Decode(await c.GetStringAsync(propPage));
        Assert.Contains("data-published-price>4,750,000.00", page);
        Assert.Contains("Price history", await c.GetStringAsync("/Pricing/History"));
        Assert.Contains("go live", await c.GetStringAsync($"/Pricing/History?propertyId={propertyId}"));

        // Request an override (admin is the maker), then a different user approves it.
        var req = await PostAsync(c, propPage, "/Pricing/RequestApproval", ("PropertyId", propertyId.ToString()), ("Type", "PriceOverride"), ("Value", "4000000"), ("EffectiveFrom", Today), ("Reason", "Negotiated with management"));
        Assert.Equal(HttpStatusCode.Redirect, req.StatusCode);
        var approvalUrl = req.Headers.Location!.ToString();
        var approvalId = Regex.Match(approvalUrl, @"/Pricing/ApprovalDetails/(\d+)").Groups[1].Value;
        var makerView = await c.GetStringAsync(approvalUrl);
        Assert.Contains("a different approver must decide", makerView);
        Assert.DoesNotContain("data-bs-target=\"#approveModal\"", makerView);
        var dupe = await PostAsync(c, propPage, "/Pricing/RequestApproval", ("PropertyId", propertyId.ToString()), ("Type", "PriceOverride"), ("Value", "3900000"), ("Reason", "Second request"));
        Assert.Equal(HttpStatusCode.OK, dupe.StatusCode);
        Assert.Contains("pending request", Decode(await dupe.Content.ReadAsStringAsync()));

        var checkerView = await director.GetStringAsync(approvalUrl);
        Assert.Contains("data-bs-target=\"#approveModal\"", checkerView);
        var rv = Decode(Regex.Match(checkerView, "name=\"rowVersion\" value=\"([^\"]+)\"").Groups[1].Value);
        var reject = await PostAsync(director, approvalUrl, $"/Pricing/Decide/{approvalId}", ("approve", "false"), ("comment", ""), ("rowVersion", rv));
        Assert.Equal(HttpStatusCode.Redirect, reject.StatusCode);   // missing comment is reported through the flash message, nothing changes
        Assert.Contains("Pending", await director.GetStringAsync(approvalUrl));
        var approve = await PostAsync(director, approvalUrl, $"/Pricing/Decide/{approvalId}", ("approve", "true"), ("comment", "ok"), ("rowVersion", rv));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        Assert.Contains("Approved", await director.GetStringAsync(approvalUrl));
        var after = Decode(await c.GetStringAsync(propPage));
        Assert.Contains("data-published-price>4,000,000.00", after);
        Assert.Contains("Approved adjustments", after);
        Assert.Contains("Approvals", await c.GetStringAsync($"/Pricing/Approvals?propertyId={propertyId}"));

        var retire = await PostAsync(c, $"/Pricing/RuleDetails/{ruleId}", $"/Pricing/RetireRule/{ruleId}", ("reason", "replaced by new rate"), ("rowVersion", Decode(Regex.Match(details, "name=\"rowVersion\" value=\"([^\"]+)\"").Groups[1].Value)));
        Assert.Equal(HttpStatusCode.Redirect, retire.StatusCode);
        Assert.Contains("Retired", await c.GetStringAsync($"/Pricing/RuleDetails/{ruleId}"));
    }

    [MySqlFact]
    public async Task PricingScreens_AreForbidden_WithoutPermission_AndFormsRequireAntiForgery()
    {
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/Pricing/Rules", "/Pricing/Calculator", "/Pricing/Approvals", "/Pricing/CreateRule" })
        {
            var r = await cashier.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        var admin = await f.LoggedInAsync(administrator: true);
        var noToken = await admin.PostAsync("/Pricing/CreateRule", new FormUrlEncodedContent(new Dictionary<string, string> { ["ProjectId"] = "1", ["Code"] = "X", ["Name"] = "X" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var anon = f.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/Pricing/Rules")).StatusCode);
    }
}
