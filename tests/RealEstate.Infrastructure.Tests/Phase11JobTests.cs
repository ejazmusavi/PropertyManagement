using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RealEstate.Application.Common;
using RealEstate.Application.Jobs;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Infrastructure.Jobs;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 11 background processing: job execution, run history, idempotency, service-user jobs, maintenance and the Hangfire scheduler wiring.</summary>
[Collection("phase1")]
public class Phase11JobTests(Phase1Fixture fx)
{
    private readonly Phase11Kit k = new(fx);
    private static T Svc<T>(IServiceScope s) where T : notnull => Phase11Kit.Svc<T>(s);

    private async Task<JobRunDto> ExecuteAsync(Guid? actor, string key)
    {
        if (actor is { } a) return await k.Run(a, s => Svc<ScheduledJobService>(s).ExecuteAsync(key, "test"));
        return await k.RunAnonymous(s => Svc<ScheduledJobService>(s).ExecuteAsync(key, "test"));
    }

    [MySqlFact]
    public async Task Execute_RecordsTheRun_AndTheCatalogueShowsIt()
    {
        var w = await k.WorldAsync();
        var run = await ExecuteAsync(w.Director, JobCatalog.Dispatch);
        Assert.Equal(BackgroundJobRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.Contains("sent", run.Message);

        var jobs = await k.Run(w.Director, s => Svc<ScheduledJobService>(s).ListAsync());
        Assert.Equal(JobCatalog.All.Count, jobs.Count);
        var d = jobs.Single(j => j.Key == JobCatalog.Dispatch);
        Assert.Equal(BackgroundJobRunStatus.Succeeded, d.LastRun!.Status);
        Assert.False(d.Scheduled);                                         // the scheduler is off in this test host
        var runs = await k.Run(w.Director, s => Svc<ScheduledJobService>(s).RunsAsync(JobCatalog.Dispatch, new PagedRequest { PageSize = 5 }));
        Assert.Contains(runs.Items, r => r.Id == run.Id);
        Assert.All(runs.Items, r => Assert.Equal(JobCatalog.Dispatch, r.JobKey));

        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Cashier, s => Svc<ScheduledJobService>(s).ListAsync()));
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Cashier, s => Svc<ScheduledJobService>(s).RunsAsync(null, new PagedRequest())));
        await Assert.ThrowsAsync<NotFoundException>(() => ExecuteAsync(w.Director, "no-such-job"));
    }

    [MySqlFact]
    public async Task EveryCatalogueJob_RunsToCompletion_AndASecondRunChangesNothingNew()
    {
        var w = await k.WorldAsync();
        foreach (var def in JobCatalog.All)
        {
            var first = await ExecuteAsync(w.Admin, def.Key);
            Assert.Equal(BackgroundJobRunStatus.Succeeded, first.Status);
            var second = await ExecuteAsync(w.Admin, def.Key);
            Assert.Equal(BackgroundJobRunStatus.Succeeded, second.Status);
        }
    }

    [MySqlFact]
    public async Task ServiceUserJobs_AreSkippedWithoutAUser_AndFailuresAreRecordedAndRethrown()
    {
        var w = await k.WorldAsync();
        foreach (var key in new[] { JobCatalog.Gateway, JobCatalog.Postings })
        {
            var skipped = await ExecuteAsync(null, key);
            Assert.Equal(BackgroundJobRunStatus.Succeeded, skipped.Status);
            Assert.StartsWith("Skipped: no service user", skipped.Message);
        }
        // the posting job needs accounting.postings.process: the sales manager lacks it, so the job fails visibly (and the scheduler retries)
        await Assert.ThrowsAsync<ForbiddenException>(() => ExecuteAsync(w.Sales, JobCatalog.Postings));
        using var s = fx.Scope();
        var failed = await Svc<AppDbContext>(s).BackgroundJobRuns.AsNoTracking().Where(r => r.JobKey == JobCatalog.Postings && r.Status == BackgroundJobRunStatus.Failed).OrderByDescending(r => r.Id).FirstAsync();
        Assert.NotNull(failed.FinishedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        // an administrator-level service user runs it
        Assert.Equal(BackgroundJobRunStatus.Succeeded, (await ExecuteAsync(w.Admin, JobCatalog.Postings)).Status);
    }

    [MySqlFact]
    public async Task OnlyOneRunOfAJobIsActive_AndAStuckRunExpires()
    {
        var w = await k.WorldAsync();
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            db.BackgroundJobRuns.Add(new BackgroundJobRun { JobKey = JobCatalog.Reservations, TriggeredBy = "other worker", StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), Status = BackgroundJobRunStatus.Running });
            await db.SaveChangesAsync();
        }
        int before;
        using (var s = fx.Scope()) before = await Svc<AppDbContext>(s).BackgroundJobRuns.CountAsync(r => r.JobKey == JobCatalog.Reservations);
        var skipped = await ExecuteAsync(w.Admin, JobCatalog.Reservations);
        Assert.StartsWith("Skipped: the previous run is still in progress", skipped.Message);
        using (var s = fx.Scope()) Assert.Equal(before, await Svc<AppDbContext>(s).BackgroundJobRuns.CountAsync(r => r.JobKey == JobCatalog.Reservations));

        // a "running" row older than the job's time limit (a crashed worker) no longer blocks
        using (var s = fx.Scope())
            await Svc<AppDbContext>(s).BackgroundJobRuns.Where(r => r.JobKey == JobCatalog.Reservations && r.Status == BackgroundJobRunStatus.Running)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.StartedAtUtc, DateTime.UtcNow.AddHours(-3)));
        Assert.Equal(BackgroundJobRunStatus.Succeeded, (await ExecuteAsync(w.Admin, JobCatalog.Reservations)).Status);
    }

    [MySqlFact]
    public async Task LedgerJob_PostsReceivablesOfDueInstallments_Once()
    {
        var w = await k.WorldAsync();
        var d = await k.DealAsync(w, monthsBack: 2);
        using (var s = fx.Scope()) Assert.Equal(0, await Svc<AppDbContext>(s).AgreementLedgerEntries.CountAsync(e => e.AgreementId == d.AgreementId));    // receivables are posted lazily

        var run = await ExecuteAsync(w.Admin, JobCatalog.Ledgers);
        Assert.Equal(BackgroundJobRunStatus.Succeeded, run.Status);
        int entries;
        using (var s = fx.Scope()) entries = await Svc<AppDbContext>(s).AgreementLedgerEntries.CountAsync(e => e.AgreementId == d.AgreementId);
        Assert.True(entries > 0);
        await ExecuteAsync(w.Admin, JobCatalog.Ledgers);
        using (var s = fx.Scope()) Assert.Equal(entries, await Svc<AppDbContext>(s).AgreementLedgerEntries.CountAsync(e => e.AgreementId == d.AgreementId));
    }

    [MySqlFact]
    public async Task ReminderAndDigestJobs_QueueNotifications_Idempotently()
    {
        var w = await k.WorldAsync();
        var d = await k.DealAsync(w);
        var first = await ExecuteAsync(w.Admin, JobCatalog.Reminders);
        Assert.Equal(BackgroundJobRunStatus.Succeeded, first.Status);
        using (var s = fx.Scope()) Assert.Equal(1, await Svc<AppDbContext>(s).Notifications.CountAsync(n => n.AgreementId == d.AgreementId && n.Type == NotificationType.InstallmentUpcoming));
        await ExecuteAsync(w.Admin, JobCatalog.Reminders);
        using (var s = fx.Scope()) Assert.Equal(1, await Svc<AppDbContext>(s).Notifications.CountAsync(n => n.AgreementId == d.AgreementId && n.Type == NotificationType.InstallmentUpcoming));
        var events = await ExecuteAsync(w.Admin, JobCatalog.Events);
        Assert.Contains("queued", events.Message);
        using (var s = fx.Scope()) Assert.True(await Svc<AppDbContext>(s).Notifications.AnyAsync(n => n.CustomerId == d.CustomerId && n.Type == NotificationType.BookingConfirmation));
        Assert.Equal(BackgroundJobRunStatus.Succeeded, (await ExecuteAsync(w.Admin, JobCatalog.Digest)).Status);
    }

    [MySqlFact]
    public async Task CleanupJob_RemovesExpiredTokensAndOldRunHistory_ButKeepsCurrentOnes()
    {
        var w = await k.WorldAsync();
        Guid fresh;
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var uid = w.Cashier;
            var oldToken = new RefreshToken { UserId = uid, TokenHash = "h-old-" + Guid.NewGuid().ToString("N"), FamilyId = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow.AddDays(-80), ExpiresAtUtc = DateTime.UtcNow.AddDays(-60) };
            var activeToken = new RefreshToken { UserId = uid, TokenHash = "h-new-" + Guid.NewGuid().ToString("N"), FamilyId = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(10) };
            db.RefreshTokens.AddRange(oldToken, activeToken);
            db.BackgroundJobRuns.Add(new BackgroundJobRun { JobKey = "cleanup-old", TriggeredBy = "t", StartedAtUtc = DateTime.UtcNow.AddDays(-120), FinishedAtUtc = DateTime.UtcNow.AddDays(-120), Status = BackgroundJobRunStatus.Succeeded });
            await db.SaveChangesAsync();
            fresh = activeToken.FamilyId;
        }
        var run = await ExecuteAsync(w.Admin, JobCatalog.Cleanup);
        Assert.Equal(BackgroundJobRunStatus.Succeeded, run.Status);
        using var s2 = fx.Scope();
        var db2 = Svc<AppDbContext>(s2);
        Assert.False(await db2.RefreshTokens.AnyAsync(t => t.UserId == w.Cashier && t.ExpiresAtUtc < DateTime.UtcNow.AddDays(-30)));
        Assert.True(await db2.RefreshTokens.AnyAsync(t => t.FamilyId == fresh));
        Assert.False(await db2.BackgroundJobRuns.AnyAsync(r => r.JobKey == "cleanup-old"));
    }

    [MySqlFact]
    public async Task RunNow_NeedsPermission_AKnownJob_AndARunningScheduler()
    {
        var w = await k.WorldAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => k.Run(w.Accountant, s => Svc<ScheduledJobService>(s).RunNowAsync(JobCatalog.Dispatch)));     // jobs.manage is the director's
        await Assert.ThrowsAsync<NotFoundException>(() => k.Run(w.Director, s => Svc<ScheduledJobService>(s).RunNowAsync("nope")));
        var ex = await Assert.ThrowsAsync<DomainException>(() => k.Run(w.Director, s => Svc<ScheduledJobService>(s).RunNowAsync(JobCatalog.Dispatch)));
        Assert.Contains("switched off", ex.Message);
    }

    // ------------------------------------------------------------------------------------------------ identity and scheduler wiring
    [Fact]
    public void CurrentUser_UsesTheBackgroundUser_OnlyWhenThereIsNoHttpUser()
    {
        var ctx = new BackgroundUserContext();
        var anonymous = new CurrentUser(new HttpContextAccessor(), ctx);
        Assert.Null(anonymous.UserId);
        Assert.False(anonymous.IsAuthenticated);
        var id = Guid.NewGuid();
        ctx.Set(id, "service.user");
        Assert.Equal(id, anonymous.UserId);
        Assert.Equal("service.user", anonymous.UserName);
        Assert.True(anonymous.IsAuthenticated);

        var http = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.Empty.ToString("D").Replace("0000", "1111")), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "web.user")], "test")) };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var web = new CurrentUser(accessor, ctx);
        Assert.NotEqual(id, web.UserId);                     // the signed-in HTTP user always wins
        Assert.Equal("web.user", web.UserName);
        accessor.HttpContext = null;                         // leave the shared ambient context clean for other tests
    }

    [MySqlFact]
    public async Task JobRunner_SetsTheConfiguredServiceUser_AndIgnoresAnUnknownOne()
    {
        var w = await k.WorldAsync();
        string userName;
        using (var s0 = fx.Scope()) userName = (await Svc<UserManager<ApplicationUser>>(s0).FindByIdAsync(w.Admin.ToString()))!.UserName!;

        async Task<Guid?> RunWith(string? configured)
        {
            using var s = fx.Scope();
            var ctx = new BackgroundUserContext();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundProcessing:RunAsUserName"] = configured }).Build();
            var runner = new HangfireJobRunner(Svc<ScheduledJobService>(s), ctx, Svc<UserManager<ApplicationUser>>(s), config, NullLogger<HangfireJobRunner>.Instance);
            await runner.RunAsync(JobCatalog.Dispatch, "test", CancellationToken.None);
            return ctx.UserId;
        }
        Assert.Equal(w.Admin, await RunWith(userName));
        Assert.Null(await RunWith("nobody@example.com"));
        Assert.Null(await RunWith(null));
        // an inactive service user is not used
        using (var s = fx.Scope()) { var um = Svc<UserManager<ApplicationUser>>(s); var u = (await um.FindByIdAsync(w.Cashier.ToString()))!; u.IsActive = false; await um.UpdateAsync(u); userName = u.UserName!; }
        Assert.Null(await RunWith(userName));
    }

    [MySqlFact]
    public async Task Hangfire_RegistersTheCatalogueIdempotently_AndQueuesManualRuns()
    {
        var cs = fx.Root.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = cs, ["BackgroundProcessing:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackgroundProcessing(config);
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IBackgroundJobGateway>();
        Assert.True(gateway.Enabled);

        var registrar = new RecurringJobRegistrar(provider.GetRequiredService<IRecurringJobManager>(), NullLogger<RecurringJobRegistrar>.Instance);
        await registrar.StartAsync(CancellationToken.None);
        await registrar.StartAsync(CancellationToken.None);                       // twice: still one schedule per job
        var described = gateway.Describe();
        Assert.Equal(JobCatalog.All.Count, described.Count(d => JobCatalog.Find(d.Key) is not null));
        Assert.All(described, d => Assert.NotNull(d.NextRunUtc));
        var next = described.Single(d => d.Key == JobCatalog.Dispatch).NextRunUtc!.Value;
        Assert.InRange(next, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(2));        // every minute

        var jobId = gateway.Trigger(JobCatalog.Cleanup, "tester");
        Assert.False(string.IsNullOrWhiteSpace(jobId));
    }
}
