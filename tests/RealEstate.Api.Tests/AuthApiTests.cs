using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstate.Application.Auth;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class AuthApiTests(ApiFactory factory)
{
    private HttpClient Client() => factory.CreateClient();

    private async Task<TokenResponse> LoginAsync(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    [MySqlFact]
    public async Task Health_ReportsHealthyDatabase()
    {
        var r = await Client().GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", body.GetProperty("database").GetString());
    }

    [MySqlFact]
    public async Task Login_Valid_ReturnsTokens_AndMeReturnsPermissions()
    {
        var email = await factory.CreateUserAsync(administrator: true);
        var c = Client();
        var tokens = await LoginAsync(c, email);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await c.GetFromJsonAsync<CurrentUserDto>("/api/v1/auth/me");
        Assert.Equal(email, me!.Email);
        Assert.Contains("Administrator", me.Roles);
        Assert.Contains("users.view", me.Permissions);
    }

    [MySqlFact]
    public async Task Me_WithoutPermissionsRole_HasNoPermissions()
    {
        var email = await factory.CreateUserAsync(administrator: false);
        var c = Client();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginAsync(c, email)).AccessToken);
        var me = await c.GetFromJsonAsync<CurrentUserDto>("/api/v1/auth/me");
        Assert.Empty(me!.Permissions);
    }

    [MySqlFact]
    public async Task Login_WrongPassword_Is401ProblemDetails()
    {
        var email = await factory.CreateUserAsync(true);
        var r = await Client().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
    }

    [MySqlFact]
    public async Task Login_InactiveUser_Is401()
    {
        var email = await factory.CreateUserAsync(true, active: false);
        var r = await Client().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [MySqlFact]
    public async Task Login_BlankBody_Is400WithValidationErrors()
    {
        var r = await Client().PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [MySqlFact]
    public async Task Login_RepeatedFailures_LockAccount()
    {
        var email = await factory.CreateUserAsync(true);
        var c = Client();
        for (var i = 0; i < 5; i++) await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong"));
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        Assert.Equal((HttpStatusCode)423, r.StatusCode);
    }

    [MySqlFact]
    public async Task Me_WithoutToken_Is401()
    {
        var r = await Client().GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [MySqlFact]
    public async Task Me_WithTamperedToken_Is401()
    {
        var c = Client();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [MySqlFact]
    public async Task Refresh_RotatesToken_AndReplayIsRejected()
    {
        var email = await factory.CreateUserAsync(true);
        var c = Client();
        var first = await LoginAsync(c, email);

        var r = await c.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var second = (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var replay = await c.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var descendant = await c.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [MySqlFact]
    public async Task Logout_RevokesRefreshToken()
    {
        var email = await factory.CreateUserAsync(true);
        var c = Client();
        var t = await LoginAsync(c, email);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(t.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.RefreshToken))).StatusCode);
    }

    [MySqlFact]
    public async Task Logout_WithoutToken_Is401()
    {
        var r = await Client().PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest("x"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [MySqlFact]
    public async Task OtherUsersCannotRevokeMyRefreshToken()
    {
        var c1 = Client();
        var mine = await LoginAsync(c1, await factory.CreateUserAsync(false));
        var c2 = Client();
        var other = await LoginAsync(c2, await factory.CreateUserAsync(false));
        c2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        await c2.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(mine.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(mine.RefreshToken))).StatusCode);
    }

    [MySqlFact]
    public async Task OpenApiDocument_DescribesAuthEndpointsAndBearerScheme()
    {
        var c = Client();
        var json = await c.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/auth/login", json);
        Assert.Contains("/api/v1/health", json);
        Assert.Contains("\"bearer\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/swagger/index.html")).StatusCode);
    }

    [MySqlFact]
    public async Task UnknownRoute_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync("/api/v1/nothing")).StatusCode);
    }
}
