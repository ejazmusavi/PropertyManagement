using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Customers;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase10ApiTests(ApiFactory factory)
{
    private const string CustomerPassword = "Cust0mer#Passw0rd!";

    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        return await LoginAsync(email, ApiFactory.Password);
    }

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

    private static async Task<long> NewCustomerAsync(HttpClient staff) => (await Ok<CustomerDetailDto>(await staff.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest("App Customer",
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", null, "Murree", null,
        [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, null)))).Id;

    /// <summary>Creates a customer and an application login through the office API; returns the customer client and ids.</summary>
    private async Task<(HttpClient Customer, long CustomerId, string Email)> CustomerLoginAsync(HttpClient staff)
    {
        var id = await NewCustomerAsync(staff);
        var email = $"app{Guid.NewGuid():N}@example.com";
        var created = await staff.PostAsJsonAsync($"/api/v1/customers/{id}/portal-account", new CreateCustomerAccountRequest(email, CustomerPassword));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await LoginAsync(email, CustomerPassword), id, email);
    }

    [MySqlFact]
    public async Task PortalAndOfficeEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/portal/dashboard", "/api/v1/portal/properties", "/api/v1/portal/payments", "/api/v1/portal/ledger", "/api/v1/portal/notifications", "/api/v1/portal/profile",
                     "/api/v1/portal/support-requests", "/api/v1/portal/announcements", "/api/v1/portal/payment-instructions", "/api/v1/support-requests", "/api/v1/gateway/transactions",
                     "/api/v1/portal-admin/announcements", "/api/v1/customers/1/portal-account" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/portal/online-payments", "/api/v1/portal/support-requests", "/api/v1/gateway/reconcile", "/api/v1/customers/1/portal-account" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task CustomerLogin_SeesOnlyTheCustomerApi_AndStaffAreRefusedThere()
    {
        var staff = await ClientAsync();
        var (customer, customerId, email) = await CustomerLoginAsync(staff);

        // the customer application works for the linked customer
        var dash = await Ok<PortalDashboardDto>(await customer.GetAsync("/api/v1/portal/dashboard"));
        Assert.Equal(0, dash.TotalProperties);
        Assert.False(dash.OnlinePaymentsEnabled);
        var profile = await Ok<PortalProfileDto>(await customer.GetAsync("/api/v1/portal/profile"));
        Assert.StartsWith("CUS-", profile.CustomerCode);
        Assert.Equal(email, profile.LoginEmail);
        Assert.Empty(await Ok<List<PortalPropertyDto>>(await customer.GetAsync("/api/v1/portal/properties")));
        Assert.Empty((await Ok<PortalNotificationListDto>(await customer.GetAsync("/api/v1/portal/notifications"))).Notifications.Items);
        Assert.NotNull(await Ok<PortalPaymentInstructionsDto>(await customer.GetAsync("/api/v1/portal/payment-instructions")));
        var me = await Ok<CurrentUserDto>(await customer.GetAsync("/api/v1/auth/me"));
        Assert.Equal([RoleNames.Customer], me.Roles);
        Assert.Equal([Permissions.PortalAccess], me.Permissions);

        // a customer id sent by the client is never used: ids of other records answer 404, staff endpoints answer 403
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/v1/portal/agreements/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/v1/portal/agreements/1/schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/v1/portal/payments/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/v1/portal/receipts/1/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/v1/portal/support-requests/1")).StatusCode);
        foreach (var url in new[] { "/api/v1/payments", $"/api/v1/customers/{customerId}", $"/api/v1/customers/{customerId}/ledger", "/api/v1/projects", "/api/v1/support-requests", "/api/v1/gateway/transactions",
                     "/api/v1/users", $"/api/v1/customers/{customerId}/portal-account", "/api/v1/portal-admin/announcements" })
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(url)).StatusCode);

        // staff (even an Administrator, who holds every permission) have no customer link
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/portal/dashboard")).StatusCode);
        var support = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync("/api/v1/portal/dashboard")).StatusCode);
    }

    [MySqlFact]
    public async Task ProfileSupport_AndNotifications_WorkOverTheApi()
    {
        var staff = await ClientAsync();
        var (customer, customerId, _) = await CustomerLoginAsync(staff);

        var updated = await Ok<PortalProfileDto>(await customer.PutAsJsonAsync("/api/v1/portal/profile", new UpdatePortalProfileRequest("0345-9998887", "me@example.com", "New address")));
        Assert.Equal("0345-9998887", updated.Mobile);
        var bad = await customer.PutAsJsonAsync("/api/v1/portal/profile", new UpdatePortalProfileRequest("abc", null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await customer.PostAsJsonAsync("/api/v1/portal/support-requests", new CreateSupportRequestRequest(SupportCategory.General, "Question", "How do I pay?", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<PortalSupportRequestDetailDto>();
        var desk = await Ok<SupportDetailDto>(await staff.GetAsync($"/api/v1/support-requests/{detail!.Request.Id}"));
        Assert.Equal(customerId, desk.Request.CustomerId);
        var replied = await Ok<SupportDetailDto>(await staff.PostAsJsonAsync($"/api/v1/support-requests/{detail.Request.Id}/replies",
            new SupportStaffReplyRequest("Use the bank details in the app.", SupportRequestStatus.Resolved, desk.RowVersion)));
        Assert.Equal(SupportRequestStatus.Resolved, replied.Request.Status);
        var mine = await Ok<PortalSupportRequestDetailDto>(await customer.GetAsync($"/api/v1/portal/support-requests/{detail.Request.Id}"));
        Assert.Equal(2, mine.Messages.Count);
        var notes = await Ok<PortalNotificationListDto>(await customer.GetAsync("/api/v1/portal/notifications"));
        Assert.True(notes.Unread >= 1);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("/api/v1/portal/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, (await Ok<PortalNotificationListDto>(await customer.GetAsync("/api/v1/portal/notifications"))).Unread);

        // announcements and instructions entered by the office reach the customer
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/v1/portal-admin/announcements", new SaveAnnouncementRequest("Welcome", "Welcome to the app", null, null, null, true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync("/api/v1/portal-admin/payment-instructions", new PaymentInstructionsNoteDto("Pay by bank transfer."))).StatusCode);
        Assert.Contains(await Ok<List<PortalAnnouncementDto>>(await customer.GetAsync("/api/v1/portal/announcements")), a => a.Title == "Welcome");
        Assert.Equal("Pay by bank transfer.", (await Ok<PortalPaymentInstructionsDto>(await customer.GetAsync("/api/v1/portal/payment-instructions"))).Note);
    }

    [MySqlFact]
    public async Task ProofSubmission_ValidatesAndIsolates_OverMultipart()
    {
        var staff = await ClientAsync();
        var (customer, _, _) = await CustomerLoginAsync(staff);
        MultipartFormDataContent Form(bool withFile)
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent("999999"), "AgreementId" }, { new StringContent("1"), "PaymentMethodId" }, { new StringContent("1000"), "Amount" },
                { new StringContent(DateTime.UtcNow.ToString("yyyy-MM-dd")), "PaymentDate" }, { new StringContent("REF1"), "Reference" }, { new StringContent(Guid.NewGuid().ToString("N")), "IdempotencyKey" },
            };
            if (withFile) form.Add(new ByteArrayContent([1, 2, 3]), "proof", "slip.png");
            return form;
        }
        // an agreement that is not the customer's: not found; without a file: validation error
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsync("/api/v1/portal/payments", Form(true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsync("/api/v1/portal/payments", Form(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/api/v1/portal/payments", Form(true))).StatusCode);
        // online payments are off by default
        var online = await customer.PostAsJsonAsync("/api/v1/portal/online-payments", new InitiateOnlinePaymentRequest(999999, 1000m, Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.NotFound, online.StatusCode);
    }

    [MySqlFact]
    public async Task PasswordReset_Endpoints_DoNotRevealAccounts()
    {
        var c = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("nobody@example.com"))).StatusCode);
        var staff = await ClientAsync();
        var (_, _, email) = await CustomerLoginAsync(staff);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("not-an-email"))).StatusCode);
        var bad = await c.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, "wrong-code", "N3w#Passw0rd-xyz!"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest("nobody@example.com", "x", "N3w#Passw0rd-xyz!"))).StatusCode);
        // the account still works with its old password
        await LoginAsync(email, CustomerPassword);
    }

    [MySqlFact]
    public async Task DisabledCustomerLogin_CannotSignInOrUseTheApi_AndTheWebhookEndpointIsClosedByDefault()
    {
        var staff = await ClientAsync();
        var (customer, customerId, email) = await CustomerLoginAsync(staff);
        var account = await Ok<CustomerAccountDto>(await staff.GetAsync($"/api/v1/customers/{customerId}/portal-account"));
        var disabled = await Ok<CustomerAccountDto>(await staff.PostAsJsonAsync($"/api/v1/customers/{customerId}/portal-account/disable", new DisableCustomerAccountRequest("closed account", account.RowVersion)));
        Assert.False(disabled.IsActive);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/portal/dashboard")).StatusCode); // existing token: refused by the account check
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, CustomerPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync("/api/v1/customers/999999/portal-account")).StatusCode);

        // no provider is configured: the anonymous callback endpoint answers 404 and does not process anything
        var hook = await factory.CreateClient().PostAsync("/api/v1/payment-webhooks/sandbox", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, hook.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/v1/portal/online-payments", new InitiateOnlinePaymentRequest(1, 1m, "k"))).StatusCode);
        var reconcile = await staff.PostAsync("/api/v1/gateway/reconcile", null);
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
    }
}
