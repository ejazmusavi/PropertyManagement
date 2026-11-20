using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase05ApiTests(ApiFactory factory)
{
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

    /// <summary>Project with a base rate, a confirmed installment booking (5 marla, 5,000,000; 500,000 at booking). Returns the agreement id.</summary>
    private static async Task<(long ProjectId, long AgreementId)> AgreementAsync(HttpClient maker, HttpClient checker)
    {
        var project = await Ok<ProjectDetailDto>(await maker.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("I" + Guid.NewGuid().ToString("N")[..10], "Installments API", null, "Murree",
            null, null, null, 80m, MeasurementUnit.Kanal, null, null)));
        (await maker.PostAsJsonAsync("/api/v1/pricing/rules", new CreatePricingRuleRequest(project.Id, "BASE", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
            null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)), null, null))).EnsureSuccessStatusCode();
        var types = (await maker.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!;
        var property = await Ok<PropertyDetailDto>(await maker.PostAsJsonAsync("/api/v1/properties", new CreatePropertyRequest(project.Id, "P" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null,
            types.Single(t => t.Code == "RES-PLOT").Id, 5m, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Developed, null, null, null)));
        var customer = await Ok<CustomerDetailDto>(await maker.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest("API Customer", IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", null, "Murree", null, [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, null)));
        var booking = await Ok<BookingDetailDto>(await maker.PostAsJsonAsync("/api/v1/bookings", new CreateBookingRequest(project.Id, null, PaymentMode.Installment,
            [new BookingPropertyInput(property.Id, 0m, null)], [new OwnerInput(customer.Id, 100m)], null, 500_000m, false, null, null, true, null)));
        booking = await Ok<BookingDetailDto>(await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest("ok", booking.RowVersion)));
        return (project.Id, booking.AgreementId!.Value);
    }

    private static async Task<InstallmentPlanDetailDto> PlanAsync(HttpClient c, long projectId)
    {
        var types = await Ok<List<ChargeTypeDto>>(await c.GetAsync("/api/v1/charge-types?activeOnly=true"));
        long T(string code) => types.Single(t => t.Code == code).Id;
        var request = new CreateInstallmentPlanRequest(projectId, "STD-24", "Standard 24 months", "Down payment, 24 monthly, possession", InstallmentPlanType.StandardProject, null, null, null,
        [
            new(T("DOWN_PAYMENT"), "Down payment", ChargeCategory.SaleConsideration, AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, null, 0, 0, null, null, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null),
            new(T("MONTHLY"), "Monthly installment", ChargeCategory.SaleConsideration, AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, null, 1, 0, null, null, 5, null, true, 25m, TaxTreatment.NotApplicable, 0m, 1000m, null),
            new(T("POSSESSION"), "Possession", ChargeCategory.SaleConsideration, AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, null, 30, 0, null, null, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null),
        ]);
        var r = await c.PostAsJsonAsync("/api/v1/installment-plans", request);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var plan = (await r.Content.ReadFromJsonAsync<InstallmentPlanDetailDto>())!;
        return await Ok<InstallmentPlanDetailDto>(await c.PostAsJsonAsync($"/api/v1/installment-plans/{plan.Id}/activate", new PlanRowVersionRequest(plan.RowVersion)));
    }

    [MySqlFact]
    public async Task InstallmentEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/installment-plans", "/api/v1/installment-plans/1", "/api/v1/agreements/1/schedule", "/api/v1/agreements/1/history", "/api/v1/charge-types", "/api/v1/late-fee-rules",
                     "/api/v1/amendments", "/api/v1/waivers" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/installment-plans", "/api/v1/agreements/1/generate-schedule", "/api/v1/agreements/1/amendments", "/api/v1/agreements/1/amendments/1/approve" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Plan_Generate_Amend_Approve_History_EndToEnd()
    {
        var maker = await ClientAsync();
        var checker = await ClientAsync();
        var (projectId, agreementId) = await AgreementAsync(maker, checker);
        var plan = await PlanAsync(maker, projectId);
        Assert.Equal(InstallmentPlanStatus.Active, plan.Status);
        var list = await Ok<PagedResult<InstallmentPlanListItemDto>>(await maker.GetAsync($"/api/v1/installment-plans?projectId={projectId}"));
        Assert.Contains(list.Items, p => p.Id == plan.Id);

        var ag = await Ok<AgreementInstallmentDto>(await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/apply-plan", new ApplyPlanRequest(plan.Id, null, null)));
        Assert.Equal(0m, ag.PriceDifference);
        var preview = await Ok<SchedulePreviewDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/schedule-preview"));
        Assert.True(preview.CanGenerate);
        Assert.Equal(26, preview.Items.Count);
        Assert.Equal(167_000m, preview.Items[1].Amount); // 4,000,000 / 24 rounded to the 1,000 unit

        var schedule = await Ok<PaymentScheduleDto>(await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/generate-schedule", new GenerateScheduleRequest(ag.RowVersion, null)));
        Assert.Equal(5_000_000m, schedule.TotalAmount);
        Assert.True(schedule.HashVerified);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/generate-schedule", new GenerateScheduleRequest(null, null))).StatusCode);
        var fetched = await Ok<PaymentScheduleDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/schedule"));
        Assert.Equal(schedule.ContentHash, fetched.ContentHash);

        var item = schedule.Items.First(i => i.Description == "Monthly installment 1/24");
        var create = await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/amendments", new CreateAmendmentRequest(AmendmentType.Waiver, "Hardship relief",
            Waiver: new WaiverInput(item.Id!.Value, WaiverKind.Principal, 5_000m)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var amendment = (await create.Content.ReadFromJsonAsync<AmendmentDetailDto>())!;
        Assert.Equal(AmendmentStatus.Pending, amendment.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/amendments/{amendment.Id}/approve",
            new DecideAmendmentRequest(null, amendment.RowVersion))).StatusCode); // maker-checker
        var waivers = await Ok<PagedResult<WaiverDto>>(await checker.GetAsync($"/api/v1/waivers?status={(int)WaiverStatus.Pending}&projectId={projectId}"));
        Assert.Contains(waivers.Items, x => x.AmendmentId == amendment.Id);
        var approved = await Ok<AmendmentDetailDto>(await checker.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/amendments/{amendment.Id}/approve",
            new DecideAmendmentRequest("ok", amendment.RowVersion)));
        Assert.Equal(AmendmentStatus.Approved, approved.Status);

        var history = await Ok<AgreementHistoryDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/history"));
        Assert.Equal(2, history.Schedules.Count);
        Assert.Single(history.Changes);
        Assert.Equal(ScheduleChangeOperation.Waived, history.Changes[0].Operation);
        var v1 = await Ok<PaymentScheduleDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/schedule?version=1"));
        Assert.Equal(PaymentScheduleStatus.Superseded, v1.Status);
        Assert.Equal(0m, v1.TotalWaived);
        var v2 = await Ok<PaymentScheduleDto>(await maker.GetAsync($"/api/v1/agreements/{agreementId}/schedule"));
        Assert.Equal(5_000m, v2.TotalWaived);
    }

    [MySqlFact]
    public async Task Validation_Authorization_AndLateFeeRules()
    {
        var admin = await ClientAsync();
        var checker = await ClientAsync();
        var (projectId, agreementId) = await AgreementAsync(admin, checker);
        var bad = await admin.PostAsJsonAsync("/api/v1/installment-plans", new CreateInstallmentPlanRequest(projectId, "", "", null, InstallmentPlanType.CustomerSpecific, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("CustomerId", await bad.Content.ReadAsStringAsync());

        var cashier = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/installment-plans",
            new CreateInstallmentPlanRequest(projectId, "X", "X", null, InstallmentPlanType.StandardProject, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/agreements/{agreementId}/schedule")).StatusCode); // not assigned to the project
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/generate-schedule", new GenerateScheduleRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/agreements/{agreementId}/schedule")).StatusCode); // not generated yet
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/agreements/{agreementId}/generate-schedule", new GenerateScheduleRequest(null, null))).StatusCode);

        var rule = await Ok<LateFeeRuleDto>(await admin.PostAsJsonAsync("/api/v1/late-fee-rules", new SaveLateFeeRuleRequest("LF-" + Guid.NewGuid().ToString("N")[..8], "Rs 500 per month",
            projectId, LateFeeMethod.FixedPerMonth, 500m, 5_000m, null, null, true, null)));
        Assert.False(rule.IsInUse);
        var rules = await Ok<PagedResult<LateFeeRuleDto>>(await admin.GetAsync($"/api/v1/late-fee-rules?projectId={projectId}"));
        Assert.Contains(rules.Items, r => r.Id == rule.Id);
        var fee = await Ok<LateFeePreviewDto>(await admin.PostAsJsonAsync("/api/v1/late-fee-rules/preview", new LateFeePreviewRequest(rule.Id, 50_000m, 50_000m, new DateOnly(2025, 1, 10), new DateOnly(2025, 4, 1))));
        Assert.Equal(1_500m, fee.LateFee);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/late-fee-rules", new SaveLateFeeRuleRequest("LF", "x", null, LateFeeMethod.PercentPerDay, 500m, null, null, null, true, null))).StatusCode);
    }
}
