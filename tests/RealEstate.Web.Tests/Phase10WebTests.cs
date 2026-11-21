using System.Net;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 10 office screens for the customer application (rendered-HTML tests; shares the setup helpers of the recovery tests).</summary>
public partial class Phase06RecoveryWebTests
{
    private async Task<(long Id, string Code)> InsertCustomerAsync() => await DbAsync(async db =>
    {
        var n = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
        var c = new Customer { CustomerCode = "CUS-T" + n[..8], FullName = "Portal Customer", IdentityType = IdentityDocumentType.Cnic, IdentityNumber = n, Mobile = "0300-1234567", Status = CustomerStatus.Active };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return (c.Id, c.CustomerCode);
    });

    [MySqlFact]
    public async Task CustomerAppLogin_IsCreatedDisabledResetAndReEnabled_ThroughTheUi()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var (id, code) = await InsertCustomerAsync();
        var email = $"ui{Guid.NewGuid():N}@example.com";

        var details = await GetOkAsync(c, $"/Customers/Details/{id}");
        Dbg.Has(details, "App login");
        var page = await GetOkAsync(c, $"/CustomerApp/Account/{id}");
        foreach (var t in new[] { "Customer app login", code, "Create a login for the customer application", "Initial password" }) Dbg.Has(page, t);

        var weak = await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/CreateAccount/{id}", ("Email", email), ("Password", "short"));
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        Assert.False(await DbAsync(db => db.CustomerAccounts.AnyAsync(a => a.CustomerId == id)));

