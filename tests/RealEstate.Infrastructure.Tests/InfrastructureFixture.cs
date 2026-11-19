using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Common;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Builds the real Infrastructure DI container on a fresh migrated MySQL database.</summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly MySqlTestDatabase db = new();
    public ServiceProvider Root { get; private set; } = default!;
    public string ConnectionString => db.ConnectionString;
    public string DatabaseName => db.DatabaseName;

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();
        if (!MySqlTestDatabase.IsAvailable) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.ConnectionString,
            ["Jwt:SigningKey"] = "test-signing-key-test-signing-key-123456",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection();
        services.AddInfrastructure(config);
        Root = services.BuildServiceProvider();
        using var scope = Root.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Root is not null) await Root.DisposeAsync();
        await db.DisposeAsync();
    }

    public IServiceScope Scope() => Root.CreateScope();
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<InfrastructureFixture>;

public sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public bool IsAuthenticated => UserId is not null;
    public string? IpAddress => "127.0.0.1";
    public string? CorrelationId => "test";
}
