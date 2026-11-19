using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase03ApiTests(ApiFactory factory)
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
        var r = await c.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("R" + Guid.NewGuid().ToString("N")[..10], "Pricing API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProjectDetailDto>())!.Id;
    }

    private static async Task<PropertyDetailDto> PropertyAsync(HttpClient c, long projectId, bool corner = false)
    {
        var typeId = (await c.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!.Single(t => t.Code == "RES-PLOT").Id;
        var r = await c.PostAsJsonAsync("/api/v1/properties", new CreatePropertyRequest(projectId, "A" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null, typeId, 5m, MeasurementUnit.Marla,
            "Loc", null, corner, false, false, DevelopmentStatus.Developed, null, null, null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<PropertyDetailDto>())!;
    }

    private static CreatePricingRuleRequest BaseRule(long projectId, decimal rate = 1_000_000m) =>
        new(projectId, "BASE", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project, null, null, null, null, null, null, PricingMethod.PerUnit, rate, MeasurementUnit.Marla, 0,
            DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)), null, null);

    [MySqlFact]
    public async Task PricingEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/pricing/rules", "/api/v1/pricing/rules/1", "/api/v1/pricing/discounts", "/api/v1/pricing/history", "/api/v1/pricing/approvals", "/api/v1/pricing/prices",
                     "/api/v1/properties/1/price", "/api/v1/properties/1/price-adjustments", "/api/v1/pricing/rate-cards/1" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/pricing/calculate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/pricing/rules", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/pricing/approvals", new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Rules_Calculate_PropertyPrice_Publish_AndHistory_RoundTrip()
    {
        var c = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var p = await PropertyAsync(c, projectId, corner: true);

        var created = await c.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var rule = (await created.Content.ReadFromJsonAsync<PricingRuleDto>())!;
        Assert.Equal("BASE", rule.Code);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/pricing/rules",
            BaseRule(projectId) with { Code = "CORNER", Name = "Corner", Kind = PricingRuleKind.Premium, Factor = PricingFactor.Corner, Method = PricingMethod.Percentage, Value = 10m, Unit = null })).StatusCode);

        var list = await c.GetFromJsonAsync<PagedResult<PricingRuleDto>>($"/api/v1/pricing/rules?projectId={projectId}&pageSize=1");
        Assert.Equal(2, list!.TotalCount);
        Assert.Single(list.Items);
        Assert.Equal("BASE", (await c.GetFromJsonAsync<PricingRuleDto>($"/api/v1/pricing/rules/{rule.Id}"))!.Code);

        var calc = await c.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null));
        Assert.Equal(HttpStatusCode.OK, calc.StatusCode);
        var result = (await calc.Content.ReadFromJsonAsync<PriceCalculationDto>())!;
        Assert.Equal(5_000_000m, result.SizeBasedPrice);
        Assert.Equal(500_000m, result.TotalPremiums);
        Assert.Equal(5_500_000m, result.FinalPrice);
        Assert.Equal(2, result.AppliedRules.Count);
        var again = (await (await c.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).Content.ReadFromJsonAsync<PriceCalculationDto>())!;
        Assert.Equal(result.FinalPrice, again.FinalPrice);

        var snap = await c.PostAsJsonAsync("/api/v1/pricing/snapshot", new CalculatePriceRequest(p.Id, 3, null));
        Assert.Equal(HttpStatusCode.OK, snap.StatusCode);
        Assert.Equal(64, (await snap.Content.ReadFromJsonAsync<PriceSnapshotDto>())!.Hash.Length);

        var before = (await c.GetFromJsonAsync<PropertyPriceViewDto>($"/api/v1/properties/{p.Id}/price"))!;
        Assert.Null(before.Published);
        Assert.Equal(5_500_000m, before.Calculation!.FinalPrice);
        var publish = await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/price/publish", new PublishPriceRequest("go live"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.True((await publish.Content.ReadFromJsonAsync<PublishOutcomeDto>())!.Changed);
        Assert.False((await (await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/price/publish", new PublishPriceRequest(null))).Content.ReadFromJsonAsync<PublishOutcomeDto>())!.Changed);
        var after = (await c.GetFromJsonAsync<PropertyPriceViewDto>($"/api/v1/properties/{p.Id}/price"))!;
        Assert.Equal(5_500_000m, after.Published!.FinalPrice);

        var hist = (await c.GetFromJsonAsync<PagedResult<PriceHistoryDto>>($"/api/v1/pricing/history?propertyId={p.Id}"))!;
        Assert.Single(hist.Items);
        var snapshot = await c.GetFromJsonAsync<PriceSnapshotDto>($"/api/v1/pricing/history/{hist.Items[0].Id}/snapshot");
        Assert.Equal(5_500_000m, snapshot!.Calculation.FinalPrice);
        var card = await c.GetFromJsonAsync<RateCardDto>($"/api/v1/pricing/rate-cards/{projectId}");
        Assert.Single(card!.BaseRates);
        var prices = await c.GetFromJsonAsync<PagedResult<PropertyPriceListItemDto>>($"/api/v1/pricing/prices?projectId={projectId}&published=true");
        Assert.Single(prices!.Items);
        var bulk = await c.PostAsJsonAsync($"/api/v1/projects/{projectId}/pricing/publish", new PublishPriceRequest(null));
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        Assert.Equal(1, (await bulk.Content.ReadFromJsonAsync<PublishProjectResultDto>())!.Unchanged);
    }

    [MySqlFact]
    public async Task Validation_NotFound_AndDiscounts()
    {
        var c = await ClientAsync();
        var projectId = await ProjectAsync(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId) with { Code = "bad code" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId) with { EffectiveFrom = new DateOnly(2020, 1, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId) with { Value = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/pricing/rules/999999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(999999999, null, null))).StatusCode);
        var p = await PropertyAsync(c, projectId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).StatusCode); // no base rate yet

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));
        var disc = new CreateDiscountRuleRequest(projectId, "EARLY", "Early", null, PricingScope.Project, null, null, null, null, PricingMethod.Percentage, 5m, false, 0, today, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/pricing/discounts", disc with { Scope = PricingScope.Customer })).StatusCode);
        var ok = await c.PostAsJsonAsync("/api/v1/pricing/discounts", disc);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var d = (await ok.Content.ReadFromJsonAsync<DiscountRuleDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/pricing/discounts/{d.Id}", new UpdateRuleDetailsRequest("Early bird", "desc", d.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PutAsJsonAsync($"/api/v1/pricing/discounts/{d.Id}", new UpdateRuleDetailsRequest("Stale", null, d.RowVersion))).StatusCode);
        var list = await c.GetFromJsonAsync<PagedResult<DiscountRuleDto>>($"/api/v1/pricing/discounts?projectId={projectId}");
        Assert.Single(list!.Items);
    }

    [MySqlFact]
    public async Task Approvals_FullWorkflow_ThroughTheApi()
    {
        var maker = await ClientAsync();                       // administrator: requests
        var checker = await ClientAsync(RoleNames.Director);   // different user holding pricing.approve
        var projectId = await ProjectAsync(maker);
        var p = await PropertyAsync(maker, projectId);
        Assert.Equal(HttpStatusCode.Created, (await maker.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId))).StatusCode);

        var bad = await maker.PostAsJsonAsync("/api/v1/pricing/approvals", new RequestPricingApprovalRequest(PricingApprovalType.PriceOverride, p.Id, null, null, null, null, 1m, null, null, "no"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var req = await maker.PostAsJsonAsync("/api/v1/pricing/approvals", new RequestPricingApprovalRequest(PricingApprovalType.PriceOverride, p.Id, null, null, null, null, 4_400_000m, null, null, "Management agreed"));
        Assert.Equal(HttpStatusCode.Created, req.StatusCode);
        var a = (await req.Content.ReadFromJsonAsync<ApprovalDto>())!;
        Assert.Equal(PricingApprovalStatus.Pending, a.Status);
        Assert.Equal(5_000_000m, a.OriginalPrice);

        var self = await maker.PostAsJsonAsync($"/api/v1/pricing/approvals/{a.Id}/decision", new DecidePricingApprovalRequest(true, null, a.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);     // the requester cannot approve
        var reject = await checker.PostAsJsonAsync($"/api/v1/pricing/approvals/{a.Id}/decision", new DecidePricingApprovalRequest(false, "", a.RowVersion));
        Assert.Equal(HttpStatusCode.BadRequest, reject.StatusCode);            // reject needs a comment
        Assert.Equal(5_000_000m, (await (await maker.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).Content.ReadFromJsonAsync<PriceCalculationDto>())!.FinalPrice);

        var ok = await checker.PostAsJsonAsync($"/api/v1/pricing/approvals/{a.Id}/decision", new DecidePricingApprovalRequest(true, "fine", a.RowVersion));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var decided = (await ok.Content.ReadFromJsonAsync<ApprovalDto>())!;
        Assert.Equal(PricingApprovalStatus.Approved, decided.Status);
        Assert.NotNull(decided.AdjustmentId);
        Assert.Equal(4_400_000m, (await (await maker.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).Content.ReadFromJsonAsync<PriceCalculationDto>())!.FinalPrice);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await checker.PostAsJsonAsync($"/api/v1/pricing/approvals/{a.Id}/decision", new DecidePricingApprovalRequest(true, null, null))).StatusCode);

        var adjustments = await maker.GetFromJsonAsync<List<AdjustmentDto>>($"/api/v1/properties/{p.Id}/price-adjustments");
        Assert.Single(adjustments!);
        var approvals = await maker.GetFromJsonAsync<PagedResult<ApprovalDto>>($"/api/v1/pricing/approvals?propertyId={p.Id}&status=Approved");
        Assert.Single(approvals!.Items);
        Assert.Equal(HttpStatusCode.OK, (await checker.PostAsJsonAsync($"/api/v1/pricing/adjustments/{adjustments![0].Id}/revoke", new RetireRequest("mistake", adjustments[0].RowVersion))).StatusCode);
        Assert.Equal(5_000_000m, (await (await maker.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).Content.ReadFromJsonAsync<PriceCalculationDto>())!.FinalPrice);

        var again = await maker.PostAsJsonAsync("/api/v1/pricing/approvals", new RequestPricingApprovalRequest(PricingApprovalType.CustomerDiscount, p.Id, 12, "Ali", null, PricingMethod.Percentage, 5m, null, null, "Loyal customer"));
        var b = (await again.Content.ReadFromJsonAsync<ApprovalDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await maker.PostAsJsonAsync($"/api/v1/pricing/approvals/{b.Id}/cancel", new CancelPricingApprovalRequest("changed mind", b.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/api/v1/pricing/approvals/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task PermissionsAreEnforced_ForRolesWithoutPricingRights()
    {
        var admin = await ClientAsync();
        var projectId = await ProjectAsync(admin);
        var p = await PropertyAsync(admin, projectId);
        var cashier = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/pricing/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/pricing/approvals", new RequestPricingApprovalRequest(PricingApprovalType.PriceOverride, p.Id, null, null, null, null, 1m, null, null, "Some reason"))).StatusCode);
        var accountant = await ClientAsync(RoleNames.Accountant);                   // view only, and not assigned to the project
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/v1/pricing/rules", BaseRule(projectId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/v1/pricing/calculate", new CalculatePriceRequest(p.Id, null, null))).StatusCode);
        var rules = await accountant.GetFromJsonAsync<PagedResult<PricingRuleDto>>($"/api/v1/pricing/rules?projectId={projectId}");
        Assert.Empty(rules!.Items);
    }
}
