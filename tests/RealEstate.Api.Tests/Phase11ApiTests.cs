using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstate.Application.Administration;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Gis;
using RealEstate.Application.Jobs;
using RealEstate.Application.Notifications;
using RealEstate.Application.Portal;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase11ApiTests(ApiFactory factory)
{
    private const string Square = """{"type":"Polygon","coordinates":[[[73.0,33.0],[73.1,33.0],[73.1,33.1],[73.0,33.1],[73.0,33.0]]]}""";

    private async Task<HttpClient> AdminAsync() => await LoginAsync(await factory.CreateUserAsync(administrator: true), ApiFactory.Password);
    private async Task<HttpClient> RoleAsync(string role) => await LoginAsync(await factory.CreateUserInRoleAsync(role), ApiFactory.Password);

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        r.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return c;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<long> ProjectAsync(HttpClient c) => (await Ok<ProjectDetailDto>(await c.PostAsJsonAsync("/api/v1/projects",
        new CreateProjectRequest("G" + Guid.NewGuid().ToString("N")[..10], "Gis API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null)))).Id;

    private static async Task<long> SrsAsync(HttpClient c, string code = "EPSG:4326") => (await Ok<List<SrsDto>>(await c.GetAsync("/api/v1/gis/spatial-reference-systems?activeOnly=true"))).Single(s => s.Code == code).Id;

    // ------------------------------------------------------------------------------------------------ security
    [MySqlFact]
    public async Task Phase11Endpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/notifications", "/api/v1/notifications/summary", "/api/v1/notifications/templates", "/api/v1/notifications/settings", "/api/v1/jobs", "/api/v1/jobs/runs",
                     "/api/v1/gis/summary", "/api/v1/gis/spatial-reference-systems", "/api/v1/gis/projects/1/boundaries", "/api/v1/gis/projects/1/geojson", "/api/v1/gis/projects/1/layers",
                     "/api/v1/gis/projects/1/documents", "/api/v1/gis/properties/1/boundaries", "/api/v1/me/notifications", "/api/v1/me/notifications/preferences",
                     "/api/v1/customers/1/notification-preferences", "/api/v1/portal/notification-preferences" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/notifications/send", "/api/v1/jobs/notifications-dispatch/run", "/api/v1/gis/projects/1/boundaries", "/api/v1/gis/projects/1/layers", "/api/v1/gis/spatial-reference-systems" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Roles_WithoutThePermissions_AreForbidden()
    {
        var cashier = await RoleAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/api/v1/notifications", "/api/v1/notifications/templates", "/api/v1/jobs", "/api/v1/gis/summary", "/api/v1/gis/spatial-reference-systems" })
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/notifications/send", new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, null, null, "T", "M", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync("/api/v1/jobs/notifications-dispatch/run", null)).StatusCode);
        // the accountant may view notifications but not change templates or run jobs
        var accountant = await RoleAsync(RoleNames.Accountant);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/v1/notifications")).StatusCode);
        var tpl = (await Ok<List<TemplateDto>>(await accountant.GetAsync("/api/v1/notifications/templates"))).First();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PutAsJsonAsync("/api/v1/notifications/templates", new SaveTemplateRequest(tpl.Type, tpl.Channel, tpl.Language, tpl.Subject, tpl.Body, true, tpl.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/v1/jobs")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ notifications
    [MySqlFact]
    public async Task Templates_CanBeListedAndEdited_WithValidationAndConcurrency()
    {
        var admin = await AdminAsync();
        var list = await Ok<List<TemplateDto>>(await admin.GetAsync("/api/v1/notifications/templates"));
        Assert.True(list.Count >= NotificationTemplateDefaults.All.Count);
        var t = list.First(x => x.Type == NotificationType.PossessionNotice && x.Channel == NotificationChannel.WhatsApp);
        Assert.Equal(t.Body, (await Ok<TemplateDto>(await admin.GetAsync($"/api/v1/notifications/templates/{t.Id}"))).Body);

        var bad = await admin.PutAsJsonAsync("/api/v1/notifications/templates", new SaveTemplateRequest(t.Type, t.Channel, "en", "S", "Hi {{Nope}}", true, t.RowVersion));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var saved = await Ok<TemplateDto>(await admin.PutAsJsonAsync("/api/v1/notifications/templates", new SaveTemplateRequest(t.Type, t.Channel, "en", "Possession", "Dear {{CustomerName}}, {{Message}} (via WhatsApp)", true, t.RowVersion)));
        Assert.Contains("via WhatsApp", saved.Body);
        var stale = await admin.PutAsJsonAsync("/api/v1/notifications/templates", new SaveTemplateRequest(t.Type, t.Channel, "en", "Possession", "Other", true, t.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/notifications/templates/999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Settings_AndSummary_AndEmptyHistory_Work()
    {
        var admin = await AdminAsync();
        var s = await Ok<ReminderSettingsDto>(await admin.GetAsync("/api/v1/notifications/settings"));
        Assert.Equal(3, s.UpcomingDaysBefore);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/notifications/settings", new ReminderSettingsDto(100, 0, 0))).StatusCode);
        var saved = await Ok<ReminderSettingsDto>(await admin.PutAsJsonAsync("/api/v1/notifications/settings", new ReminderSettingsDto(5, 7, 7)));
        Assert.Equal(5, saved.UpcomingDaysBefore);
        await Ok<ReminderSettingsDto>(await admin.PutAsJsonAsync("/api/v1/notifications/settings", s));
        Assert.NotNull(await Ok<NotificationSummaryDto>(await admin.GetAsync("/api/v1/notifications/summary")));
        var page = await Ok<PagedResult<NotificationListItemDto>>(await admin.GetAsync("/api/v1/notifications?page=1&pageSize=5&type=InstallmentOverdue"));
        Assert.True(page.PageSize <= 5);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/notifications/99999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/notifications/99999999/cancel", new CancelNotificationRequest("a long enough reason", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/notifications/deliveries/99999999/retry", null)).StatusCode);
    }

    [MySqlFact]
    public async Task ManualSend_ValidatesTheRequest_AndNeedsRecipients()
    {
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/notifications/send", new SendNotificationRequest(NotificationType.InstallmentOverdue, 1, null, null, "T", "M", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/notifications/send", new SendNotificationRequest(NotificationType.CompanyAnnouncement, 1, null, null, "", "M", null))).StatusCode);
        var projectId = await ProjectAsync(admin);        // a project without bookings: nobody to send to
        var none = await admin.PostAsJsonAsync("/api/v1/notifications/send", new SendNotificationRequest(NotificationType.CompanyAnnouncement, null, projectId, null, "Hello", "Nobody here", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/notifications/send", new SendNotificationRequest(NotificationType.CompanyAnnouncement, 987654, null, null, "Hello", "Unknown customer", null))).StatusCode);
    }

    [MySqlFact]
    public async Task StaffPreferences_AndInbox_AreAvailableToTheSignedInUser()
    {
        var admin = await AdminAsync();
        var prefs = await Ok<List<PreferenceDto>>(await admin.GetAsync("/api/v1/me/notifications/preferences"));
        Assert.All(prefs, p => { Assert.Equal(NotificationType.StaffDigest, p.Type); Assert.False(p.IsEnabled); });
        var on = await Ok<List<PreferenceDto>>(await admin.PutAsJsonAsync("/api/v1/me/notifications/preferences", new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.InApp, NotificationType.StaffDigest, true)])));
        Assert.True(on.Single(p => p.Channel == NotificationChannel.InApp).IsEnabled);
        Assert.False(on.Single(p => p.Channel == NotificationChannel.Email).IsEnabled);
        var inbox = await Ok<PagedResult<StaffInboxItemDto>>(await admin.GetAsync("/api/v1/me/notifications"));
        Assert.Empty(inbox.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/me/notifications/999999/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/me/notifications/preferences", new SavePreferencesRequest([new SavePreferenceRequest((NotificationChannel)77, null, true)]))).StatusCode);
        // a user without company-wide figures cannot subscribe
        var sales = await RoleAsync(RoleNames.SalesManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PutAsJsonAsync("/api/v1/me/notifications/preferences", new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.Email, NotificationType.StaffDigest, true)]))).StatusCode);
    }

    [MySqlFact]
    public async Task CustomerChannels_AreEditableByTheCustomerAndTheOffice()
    {
        var staff = await AdminAsync();
        var customerId = (await Ok<CustomerDetailDto>(await staff.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest("Channel Customer", IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", null, "Murree", null, [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, null)))).Id;
        var email = $"app{Guid.NewGuid():N}@example.com";
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/customers/{customerId}/portal-account", new CreateCustomerAccountRequest(email, "Cust0mer#Passw0rd!"))).StatusCode);
        var customer = await LoginAsync(email, "Cust0mer#Passw0rd!");

        var mine = await Ok<List<PreferenceDto>>(await customer.GetAsync("/api/v1/portal/notification-preferences"));
        Assert.Contains(mine, p => p is { Channel: NotificationChannel.InApp, Locked: true });
        Assert.All(mine.Where(p => !p.Locked), p => Assert.True(p.IsEnabled));
        var saved = await Ok<List<PreferenceDto>>(await customer.PutAsJsonAsync("/api/v1/portal/notification-preferences", new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.Sms, null, false)])));
        Assert.False(saved.Single(p => p.Channel == NotificationChannel.Sms && p.Type == NotificationType.PaymentConfirmation).IsEnabled);

        // the office sees the customer's choice and can change it; the customer API is closed to staff
        var seen = await Ok<List<PreferenceDto>>(await staff.GetAsync($"/api/v1/customers/{customerId}/notification-preferences"));
        Assert.False(seen.Single(p => p.Channel == NotificationChannel.Sms && p.Type == NotificationType.InstallmentOverdue).IsEnabled);
        var changed = await Ok<List<PreferenceDto>>(await staff.PutAsJsonAsync($"/api/v1/customers/{customerId}/notification-preferences", new SavePreferencesRequest([new SavePreferenceRequest(NotificationChannel.Sms, null, true)])));
        Assert.True(changed.Single(p => p.Channel == NotificationChannel.Sms && p.Type == NotificationType.InstallmentOverdue).IsEnabled);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/portal/notification-preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/v1/customers/{customerId}/notification-preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync("/api/v1/customers/99999999/notification-preferences")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ jobs
    [MySqlFact]
    public async Task Jobs_AreListed_WithHistory_AndRunNowNeedsTheScheduler()
    {
        var admin = await AdminAsync();
        var jobs = await Ok<List<JobDto>>(await admin.GetAsync("/api/v1/jobs"));
        Assert.Equal(JobCatalog.All.Count, jobs.Count);
        Assert.Contains(jobs, j => j.Key == JobCatalog.Reminders && j.NeedsServiceUser == false);
        Assert.Contains(jobs, j => j.Key == JobCatalog.Gateway && j.NeedsServiceUser);
        var runs = await Ok<PagedResult<JobRunDto>>(await admin.GetAsync("/api/v1/jobs/runs?key=notifications-dispatch&pageSize=5"));
        Assert.All(runs.Items, r => Assert.Equal("notifications-dispatch", r.JobKey));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/jobs/no-such-job/run", null)).StatusCode);
        var off = await admin.PostAsync("/api/v1/jobs/notifications-dispatch/run", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, off.StatusCode);        // BackgroundProcessing:Enabled is off in the test host
        Assert.Contains("switched off", await off.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------------------------------------ GIS
    [MySqlFact]
    public async Task Gis_BoundaryWorkflow_Export_Layers_AndDocuments()
    {
        var admin = await AdminAsync();
        var pm = await RoleAsync(RoleNames.ProjectManager);
        var projectId = await ProjectAsync(admin);
        var pmId = await UserIdAsync(admin, pm);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/projects/{projectId}/access", new AssignProjectAccessRequest(pmId))).StatusCode);
        var srs = await SrsAsync(pm);

        // submit, refuse invalid geometry
        var bad = await pm.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries", new SubmitBoundaryRequest(srs, """{"type":"Point","coordinates":[1,2]}""", "SV-1", "Surveyor", new DateOnly(2026, 1, 5), null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var created = await pm.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries", new SubmitBoundaryRequest(srs, Square, "SV-1", "Surveyor", new DateOnly(2026, 1, 5), null, "api test"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<BoundaryDto>();
        Assert.Equal(BoundaryStatus.Draft, draft!.Status);
        Assert.Equal(1, draft.VersionNo);

        // the submitter cannot verify (no permission); an administrator can; rejection needs a comment
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries/{draft.Id}/decision", new BoundaryDecisionRequest(true, null, draft.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries/{draft.Id}/decision", new BoundaryDecisionRequest(false, null, draft.RowVersion))).StatusCode);
        var verified = await Ok<BoundaryDto>(await admin.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries/{draft.Id}/decision", new BoundaryDecisionRequest(true, "matches plan", draft.RowVersion)));
        Assert.Equal(BoundaryStatus.Verified, verified.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/boundaries/{draft.Id}/decision", new BoundaryDecisionRequest(true, null, verified.RowVersion))).StatusCode);

        var one = await Ok<BoundaryDto>(await pm.GetAsync($"/api/v1/gis/projects/{projectId}/boundaries/{draft.Id}"));
        Assert.Contains("Polygon", one.GeometryJson);
        var list = await Ok<List<BoundaryDto>>(await pm.GetAsync($"/api/v1/gis/projects/{projectId}/boundaries"));
        Assert.Null(Assert.Single(list).GeometryJson);

        var geo = await pm.GetAsync($"/api/v1/gis/projects/{projectId}/geojson");
        Assert.Equal("application/geo+json", geo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, JsonDocument.Parse(await geo.Content.ReadAsStringAsync()).RootElement.GetProperty("features").GetArrayLength());

        // layers
        var layerReq = new SaveMapLayerRequest("PLOTS", "Plots", MapLayerKind.Plots, srs, 1, true, null, null, null, true, null);
        var layer = await Ok<MapLayerDto>(await pm.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/layers", layerReq));
        Assert.Equal(HttpStatusCode.BadRequest, (await pm.PostAsJsonAsync($"/api/v1/gis/projects/{projectId}/layers", layerReq)).StatusCode);       // duplicate code
        var updated = await Ok<MapLayerDto>(await pm.PutAsJsonAsync($"/api/v1/gis/projects/{projectId}/layers/{layer.Id}", layerReq with { Name = "All plots", RowVersion = layer.RowVersion }));
        Assert.Equal("All plots", updated.Name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await pm.PutAsJsonAsync($"/api/v1/gis/projects/{projectId}/layers/{layer.Id}", layerReq with { RowVersion = layer.RowVersion })).StatusCode);
        Assert.Single(await Ok<List<MapLayerDto>>(await pm.GetAsync($"/api/v1/gis/projects/{projectId}/layers")));

        // documents
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([37, 80, 68, 70, 45]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "plan.pdf");
        form.Add(new StringContent("SurveyPlan"), "type");
        form.Add(new StringContent("Signed plan"), "title");
        var doc = await Ok<MapDocumentDto>(await pm.PostAsync($"/api/v1/gis/projects/{projectId}/documents", form));
        Assert.Equal("Signed plan", doc.Title);
        var dl = await pm.GetAsync($"/api/v1/gis/projects/{projectId}/documents/{doc.Id}/download");
        Assert.Equal(5, (await dl.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await pm.DeleteAsync($"/api/v1/gis/projects/{projectId}/documents/{doc.Id}?reason=no")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pm.DeleteAsync($"/api/v1/gis/projects/{projectId}/documents/{doc.Id}?reason=uploaded%20twice")).StatusCode);
        Assert.Empty(await Ok<List<MapDocumentDto>>(await pm.GetAsync($"/api/v1/gis/projects/{projectId}/documents")));

        // another project manager has no access to this project
        var stranger = await RoleAsync(RoleNames.ProjectManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/v1/gis/projects/{projectId}/boundaries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/v1/gis/projects/{projectId}/geojson")).StatusCode);
        var summary = await Ok<List<GisSummaryDto>>(await pm.GetAsync("/api/v1/gis/summary"));
        Assert.Contains(summary, s => s.ProjectId == projectId && s.HasVerifiedBoundary);
        Assert.DoesNotContain(await Ok<List<GisSummaryDto>>(await stranger.GetAsync("/api/v1/gis/summary")), s => s.ProjectId == projectId);
    }

    [MySqlFact]
    public async Task Gis_CoordinateSystems_CanBeAdded_ByAllProjectUsersOnly()
    {
        var admin = await AdminAsync();
        var pm = await RoleAsync(RoleNames.ProjectManager);
        var code = "EPSG:" + Random.Shared.Next(100000, 999999);
        var req = new SaveSrsRequest(code, int.Parse(code[5..]), "Test grid", SpatialCoordinateKind.Projected, "metre", null, true, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync("/api/v1/gis/spatial-reference-systems", req)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/gis/spatial-reference-systems", req)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/gis/spatial-reference-systems", req)).StatusCode);
        var all = await Ok<List<SrsDto>>(await admin.GetAsync("/api/v1/gis/spatial-reference-systems"));
        var created = all.Single(s => s.Code == code);
        var renamed = await Ok<SrsDto>(await admin.PutAsJsonAsync($"/api/v1/gis/spatial-reference-systems/{created.Id}", req with { Name = "Renamed grid", RowVersion = created.RowVersion }));
        Assert.Equal("Renamed grid", renamed.Name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"/api/v1/gis/spatial-reference-systems/{created.Id}", req with { Srid = 9, RowVersion = renamed.RowVersion })).StatusCode);
    }

    private static async Task<Guid> UserIdAsync(HttpClient admin, HttpClient user)
    {
        var me = await user.GetFromJsonAsync<CurrentUserDto>("/api/v1/auth/me");
        return me!.Id;
    }
}