        var created = await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/CreateAccount/{id}", ("Email", email), ("Password", "Cust0mer#Passw0rd!"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        page = await GetOkAsync(c, $"/CustomerApp/Account/{id}");
        foreach (var t in new[] { email, "Enabled", "Disable login", "Reset password", "Send an in-app notification" }) Dbg.Has(page, t);
        Assert.Contains(RoleNames.Customer, await DbAsync(async db => await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id join a in db.CustomerAccounts on ur.UserId equals a.UserId
                                                                             where a.CustomerId == id select r.Name).ToListAsync()));

        var again = await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/CreateAccount/{id}", ("Email", "other" + email), ("Password", "Cust0mer#Passw0rd!"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Dbg.Has(await BodyAsync(again), "already has an application login");

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/SendNotification/{id}", ("title", "Hello"), ("message", "Welcome"))).StatusCode);
        Assert.True(await DbAsync(db => db.CustomerNotifications.AnyAsync(n => n.CustomerId == id && n.Title == "Hello")));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/ResetAccountPassword/{id}", ("newPassword", "Reset#Passw0rd-77"))).StatusCode);

        var rv = Rv(page, "rowVersion");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/DisableAccount/{id}", ("reason", "closed account"), ("rowVersion", rv))).StatusCode);
        page = await GetOkAsync(c, $"/CustomerApp/Account/{id}");
        foreach (var t in new[] { "Disabled", "closed account", "Enable login" }) Dbg.Has(page, t);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, $"/CustomerApp/Account/{id}", $"/CustomerApp/EnableAccount/{id}", ("rowVersion", Rv(page, "rowVersion")))).StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/CustomerApp/Account/{id}"), "Disable login");

        // anti-forgery is required on every action
        var noToken = await c.PostAsync($"/CustomerApp/DisableAccount/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    [MySqlFact]
    public async Task Announcements_AndPaymentInstructions_AreManagedThroughTheUi()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        Dbg.Has(await GetOkAsync(c, "/CustomerApp/Announcements"), "Customer announcements");
        var form = await GetOkAsync(c, "/CustomerApp/AnnouncementForm");
        Dbg.Has(form, "New announcement"); Dbg.Has(form, "All customers");
        var title = "Notice " + Guid.NewGuid().ToString("N")[..6];
        var bad = await PostAsync(c, "/CustomerApp/AnnouncementForm", "/CustomerApp/AnnouncementForm", ("Title", ""), ("Body", "Text"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var created = await PostAsync(c, "/CustomerApp/AnnouncementForm", "/CustomerApp/AnnouncementForm", ("Title", title), ("Body", "The office is closed on Friday."), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Dbg.Has(await GetOkAsync(c, "/CustomerApp/Announcements"), title);
        var id = await DbAsync(async db => await db.Announcements.Where(a => a.Title == title).Select(a => a.Id).FirstAsync());
        var edit = await GetOkAsync(c, $"/CustomerApp/AnnouncementForm/{id}");
        Dbg.Has(edit, "Edit announcement"); Dbg.Has(edit, "The office is closed on Friday.");
        var saved = await PostAsync(c, $"/CustomerApp/AnnouncementForm/{id}", "/CustomerApp/AnnouncementForm", ("Id", id.ToString()), ("Title", title), ("Body", "Changed"), ("IsActive", "false"), ("RowVersion", Rv(edit)));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.False(await DbAsync(async db => (await db.Announcements.FirstAsync(a => a.Id == id)).IsActive));

        var page = await GetOkAsync(c, "/CustomerApp/Instructions");
        Dbg.Has(page, "Payment instructions");
        var note = "Pay to account 123 and send the slip " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(c, "/CustomerApp/Instructions", "/CustomerApp/Instructions", ("Note", note))).StatusCode);
        Dbg.Has(await GetOkAsync(c, "/CustomerApp/Instructions"), note);
    }

    [MySqlFact]
    public async Task SupportDesk_AndGatewayScreens_RenderAndWork()
    {
        var admin = await NewAdminAsync();
        var c = admin.Client;
        var (customerId, code) = await InsertCustomerAsync();
        var requestId = await DbAsync(async db =>
        {
            var s = new SupportRequest { RequestNumber = "SUP-T" + Guid.NewGuid().ToString("N")[..8], CustomerId = customerId, Category = SupportCategory.Payment, Subject = "Receipt missing", LastActivityAtUtc = DateTime.UtcNow };
            s.Messages.Add(new SupportRequestMessage { AuthorType = SupportAuthorType.Customer, AuthorName = "Portal Customer", Body = "I did not get my receipt", CreatedAtUtc = DateTime.UtcNow });
            db.SupportRequests.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var list = await GetOkAsync(c, "/Support?search=Receipt");
        Dbg.Has(list, "Customer support requests"); Dbg.Has(list, "Receipt missing"); Dbg.Has(list, code);
        var details = await GetOkAsync(c, $"/Support/Details/{requestId}");
        Dbg.Has(details, "I did not get my receipt"); Dbg.Has(details, "Send reply");
        var reply = await PostAsync(c, $"/Support/Details/{requestId}", $"/Support/Reply/{requestId}", ("message", "We sent it by e-mail."), ("status", ((int)SupportRequestStatus.Resolved).ToString()), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, reply.StatusCode);
        details = await GetOkAsync(c, $"/Support/Details/{requestId}");
        Dbg.Has(details, "We sent it by e-mail."); Dbg.Has(details, "Resolved");
        Assert.True(await DbAsync(db => db.CustomerNotifications.AnyAsync(n => n.CustomerId == customerId && n.Kind == NotificationKind.Support)));
        var closed = await PostAsync(c, $"/Support/Details/{requestId}", $"/Support/SetStatus/{requestId}", ("status", ((int)SupportRequestStatus.Closed).ToString()), ("rowVersion", Rv(await GetOkAsync(c, $"/Support/Details/{requestId}"), "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.DoesNotContain("Send reply", await GetOkAsync(c, $"/Support/Details/{requestId}"));

        // gateway monitoring: pages render, a rejected callback is visible, reconciliation runs
        Dbg.Has(await GetOkAsync(c, "/Gateway"), "Online payments (gateway)");
        Dbg.Has(await GetOkAsync(c, "/Gateway/Webhooks"), "Payment callback log");
        await DbAsync(async db =>
        {
            db.PaymentWebhookLogs.Add(new PaymentWebhookLog { Provider = "sandbox", EventId = "ui-" + Guid.NewGuid().ToString("N")[..8], SignatureValid = false, Payload = "{}", ReceivedAtUtc = DateTime.UtcNow,
                ProcessingStatus = WebhookProcessingStatus.Rejected, ProcessingMessage = "Invalid signature." });
            return await db.SaveChangesAsync();
        });
        var logs = await GetOkAsync(c, "/Gateway/Webhooks?status=Rejected");
        Dbg.Has(logs, "Invalid signature."); Dbg.Has(logs, "Invalid");
        var reconcile = await PostAsync(c, "/Gateway", "/Gateway/Reconcile");
        Assert.Equal(HttpStatusCode.Redirect, reconcile.StatusCode);
        Dbg.Has(await FollowAsync(c, reconcile), "Reconciliation checked");
    }

    [MySqlFact]
    public async Task CustomerAppScreens_AreForbidden_WithoutPermission()
    {
        var plain = await f.LoggedInAsync(administrator: false);
        foreach (var url in new[] { "/Support", "/Gateway", "/Gateway/Webhooks", "/CustomerApp/Announcements", "/CustomerApp/AnnouncementForm", "/CustomerApp/Instructions", "/CustomerApp/Account/1" })
        {
            var r = await plain.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Redirect, (await cashier.GetAsync("/Gateway")).StatusCode);
        var support = await f.LoggedInAsRoleAsync(RoleNames.CustomerSupport);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/Support")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/CustomerApp/Announcements")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await support.GetAsync("/Gateway")).StatusCode);
        // the proof download needs an anti-forgery token and the payment permission
        Assert.Equal(HttpStatusCode.BadRequest, (await support.PostAsync("/PaymentProofs/Download", new FormUrlEncodedContent(new Dictionary<string, string> { ["paymentId"] = "1", ["documentId"] = "1" }))).StatusCode);
    }
}

public partial class Phase06RecoveryWebTests
{
    [MySqlFact]
    public async Task CustomerLogin_IsRefusedByTheOfficeWebSite()
    {
        var email = await f.CreateUserAsync(administrator: false);
        await DbAsync(async db =>
        {
            var user = await db.Users.FirstAsync(u => u.Email == email);
            var role = await db.Roles.FirstAsync(r => r.Name == RoleNames.Customer);
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            return await db.SaveChangesAsync();
        });
        var c = f.NewClient();
        var r = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = email, ["Password"] = WebFactory.Password, ["__RequestVerificationToken"] = await WebFactory.TokenAsync(c, "/Account/Login") }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Dbg.Has(await BodyAsync(r), "This login is for the customer app");
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode); // still not signed in
    }
}
