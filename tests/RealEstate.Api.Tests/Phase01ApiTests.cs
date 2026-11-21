using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstate.Application.Administration;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase01ApiTests(ApiFactory factory)
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

    private static CreateProjectRequest NewProject(decimal area = 100m) =>
        new("A" + Guid.NewGuid().ToString("N")[..10], "API Project", "desc", "Skardu", "Main road", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), area, MeasurementUnit.Kanal, null,
            new ProjectAddressDto("Line 1", "Loc", "GB", "Pakistan", "16100", 35.29m, 75.63m, "Near lake"));

    private static async Task<ProjectDetailDto> CreateAsync(HttpClient c, decimal area = 100m)
    {
        var r = await c.PostAsJsonAsync("/api/v1/projects", NewProject(area));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.NotNull(r.Headers.Location);
        return (await r.Content.ReadFromJsonAsync<ProjectDetailDto>())!;
    }

    [MySqlFact]
    public async Task Endpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/projects", "/api/v1/projects/1", "/api/v1/projects/1/land", "/api/v1/users", "/api/v1/roles", "/api/v1/permissions", "/api/v1/company" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
    }

    [MySqlFact]
    public async Task Project_CreateGetUpdateList_RoundTrip()
    {
        var (c, _) = await ClientAsync();
        var p = await CreateAsync(c, 55.25m);
        Assert.Equal(55.25m, p.CurrentArea);
        Assert.Equal("Near lake", p.AddressDetails!.Landmark);
        var got = await c.GetFromJsonAsync<ProjectDetailDto>($"/api/v1/projects/{p.Id}");
        Assert.Equal(p.ProjectCode, got!.ProjectCode);

        var put = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}", new UpdateProjectRequest("Renamed", null, "Gilgit", null, null, null, null, null, got.RowVersion));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stale = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}", new UpdateProjectRequest("Stale", null, "Gilgit", null, null, null, null, null, got.RowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);

        var list = await c.GetFromJsonAsync<PagedResult<ProjectListItemDto>>($"/api/v1/projects?search={p.ProjectCode}&pageSize=5&sortBy=name");
        Assert.Single(list!.Items);
        Assert.Equal("Renamed", list.Items[0].ProjectName);
        var byCity = await c.GetFromJsonAsync<PagedResult<ProjectListItemDto>>("/api/v1/projects?city=Gilgit&pageSize=100");
        Assert.Contains(byCity!.Items, x => x.Id == p.Id);
    }

    [MySqlFact]
    public async Task Project_InvalidRequest_Returns400ProblemDetails()
    {
        var (c, _) = await ClientAsync();
        var r = await c.PostAsJsonAsync("/api/v1/projects", NewProject() with { InitialArea = -5, City = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("InitialArea", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/projects/99999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Land_Acquire_Adjust_History_Flow()
    {
        var (c, adminId) = await ClientAsync();
        var p = await CreateAsync(c, 100m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var acq = await c.PostAsJsonAsync($"/api/v1/projects/{p.Id}/land/acquisitions", new AcquireLandRequest(today, "Seller", 20m, 500000m, "REG-9", null, null, null));
        Assert.Equal(HttpStatusCode.Created, acq.StatusCode);
        var adj = await c.PostAsJsonAsync($"/api/v1/projects/{p.Id}/land/adjustments", new AdjustLandRequest(today, LandAdjustmentType.Reduction, 5m, "Road widening", "Deed 77", adminId, null));
        Assert.Equal(HttpStatusCode.Created, adj.StatusCode);

        var land = await c.GetFromJsonAsync<LandOverviewDto>($"/api/v1/projects/{p.Id}/land");
        Assert.Equal(115m, land!.TotalProjectArea);
        Assert.True(land.IsReconciled);
        var history = await c.GetFromJsonAsync<PagedResult<AreaHistoryDto>>($"/api/v1/projects/{p.Id}/land/history");
        Assert.Equal(3, history!.TotalCount);
        Assert.Equal(AreaChangeSource.Adjustment, history.Items[0].Source);

        var noSupport = await c.PostAsJsonAsync($"/api/v1/projects/{p.Id}/land/adjustments", new AdjustLandRequest(today, LandAdjustmentType.Addition, 5m, "Reason here", "", adminId, null));
        Assert.Equal(HttpStatusCode.BadRequest, noSupport.StatusCode);

        var version = land.DistributionVersion;
        var noVersion = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}/land/distribution", new SetLandDistributionRequest([new(LandCategory.Residential, 10m)], "Plan", ""));
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        var dist = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}/land/distribution", new SetLandDistributionRequest([new(LandCategory.Residential, 100m), new(LandCategory.Roads, 20m)], "Plan", version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dist.StatusCode);
        var ok = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}/land/distribution", new SetLandDistributionRequest([new(LandCategory.Residential, 60m)], "Plan", version));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var stale = await c.PutAsJsonAsync($"/api/v1/projects/{p.Id}/land/distribution", new SetLandDistributionRequest([new(LandCategory.Roads, 10m)], "Stale edit", version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Contains("changed by another user", await stale.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task UnassignedUser_CannotSeeProject_UntilAssigned()
    {
        var (admin, _) = await ClientAsync();
        var p = await CreateAsync(admin);
        var (cashier, cashierId) = await ClientAsync("Cashier");

        var r = await cashier.GetAsync($"/api/v1/projects/{p.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var list = await cashier.GetFromJsonAsync<PagedResult<ProjectListItemDto>>("/api/v1/projects?pageSize=100");
        Assert.DoesNotContain(list!.Items, x => x.Id == p.Id);
        // Cashier has no land permission at all, so land endpoints are forbidden regardless of assignment.
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/projects/{p.Id}/land")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/projects", NewProject())).StatusCode);

        var grant = await admin.PostAsJsonAsync($"/api/v1/projects/{p.Id}/access", new AssignProjectAccessRequest(cashierId));
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"/api/v1/projects/{p.Id}")).StatusCode);
        var access = await admin.GetFromJsonAsync<List<ProjectAccessUserDto>>($"/api/v1/projects/{p.Id}/access");
        Assert.Contains(access!, a => a.UserId == cashierId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/projects/{p.Id}/access/{cashierId}?reason=Rotated")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/v1/projects/{p.Id}")).StatusCode);
    }

    [MySqlFact]
    public async Task Users_Roles_Permissions_Endpoints()
    {
        var (c, _) = await ClientAsync();
        var roles = await c.GetFromJsonAsync<List<RoleDto>>("/api/v1/roles");
        Assert.Equal(10, roles!.Count(r => r.IsSystem));
        var perms = await c.GetFromJsonAsync<List<PermissionDto>>("/api/v1/permissions");
        Assert.Contains(perms!, p => p.Code == "land.approve");

        var email = $"n{Guid.NewGuid():N}@example.com";
        var created = await c.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Created Via Api", null, ApiFactory.Password, true, ["Project Manager"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<UserDetailDto>())!;
        var upd = await c.PutAsJsonAsync($"/api/v1/users/{user.Id}", new UpdateUserRequest("Changed", null, false, ["Cashier"]));
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var got = await c.GetFromJsonAsync<UserDetailDto>($"/api/v1/users/{user.Id}");
        Assert.False(got!.IsActive);
        Assert.Equal(["Cashier"], got.Roles);
        var page = await c.GetFromJsonAsync<PagedResult<RealEstate.Application.Users.UserListItemDto>>($"/api/v1/users?search={email}");
        Assert.Single(page!.Items);

        var dup = await c.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Dup", null, ApiFactory.Password, true, []));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    [MySqlFact]
    public async Task NonPrivilegedUser_CannotManageUsersOrRoles()
    {
        var (c, _) = await ClientAsync("Project Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("x@example.com", "X", null, ApiFactory.Password, true, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/company")).StatusCode);
    }

    [MySqlFact]
    public async Task Company_GetAndUpdate()
    {
        var (c, _) = await ClientAsync();
        var current = await c.GetFromJsonAsync<RealEstate.Application.Companies.CompanyDto>("/api/v1/company");
        var req = new RealEstate.Application.Companies.UpdateCompanyRequest("API Company", null, null, null, null, null, null, null, null, null, "USD", "$", 2, RoundingMode.Nearest, 1, "UTC", 5, "pdf,png", current!.RowVersion);
        var put = await c.PutAsJsonAsync("/api/v1/company", req);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var back = await put.Content.ReadFromJsonAsync<RealEstate.Application.Companies.CompanyDto>();
        Assert.Equal("USD", back!.CurrencyCode);
    }

    [MySqlFact]
    public async Task ProjectDocuments_UploadDownloadDelete_OverMultipart()
    {
        var (c, _) = await ClientAsync();
        var p = await CreateAsync(c);
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("%PDF-1.4 api"u8.ToArray()), "file", "layout.pdf" },
            { new StringContent("Layout"), "type" }, { new StringContent("Master layout"), "title" },
        };
        var up = await c.PostAsync($"/api/v1/projects/{p.Id}/documents", content);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var doc = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        var dl = await c.GetAsync($"/api/v1/projects/{p.Id}/documents/{doc.Id}/download");
        Assert.Equal("%PDF-1.4 api", await dl.Content.ReadAsStringAsync());
        var bad = new MultipartFormDataContent { { new ByteArrayContent([1, 2]), "file", "x.exe" } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsync($"/api/v1/projects/{p.Id}/documents", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/projects/{p.Id}/documents/{doc.Id}?reason=Replaced")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/projects/{p.Id}/documents/{doc.Id}/download")).StatusCode);
    }

    [MySqlFact]
    public async Task OpenApi_DocumentsPhase1Endpoints()
    {
        var doc = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = doc.GetProperty("paths");
        foreach (var p in new[] { "/api/v1/projects", "/api/v1/projects/{id}", "/api/v1/projects/{projectId}/land", "/api/v1/projects/{projectId}/land/acquisitions",
                     "/api/v1/projects/{projectId}/land/adjustments", "/api/v1/projects/{projectId}/land/history", "/api/v1/users", "/api/v1/users/{id}", "/api/v1/roles", "/api/v1/permissions" })
            Assert.True(paths.TryGetProperty(p, out _), p);
    }
}
