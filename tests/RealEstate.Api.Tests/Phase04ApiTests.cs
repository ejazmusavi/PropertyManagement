using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase04ApiTests(ApiFactory factory)
{
    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        var tokens = (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return c;
    }

    private static async Task<long> ProjectAsync(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("S" + Guid.NewGuid().ToString("N")[..10], "Sales API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null));
        r.EnsureSuccessStatusCode();
        var id = (await r.Content.ReadFromJsonAsync<ProjectDetailDto>())!.Id;
        (await c.PostAsJsonAsync("/api/v1/pricing/rules", new CreatePricingRuleRequest(id, "BASE", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project, null, null, null, null, null, null,
            PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)), null, null))).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<PropertyDetailDto> PropertyAsync(HttpClient c, long projectId)
    {
        var types = (await c.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!;
        var r = await c.PostAsJsonAsync("/api/v1/properties", new CreatePropertyRequest(projectId, "B" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null, types.Single(t => t.Code == "RES-PLOT").Id,
            5m, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Developed, null, null, null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<PropertyDetailDto>())!;
    }

    private static CreateCustomerRequest NewCustomer(string? cnic = null) =>
        new("API Customer", IdentityDocumentType.Cnic, cnic ?? Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "api@example.com", "Murree", null,
            [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, [new NomineeDto("Nom", "Son", null, null, 100m)]);

    private static async Task<CustomerDetailDto> CustomerAsync(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/v1/customers", NewCustomer());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<CustomerDetailDto>())!;
    }

    private static CreateBookingRequest Book(long projectId, long propertyId, IEnumerable<(long, decimal)> owners, bool submit = true, string? key = null, PaymentMode mode = PaymentMode.Installment, decimal amount = 500_000m) =>
        new(projectId, null, mode, [new BookingPropertyInput(propertyId, 0m, null)], owners.Select(o => new OwnerInput(o.Item1, o.Item2)).ToList(), null, amount, false, null, null, submit, key);

    [MySqlFact]
    public async Task SalesEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/customers", "/api/v1/customers/1", "/api/v1/customers/lookup", "/api/v1/customers/1/documents", "/api/v1/bookings", "/api/v1/bookings/1", "/api/v1/bookings/quote?propertyId=1",
                     "/api/v1/bookings/1/agreement" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/customers", "/api/v1/bookings", "/api/v1/bookings/1/approve", "/api/v1/bookings/1/reject", "/api/v1/bookings/1/cancel", "/api/v1/bookings/1/transfer", "/api/v1/bookings/1/submit" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Customer_CreateGetUpdateListLookup_RoundTrip()
    {
        var c = await ClientAsync();
        var created = await CustomerAsync(c);
        Assert.StartsWith("CUS-", created.CustomerCode);
        var got = await c.GetFromJsonAsync<CustomerDetailDto>($"/api/v1/customers/{created.Id}");
        Assert.Single(got!.Nominees);
        var upd = new UpdateCustomerRequest("Renamed", got.IdentityType, got.IdentityNumber, got.Mobile, got.Email, got.Address, "note", CustomerStatus.Active, null, null, null, null, got.RowVersion);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/customers/{created.Id}", upd)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PutAsJsonAsync($"/api/v1/customers/{created.Id}", upd)).StatusCode); // stale row version
        var list = await c.GetFromJsonAsync<PagedResult<CustomerListItemDto>>($"/api/v1/customers?search={created.CustomerCode}&pageSize=5");
        Assert.Single(list!.Items); Assert.Equal("Renamed", list.Items[0].FullName);
        var lookup = await c.GetFromJsonAsync<List<CustomerLookupDto>>($"/api/v1/customers/lookup?term={created.CustomerCode}");
        Assert.Single(lookup!);
    }

    [MySqlFact]
    public async Task Customer_Invalid_ReturnsProblemDetails_AndDuplicateIdentityRejected()
    {
        var c = await ClientAsync();
        var bad = await c.PostAsJsonAsync("/api/v1/customers", NewCustomer("123") with { FullName = "", Mobile = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        var cnic = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/customers", NewCustomer(cnic))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/customers", NewCustomer(cnic))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/customers/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Customer_IsHiddenFromOtherUsers_ButNotFromAdministrators()
    {
        var owner = await ClientAsync(RoleNames.SalesManager);
        var created = await CustomerAsync(owner);
        var other = await ClientAsync(RoleNames.SalesManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/customers/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/v1/customers/{created.Id}", new UpdateCustomerRequest("X", created.IdentityType, created.IdentityNumber, created.Mobile, null, null, null, CustomerStatus.Active, null, null, null, null, created.RowVersion))).StatusCode);
        var list = await other.GetFromJsonAsync<PagedResult<CustomerListItemDto>>($"/api/v1/customers?search={created.CustomerCode}");
        Assert.Empty(list!.Items);
        var admin = await ClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/customers/{created.Id}")).StatusCode);
        var cashier = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/customers", NewCustomer())).StatusCode);
    }

    [MySqlFact]
    public async Task Booking_FullLifecycle_ThroughTheApi()
    {
        var maker = await ClientAsync();
        var checker = await ClientAsync();
        var projectId = await ProjectAsync(maker);
        var prop = await PropertyAsync(maker, projectId);
        var a = await CustomerAsync(maker); var b = await CustomerAsync(maker);

        var created = await maker.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(a.Id, 70m), (b.Id, 30m)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var booking = (await created.Content.ReadFromJsonAsync<BookingDetailDto>())!;
        Assert.Equal(BookingStatus.PendingApproval, booking.Status);
        Assert.Equal(5_000_000m, booking.TotalPayable);
        Assert.Equal(2, booking.Customers.Count);

        // the property is held, a second booking conflicts
        var conflict = await maker.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(a.Id, 100m)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);

        // maker-checker
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest(null, null))).StatusCode);
        var approve = await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest("ok", booking.RowVersion));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = (await approve.Content.ReadFromJsonAsync<BookingDetailDto>())!;
        Assert.Equal(BookingStatus.Confirmed, approved.Status);
        Assert.StartsWith("AGR-", approved.AgreementNumber);

        var agreement = await maker.GetFromJsonAsync<AgreementDto>($"/api/v1/bookings/{booking.Id}/agreement");
        Assert.True(agreement!.ContentVerified); Assert.Contains("API Customer", agreement.Content);

        var property = await maker.GetFromJsonAsync<PropertyDetailDto>($"/api/v1/properties/{prop.Id}");
        Assert.Equal(PropertyStatus.Sold, property!.Status);
        var customer = await maker.GetFromJsonAsync<CustomerDetailDto>($"/api/v1/customers/{a.Id}");
        Assert.Single(customer!.Ownerships, o => o.IsActive && o.SharePercent == 70m);

        // transfer to a third customer
        var c3 = await CustomerAsync(maker);
        var transfer = await maker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/transfer", new TransferBookingRequest("Sold on", [new OwnerInput(c3.Id, 100m)], approved.RowVersion));
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        var transferred = (await transfer.Content.ReadFromJsonAsync<BookingDetailDto>())!;
        Assert.Equal(2, transferred.AgreementVersion);
        var oldVersion = await maker.GetFromJsonAsync<AgreementDto>($"/api/v1/bookings/{booking.Id}/agreement?version=1");
        Assert.Contains("API Customer", oldVersion!.Content); Assert.Equal(1, oldVersion.ViewingVersionNo);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/transfer", new TransferBookingRequest("Same stale version", [new OwnerInput(a.Id, 100m)], approved.RowVersion))).StatusCode);

        // cancel with a refund request, then the refund is decided by the other admin
        var cancel = await maker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/cancel", new CancelBookingRequest("Customer withdrew", 250_000m, "Partial refund", transferred.RowVersion));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = (await cancel.Content.ReadFromJsonAsync<BookingDetailDto>())!;
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        var refund = Assert.Single(cancelled.Refunds);
        var decide = await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/refunds/{refund.Id}/decision", new DecideRefundRequest(true, "ok", refund.RowVersion));
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        Assert.Equal(PropertyStatus.Available, (await maker.GetFromJsonAsync<PropertyDetailDto>($"/api/v1/properties/{prop.Id}"))!.Status);

        var list = await maker.GetFromJsonAsync<PagedResult<BookingListItemDto>>($"/api/v1/bookings?projectId={projectId}&status=Cancelled");
        Assert.Single(list!.Items);
    }

    [MySqlFact]
    public async Task Booking_Reject_ReleasesProperty_AndInvalidInputReturns400()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        var projectId = await ProjectAsync(maker);
        var prop = await PropertyAsync(maker, projectId);
        var c = await CustomerAsync(maker);
        var booking = (await (await maker.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(c.Id, 100m)], submit: false))).Content.ReadFromJsonAsync<BookingDetailDto>())!;
        Assert.Equal(BookingStatus.Draft, booking.Status);
        var bad = await maker.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(c.Id, 60m)], key: "k" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        var submit = await maker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/submit", new SubmitBookingRequest(booking.RowVersion));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/reject", new RejectBookingRequest("", null))).StatusCode);
        var reject = await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/reject", new RejectBookingRequest("Price not agreed", null));
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal(PropertyStatus.Available, (await maker.GetFromJsonAsync<PropertyDetailDto>($"/api/v1/properties/{prop.Id}"))!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/api/v1/bookings/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Booking_Idempotency_AndQuote()
    {
        var c = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var prop = await PropertyAsync(c, projectId);
        var cust = await CustomerAsync(c);
        var quote = await c.GetFromJsonAsync<PriceCalculationDto>($"/api/v1/bookings/quote?propertyId={prop.Id}&customerId={cust.Id}");
        Assert.Equal(5_000_000m, quote!.FinalPrice);
        var key = Guid.NewGuid().ToString("N");
        var first = await c.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(cust.Id, 100m)], key: key));
        var second = await c.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(cust.Id, 100m)], key: key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<BookingDetailDto>())!.Id, (await second.Content.ReadFromJsonAsync<BookingDetailDto>())!.Id);
    }

    [MySqlFact]
    public async Task Booking_Authorization_IsEnforced()
    {
        var admin = await ClientAsync();
        var projectId = await ProjectAsync(admin);
        var prop = await PropertyAsync(admin, projectId);
        var cust = await CustomerAsync(admin);
        var booking = (await (await admin.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(cust.Id, 100m)]))).Content.ReadFromJsonAsync<BookingDetailDto>())!;
        // cashier: can neither create nor view; unassigned sales manager cannot see another project's booking
        var cashier = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(cust.Id, 100m)]))).StatusCode);
        var sales = await ClientAsync(RoleNames.SalesManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/api/v1/bookings/{booking.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync("/api/v1/bookings", Book(projectId, prop.Id, [(cust.Id, 100m)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/transfer", new TransferBookingRequest("Not allowed", [new OwnerInput(cust.Id, 100m)], null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/bookings/quote?propertyId={prop.Id}")).StatusCode);
    }
}
