using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 11 office screens: notifications, templates, settings, channel choices, staff inbox, background jobs and the GIS foundation (rendered-HTML tests; share the helpers of the recovery tests).</summary>
public partial class Phase06RecoveryWebTests
{
    private const string Square = """{"type":"Polygon","coordinates":[[[73.0,33.0],[73.1,33.0],[73.1,33.1],[73.0,33.1],[73.0,33.0]]]}""";

    private async Task<long> Srs4326Async() => await DbAsync(async db => await db.SpatialReferenceSystems.Where(s => s.Code == "EPSG:4326").Select(s => s.Id).FirstAsync());

    private static async Task<HttpResponseMessage> UploadFormAsync(HttpClient c, string formPage, string action, string fileName, params (string, string)[] fields)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        form.Add(new StringContent(await WebFactory.TokenAsync(c, formPage)), "__RequestVerificationToken");
        var file = new ByteArrayContent([37, 80, 68, 70, 45, 49]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        return await c.PostAsync(action, form);
    }

    // ------------------------------------------------------------------------------------------------ authorization
    [MySqlFact]
    public async Task Phase11Screens_AreForbidden_WithoutPermission_AndNeedLogin()
    {
        var anon = f.NewClient();
        foreach (var url in new[] { "/Notifications", "/Notifications/Send", "/Notifications/Templates", "/Notifications/Settings", "/Jobs", "/Jobs/Runs", "/Gis", "/Gis/Systems", "/MyNotifications" })
        {
            var r = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("Login", r.Headers.Location!.ToString());
        }
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/Notifications", "/Notifications/Send", "/Notifications/Templates", "/Notifications/Settings", "/Jobs", "/Jobs/Runs", "/Gis", "/Gis/Systems", "/Gis/SrsForm" })
        {
            var r = await cashier.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/MyNotifications")).StatusCode);      // everyone has their own inbox
        var accountant = await f.LoggedInAsRoleAsync(RoleNames.Accountant);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/Notifications")).StatusCode);        // view only
        Assert.Contains("AccessDenied", (await accountant.GetAsync("/Notifications/Send")).Headers.Location!.ToString());
        Assert.Contains("AccessDenied", (await accountant.GetAsync("/Jobs")).Headers.Location!.ToString());
    }

    // ------------------------------------------------------------------------------------------------ notifications
    [MySqlFact]
    public async Task Notifications_AreSent_Listed_Inspected_Retried_AndCancelled_ThroughTheUi()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var (customerId, code) = await InsertCustomerAsync();

        var index = await GetOkAsync(c, "/Notifications");
        foreach (var t in new[] { "Notifications", "Send a message", "Templates", "Reminder settings", "Background jobs", "Failed deliveries" }) Dbg.Has(index, t);
        var form = await GetOkAsync(c, "/Notifications/Send");
        foreach (var t in new[] { "Send a message to customers", "Company announcement", "Possession notice", "Owners in one project" }) Dbg.Has(form, t);

        // validation: audience customer without an id, and a missing title
        var bad = await PostAsync(c, "/Notifications/Send", "/Notifications/Send", ("Type", "CompanyAnnouncement"), ("Audience", "customer"), ("Title", ""), ("Message", "Hello"), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Enter the customer id.");

        var title = "Office notice " + Guid.NewGuid().ToString("N")[..6];
        var key = Guid.NewGuid().ToString("N");
        var sent = await PostAsync(c, "/Notifications/Send", "/Notifications/Send", ("Type", "CompanyAnnouncement"), ("Audience", "customer"), ("CustomerId", customerId.ToString()), ("Title", title), ("Message", "We are closed on Friday."), ("IdempotencyKey", key));
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        // sending the same form again (same key) queues nothing new
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/Notifications/Send", "/Notifications/Send", ("Type", "CompanyAnnouncement"), ("Audience", "customer"), ("CustomerId", customerId.ToString()), ("Title", title), ("Message", "We are closed on Friday."), ("IdempotencyKey", key))).StatusCode);
        Assert.Equal(1, await DbAsync(db => db.Notifications.CountAsync(n => n.Title == title)));

        var list = await GetOkAsync(c, $"/Notifications?search={Uri.EscapeDataString(title)}&type=CompanyAnnouncement");
        Dbg.Has(list, title); Dbg.Has(list, "Portal Customer"); Dbg.Has(list, "Pending");
        var id = await DbAsync(async db => await db.Notifications.Where(n => n.Title == title).Select(n => n.Id).FirstAsync());
        var details = await GetOkAsync(c, $"/Notifications/Details/{id}");
        foreach (var t in new[] { title, "We are closed on Friday.", "Deliveries", "InApp", "Email", "Sms", "Cancel this notification", $"manual:{key}" }) Dbg.Has(details, t);

        // a failed delivery can be retried from the screen
        var deliveryId = await DbAsync(async db =>
        {
            var d = await db.NotificationDeliveryLogs.FirstAsync(x => x.NotificationId == id && x.Channel == NotificationChannel.Sms);
            d.Status = DeliveryStatus.Failed; d.Attempts = 5; d.LastError = "gateway timeout";
            await db.SaveChangesAsync();
            return d.Id;
        });
        details = await GetOkAsync(c, $"/Notifications/Details/{id}");
        Dbg.Has(details, "gateway timeout"); Dbg.Has(details, "Retry");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Notifications/Details/{id}", "/Notifications/Retry", ("deliveryId", deliveryId.ToString()), ("id", id.ToString()))).StatusCode);
        Assert.Equal(DeliveryStatus.Retrying, await DbAsync(async db => (await db.NotificationDeliveryLogs.FirstAsync(x => x.Id == deliveryId)).Status));

        // cancel needs a reason; then nothing is sent and the status shows
        details = await GetOkAsync(c, $"/Notifications/Details/{id}");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Notifications/Details/{id}", "/Notifications/Cancel", ("id", id.ToString()), ("reason", "x"), ("rowVersion", Rv(details, "rowVersion")))).StatusCode);
        Assert.Equal(NotificationStatus.Pending, await DbAsync(async db => (await db.Notifications.FirstAsync(n => n.Id == id)).Status));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Notifications/Details/{id}", "/Notifications/Cancel", ("id", id.ToString()), ("reason", "sent by mistake"), ("rowVersion", Rv(details, "rowVersion")))).StatusCode);
        Assert.Equal(NotificationStatus.Cancelled, await DbAsync(async db => (await db.Notifications.FirstAsync(n => n.Id == id)).Status));
        details = await GetOkAsync(c, $"/Notifications/Details/{id}");
        Dbg.Has(details, "Cancelled"); Dbg.Has(details, "sent by mistake");

        // anti-forgery is required
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/Notifications/Send", new FormUrlEncodedContent(new Dictionary<string, string> { ["Title"] = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/Notifications/Details/99999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Templates_AndReminderSettings_AreEditedThroughTheUi()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var list = await GetOkAsync(c, "/Notifications/Templates");
        foreach (var t in new[] { "Notification templates", "InstallmentUpcoming", "WhatsApp", "{{CustomerName}}" }) Dbg.Has(list, t);

        var tplId = await DbAsync(async db => await db.NotificationTemplates.Where(t => t.Type == NotificationType.BookingConfirmation && t.Channel == NotificationChannel.Push).Select(t => t.Id).FirstAsync());
        var form = await GetOkAsync(c, $"/Notifications/TemplateForm/{tplId}");
        Dbg.Has(form, "Edit template"); Dbg.Has(form, "BookingConfirmation");
        var rv = Rv(form);
        var invalid = await PostAsync(c, $"/Notifications/TemplateForm/{tplId}", $"/Notifications/TemplateForm/{tplId}", ("Type", "BookingConfirmation"), ("Channel", "Push"), ("Language", "en"),
            ("Subject", "Booked"), ("Body", "Hello {{Password}}"), ("IsActive", "true"), ("RowVersion", rv));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Dbg.Has(await BodyAsync(invalid), "Unknown placeholder");
        var ok = await PostAsync(c, $"/Notifications/TemplateForm/{tplId}", $"/Notifications/TemplateForm/{tplId}", ("Type", "BookingConfirmation"), ("Channel", "Push"), ("Language", "en"),
            ("Subject", "Booked!"), ("Body", "Your booking of {{PropertyCode}} is confirmed."), ("IsActive", "true"), ("RowVersion", rv));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("Booked!", await DbAsync(async db => (await db.NotificationTemplates.FirstAsync(t => t.Id == tplId)).Subject));
        // the same (now stale) form is refused
        var stale = await PostAsync(c, $"/Notifications/TemplateForm/{tplId}", $"/Notifications/TemplateForm/{tplId}", ("Type", "BookingConfirmation"), ("Channel", "Push"), ("Language", "en"),
            ("Subject", "Again"), ("Body", "Other"), ("IsActive", "true"), ("RowVersion", rv));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Dbg.Has(await BodyAsync(stale), "changed by another user");

        var settings = await GetOkAsync(c, "/Notifications/Settings");
        foreach (var t in new[] { "Reminder settings", "Remind this many days before the due date", "Repeat overdue reminders every (days)" }) Dbg.Has(settings, t);
        var badSettings = await PostAsync(c, "/Notifications/Settings", "/Notifications/Settings", ("UpcomingDaysBefore", "99"), ("OverdueRepeatDays", "0"), ("EventLookbackDays", "7"));
        Assert.Equal(HttpStatusCode.OK, badSettings.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/Notifications/Settings", "/Notifications/Settings", ("UpcomingDaysBefore", "3"), ("OverdueRepeatDays", "7"), ("EventLookbackDays", "7"))).StatusCode);
    }

    [MySqlFact]
    public async Task CustomerChannels_AreEditedFromTheCustomerPage()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var (customerId, code) = await InsertCustomerAsync();
        Dbg.Has(await GetOkAsync(c, $"/Customers/Details/{customerId}"), "Notification channels");
        var page = await GetOkAsync(c, $"/Notifications/CustomerPreferences/{customerId}");
        foreach (var t in new[] { "Notification channels - Portal Customer", code, "In-app notifications are always on", "WhatsApp", "Installment Upcoming", "Payment Confirmation" }) Dbg.Has(page, t);
        Assert.Contains("checked", Regex.Match(page, "<input[^>]*value=\"Sms:2\"[^>]*>").Value);

        // tick only e-mail for overdue installments: everything else is switched off
        var r = await PostAsync(c, $"/Notifications/CustomerPreferences/{customerId}", $"/Notifications/CustomerPreferences/{customerId}", ("enabled", "Email:2"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var rows = await DbAsync(db => db.NotificationPreferences.Where(p => p.CustomerId == customerId).ToListAsync());
        Assert.Contains(rows, p => p is { Channel: NotificationChannel.Sms, TypeKey: 2, IsEnabled: false });
        Assert.DoesNotContain(rows, p => p is { Channel: NotificationChannel.Email, TypeKey: 2 });          // default on: no row needed
        Assert.Contains(rows, p => p is { Channel: NotificationChannel.Email, TypeKey: 1, IsEnabled: false });
        var after = await GetOkAsync(c, $"/Notifications/CustomerPreferences/{customerId}");
        Assert.DoesNotContain("checked", Regex.Match(after, "<input[^>]*value=\"Sms:2\"[^>]*>").Value);
        Assert.Contains("checked", Regex.Match(after, "<input[^>]*value=\"Email:2\"[^>]*>").Value);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/Notifications/CustomerPreferences/99999999")).StatusCode);
    }

    [MySqlFact]
    public async Task MyNotifications_ShowsTheStaffInbox_AndStoresTheDigestChoice()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var page = await GetOkAsync(c, "/MyNotifications");
        foreach (var t in new[] { "My notifications", "No notifications yet", "Daily summary", "By e-mail", "In this inbox" }) Dbg.Has(page, t);

        var title = "Daily summary " + Guid.NewGuid().ToString("N")[..6];
        var id = await DbAsync(async db =>
        {
            var n = new Notification { Type = NotificationType.StaffDigest, RecipientUserId = admin.Id, Title = title, Body = "3 payments posted yesterday.", DedupeKey = "ui-digest-" + Guid.NewGuid().ToString("N"), ScheduledForUtc = DateTime.UtcNow, Status = NotificationStatus.Completed };
            n.Deliveries.Add(new NotificationDeliveryLog { Channel = NotificationChannel.InApp, Recipient = "in-app", Subject = title, Body = "3 payments posted yesterday.", Status = DeliveryStatus.Sent, SentAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow });
            db.Notifications.Add(n);
            await db.SaveChangesAsync();
            return n.Id;
        });
        page = await GetOkAsync(c, "/MyNotifications");
        Dbg.Has(page, title); Dbg.Has(page, "3 payments posted yesterday."); Dbg.Has(page, "Mark read");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/MyNotifications", "/MyNotifications/MarkRead", ("id", id.ToString()))).StatusCode);
        Assert.NotNull(await DbAsync(async db => (await db.Notifications.FirstAsync(n => n.Id == id)).ReadAtUtc));
        Assert.DoesNotContain("Mark read", await GetOkAsync(c, "/MyNotifications"));

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/MyNotifications", "/MyNotifications/SavePreferences", ("enabled", "InApp:8"), ("enabled", "Email:8"))).StatusCode);
        var prefs = await DbAsync(db => db.NotificationPreferences.Where(p => p.UserId == admin.Id && p.TypeKey == 8).ToListAsync());
        Assert.Equal(2, prefs.Count(p => p.IsEnabled));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/MyNotifications", "/MyNotifications/SavePreferences", ("enabled", "InApp:8"))).StatusCode);
        Assert.False((await DbAsync(db => db.NotificationPreferences.FirstAsync(p => p.UserId == admin.Id && p.Channel == NotificationChannel.Email && p.TypeKey == 8))).IsEnabled);
    }

    // ------------------------------------------------------------------------------------------------ jobs
    [MySqlFact]
    public async Task Jobs_ShowTheCatalogueAndHistory_AndRunNowIsUnavailableWhenTheSchedulerIsOff()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        await DbAsync(async db =>
        {
            db.BackgroundJobRuns.Add(new BackgroundJobRun { JobKey = "notifications-dispatch", TriggeredBy = "schedule", StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), FinishedAtUtc = DateTime.UtcNow, Status = BackgroundJobRunStatus.Succeeded, ItemsProcessed = 7, Message = "7 sent, 0 to retry, 0 failed, 0 skipped." });
            db.BackgroundJobRuns.Add(new BackgroundJobRun { JobKey = "payments-gateway-reconcile", TriggeredBy = "schedule", StartedAtUtc = DateTime.UtcNow.AddMinutes(-2), FinishedAtUtc = DateTime.UtcNow, Status = BackgroundJobRunStatus.Failed, Message = "provider unreachable" });
            return await db.SaveChangesAsync();
        });
        var page = await GetOkAsync(c, "/Jobs");
        foreach (var t in new[] { "Background jobs", "Notification dispatch", "Installment reminders", "Online payment reconciliation", "Automatic accounting postings", "Daily management summary", "Every minute",
                     "7 sent, 0 to retry", "provider unreachable", "switched off on this server", "Runs as the configured service user" }) Dbg.Has(page, t);
        Assert.DoesNotContain(">Run now</button>", page);   // no run-now button with the scheduler off

        var runs = await GetOkAsync(c, "/Jobs/Runs?key=notifications-dispatch");
        Dbg.Has(runs, "Job run history - notifications-dispatch"); Dbg.Has(runs, "7 sent");
        Assert.DoesNotContain("provider unreachable", runs);
        Dbg.Has(await GetOkAsync(c, "/Jobs/Runs"), "provider unreachable");

        var run = await PostAsync(c, "/Jobs", "/Jobs/Run", ("key", "notifications-dispatch"));
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);                  // refused with a message, no run is queued
        Assert.Equal(1, await DbAsync(db => db.BackgroundJobRuns.CountAsync(r => r.JobKey == "notifications-dispatch" && r.TriggeredBy == "schedule" && r.ItemsProcessed == 7)));
    }

    // ------------------------------------------------------------------------------------------------ GIS
    [MySqlFact]
    public async Task Gis_Boundaries_AreSubmitted_AndVerifiedByAnotherUser_ThroughTheUi()
    {
        var maker = await NewAdminAsync();
        var checker = await NewAdminAsync();
        var c = maker.Client;
        var projectId = await CreateProjectWithPriceAsync(c);
        var srs = await Srs4326Async();

        var overview = await GetOkAsync(c, "/Gis");
        foreach (var t in new[] { "GIS foundation", "Coordinate systems", "None verified" }) Dbg.Has(overview, t);
        var project = await GetOkAsync(c, $"/Gis/Project/{projectId}");
        foreach (var t in new[] { "Project boundary versions", "No boundary has been submitted yet.", "Submit new survey", "Map layers", "Survey and map documents", "Export GeoJSON" }) Dbg.Has(project, t);

        var form = await GetOkAsync(c, $"/Gis/SubmitBoundary?projectId={projectId}");
        foreach (var t in new[] { "Submit a surveyed boundary", "EPSG:4326", "Survey reference", "Surveyed by" }) Dbg.Has(form, t);
        var bad = await PostAsync(c, $"/Gis/SubmitBoundary?projectId={projectId}", $"/Gis/SubmitBoundary?projectId={projectId}", ("SrsId", srs.ToString()), ("GeometryJson", """{"type":"Point","coordinates":[1,2]}"""),
            ("SurveyReference", "SV-1"), ("SurveyedBy", "Survey Co"), ("SurveyDate", "2026-02-01"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Polygon or MultiPolygon");

        var ok = await PostAsync(c, $"/Gis/SubmitBoundary?projectId={projectId}", $"/Gis/SubmitBoundary?projectId={projectId}", ("SrsId", srs.ToString()), ("GeometryJson", Square),
            ("SurveyReference", "SV-1"), ("SurveyedBy", "Survey Co"), ("SurveyDate", "2026-02-01"), ("Remarks", "ui test"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var location = ok.Headers.Location!.ToString();
        Assert.Contains("/Gis/BoundaryDetails", location);
        var boundaryId = await DbAsync(async db => await db.ProjectBoundaries.Where(b => b.ProjectId == projectId).Select(b => b.Id).SingleAsync());

        var details = await GetOkAsync(c, $"/Gis/BoundaryDetails?projectId={projectId}&id={boundaryId}");
        foreach (var t in new[] { "boundary v1", "Draft", "SV-1", "Verify against the survey", "Geometry (GeoJSON)", "Polygon" }) Dbg.Has(details, t);

        // the submitter cannot verify their own boundary
        var own = await PostAsync(c, $"/Gis/BoundaryDetails?projectId={projectId}&id={boundaryId}", "/Gis/DecideBoundary", ("projectId", projectId.ToString()), ("id", boundaryId.ToString()), ("approve", "true"), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, own.StatusCode);
        Assert.Contains("AccessDenied", own.Headers.Location!.ToString());
        Assert.Equal(BoundaryStatus.Draft, await DbAsync(async db => (await db.ProjectBoundaries.FirstAsync(b => b.Id == boundaryId)).Status));

        // someone else rejects without a comment (refused), then verifies
        var noComment = await PostAsync(checker.Client, $"/Gis/BoundaryDetails?projectId={projectId}&id={boundaryId}", "/Gis/DecideBoundary", ("projectId", projectId.ToString()), ("id", boundaryId.ToString()), ("approve", "false"), ("comment", ""), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, noComment.StatusCode);
        Assert.Equal(BoundaryStatus.Draft, await DbAsync(async db => (await db.ProjectBoundaries.FirstAsync(b => b.Id == boundaryId)).Status));
        var verify = await PostAsync(checker.Client, $"/Gis/BoundaryDetails?projectId={projectId}&id={boundaryId}", "/Gis/DecideBoundary", ("projectId", projectId.ToString()), ("id", boundaryId.ToString()), ("approve", "true"), ("comment", "matches plan"), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, verify.StatusCode);
        details = await GetOkAsync(c, $"/Gis/BoundaryDetails?projectId={projectId}&id={boundaryId}");
        foreach (var t in new[] { "Verified", "matches plan" }) Dbg.Has(details, t);
        Assert.DoesNotContain("Verify against the survey", details);
        Dbg.Has(await GetOkAsync(c, "/Gis"), "Verified v1");

        var export = await c.GetAsync($"/Gis/ExportGeoJson?projectId={projectId}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/geo+json", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("FeatureCollection", await export.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task Gis_Layers_Documents_PropertyBoundaries_AndSystems_WorkThroughTheUi()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var projectId = await CreateProjectWithPriceAsync(c);
        var propertyId = await CreatePropertyAsync(c, projectId);
        var srs = await Srs4326Async();

        // layers
        Dbg.Has(await GetOkAsync(c, $"/Gis/LayerForm?projectId={projectId}"), "New map layer");
        var layerFields = new[] { ("Code", "PLOTS"), ("Name", "Plots"), ("Kind", "Plots"), ("SrsId", srs.ToString()), ("SortOrder", "1"), ("IsVisibleByDefault", "true"), ("IsActive", "true") };
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Gis/LayerForm?projectId={projectId}", $"/Gis/LayerForm?projectId={projectId}", layerFields)).StatusCode);
        var dup = await PostAsync(c, $"/Gis/LayerForm?projectId={projectId}", $"/Gis/LayerForm?projectId={projectId}", layerFields);
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Dbg.Has(await BodyAsync(dup), "already has a layer with that code");
        var badStyle = await PostAsync(c, $"/Gis/LayerForm?projectId={projectId}", $"/Gis/LayerForm?projectId={projectId}", ("Code", "OTHER"), ("Name", "Other"), ("Kind", "Streets"), ("SrsId", srs.ToString()), ("SortOrder", "2"), ("StyleJson", "not json"));
        Assert.Equal(HttpStatusCode.OK, badStyle.StatusCode);
        var layerId = await DbAsync(async db => await db.MapLayers.Where(l => l.ProjectId == projectId).Select(l => l.Id).SingleAsync());
        var edit = await GetOkAsync(c, $"/Gis/LayerForm?projectId={projectId}&id={layerId}");
        Dbg.Has(edit, "Edit map layer");
        var saved = await PostAsync(c, $"/Gis/LayerForm?projectId={projectId}&id={layerId}", $"/Gis/LayerForm?projectId={projectId}", ("Id", layerId.ToString()), ("Code", "PLOTS"), ("Name", "All plots"), ("Kind", "Plots"),
            ("SrsId", srs.ToString()), ("SortOrder", "1"), ("IsVisibleByDefault", "true"), ("IsActive", "true"), ("RowVersion", Rv(edit)));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/Gis/Project/{projectId}"), "All plots");

        // documents: upload, list, download, remove
        var up = await UploadFormAsync(c, $"/Gis/Project/{projectId}", "/Gis/UploadDocument", "survey.pdf", ("projectId", projectId.ToString()), ("type", "SurveyPlan"), ("title", "Survey plan 2026"), ("srsId", srs.ToString()), ("surveyReference", "SV-7"));
        Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
        var page = await GetOkAsync(c, $"/Gis/Project/{projectId}");
        Dbg.Has(page, "Survey plan 2026"); Dbg.Has(page, "SV-7"); Dbg.Has(page, "Download");
        var docId = await DbAsync(async db => await db.MapDocuments.Where(d => d.ProjectId == projectId).Select(d => d.Id).SingleAsync());
        var dl = await c.GetAsync($"/Gis/DownloadDocument?projectId={projectId}&documentId={docId}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(6, (await dl.Content.ReadAsByteArrayAsync()).Length);
        var exe = await UploadFormAsync(c, $"/Gis/Project/{projectId}", "/Gis/UploadDocument", "tool.exe", ("projectId", projectId.ToString()), ("type", "Other"));
        Assert.Equal(HttpStatusCode.Redirect, exe.StatusCode);
        Assert.Equal(1, await DbAsync(db => db.MapDocuments.CountAsync(d => d.ProjectId == projectId)));          // refused: only the pdf is stored
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/Gis/Project/{projectId}", "/Gis/DeleteDocument", ("projectId", projectId.ToString()), ("documentId", docId.ToString()), ("reason", "uploaded twice"))).StatusCode);
        Assert.True(await DbAsync(async db => (await db.MapDocuments.FirstAsync(d => d.Id == docId)).IsDeleted));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/Gis/DownloadDocument?projectId={projectId}&documentId={docId}")).StatusCode);

        // property boundaries
        var pb = await GetOkAsync(c, $"/Gis/PropertyBoundaries?propertyId={propertyId}");
        foreach (var t in new[] { "Boundaries of", "No boundary has been submitted for this property.", "Submit new survey" }) Dbg.Has(pb, t);
        Dbg.Has(await GetOkAsync(c, $"/Properties/Details/{propertyId}"), "Boundaries");
        var submitted = await PostAsync(c, $"/Gis/SubmitBoundary?projectId={projectId}&propertyId={propertyId}", $"/Gis/SubmitBoundary?projectId={projectId}&propertyId={propertyId}", ("SrsId", srs.ToString()), ("GeometryJson", Square),
            ("SurveyReference", "SV-P1"), ("SurveyedBy", "Survey Co"), ("SurveyDate", "2026-02-02"));
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/Gis/PropertyBoundaries?propertyId={propertyId}"), "SV-P1");

        // coordinate systems
        var systems = await GetOkAsync(c, "/Gis/Systems");
        foreach (var t in new[] { "EPSG:4326", "EPSG:32643", "Standard", "New system" }) Dbg.Has(systems, t);
        var code = "EPSG:" + Random.Shared.Next(100000, 999999);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/Gis/SrsForm", "/Gis/SrsForm", ("Code", code), ("Srid", code[5..]), ("Name", "UI grid"), ("Kind", "Projected"), ("Unit", "metre"), ("IsActive", "true"))).StatusCode);
        var dupSrs = await PostAsync(c, "/Gis/SrsForm", "/Gis/SrsForm", ("Code", code), ("Srid", code[5..]), ("Name", "UI grid"), ("Kind", "Projected"), ("Unit", "metre"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, dupSrs.StatusCode);
        Dbg.Has(await BodyAsync(dupSrs), "already exists");
        Dbg.Has(await GetOkAsync(c, "/Gis/Systems"), "UI grid");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/Gis/SrsForm/99999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Navigation_ShowsThePhase11Entries_ToAuthorizedUsers()
    {
        var admin = await NewAdminAsync();
        var home = await GetOkAsync(admin.Client, "/");
        foreach (var t in new[] { "My notifications", "Automation and GIS", "Notifications", "Background jobs", "GIS foundation" }) Dbg.Has(home, t);
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        var cashierHome = Decode(await cashier.GetStringAsync("/"));
        Dbg.Has(cashierHome, "My notifications");
        Assert.DoesNotContain("Automation and GIS", cashierHome);
    }
}
