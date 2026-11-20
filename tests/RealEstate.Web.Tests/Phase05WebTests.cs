using System.Net;
using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class Phase05WebTests(WebFactory f)
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

    // ----- tests -----
    [MySqlFact]
    public async Task Admin_CanOpenEveryInstallmentScreen()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var (projectId, agreementId) = await ConfirmedAgreementAsync(c, checker);
        var planId = await ActivePlanAsync(c, projectId);
        await GenerateScheduleAsync(c, agreementId, planId);
        var chargeTypeId = Regex.Match(await c.GetStringAsync("/InstallmentPlans/ChargeTypes"), @"/InstallmentPlans/EditChargeType/(\d+)").Groups[1].Value;

        foreach (var (url, text) in new[]
                 {
                     ("/InstallmentPlans", "Installment plan templates"), ("/InstallmentPlans?search=Standard&status=Active", "Standard 24 months"), ("/InstallmentPlans/Create", "Select the project"),
                     ($"/InstallmentPlans/Create?projectId={projectId}", "Plan type"), ($"/InstallmentPlans/Details/{planId}", "Sample-price preview"),
                     ($"/InstallmentPlans/Details/{planId}?preview=true&price=5000000&bookingAmount=500000&startDate={Today}", "Resolved charges"),
                     ($"/InstallmentPlans/Edit/{planId}", "Edit plan"), ($"/InstallmentPlans/AddComponent/{planId}", "Add component"),
                     ("/InstallmentPlans/ChargeTypes", "Charge types"), ("/InstallmentPlans/CreateChargeType", "New charge type"), ($"/InstallmentPlans/EditChargeType/{chargeTypeId}", "Edit charge type"),
                     ("/LateFeeRules", "Late fee rules"), ("/LateFeeRules/Create", "New late fee rule"), ("/LateFeeRules/Calculator", "Late fee calculator"),
                     ("/InstallmentAgreements", "Installment agreements"), ($"/InstallmentAgreements?hasSchedule=true&paymentMode=Installment", "Open"),
                     ($"/InstallmentAgreements/Details/{agreementId}", "Active schedule"), ($"/InstallmentAgreements/Builder/{agreementId}", "Schedule builder"),
                     ($"/InstallmentAgreements/Preview/{agreementId}", "Schedule preview"), ($"/InstallmentAgreements/History/{agreementId}", "Schedule history"),
                     ($"/InstallmentAgreements/History/{agreementId}?version=1", "Version 1"),
                     ($"/AgreementAmendments", "Schedule amendments"), ($"/AgreementAmendments/Pending", "Pending amendments"), ($"/AgreementAmendments/Waivers", "Waiver approvals"),
                     ($"/AgreementAmendments/Create/{agreementId}", "Request schedule amendment"), ($"/AgreementAmendments/Create/{agreementId}?type=AdditionalCharge", "Charge.ChargeTypeId"),
                 })
        {
            var html = await GetOkAsync(c, url);
            Dbg.Has(html, text);
        }
        // the nested additional-charge fields are prefixed so they bind to the Charge property
        Assert.Contains("name=\"Charge.Name\"", await c.GetStringAsync($"/AgreementAmendments/Create/{agreementId}"));
        var nav = await c.GetStringAsync("/");
        Assert.Contains("Installment plans", nav); Assert.Contains("Late fee rules", nav);
    }

    [MySqlFact]
    public async Task Plan_Create_AddComponent_Activate_Copy_Retire_AndValidation()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var projectId = await CreateProjectWithPriceAsync(c);

        // a validation error re-renders the form
        var bad = await PostAsync(c, $"/InstallmentPlans/Create?projectId={projectId}", "/InstallmentPlans/Create", ("ProjectId", projectId.ToString()), ("Code", "BAD CODE"), ("Name", ""), ("PlanType", "StandardProject"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badHtml = await BodyAsync(bad);
        Dbg.Has(badHtml, "required"); Dbg.Has(badHtml, "Plan type");

        var planId = await ActivePlanAsync(c, projectId);
        var details = await GetOkAsync(c, $"/InstallmentPlans/Details/{planId}");
        Dbg.Has(details, "Active"); Dbg.Has(details, "Possession"); Dbg.Has(details, "Remainder"); Dbg.Has(details, "10% of price");
        Assert.DoesNotContain("Add component", details); // an active plan is read-only
        var addToActive = await c.GetAsync($"/InstallmentPlans/AddComponent/{planId}");
        Assert.Equal(HttpStatusCode.OK, addToActive.StatusCode);

        // the component form validates server-side (no charge type)
        var draft = await PostAsync(c, $"/InstallmentPlans/Create?projectId={projectId}", "/InstallmentPlans/Create", ("ProjectId", projectId.ToString()), ("Code", "DR" + Guid.NewGuid().ToString("N")[..8]), ("Name", "Draft plan"), ("PlanType", "StandardProject"));
        var draftId = long.Parse(Regex.Match(draft.Headers.Location!.ToString(), @"/InstallmentPlans/Details/(\d+)").Groups[1].Value);
        var form = await c.GetStringAsync($"/InstallmentPlans/AddComponent/{draftId}");
        var noType = await PostAsync(c, $"/InstallmentPlans/AddComponent/{draftId}", $"/InstallmentPlans/AddComponent/{draftId}", ("RowVersion", Rv(form)), ("Name", "X"), ("Category", "SaleConsideration"), ("AmountBasis", "Remainder"),
            ("Frequency", "OneTime"), ("GracePeriodDays", "0"), ("MaxWaiverPercent", "0"), ("TaxTreatment", "NotApplicable"), ("TaxRatePercent", "0"));
        Assert.Equal(HttpStatusCode.OK, noType.StatusCode);
        Dbg.Has(await BodyAsync(noType), "charge type");
        // edit the draft header
        var edit = await c.GetStringAsync($"/InstallmentPlans/Edit/{draftId}");
        var renamed = await PostAsync(c, $"/InstallmentPlans/Edit/{draftId}", $"/InstallmentPlans/Edit/{draftId}", ("ProjectId", projectId.ToString()), ("PlanType", "StandardProject"), ("Name", "Renamed draft"), ("RowVersion", Rv(edit)));
        Assert.True(renamed.StatusCode == HttpStatusCode.Redirect, "Edit failed: " + Strip(await BodyAsync(renamed)));
        Assert.Contains("Renamed draft", await c.GetStringAsync($"/InstallmentPlans/Details/{draftId}"));
        // activating an empty draft is refused with a message
        var draftDetails = await c.GetStringAsync($"/InstallmentPlans/Details/{draftId}");
        var refused = await PostAsync(c, $"/InstallmentPlans/Details/{draftId}", $"/InstallmentPlans/Activate/{draftId}", ("rowVersion", Rv(draftDetails, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("Draft", await c.GetStringAsync($"/InstallmentPlans/Details/{draftId}"));

        // copy and retire
        var copyCode = "CP" + Guid.NewGuid().ToString("N")[..8];
        var copy = await PostAsync(c, $"/InstallmentPlans/Details/{planId}", $"/InstallmentPlans/Copy/{planId}", ("code", copyCode), ("name", "Copied plan"));
        Assert.Equal(HttpStatusCode.Redirect, copy.StatusCode);
        var copyHtml = await GetOkAsync(c, copy.Headers.Location!.ToString());
        Dbg.Has(copyHtml, copyCode.ToUpperInvariant()); Dbg.Has(copyHtml, "Draft");
        var detailsNow = await c.GetStringAsync($"/InstallmentPlans/Details/{planId}");
        var noReason = await PostAsync(c, $"/InstallmentPlans/Details/{planId}", $"/InstallmentPlans/Retire/{planId}", ("reason", ""), ("rowVersion", Rv(detailsNow, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, noReason.StatusCode);
        Assert.Contains("Active", await c.GetStringAsync($"/InstallmentPlans/Details/{planId}"));
        var retire = await PostAsync(c, $"/InstallmentPlans/Details/{planId}", $"/InstallmentPlans/Retire/{planId}", ("reason", "Superseded by a new plan"), ("rowVersion", Rv(detailsNow, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, retire.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/InstallmentPlans/Details/{planId}"), "Superseded by a new plan");
    }

    [MySqlFact]
    public async Task ScheduleBuilder_ApplyPlan_Preview_Generate_ThenCharges_AreLocked()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var (projectId, agreementId) = await ConfirmedAgreementAsync(c, checker);
        var planId = await ActivePlanAsync(c, projectId);

        var empty = await GetOkAsync(c, $"/InstallmentAgreements/Preview/{agreementId}");
        Assert.DoesNotContain("data-bs-target=\"#generateModal\"", empty); // nothing to generate yet
        var emptyHistory = await GetOkAsync(c, $"/InstallmentAgreements/History/{agreementId}");
        Dbg.Has(emptyHistory, "No schedule has been generated");

        var builder = await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}");
        Dbg.Has(builder, "Standard 24 months"); Dbg.Has(builder, "Reconciliation");
        var apply = await PostAsync(c, $"/InstallmentAgreements/Builder/{agreementId}", $"/InstallmentAgreements/ApplyPlan/{agreementId}", ("planId", planId.ToString()), ("startDate", Today), ("rowVersion", Rv(builder, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        var applied = await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}");
        Dbg.Has(applied, "Monthly installment"); Dbg.Has(applied, "Possession"); Dbg.Has(applied, "Reconciled");

        var preview = await GetOkAsync(c, $"/InstallmentAgreements/Preview/{agreementId}");
        Dbg.Has(preview, "Installments (26)"); Dbg.Has(preview, "5,000,000.00"); Dbg.Has(preview, "data-bs-target=\"#generateModal\"");
        var generate = await PostAsync(c, $"/InstallmentAgreements/Preview/{agreementId}", $"/InstallmentAgreements/Generate/{agreementId}", ("rowVersion", Rv(preview, "rowVersion")), ("remarks", "initial"));
        Assert.EndsWith($"/InstallmentAgreements/Details/{agreementId}", generate.Headers.Location!.ToString());
        var details = await GetOkAsync(c, generate.Headers.Location!.ToString());
        Dbg.Has(details, "Schedule version 1 generated"); Dbg.Has(details, "Version"); Dbg.Has(details, "Monthly installment");
        var locked = await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}");
        Dbg.Has(locked, "can no longer be edited");
        Assert.DoesNotContain("Add charge", locked);
        var list = await GetOkAsync(c, "/InstallmentAgreements?hasSchedule=true");
        Assert.Contains($"/InstallmentAgreements/Details/{agreementId}", list);
        Assert.Contains("Version 1", list);
    }

    [MySqlFact]
    public async Task ScheduleBuilder_ManualCharge_Add_Edit_Remove_AndValidation()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var (_, agreementId) = await ConfirmedAgreementAsync(c, checker);
        var url = $"/InstallmentAgreements/AddCharge/{agreementId}";
        var form = await c.GetStringAsync(url);
        var typeId = Regex.Match(form, "<option value=\"(\\d+)\"[^>]*>Down payment</option>").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);

        // the engine's validation surfaces inside the form (amount missing)
        var bad = await PostAsync(c, url, url, ("RowVersion", Rv(form)), ("ChargeTypeId", typeId), ("Name", "Full payment"), ("Category", "SaleConsideration"), ("Frequency", "OneTime"), ("FirstDueDate", Today),
            ("GracePeriodDays", "0"), ("MaxWaiverPercent", "0"), ("TaxTreatment", "NotApplicable"), ("TaxRatePercent", "0"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Amount");

        var ok = await PostAsync(c, url, url, ("RowVersion", Rv(form)), ("ChargeTypeId", typeId), ("Name", "Full payment"), ("Category", "SaleConsideration"), ("Frequency", "OneTime"), ("Amount", "5000000"),
            ("FirstDueDate", Today), ("GracePeriodDays", "0"), ("MaxWaiverPercent", "0"), ("TaxTreatment", "NotApplicable"), ("TaxRatePercent", "0"));
        Assert.True(ok.StatusCode == HttpStatusCode.Redirect, "AddCharge failed: " + Strip(await BodyAsync(ok)));
        var builder = await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}");
        Dbg.Has(builder, "Full payment"); Dbg.Has(builder, "5,000,000.00"); Dbg.Has(builder, "Reconciled");
        var chargeId = Regex.Match(builder, @"/InstallmentAgreements/EditCharge/\d+\?chargeId=(\d+)").Groups[1].Value;

        var editUrl = $"/InstallmentAgreements/EditCharge/{agreementId}?chargeId={chargeId}";
        var editForm = await GetOkAsync(c, editUrl);
        Dbg.Has(editForm, "Edit charge");
        var edit = await PostAsync(c, editUrl, editUrl, ("RowVersion", Rv(editForm)), ("ChargeTypeId", typeId), ("Name", "Lump sum"), ("Category", "SaleConsideration"), ("Frequency", "OneTime"), ("Amount", "5000000"),
            ("FirstDueDate", Today), ("GracePeriodDays", "0"), ("MaxWaiverPercent", "0"), ("TaxTreatment", "NotApplicable"), ("TaxRatePercent", "0"));
        Assert.True(edit.StatusCode == HttpStatusCode.Redirect, "EditCharge failed: " + Strip(await BodyAsync(edit)));
        var builder2 = await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}");
        Dbg.Has(builder2, "Lump sum");

        var remove = await PostAsync(c, $"/InstallmentAgreements/Builder/{agreementId}", $"/InstallmentAgreements/RemoveCharge/{agreementId}", ("chargeId", chargeId), ("rowVersion", Rv(builder2, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/InstallmentAgreements/Builder/{agreementId}"), "No charges yet");
    }

    [MySqlFact]
    public async Task Amendment_Request_Approve_BySecondUser_ShowsVersion2_AndRejectNeedsComment()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var (projectId, agreementId) = await ConfirmedAgreementAsync(c, checker);
        var planId = await ActivePlanAsync(c, projectId);
        await GenerateScheduleAsync(c, agreementId, planId);

        var createUrl = $"/AgreementAmendments/Create/{agreementId}";
        var form = await GetOkAsync(c, createUrl);
        var itemIds = Regex.Matches(form, "name=\"DefItemIds\" value=\"(\\d+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(itemIds.Count >= 3, "expected the schedule lines on the form");

        // the reason is required
        var noReason = await PostAsync(c, createUrl, createUrl, ("Type", "Deferment"), ("Reason", ""), ("DefItemIds", itemIds[2]), ("DefMonths", "1"));
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
        Dbg.Has(await BodyAsync(noReason), "required");

        var create = await PostAsync(c, createUrl, createUrl, ("Type", "Deferment"), ("Reason", "Customer asked for a month"), ("IdempotencyKey", Guid.NewGuid().ToString("N")), ("DefItemIds", itemIds[2]), ("DefMonths", "1"), ("DefCascade", "true"));
        Assert.True(create.StatusCode == HttpStatusCode.Redirect, "Create amendment failed: " + Strip(await BodyAsync(create)));
        var detailsUrl = create.Headers.Location!.ToString();
        var amendmentId = Regex.Match(detailsUrl, @"amendmentId=(\d+)").Groups[1].Value;

        var asMaker = await GetOkAsync(c, detailsUrl);
        Dbg.Has(asMaker, "Deferment"); Dbg.Has(asMaker, "Pending"); Dbg.Has(asMaker, "another approver must decide"); Dbg.Has(asMaker, "Preview of the new schedule");
        Assert.DoesNotContain("approveModal", asMaker);
        Assert.Contains("cancelModal", asMaker);
        Dbg.Has(await GetOkAsync(c, "/AgreementAmendments/Pending"), $"amendmentId={amendmentId}");
        Dbg.Has(await GetOkAsync(c, $"/InstallmentAgreements/Details/{agreementId}"), "waiting for approval");
        // only one pending amendment per agreement
        var second = await PostAsync(c, createUrl, createUrl, ("Type", "Deferment"), ("Reason", "Another request"), ("DefItemIds", itemIds[3]), ("DefMonths", "1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // the requester cannot approve; another user can
        var asChecker = await GetOkAsync(checker, detailsUrl);
        Dbg.Has(asChecker, "approveModal"); Dbg.Has(asChecker, "rejectModal");
        var rowVersion = Rv(asChecker, "rowVersion");
        var self = await PostAsync(c, detailsUrl, $"/AgreementAmendments/Approve/{agreementId}?amendmentId={amendmentId}", ("rowVersion", rowVersion), ("comments", "self"));
        Assert.Equal(HttpStatusCode.Redirect, self.StatusCode);
        Assert.Contains("Pending", await GetOkAsync(c, detailsUrl));

        // reject needs a comment
        var rejectNoComment = await PostAsync(checker, detailsUrl, $"/AgreementAmendments/Reject/{agreementId}?amendmentId={amendmentId}", ("rowVersion", rowVersion), ("comments", ""));
        Assert.Equal(HttpStatusCode.Redirect, rejectNoComment.StatusCode);
        Dbg.Has(await GetOkAsync(checker, detailsUrl), "Pending");

        var approve = await PostAsync(checker, detailsUrl, $"/AgreementAmendments/Approve/{agreementId}?amendmentId={amendmentId}", ("rowVersion", rowVersion), ("comments", "approved"));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        var approved = await GetOkAsync(checker, detailsUrl);
        Dbg.Has(approved, "Approved"); Dbg.Has(approved, "Due date changed");

        var history = await GetOkAsync(c, $"/InstallmentAgreements/History/{agreementId}");
        Assert.Matches("<td data-label=\"Version\">2</td>", history);
        Dbg.Has(history, "Deferment"); Dbg.Has(history, "Superseded"); Dbg.Has(history, "Line changes for version 2");
        Dbg.Has(await GetOkAsync(c, $"/InstallmentAgreements/History/{agreementId}?version=1"), "Version 1");
        Dbg.Has(await GetOkAsync(c, $"/InstallmentAgreements/Details/{agreementId}"), "Version");

        // a due-date change that is then rejected with a comment
        var form2 = await GetOkAsync(c, createUrl);
        var changeItem = Regex.Matches(form2, "name=\"DueChanges\\[(\\d+)\\]\\.ItemId\" value=\"(\\d+)\"").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Skip(4).First();
        var newDate = DateTime.UtcNow.AddHours(5).AddMonths(40).ToString("yyyy-MM-dd");
        var due = await PostAsync(c, createUrl, createUrl, ("Type", "DueDateChange"), ("Reason", "Align with salary date"), ("IdempotencyKey", Guid.NewGuid().ToString("N")),
            ($"DueChanges[0].ItemId", changeItem.Item2), ($"DueChanges[0].NewDueDate", newDate));
        Assert.True(due.StatusCode == HttpStatusCode.Redirect, "Create due-date amendment failed: " + Strip(await BodyAsync(due)));
        var dueUrl = due.Headers.Location!.ToString();
        var dueId = Regex.Match(dueUrl, @"amendmentId=(\d+)").Groups[1].Value;
        var dueChecker = await GetOkAsync(checker, dueUrl);
        var reject = await PostAsync(checker, dueUrl, $"/AgreementAmendments/Reject/{agreementId}?amendmentId={dueId}", ("rowVersion", Rv(dueChecker, "rowVersion")), ("comments", "Not acceptable to finance"));
        Assert.Equal(HttpStatusCode.Redirect, reject.StatusCode);
        var rejected = await GetOkAsync(checker, dueUrl);
        Dbg.Has(rejected, "Rejected"); Dbg.Has(rejected, "Not acceptable to finance");
        Dbg.Has(await GetOkAsync(c, "/AgreementAmendments?status=Rejected"), $"amendmentId={dueId}");
    }

    [MySqlFact]
    public async Task Waiver_Request_AppearsInWaiverApprovals_AndCanBeCancelled()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var checker = await f.LoggedInAsync(administrator: true);
        var (projectId, agreementId) = await ConfirmedAgreementAsync(c, checker);
        var planId = await ActivePlanAsync(c, projectId);
        await GenerateScheduleAsync(c, agreementId, planId);
        var createUrl = $"/AgreementAmendments/Create/{agreementId}";
        var form = await GetOkAsync(c, createUrl);
        var itemId = Regex.Matches(form, "name=\"WvItemId\"[^>]*>(.*?)</select>", RegexOptions.Singleline)[0].Value;
        // the down payment does not allow waivers; the monthly installments do (second line onwards)
        var firstItem = Regex.Matches(itemId, "<option value=\"(\\d+)\"")[1].Groups[1].Value;
        var create = await PostAsync(c, createUrl, createUrl, ("Type", "Waiver"), ("Reason", "Goodwill waiver on installment"), ("IdempotencyKey", Guid.NewGuid().ToString("N")), ("WvItemId", firstItem), ("WvKind", "Principal"), ("WvAmount", "100"));
        Assert.True(create.StatusCode == HttpStatusCode.Redirect, "Create waiver failed: " + Strip(await BodyAsync(create)));
        var waivers = await GetOkAsync(checker, "/AgreementAmendments/Waivers?status=Pending");
        Dbg.Has(waivers, "Review"); Dbg.Has(waivers, "Goodwill waiver");
        var detailsUrl = create.Headers.Location!.ToString();
        var amendmentId = Regex.Match(detailsUrl, @"amendmentId=(\d+)").Groups[1].Value;
        var details = await GetOkAsync(c, detailsUrl);
        var cancel = await PostAsync(c, detailsUrl, $"/AgreementAmendments/Cancel/{agreementId}?amendmentId={amendmentId}", ("rowVersion", Rv(details, "rowVersion")), ("comments", "changed my mind"));
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Dbg.Has(await GetOkAsync(c, detailsUrl), "Cancelled");
        Assert.DoesNotContain("Goodwill waiver", await GetOkAsync(checker, "/AgreementAmendments/Waivers?status=Pending"));
    }

    [MySqlFact]
    public async Task LateFeeRule_Create_Validate_Edit_Calculate_AndChargeTypes()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var code = "LF" + Guid.NewGuid().ToString("N")[..8];
        var bad = await PostAsync(c, "/LateFeeRules/Create", "/LateFeeRules/Create", ("Code", code), ("Name", "Monthly 2 percent"), ("Method", "PercentPerMonth"), ("Value", "150"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "cannot exceed 100%");
        var noName = await PostAsync(c, "/LateFeeRules/Create", "/LateFeeRules/Create", ("Code", code), ("Name", ""), ("Method", "PercentPerMonth"), ("Value", "2"));
        Assert.Equal(HttpStatusCode.OK, noName.StatusCode);
        Dbg.Has(await BodyAsync(noName), "required");

        var ok = await PostAsync(c, "/LateFeeRules/Create", "/LateFeeRules/Create", ("Code", code), ("Name", "Monthly 2 percent"), ("Method", "PercentPerMonth"), ("Value", "2"), ("MaxPercentOfInstallment", "10"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var list = await GetOkAsync(c, $"/LateFeeRules?search={code}");
        Dbg.Has(list, code.ToUpperInvariant());
        var id = Regex.Match(list, @"/LateFeeRules/Edit/(\d+)").Groups[1].Value;

        var dup = await PostAsync(c, "/LateFeeRules/Create", "/LateFeeRules/Create", ("Code", code), ("Name", "Duplicate"), ("Method", "PercentPerMonth"), ("Value", "2"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);

        var edit = await GetOkAsync(c, $"/LateFeeRules/Edit/{id}");
        var renamed = await PostAsync(c, $"/LateFeeRules/Edit/{id}", $"/LateFeeRules/Edit/{id}", ("RowVersion", Rv(edit)), ("Code", code), ("Name", "Monthly 3 percent"), ("Method", "PercentPerMonth"), ("Value", "3"), ("MaxPercentOfInstallment", "10"), ("IsActive", "true"));
        Assert.True(renamed.StatusCode == HttpStatusCode.Redirect, "Edit failed: " + Strip(await BodyAsync(renamed)));
        Assert.Contains("Monthly 3 percent", await c.GetStringAsync($"/LateFeeRules?search={code}"));

        var calc = await GetOkAsync(c, $"/LateFeeRules/Calculator?ruleId={id}&installmentAmount=100000&graceEndDate=2026-01-01&asOf=2026-03-15");
        Dbg.Has(calc, "Overdue days"); Dbg.Has(calc, "Late fee");

        // charge types
        var typeCode = "CT_" + Guid.NewGuid().ToString("N")[..6];
        var type = await PostAsync(c, "/InstallmentPlans/CreateChargeType", "/InstallmentPlans/CreateChargeType", ("Code", typeCode), ("Name", "Club membership"), ("Kind", "CustomCharge"),
            ("DefaultCategory", "OtherCharges"), ("DefaultFrequency", "OneTime"), ("IsActive", "true"));
        Assert.True(type.StatusCode == HttpStatusCode.Redirect, "Create charge type failed: " + Strip(await BodyAsync(type)));
        var types = await GetOkAsync(c, "/InstallmentPlans/ChargeTypes");
        Dbg.Has(types, "Club membership");
        var typeRow = Regex.Match(types, $"{typeCode}.*?EditChargeType/(\\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
        var typeEdit = await PostAsync(c, $"/InstallmentPlans/EditChargeType/{typeRow}", $"/InstallmentPlans/EditChargeType/{typeRow}", ("Code", typeCode), ("Name", "Club membership fee"), ("Kind", "CustomCharge"),
            ("DefaultCategory", "OtherCharges"), ("DefaultFrequency", "OneTime"), ("IsActive", "true"));
        Assert.True(typeEdit.StatusCode == HttpStatusCode.Redirect, "Edit charge type failed: " + Strip(await BodyAsync(typeEdit)));
        Assert.Contains("Club membership fee", await c.GetStringAsync("/InstallmentPlans/ChargeTypes"));
    }

    [MySqlFact]
    public async Task Authorization_And_AntiForgery_AreEnforced()
    {
        var anon = f.NewClient();
        foreach (var url in new[] { "/InstallmentPlans", "/InstallmentAgreements", "/LateFeeRules", "/AgreementAmendments/Pending", "/AgreementAmendments/Waivers" })
        {
            var r = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.InventoryManager);
        foreach (var url in new[] { "/InstallmentPlans", "/InstallmentPlans/Create", "/InstallmentPlans/ChargeTypes", "/LateFeeRules", "/LateFeeRules/Create", "/InstallmentAgreements", "/InstallmentAgreements/Details/1",
                     "/InstallmentAgreements/Builder/1", "/AgreementAmendments", "/AgreementAmendments/Create/1", "/AgreementAmendments/Waivers" })
        {
            var r = await cashier.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        Assert.DoesNotContain("Installment plans", await cashier.GetStringAsync("/"));

        // a viewer-only user can read but not manage
        var recovery = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        var admin = await f.LoggedInAsync(administrator: true);
        Assert.Equal(HttpStatusCode.OK, (await recovery.GetAsync("/InstallmentPlans")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await recovery.GetAsync("/InstallmentPlans/Create")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await recovery.GetAsync("/LateFeeRules/Create")).StatusCode);

        // POST without an anti-forgery token is rejected
        var noToken = await admin.PostAsync("/LateFeeRules/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = "NOTOKEN", ["Name"] = "x", ["Method"] = "PercentPerMonth", ["Value"] = "2" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var noTokenAmend = await admin.PostAsync("/AgreementAmendments/Approve/1?amendmentId=1", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noTokenAmend.StatusCode);
        // unknown agreements are not found
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/InstallmentAgreements/Details/99999999")).StatusCode);
    }
}
