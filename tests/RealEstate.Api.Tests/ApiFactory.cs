using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Str0ng#Passw0rd!";
    private readonly MySqlTestDatabase db = new();

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();
        if (!MySqlTestDatabase.IsAvailable) return;
        _ = Services; // starts the host, applies migrations (Database:MigrateOnStartup)
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (!MySqlTestDatabase.IsAvailable) return;
        builder.UseSetting("ConnectionStrings:DefaultConnection", db.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "api-test-signing-key-api-test-signing-key-123");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:LoginPermitLimit", "10000");
        builder.UseSetting("Swagger:Enabled", "true");
    }

    public async Task<string> CreateUserAsync(bool administrator, bool active = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email, FullName = "Api Tester", IsActive = active, CreatedAtUtc = DateTime.UtcNow };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (administrator) Assert.True((await users.AddToRoleAsync(user, RoleNames.Administrator)).Succeeded);
        return email;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await db.DisposeAsync();
    }
}
