using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class Phase04WebTests(WebFactory f)
{
    private static string Today => DateTime.UtcNow.AddHours(5).ToString("yyyy-MM-dd");
    private static string Decode(string s) => WebUtility.HtmlDecode(s);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPage, string action, params (string, string)[] kv)
    {
        var data = kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        data.Add(new("__RequestVerificationToken", await WebFactory.TokenAsync(c, formPage)));
        return await c.PostAsync(action, new FormUrlEncodedContent(data));
    }

    private static async Task<long> CreateProjectWithPriceAsync(HttpClient c)
    {
        var code = "K" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Sales UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
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
        var r = await PostAsync(c, $"/Properties/Create?projectId={projectId}", "/Properties/Create", ("ProjectId", projectId.ToString()), ("PropertyCode", "SL-" + Guid.NewGuid().ToString("N")[..8]), ("PropertyNumber", Random.Shared.Next(1, 100000).ToString()),
            ("PropertyTypeId", typeId), ("Size", "5"), ("MeasurementUnit", "Marla"), ("DevelopmentStatus", "Developed"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Properties/Details/(\d+)").Groups[1].Value);
    }

    private static (string, string)[] CustomerForm(string? cnic = null, params (string, string)[] over)
    {
        var d = new Dictionary<string, string>
        {
            ["FullName"] = "Web Customer", ["IdentityType"] = "Cnic", ["IdentityNumber"] = cnic ?? Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), ["Mobile"] = "0300-1234567",
            ["Email"] = "web@example.com", ["Address"] = "Murree", ["Status"] = "Active", ["Addresses[0].AddressType"] = "Home", ["Addresses[0].Line1"] = "House 1", ["Addresses[0].IsPrimary"] = "true",
            ["Nominees[0].FullName"] = "Nominee One", ["Nominees[0].Relationship"] = "Son", ["Nominees[0].SharePercent"] = "100",
        };
        foreach (var (k, v) in over) d[k] = v;
        return d.Select(x => (x.Key, x.Value)).ToArray();
    }

    private static async Task<long> CreateCustomerAsync(HttpClient c, string? cnic = null)
    {
        var r = await PostAsync(c, "/Customers/Create", "/Customers/Create", CustomerForm(cnic));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Customers/Details/(\d+)").Groups[1].Value);
    }

    private static (string, string)[] BookingForm(long projectId, long propertyId, IEnumerable<(long Customer, string Share)> owners, string mode = "Installment", string amount = "500000", bool submit = true)
    {
        var l = new List<(string, string)> { ("ProjectId", projectId.ToString()), ("PaymentMode", mode), ("BookingAmount", amount), ("SubmitForApproval", submit ? "true" : "false"), ("IdempotencyKey", Guid.NewGuid().ToString("N")),
            ("Lines[0].PropertyId", propertyId.ToString()), ("Lines[0].ExtraDiscount", "0") };
        var i = 0;
        foreach (var (cust, share) in owners) { l.Add(($"Owners[{i}].CustomerId", cust.ToString())); l.Add(($"Owners[{i}].SharePercent", share)); i++; }
        return l.ToArray();
    }

    private static async Task<long> CreateBookingAsync(HttpClient c, long projectId, long propertyId, long customerId, bool submit = true)
    {
        var r = await PostAsync(c, $"/Bookings/Create?projectId={projectId}", "/Bookings/Create", BookingForm(projectId, propertyId, [(customerId, "100")], submit: submit));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return long.Parse(Regex.Match(r.Headers.Location!.ToString(), @"/Bookings/Details/(\d+)").Groups[1].Value);
    }

    [MySqlFact]
    public async Task Admin_CanOpenEverySalesScreen()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectWithPriceAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId);
        var customerId = await CreateCustomerAsync(c);
        var bookingId = await CreateBookingAsync(c, projectId, propertyId, customerId);
        var approve = await PostAsync(checker, $"/Bookings/Details/{bookingId}", $"/Bookings/Approve/{bookingId}", ("comments", "ok"));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        foreach (var (url, text) in new[]
                 {
                     ("/Customers", "Customers"), ("/Customers?search=Web", "Web Customer"), ("/Customers/Create", "Register customer"), ($"/Customers/Details/{customerId}", "Properties owned"),
                     ($"/Customers/Edit/{customerId}", "Edit customer"), ($"/Customers/Documents/{customerId}", "Customer documents"),
                     ("/Bookings", "Bookings"), ("/Bookings/Create", "Select project"), ($"/Bookings/Create?projectId={projectId}", "Customers &amp; ownership"), ("/Bookings/Approvals", "Booking approvals"),
                     ($"/Bookings/Details/{bookingId}", "price snapshot"), ($"/Bookings/Agreement/{bookingId}", "PROPERTY SALE AGREEMENT"), ($"/Bookings/Agreement/{bookingId}", "BOOKING CONFIRMATION RECEIPT"),
                     ($"/Bookings/Cancel/{bookingId}", "Cancel booking"), ($"/Bookings/Transfer/{bookingId}", "Transfer or change ownership"),
                 })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {r.StatusCode}");
            Assert.Contains(text, await r.Content.ReadAsStringAsync());
        }
        Assert.Contains("Sales", await c.GetStringAsync("/"));
        Assert.Contains("/Bookings/Create?propertyId=", await c.GetStringAsync($"/Properties/Details/{await CreatePropertyAsync(c, projectId)}"));
        var lookup = await c.GetStringAsync("/Bookings/CustomerLookup?term=Web");
        Assert.Contains("Web Customer", lookup);
        var quote = await c.GetStringAsync($"/Bookings/Quote?propertyId={propertyId}&customerId={customerId}");
        Assert.Contains("5000000", quote);
    }

    [MySqlFact]
    public async Task Customer_Registration_Validation_Duplicate_Edit_Relationship_AndDocuments()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var bad = await PostAsync(c, "/Customers/Create", "/Customers/Create", CustomerForm("123", ("FullName", "")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var html = Decode(await bad.Content.ReadAsStringAsync());
        Assert.Contains("13 digits", html); Assert.Contains("required", html);

        var cnic = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
        var id = await CreateCustomerAsync(c, cnic);
        var dup = await PostAsync(c, "/Customers/Create", "/Customers/Create", CustomerForm(cnic));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already registered", Decode(await dup.Content.ReadAsStringAsync()));

        var details = Decode(await c.GetStringAsync($"/Customers/Details/{id}"));
        Assert.Contains("CUS-", details); Assert.Contains("Nominee One", details); Assert.Contains("House 1", details);

        var edit = await c.GetStringAsync($"/Customers/Edit/{id}");
        var rowVersion = Regex.Match(edit, "name=\"RowVersion\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var noReason = await PostAsync(c, $"/Customers/Edit/{id}", $"/Customers/Edit/{id}", CustomerForm(cnic, ("Status", "Inactive"), ("RowVersion", rowVersion)));
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
        Dbg.Has(Decode(await noReason.Content.ReadAsStringAsync()), "reason is required");
        var ok = await PostAsync(c, $"/Customers/Edit/{id}", $"/Customers/Edit/{id}", CustomerForm(cnic, ("Status", "Inactive"), ("StatusReason", "Moved abroad"), ("RowVersion", rowVersion), ("FullName", "Renamed Customer")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("Renamed Customer", await c.GetStringAsync($"/Customers/Details/{id}"));
        var stale = await PostAsync(c, $"/Customers/Edit/{id}", $"/Customers/Edit/{id}", CustomerForm(cnic, ("Status", "Inactive"), ("RowVersion", rowVersion)));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("changed by another user", Decode(await stale.Content.ReadAsStringAsync()));

        var other = await CreateCustomerAsync(c);
        var rel = await PostAsync(c, $"/Customers/Details/{id}", $"/Customers/AddRelationship/{id}", ("relatedCustomerId", other.ToString()), ("relationshipType", "Spouse"), ("notes", "married"));
        Assert.Equal(HttpStatusCode.Redirect, rel.StatusCode);
        Assert.Contains("Spouse", await c.GetStringAsync($"/Customers/Details/{id}"));
        var relId = Regex.Match(await c.GetStringAsync($"/Customers/Details/{id}"), "data-rel-id=\"(\\d+)\"").Groups[1].Value;
        var removed = await PostAsync(c, $"/Customers/Details/{id}", $"/Customers/RemoveRelationship/{id}", ("relationshipId", relId), ("reason", "Entered by mistake"));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.DoesNotContain("data-rel-id", await c.GetStringAsync($"/Customers/Details/{id}"));

        // documents: upload, download, remove
        var token = await WebFactory.TokenAsync(c, $"/Customers/Documents/{id}");
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("Cnic"), "type" }, { new StringContent("CNIC scan"), "title" } };
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4")); file.Headers.ContentType = new("application/pdf"); form.Add(file, "file", "cnic.pdf");
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync($"/Customers/UploadDocument/{id}", form)).StatusCode);
        var docs = await c.GetStringAsync($"/Customers/Documents/{id}");
        Assert.Contains("CNIC scan", docs);
        var docId = Regex.Match(docs, "documentId=(\\d+)").Groups[1].Value;
        var dl = await c.GetAsync($"/Customers/DownloadDocument/{id}?documentId={docId}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode); Assert.Equal("%PDF-1.4", await dl.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Customers/Documents/{id}", $"/Customers/DeleteDocument/{id}", ("documentId", docId), ("reason", "Wrong file"))).StatusCode);
        Assert.DoesNotContain("CNIC scan", await c.GetStringAsync($"/Customers/Documents/{id}"));
    }

    [MySqlFact]
    public async Task BookingWizard_Validation_Conflict_Approval_Agreement_Transfer_AndCancel()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectWithPriceAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId);
        var a = await CreateCustomerAsync(c); var b = await CreateCustomerAsync(c);

        var bad = await PostAsync(c, $"/Bookings/Create?projectId={projectId}", "/Bookings/Create", BookingForm(projectId, propertyId, [(a, "60"), (b, "30")]));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(Decode(await bad.Content.ReadAsStringAsync()), "must total exactly 100%");
        var wizardPage = await c.GetStringAsync($"/Bookings/Create?projectId={projectId}");
        Assert.Contains("prop-check", wizardPage);

        var bookingId = await CreateBookingAsync(c, projectId, propertyId, a);
        var details = Decode(await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains("BK-", details); Assert.Contains("Pending approval", details); Assert.Contains("5,000,000.00", details); Assert.Contains("another approver must decide", details);
        Assert.DoesNotContain("data-bs-target=\"#approveModal\"", details);

        var conflict = await PostAsync(c, $"/Bookings/Create?projectId={projectId}", "/Bookings/Create", BookingForm(projectId, propertyId, [(b, "100")]));
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        Assert.Contains("is Booked", Decode(await conflict.Content.ReadAsStringAsync()));

        var self = await PostAsync(c, $"/Bookings/Details/{bookingId}", $"/Bookings/Approve/{bookingId}", ("comments", "self"));
        Assert.Equal(HttpStatusCode.Redirect, self.StatusCode); // error is surfaced through TempData
        Assert.Contains("cannot be approved by the user who submitted", Decode(await (await c.GetAsync(self.Headers.Location!.ToString())).Content.ReadAsStringAsync()));
        Assert.Contains("approveModal", await checker.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains($"data-id=\"{bookingId}\"", await checker.GetStringAsync("/Bookings/Approvals"));

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker, $"/Bookings/Details/{bookingId}", $"/Bookings/Approve/{bookingId}", ("comments", "ok"))).StatusCode);
        var approved = Decode(await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains("Confirmed", approved); Assert.Contains("AGR-", approved);
        var agreement = Decode(await c.GetStringAsync($"/Bookings/Agreement/{bookingId}"));
        Assert.Contains("PROPERTY SALE AGREEMENT", agreement); Assert.Contains("Web Customer", agreement);

        // transfer to the second customer
        var tr = await c.GetStringAsync($"/Bookings/Transfer/{bookingId}");
        var rv = Regex.Match(tr, "name=\"RowVersion\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var badTransfer = await PostAsync(c, $"/Bookings/Transfer/{bookingId}", $"/Bookings/Transfer/{bookingId}", ("RowVersion", rv), ("Reason", "Sold on"), ("Owners[0].CustomerId", b.ToString()), ("Owners[0].SharePercent", "90"));
        Assert.Equal(HttpStatusCode.OK, badTransfer.StatusCode); Dbg.Has(Decode(await badTransfer.Content.ReadAsStringAsync()), "must total exactly 100%");
        var transfer = await PostAsync(c, $"/Bookings/Transfer/{bookingId}", $"/Bookings/Transfer/{bookingId}", ("RowVersion", rv), ("Reason", "Sold on"), ("Owners[0].CustomerId", b.ToString()), ("Owners[0].SharePercent", "100"));
        Assert.Equal(HttpStatusCode.Redirect, transfer.StatusCode);
        var v2 = Decode(await c.GetStringAsync($"/Bookings/Agreement/{bookingId}"));
        Assert.Contains("version 2 of 2", v2); Assert.Contains("Customer transfer", v2);
        Assert.Contains("version 1 of 2", await c.GetStringAsync($"/Bookings/Agreement/{bookingId}?version=1"));
        Assert.Contains("Former", await c.GetStringAsync($"/Bookings/Details/{bookingId}"));

        // cancel with a refund
        var cancelPage = await c.GetStringAsync($"/Bookings/Cancel/{bookingId}");
        var rv2 = Regex.Match(cancelPage, "name=\"RowVersion\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var noReason = await PostAsync(c, $"/Bookings/Cancel/{bookingId}", $"/Bookings/Cancel/{bookingId}", ("RowVersion", rv2), ("Reason", ""));
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
        var cancel = await PostAsync(c, $"/Bookings/Cancel/{bookingId}", $"/Bookings/Cancel/{bookingId}", ("RowVersion", rv2), ("Reason", "Customer withdrew"), ("RequestRefund", "true"), ("RefundAmount", "250000"));
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        var cancelled = Decode(await checker.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains("Cancelled", cancelled); Assert.Contains("250,000.00", cancelled); Assert.Contains("data-refund-id", cancelled);
        var refundId = Regex.Match(cancelled, "data-refund-id=\"(\\d+)\"").Groups[1].Value;
        var refundVersion = Regex.Match(cancelled, "data-refund-version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker, $"/Bookings/Details/{bookingId}", $"/Bookings/DecideRefund/{bookingId}", ("refundId", refundId), ("approve", "true"), ("rowVersion", refundVersion), ("comments", "ok"))).StatusCode);
        Assert.Contains("Approved", await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains("Available", await c.GetStringAsync($"/Properties/Details/{propertyId}"));
    }

    [MySqlFact]
    public async Task Reject_FromApprovalsScreen_ReleasesProperty_AndDraftCanBeSubmitted()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectWithPriceAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId);
        var customerId = await CreateCustomerAsync(c);
        var bookingId = await CreateBookingAsync(c, projectId, propertyId, customerId, submit: false);
        Assert.Contains("Submit for approval", await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Bookings/Details/{bookingId}", $"/Bookings/Submit/{bookingId}")).StatusCode);
        Assert.Contains("Pending approval", await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        var rejectNoComment = await PostAsync(checker, "/Bookings/Approvals", $"/Bookings/Reject/{bookingId}", ("comments", ""), ("returnTo", "approvals"));
        Assert.Equal(HttpStatusCode.Redirect, rejectNoComment.StatusCode);
        Dbg.Has(Decode(await (await checker.GetAsync(rejectNoComment.Headers.Location!.ToString())).Content.ReadAsStringAsync()), "comment is required");
        var reject = await PostAsync(checker, "/Bookings/Approvals", $"/Bookings/Reject/{bookingId}", ("comments", "Price not agreed"), ("returnTo", "approvals"));
        Assert.EndsWith("/Bookings/Approvals", reject.Headers.Location!.ToString());
        Assert.Contains("Rejected", await c.GetStringAsync($"/Bookings/Details/{bookingId}"));
        Assert.Contains("Available", await c.GetStringAsync($"/Properties/Details/{propertyId}"));
    }

    [MySqlFact]
    public async Task Authorization_IsEnforcedOnSalesScreens()
    {
        var anon = f.NewClient();
        foreach (var url in new[] { "/Customers", "/Bookings", "/Bookings/Approvals", "/Bookings/Create", "/Customers/Create" })
        {
            var r = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/Customers")).StatusCode);
        foreach (var url in new[] { "/Customers/Create", "/Bookings/Create", "/Bookings/Approvals", "/Bookings/Cancel/1", "/Bookings/Transfer/1" })
        {
            var r = await cashier.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        var support = await f.LoggedInAsRoleAsync(RoleNames.CustomerSupport);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/Customers/Create")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await support.GetAsync("/Bookings/Create")).StatusCode);
        var nav = await cashier.GetStringAsync("/");
        Assert.Contains("Customers", nav); Assert.DoesNotContain("Booking approvals", nav);
    }
}

internal static class Dbg
{
    public static void Has(string html, string text)
    {
        if (html.Contains(text)) return;
        var i = Math.Max(html.IndexOf("<main", StringComparison.Ordinal), 0);
        var plain = System.Text.RegularExpressions.Regex.Replace(html[i..], @"<script.*?</script>|<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ");
        throw new Xunit.Sdk.XunitException($"Missing '{text}'. Page: {plain[..Math.Min(plain.Length, 1200)]}");
    }
}
