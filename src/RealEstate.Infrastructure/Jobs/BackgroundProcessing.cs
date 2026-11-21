using Hangfire;
using Hangfire.MySql;
using Hangfire.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using RealEstate.Application.Jobs;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Jobs;

/// <summary>
/// Runs one catalogued job under Hangfire. Retry policy: five automatic retries with growing delays (1 min, 5 min, 15 min, 1 h, 6 h), then the job is
/// marked failed and stays visible in the run history. The job itself is idempotent, so a retry never repeats a business effect. Jobs that call
/// permission-checked services run as the service user named in <c>BackgroundProcessing:RunAsUserName</c> (a normal, active user with the needed role).
/// </summary>
public class HangfireJobRunner(ScheduledJobService jobs, BackgroundUserContext context, UserManager<ApplicationUser> users, IConfiguration config, ILogger<HangfireJobRunner> logger)
{
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [60, 300, 900, 3600, 21600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(string key, string triggeredBy, CancellationToken ct)
    {
        var name = config["BackgroundProcessing:RunAsUserName"];
        if (!string.IsNullOrWhiteSpace(name))
        {
            var u = await users.FindByNameAsync(name);
            if (u is { IsActive: true }) context.Set(u.Id, u.UserName ?? name);
            else logger.LogWarning("The background service user '{User}' does not exist or is inactive; jobs that need it are skipped", name);
        }
        await jobs.ExecuteAsync(key, triggeredBy, ct);
    }
}

public sealed class DisabledJobGateway : IBackgroundJobGateway
{
    public bool Enabled => false;
    public IReadOnlyList<JobGatewayInfo> Describe() => [];
    public string Trigger(string key, string triggeredBy) => throw new InvalidOperationException("Background processing is switched off.");
}

public sealed class HangfireJobGateway(IBackgroundJobClient client, JobStorage storage) : IBackgroundJobGateway
{
    public bool Enabled => true;

    public IReadOnlyList<JobGatewayInfo> Describe()
    {
        try
        {
            using var connection = storage.GetConnection();
            return [.. connection.GetRecurringJobs().Select(j => new JobGatewayInfo(j.Id, j.NextExecution?.ToUniversalTime(), j.LastExecution?.ToUniversalTime(), j.LastJobState))];
        }
        catch (Exception) { return []; }   // the screen still lists the catalogue when the scheduler store cannot be read
    }

    public string Trigger(string key, string triggeredBy) => client.Enqueue<HangfireJobRunner>(r => r.RunAsync(key, triggeredBy, CancellationToken.None));
}

/// <summary>Registers (or refreshes) the recurring jobs of the catalogue when the server host starts. Idempotent: running it twice leaves one schedule per job.</summary>
public sealed class RecurringJobRegistrar(IRecurringJobManager recurring, ILogger<RecurringJobRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var d in JobCatalog.All)
        {
            var key = d.Key;
            recurring.AddOrUpdate<HangfireJobRunner>(key, r => r.RunAsync(key, "schedule", CancellationToken.None), d.Cron, new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
        logger.LogInformation("{Count} recurring background jobs registered", JobCatalog.All.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class BackgroundProcessingExtensions
{
    public const string Section = "BackgroundProcessing";

    /// <summary>
    /// Client side (every host): with <c>BackgroundProcessing:Enabled</c> the Hangfire MySQL storage is configured so the host can queue and describe jobs;
    /// otherwise a disabled gateway is registered and nothing touches the scheduler tables.
    /// </summary>
    public static IServiceCollection AddBackgroundProcessing(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<HangfireJobRunner>();
        if (!config.GetValue<bool>($"{Section}:Enabled"))
        {
            services.AddSingleton<IBackgroundJobGateway, DisabledJobGateway>();
            return services;
        }
        var connection = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        var builder = new MySqlConnectionStringBuilder(connection) { AllowUserVariables = true };
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseStorage(new MySqlStorage(builder.ConnectionString, new MySqlStorageOptions { TablesPrefix = "Hangfire", PrepareSchemaIfNecessary = true })));
        services.AddSingleton<IBackgroundJobGateway, HangfireJobGateway>();
        return services;
    }

    /// <summary>Server side (the API host): runs the Hangfire workers and registers the recurring jobs. Does nothing unless background processing is enabled.</summary>
    public static IServiceCollection AddBackgroundProcessingServer(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue<bool>($"{Section}:Enabled") || !config.GetValue($"{Section}:RunServer", true)) return services;
        services.AddHangfireServer(o => { o.WorkerCount = Math.Clamp(config.GetValue($"{Section}:WorkerCount", 2), 1, 8); o.ServerName = $"realestate-{Environment.MachineName}"; });
        services.AddHostedService<RecurringJobRegistrar>();
        return services;
    }
}
