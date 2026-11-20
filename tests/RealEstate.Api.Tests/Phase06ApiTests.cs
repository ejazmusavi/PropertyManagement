using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase06ApiTests(ApiFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return c;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Confirmed installment booking with a generated schedule (down payment 500,000 due today). Returns (agreement id, customer id).</summary>
    private static async Task<(long AgreementId, long CustomerId)> AgreementAsync(HttpClient maker, HttpClient checker)
    {
        var project = await Ok<ProjectDetailDto>(await maker.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Y" + Guid.NewGuid().ToString("N")[..10], "Payments API", null, "Murree",
            null, null, null, 80m, MeasurementUnit.Kanal, null, null)));
        (await maker.PostAsJsonAsync("/api/v1/pricing/rules", new CreatePricingRuleRequest(project.Id, "BASE", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
            null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, Today, null, null))).EnsureSuccessStatusCode();
        var types = (await maker.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!;
        var property = await Ok<PropertyDetailDto>(await maker.PostAsJsonAsync("/api/v1/properties", new CreatePropertyRequest(project.Id, "P" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null,
            types.Single(t => t.Code == "RES-PLOT").Id, 5m, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Developed, null, null, null)));
        var customer = await Ok<CustomerDetailDto>(await maker.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest("API Payer", IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", null, "Murree", null, [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, null)));
        var booking = await Ok<BookingDetailDto>(await maker.PostAsJsonAsync("/api/v1/bookings", new CreateBookingRequest(project.Id, null, PaymentMode.Installment,
            [new BookingPropertyInput(property.Id, 0m, null)], [new OwnerInput(customer.Id, 100m)], null, 500_000m, false, null, null, true, null)));
        booking = await Ok<BookingDetailDto>(await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest("ok", booking.RowVersion)));
        var agreementId = booking.AgreementId!.Value;
        var ct = await Ok<List<ChargeTypeDto>>(await maker.GetAsync("/api/v1/charge-types?activeOnly=true"));
        long T(string code) => ct.Single(t => t.Code == code).Id;
        var plan = await Ok<InstallmentPlanDetailDto>(await maker.PostAsJsonAsync("/api/v1/installment-plans", new CreateInstallmentPlanRequest(project.Id, "STD", "Standard", null,
            InstallmentPlanType.StandardProject, null, null, null,
            [
                new(T("DOWN_PAYMENT"), "Down payment", ChargeCategory.SaleConsideration, AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, null, 0, 0, null, null, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null),
                new(T("MONTHLY"), "Monthly installment", ChargeCategory.SaleConsideration, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 12, null, 1, 0, null, null, 5, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null),
            ])));
        plan = await Ok<InstallmentPlanDetailDto>(await maker.PostAsJsonAsync($"/api/v1/installment-plans/{plan.Id}/activate", new PlanRowVersionRequest(plan.RowVersion)));
        var ag = await Ok<AgreementInstallmentDto>(await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/apply-plan", new ApplyPlanRequest(plan.Id, null, null)));
        await Ok<PaymentScheduleDto>(await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/generate-schedule", new GenerateScheduleRequest(ag.RowVersion, null)));
        return (agreementId, customer.Id);
    }

    private static async Task<long> CashAsync(HttpClient c) => (await Ok<List<PaymentMethodDto>>(await c.GetAsync("/api/v1/payments/methods"))).Single(m => m.Code == "CASH").Id;

    private static CreatePaymentRequest Payment(long agreementId, long methodId, decimal amount, string key, AllocationMode mode = AllocationMode.OldestDueFirst) =>
        new(agreementId, null, methodId, PaymentSource.Office, amount, Today, null, null, null, null, null, mode, null, null, false, key);

    [MySqlFact]
    public async Task PaymentEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/payments", "/api/v1/payments/1", "/api/v1/customers/1/ledger", "/api/v1/agreements/1/ledger", "/api/v1/recovery/overdue", "/api/v1/refunds" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/payments", "/api/v1/payments/1/verify", "/api/v1/payments/1/approve", "/api/v1/payments/1/allocate", "/api/v1/refunds", "/api/v1/refunds/1/approve" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Submit_Approve_Allocate_Ledgers_Refund_EndToEnd()
    {
        var maker = await ClientAsync();
        var checker = await ClientAsync();
        var (agreementId, customerId) = await AgreementAsync(maker, checker);
        var cash = await CashAsync(maker);
        var key = Guid.NewGuid().ToString("N");

        var r = await maker.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 700_000m, key, AllocationMode.Unallocated));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var p = (await r.Content.ReadFromJsonAsync<PaymentDetailDto>())!;
        // duplicate submission returns the same payment; a changed payload with the same key is a 400
        var again = await Ok<PaymentDetailDto>(await maker.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 700_000m, key, AllocationMode.Unallocated)));
        Assert.Equal(p.Id, again.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 1m, key))).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/payments/{p.Id}/approve", new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await checker.PostAsJsonAsync($"/api/v1/payments/{p.Id}/verify", new VerifyPaymentRequest(true, "n/a for cash", null, null, p.RowVersion))).StatusCode);
        p = await Ok<PaymentDetailDto>(await checker.PostAsJsonAsync($"/api/v1/payments/{p.Id}/approve", new ApprovePaymentRequest("ok", null, null, null, null, p.RowVersion)));
        Assert.Equal(PaymentStatus.Posted, p.Status);
        Assert.Equal(700_000m, p.Unallocated);
        Assert.NotNull(p.Receipt);

        var options = await Ok<AllocationOptionsDto>(await maker.GetAsync($"/api/v1/payments/{p.Id}/allocation-options"));
        Assert.NotEmpty(options.Lines);
        p = await Ok<PaymentDetailDto>(await maker.PostAsJsonAsync($"/api/v1/payments/{p.Id}/allocate", new AllocatePaymentRequest(AllocationMode.SpecificInstallment, null,
            [new AllocationLineInput(options.Lines[0].ItemId, 500_000m)], false, true, null)));
        Assert.Equal(500_000m, p.Allocated);
        Assert.Equal(200_000m, p.Advance);
        // nothing left: a second allocation is refused (no over-allocation)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/payments/{p.Id}/allocate", new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, false, false, null))).StatusCode);
        Assert.Equal(p.Id, (await Ok<PaymentDetailDto>(await maker.GetAsync($"/api/v1/payments/{p.Id}"))).Id);
        Assert.Contains((await Ok<PagedResult<PaymentListItemDto>>(await maker.GetAsync($"/api/v1/payments?agreementId={agreementId}"))).Items, x => x.Id == p.Id);

        var ledger = await Ok<AgreementLedgerDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/ledger"));
        Assert.True(ledger.Summary.Reconciled);
        Assert.Equal(0m, ledger.Summary.Outstanding);
        Assert.Equal(200_000m, ledger.Summary.AdvanceBalance);
        var customerLedger = await Ok<CustomerLedgerDto>(await maker.GetAsync($"/api/v1/customers/{customerId}/ledger"));
        Assert.Equal(-200_000m, customerLedger.Balance);
        Assert.True(customerLedger.Reconciled);

        var pdf = await maker.GetAsync($"/api/v1/payments/receipts/{p.Receipt!.Id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);

        // refund 150,000 of the advance
        var created = await maker.PostAsJsonAsync("/api/v1/refunds", new CreateRefundRequest(agreementId, 150_000m, "Customer asked for part of the advance", null, Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var refund = (await created.Content.ReadFromJsonAsync<RefundDetailDto>())!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/refunds/{refund.Id}/approve", new ApproveRefundRequest(null, refund.RowVersion))).StatusCode);
        refund = await Ok<RefundDetailDto>(await checker.PostAsJsonAsync($"/api/v1/refunds/{refund.Id}/approve", new ApproveRefundRequest("ok", refund.RowVersion)));
        refund = await Ok<RefundDetailDto>(await checker.PostAsJsonAsync($"/api/v1/refunds/{refund.Id}/pay", new PayRefundRequest(cash, Today, null, refund.RowVersion)));
        Assert.Equal(PaymentRefundStatus.Paid, refund.Status);
        ledger = await Ok<AgreementLedgerDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/ledger"));
        Assert.Equal(150_000m, ledger.Summary.Refunds);
        Assert.True(ledger.Summary.Reconciled);

        var overdue = await Ok<DueListDto>(await maker.GetAsync("/api/v1/recovery/overdue"));
        Assert.NotNull(overdue.Aging);
    }

    [MySqlFact]
    public async Task PaymentEndpoints_EnforcePermissions_AndValidation()
    {
        var admin = await ClientAsync();
        var (agreementId, customerId) = await AgreementAsync(admin, await ClientAsync());
        var cash = await CashAsync(admin);
        var officer = await ClientAsync(RoleNames.RecoveryOfficer); // not assigned to the project, no collect permission
        Assert.Equal(HttpStatusCode.Forbidden, (await officer.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 1_000m, Guid.NewGuid().ToString("N")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await officer.GetAsync($"/api/v1/agreements/{agreementId}/ledger")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await officer.GetAsync($"/api/v1/customers/{customerId}/ledger")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await officer.GetAsync("/api/v1/recovery/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await officer.GetAsync("/api/v1/recovery/overdue")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 0m, Guid.NewGuid().ToString("N")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/payments", Payment(agreementId, cash, 10m, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/payments/999999999")).StatusCode);
    }
}
