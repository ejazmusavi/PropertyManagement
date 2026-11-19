using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

public sealed class WebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Str0ng#Passw0rd!";
    private readonly MySqlTestDatabase db = new();

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();
        if (MySqlTestDatabase.IsAvailable) _ = Services;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (!MySqlTestDatabase.IsAvailable) return;
        builder.UseSetting("ConnectionStrings:DefaultConnection", db.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "web-test-signing-key-web-test-signing-key-123");
        builder.UseSetting("Database:MigrateOnStartup", "true");
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<string> CreateUserAsync(bool administrator)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email, FullName = "Web Tester", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (administrator) Assert.True((await users.AddToRoleAsync(user, RoleNames.Administrator)).Succeeded);
        return email;
    }

    public static async Task<string> TokenAsync(HttpClient c, string url)
    {
        var html = await c.GetStringAsync(url);
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    }

    public async Task<HttpClient> LoggedInAsync(bool administrator)
    {
        var email = await CreateUserAsync(administrator);
        var c = NewClient();
        var r = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = Password, ["__RequestVerificationToken"] = await TokenAsync(c, "/Account/Login"),
        }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        return c;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await db.DisposeAsync();
    }
}
