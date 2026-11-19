using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace RealEstate.Tests.Support;

/// <summary>
/// Creates a throw-away MySQL database for a test class and drops it afterwards.
/// Configure a MySQL account that may CREATE/DROP databases via env var REALESTATE_TEST_MYSQL or
/// `dotnet user-secrets set TestMySql "Server=...;User=...;Password=..." --project tests/RealEstate.Infrastructure.Tests`.
/// Only databases named realestate_test_* created by this class are ever dropped.
/// </summary>
public sealed class MySqlTestDatabase : IAsyncLifetime
{
    private static readonly string? Admin = Resolve();
    public static bool IsAvailable => Admin is not null;

    public string DatabaseName { get; } = "realestate_test_" + Guid.NewGuid().ToString("N")[..12];
    public string ConnectionString { get; private set; } = "";

    private static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("REALESTATE_TEST_MYSQL");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var cfg = new ConfigurationBuilder().AddUserSecrets("realestate-tests-5e2b7d90").Build();
        var secret = cfg["TestMySql"];
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    public async Task InitializeAsync()
    {
        if (Admin is null) return;
        await using var conn = new MySqlConnection(Admin);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{DatabaseName}` CHARACTER SET utf8mb4";
        await cmd.ExecuteNonQueryAsync();
        ConnectionString = new MySqlConnectionStringBuilder(Admin) { Database = DatabaseName }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (Admin is null || !DatabaseName.StartsWith("realestate_test_")) return;
        MySqlConnection.ClearAllPools();
        await using var conn = new MySqlConnection(Admin);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{DatabaseName}`";
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Skipped (reported as skipped, not passed) when no test MySQL server is configured.</summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (!MySqlTestDatabase.IsAvailable)
            Skip = "No test MySQL configured (set REALESTATE_TEST_MYSQL or user-secret TestMySql).";
    }
}
