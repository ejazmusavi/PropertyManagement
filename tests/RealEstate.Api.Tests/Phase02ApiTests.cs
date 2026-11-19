using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase02ApiTests(ApiFactory factory)
{
    private async Task<(HttpClient Client, Guid UserId)> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        var tokens = (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await c.GetFromJsonAsync<CurrentUserDto>("/api/v1/auth/me");
        return (c, me!.Id);
    }

    private static async Task<long> ProjectAsync(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("I" + Guid.NewGuid().ToString("N")[..10], "Inventory API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ProjectDetailDto>())!.Id;
    }

    private static async Task<long> TypeIdAsync(HttpClient c, string code) =>
        (await c.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!.Single(t => t.Code == code).Id;

    private static CreatePropertyRequest NewProperty(long projectId, long typeId, string? code = null, int? floor = null) =>
        new(projectId, code ?? "A" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null, typeId, 5m, MeasurementUnit.Marla, "Loc", floor, false, true, true, DevelopmentStatus.Developed, null,
            [new DimensionDto("Length", 40, "ft")], [new FeatureDto("Gas", null)]);

    [MySqlFact]
    public async Task InventoryEndpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/properties", "/api/v1/properties/available", "/api/v1/properties/1", "/api/v1/projects/1/inventory-summary", "/api/v1/property-types", "/api/v1/property-categories",
                     "/api/v1/projects/1/blocks", "/api/v1/projects/1/sectors", "/api/v1/projects/1/streets", "/api/v1/inventory/reports", "/api/v1/inventory/history" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/properties/1/reserve", new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Property_CreateGetUpdateList_RoundTrip()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var typeId = await TypeIdAsync(c, "RES-PLOT");
        var req = NewProperty(projectId, typeId);
        var created = await c.PostAsJsonAsync("/api/v1/properties", req);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var p = (await created.Content.ReadFromJsonAsync<PropertyDetailDto>())!;
        Assert.Equal(req.PropertyCode.ToUpperInvariant(), p.PropertyCode);
        Assert.Equal(PropertyStatus.Available, p.Status);

        var got = await c.GetFromJsonAsync<PropertyDetailDto>($"/api/v1/properties/{p.Id}");
        Assert.Single(got!.Dimensions);

        var upd = new UpdatePropertyRequest("2", null, null, null, typeId, 6m, MeasurementUnit.Marla, "Moved", null, true, true, true, DevelopmentStatus.Developed, "r", [new DimensionDto("Width", 30, "ft")], [], got.RowVersion);
        var put = await c.PutAsJsonAsync($"/api/v1/properties/{p.Id}", upd);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PutAsJsonAsync($"/api/v1/properties/{p.Id}", upd)).StatusCode); // stale row version

        var list = await c.GetFromJsonAsync<PagedResult<PropertyListItemDto>>($"/api/v1/properties?projectId={projectId}&search={req.PropertyCode}&pageSize=5&sortBy=size");
        Assert.Single(list!.Items);
        Assert.Equal(6m, list.Items[0].Size);
    }

    [MySqlFact]
    public async Task Property_Invalid_ReturnsProblemDetails_AndDuplicateCodeConflict()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var typeId = await TypeIdAsync(c, "RES-PLOT");
        var bad = await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, typeId) with { Size = -1m, PropertyCode = "bad code" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);

        var req = NewProperty(projectId, typeId);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/v1/properties", req)).StatusCode);
        var dup = await c.PostAsJsonAsync("/api/v1/properties", req with { PropertyNumber = "9" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        var floorless = await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, await TypeIdAsync(c, "APARTMENT")));
        Assert.Equal(HttpStatusCode.BadRequest, floorless.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/properties/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Reserve_Release_Available_AndSummary()
    {
        var (c, me) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var typeId = await TypeIdAsync(c, "HOUSE");
        var p = (await (await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, typeId))).Content.ReadFromJsonAsync<PropertyDetailDto>())!;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var reserve = await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/reserve", new ReservePropertyRequest(today, today.AddDays(5), 2500.50m, null, "Sara", "0300", me, p.RowVersion));
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var reserved = (await reserve.Content.ReadFromJsonAsync<PropertyDetailDto>())!;
        Assert.Equal(PropertyStatus.Reserved, reserved.Status);
        Assert.Equal(2500.50m, reserved.ActiveReservation!.Amount);

        var available = await c.GetFromJsonAsync<PagedResult<PropertyListItemDto>>($"/api/v1/properties/available?projectId={projectId}");
        Assert.DoesNotContain(available!.Items, x => x.Id == p.Id);

        var second = await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/reserve", new ReservePropertyRequest(today, today.AddDays(5), 1m, null, "Other", null, me, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode); // already reserved: no double booking

        var summary = await c.GetFromJsonAsync<InventorySummaryDto>($"/api/v1/projects/{projectId}/inventory-summary");
        Assert.Equal(1, summary!.ByStatus.Single(s => s.Status == PropertyStatus.Reserved).Count);

        var release = await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/release-reservation", new ReleaseReservationRequest("Customer cancelled", reserved.RowVersion));
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        available = await c.GetFromJsonAsync<PagedResult<PropertyListItemDto>>($"/api/v1/properties/available?projectId={projectId}");
        Assert.Contains(available!.Items, x => x.Id == p.Id);
        var history = await c.GetFromJsonAsync<PagedResult<PropertyStatusHistoryDto>>($"/api/v1/properties/{p.Id}/history");
        Assert.Equal(3, history!.TotalCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/release-reservation", new ReleaseReservationRequest("Again", null))).StatusCode);
    }

    [MySqlFact]
    public async Task StatusEndpoint_EnforcesTransitions()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var p = (await (await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, await TypeIdAsync(c, "SHOP"), floor: 0))).Content.ReadFromJsonAsync<PropertyDetailDto>())!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/status", new ChangePropertyStatusRequest(PropertyStatus.Sold, "skip ahead", p.RowVersion))).StatusCode);
        var ok = await c.PostAsJsonAsync($"/api/v1/properties/{p.Id}/status", new ChangePropertyStatusRequest(PropertyStatus.Booked, "Customer booked", p.RowVersion));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [MySqlFact]
    public async Task Setup_Endpoints_CreateCategoriesTypesAndZones()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var code = "C" + Guid.NewGuid().ToString("N")[..8];
        var cat = await c.PostAsJsonAsync("/api/v1/property-categories", new SaveCategoryRequest(code, "Cat " + code, null, true));
        Assert.Equal(HttpStatusCode.Created, cat.StatusCode);
        var catDto = (await cat.Content.ReadFromJsonAsync<CategoryDto>())!;
        var type = await c.PostAsJsonAsync("/api/v1/property-types", new SavePropertyTypeRequest("T" + code, "Type " + code, catDto.Id, PropertyForm.Other, false, true));
        Assert.Equal(HttpStatusCode.Created, type.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/property-categories", new SaveCategoryRequest(code, "Dup", null, true))).StatusCode);

        foreach (var kind in new[] { "blocks", "sectors", "streets" })
        {
            var z = await c.PostAsJsonAsync($"/api/v1/projects/{projectId}/{kind}", new SaveZoneRequest("Z1", "Zone " + kind, true));
            Assert.Equal(HttpStatusCode.Created, z.StatusCode);
            var dto = (await z.Content.ReadFromJsonAsync<ZoneDto>())!;
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/projects/{projectId}/{kind}/{dto.Id}", new SaveZoneRequest("Z1", "Renamed", true))).StatusCode);
            Assert.Single((await c.GetFromJsonAsync<List<ZoneDto>>($"/api/v1/projects/{projectId}/{kind}"))!);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/v1/projects/{projectId}/{kind}", new SaveZoneRequest("Z1", "Again", true))).StatusCode);
        }
    }

    [MySqlFact]
    public async Task Authorization_RolesWithoutPermission_AreForbidden_AndViewOnlyCannotMutate()
    {
        var (admin, _) = await ClientAsync();
        var projectId = await ProjectAsync(admin);
        var typeId = await TypeIdAsync(admin, "RES-PLOT");
        var p = (await (await admin.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, typeId))).Content.ReadFromJsonAsync<PropertyDetailDto>())!;

        var (cashier, _) = await ClientAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/properties")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/projects/{projectId}/inventory-summary")).StatusCode);

        var (support, _) = await ClientAsync(RoleNames.CustomerSupport);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/api/v1/properties")).StatusCode); // view permission, no projects assigned => empty
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync($"/api/v1/properties/{p.Id}")).StatusCode); // not assigned to the project
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, typeId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/api/v1/properties/{p.Id}/status", new ChangePropertyStatusRequest(PropertyStatus.Cancelled, "nope", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync("/api/v1/property-categories", new SaveCategoryRequest("Q1", "Q1", null, true))).StatusCode);
        var list = await support.GetFromJsonAsync<PagedResult<PropertyListItemDto>>("/api/v1/properties?pageSize=100");
        Assert.DoesNotContain(list!.Items, x => x.Id == p.Id); // other projects' data is never listed
    }

    [MySqlFact]
    public async Task Reports_AndHistory_Endpoints_Respond()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var typeId = await TypeIdAsync(c, "RES-PLOT");
        await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, typeId));
        var report = await c.GetFromJsonAsync<InventoryReportDto>($"/api/v1/inventory/reports?projectId={projectId}");
        Assert.Equal(1, report!.Total);
        Assert.Single(report.ByProject);
        var history = await c.GetFromJsonAsync<PagedResult<PropertyStatusHistoryDto>>($"/api/v1/inventory/history?projectId={projectId}");
        Assert.Equal(1, history!.TotalCount);
    }

    [MySqlFact]
    public async Task Documents_UploadListDownloadDelete()
    {
        var (c, _) = await ClientAsync();
        var projectId = await ProjectAsync(c);
        var p = (await (await c.PostAsJsonAsync("/api/v1/properties", NewProperty(projectId, await TypeIdAsync(c, "RES-PLOT")))).Content.ReadFromJsonAsync<PropertyDetailDto>())!;
        using var form = new MultipartFormDataContent { { new ByteArrayContent("%PDF-1"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "plan.pdf" }, { new StringContent("Plan"), "type" } };
        var up = await c.PostAsync($"/api/v1/properties/{p.Id}/documents", form);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var doc = (await up.Content.ReadFromJsonAsync<PropertyDocumentDto>())!;
        Assert.Single((await c.GetFromJsonAsync<List<PropertyDocumentDto>>($"/api/v1/properties/{p.Id}/documents"))!);
        var dl = await c.GetAsync($"/api/v1/properties/{p.Id}/documents/{doc.Id}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("%PDF-1", await dl.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/properties/{p.Id}/documents/{doc.Id}?reason=Replaced")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<PropertyDocumentDto>>($"/api/v1/properties/{p.Id}/documents"))!);
    }
}
