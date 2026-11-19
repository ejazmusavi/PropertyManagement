using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Identity;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Infrastructure.Persistence.Bootstrap;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

[Collection("db")]
public class DatabaseTests(InfrastructureFixture fx)
{
    private static string Email() => $"u{Guid.NewGuid():N}@example.com";
    private const string GoodPassword = "Str0ng#Passw0rd!";

    private async Task<ApplicationUser> CreateUserAsync(IServiceScope s, string? role = null, bool active = true)
    {
        var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = new ApplicationUser { UserName = Email(), Email = null, FullName = "Test User", IsActive = active, CreatedAtUtc = DateTime.UtcNow };
        u.Email = u.UserName;
        Assert.True((await users.CreateAsync(u, GoodPassword)).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(u, role)).Succeeded);
        return u;
    }

    [MySqlFact]
    public async Task Migrations_CreateAllPhase0Tables()
    {
        var expected = new[] { "Companies", "Users", "Roles", "UserRoles", "Permissions", "RolePermissions", "UserProjects", "AuditLogs", "ApplicationSettings", "RefreshTokens", "__EFMigrationsHistory" };
        await using var conn = new MySqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db", conn);
        cmd.Parameters.AddWithValue("db", fx.DatabaseName);
        var tables = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) tables.Add(r.GetString(0));
        Assert.All(expected, t => Assert.Contains(tables, x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))); // Windows MySQL lower-cases table names
    }

    [MySqlFact]
    public async Task Migrate_IsIdempotent_AndHasNoPendingModelChanges()
    {
        using var s = fx.Scope();
        var init = s.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await init.MigrateAsync();
        await init.MigrateAsync();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(Permissions.All.Count, await db.Permissions.CountAsync());
    }

    [MySqlFact]
    public async Task AdministratorRole_HoldsEveryPermission()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = await db.Roles.SingleAsync(r => r.Name == RoleNames.Administrator);
        var count = await db.RolePermissions.CountAsync(rp => rp.RoleId == role.Id);
        Assert.Equal(Permissions.All.Count, count);
    }

    [MySqlFact]
    public async Task PermissionCode_IsUnique()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Permissions.Add(new Permission { Code = Permissions.UsersView, Name = "dup", Module = "x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task PermissionService_ReflectsRolesAndActiveState()
    {
        using var s = fx.Scope();
        var perms = s.ServiceProvider.GetRequiredService<IPermissionService>();
        var admin = await CreateUserAsync(s, RoleNames.Administrator);
        var plain = await CreateUserAsync(s);
        var inactive = await CreateUserAsync(s, RoleNames.Administrator, active: false);

        Assert.True(await perms.HasPermissionAsync(admin.Id, Permissions.UsersView));
        Assert.Equal(Permissions.All.Count, (await perms.GetPermissionsAsync(admin.Id)).Count);
        Assert.False(await perms.HasPermissionAsync(plain.Id, Permissions.UsersView));
        Assert.False(await perms.HasPermissionAsync(inactive.Id, Permissions.UsersView));
        Assert.False(await perms.HasPermissionAsync(Guid.NewGuid(), Permissions.UsersView));
    }

    [MySqlFact]
    public async Task Identity_EmailMustBeUnique_AndPasswordPolicyEnforced()
    {
        using var s = fx.Scope();
        var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var first = await CreateUserAsync(s);
        var dup = new ApplicationUser { UserName = "other" + Guid.NewGuid().ToString("N"), Email = first.Email, FullName = "Dup" };
        Assert.False((await users.CreateAsync(dup, GoodPassword)).Succeeded);
        var weak = new ApplicationUser { UserName = Email(), FullName = "Weak" };
        weak.Email = weak.UserName;
        Assert.False((await users.CreateAsync(weak, "password")).Succeeded);
    }

    [MySqlFact]
    public async Task IdentityService_LocksOutAfterRepeatedFailures()
    {
        using var s = fx.Scope();
        var identity = s.ServiceProvider.GetRequiredService<IIdentityService>();
        var u = await CreateUserAsync(s);
        for (var i = 0; i < 5; i++) await identity.CheckPasswordAsync(u.Email!, "wrong", default);
        var (status, _) = await identity.CheckPasswordAsync(u.Email!, GoodPassword, default);
        Assert.Equal(CredentialStatus.LockedOut, status);
    }

    [MySqlFact]
    public async Task IdentityService_ValidInactiveAndUnknown()
    {
        using var s = fx.Scope();
        var identity = s.ServiceProvider.GetRequiredService<IIdentityService>();
        var ok = await CreateUserAsync(s);
        var off = await CreateUserAsync(s, active: false);
        Assert.Equal(CredentialStatus.Valid, (await identity.CheckPasswordAsync(ok.Email!, GoodPassword, default)).Status);
        Assert.Equal(CredentialStatus.Inactive, (await identity.CheckPasswordAsync(off.Email!, GoodPassword, default)).Status);
        Assert.Equal(CredentialStatus.Invalid, (await identity.CheckPasswordAsync("nobody@example.com", GoodPassword, default)).Status);
    }

    [MySqlFact]
    public async Task IdentityService_ChangePassword_WorksAndRejectsWrongCurrent()
    {
        using var s = fx.Scope();
        var identity = s.ServiceProvider.GetRequiredService<IIdentityService>();
        var u = await CreateUserAsync(s);
        Assert.NotEmpty(await identity.ChangePasswordAsync(u.Id, "wrong", "N3w#Passw0rd!!", default));
        Assert.Empty(await identity.ChangePasswordAsync(u.Id, GoodPassword, "N3w#Passw0rd!!", default));
        Assert.Equal(CredentialStatus.Valid, (await identity.CheckPasswordAsync(u.Email!, "N3w#Passw0rd!!", default)).Status);
    }

    [MySqlFact]
    public async Task AuditLog_IsAppendOnly_AndStoresValues()
    {
        using var s = fx.Scope();
        var audit = s.ServiceProvider.GetRequiredService<IAuditService>();
        await audit.RecordAsync(new AuditEntry("Test.Action", "Thing", "42", OldValues: new { A = 1 }, NewValues: new { A = 2 }, Reason: "because", UserName: "tester"));
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AuditLogs.OrderByDescending(a => a.Id).FirstAsync(a => a.Action == "Test.Action");
        Assert.Equal("""{"A":1}""", row.OldValues);
        Assert.Equal("""{"A":2}""", row.NewValues);
        Assert.Equal("because", row.Reason);

        row.Reason = "tampered";
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var again = await db.AuditLogs.FirstAsync(a => a.Id == row.Id);
        db.AuditLogs.Remove(again);
        await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task AuditFields_AreStampedOnCreateAndUpdate()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = new ApplicationSetting { Key = "k" + Guid.NewGuid().ToString("N"), Value = "1" };
        db.ApplicationSettings.Add(setting);
        await db.SaveChangesAsync();
        Assert.NotEqual(default, setting.CreatedAtUtc);
        Assert.Equal("system", setting.CreatedBy);
        Assert.Null(setting.ModifiedAtUtc);

        setting.Value = "2";
        await db.SaveChangesAsync();
        Assert.NotNull(setting.ModifiedAtUtc);
        Assert.Equal("system", setting.ModifiedBy);
    }

    [MySqlFact]
    public async Task OptimisticConcurrency_RejectsStaleCompanyUpdate()
    {
        using var s1 = fx.Scope();
        using var s2 = fx.Scope();
        var seed = s1.ServiceProvider.GetRequiredService<AppDbContext>();
        seed.Companies.Add(new Company("Concurrency Co"));
        await seed.SaveChangesAsync();
        var id = (await seed.Companies.OrderByDescending(c => c.Id).FirstAsync()).Id;

        var a = await s1.ServiceProvider.GetRequiredService<AppDbContext>().Companies.FindAsync(id);
        var dbB = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        var b = await dbB.Companies.FindAsync(id);
        a!.Rename("First");
        await s1.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        b!.Rename("Second");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task UserProject_IsUniquePerUserAndProject()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await CreateUserAsync(s);
        db.UserProjects.Add(new UserProject { UserId = u.Id, ProjectId = 7 });
        await db.SaveChangesAsync();
        db.UserProjects.Add(new UserProject { UserId = u.Id, ProjectId = 7 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task UserQuery_PagesSearchesAndSorts()
    {
        using var s = fx.Scope();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        for (var i = 0; i < 5; i++)
        {
            var u = new ApplicationUser { UserName = $"{tag}{i}@example.com", Email = $"{tag}{i}@example.com", FullName = $"Zed {tag} {i}", CreatedAtUtc = DateTime.UtcNow };
            Assert.True((await users.CreateAsync(u, GoodPassword)).Succeeded);
        }
        var q = s.ServiceProvider.GetRequiredService<RealEstate.Application.Users.IUserQueryService>();
        var page1 = await q.ListAsync(new PagedRequest { Search = tag, PageSize = 2, Page = 1 });
        var page3 = await q.ListAsync(new PagedRequest { Search = tag, PageSize = 2, Page = 3 });
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page3.Items);
        Assert.Equal(3, page1.TotalPages);
        var desc = await q.ListAsync(new PagedRequest { Search = tag, SortBy = "email", Descending = true });
        Assert.Equal($"{tag}4@example.com", desc.Items[0].Email);
    }

    [MySqlFact]
    public async Task AdminBootstrapper_CreatesAdminOnce_AndRejectsWeakPasswords()
    {
        using var s = fx.Scope();
        var boot = s.ServiceProvider.GetRequiredService<AdminBootstrapper>();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var adminRoleId = (await db.Roles.SingleAsync(r => r.Name == RoleNames.Administrator)).Id;
        // Other tests may already have created admins in this database; only the first call can succeed.
        var hadAdmin = await db.UserRoles.AnyAsync(ur => ur.RoleId == adminRoleId);

        var weak = await boot.CreateInitialAdministratorAsync(Email(), "Weak", "password", "Co");
        Assert.False(weak.Succeeded);

        var result = await boot.CreateInitialAdministratorAsync(Email(), "Admin", GoodPassword, "Co");
        Assert.Equal(!hadAdmin, result.Succeeded);
        var second = await boot.CreateInitialAdministratorAsync(Email(), "Admin 2", GoodPassword, "Co");
        Assert.False(second.Succeeded);
        Assert.Equal("bootstrap.exists", second.Error!.Code);
    }
}

[Collection("db")]
public class RefreshTokenTests(InfrastructureFixture fx)
{
    private async Task<(IServiceScope Scope, Guid UserId)> NewUserAsync()
    {
        var s = fx.Scope();
        var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = new ApplicationUser { UserName = $"{Guid.NewGuid():N}@example.com", FullName = "T", CreatedAtUtc = DateTime.UtcNow };
        u.Email = u.UserName;
        Assert.True((await users.CreateAsync(u, "Str0ng#Passw0rd!")).Succeeded);
        return (s, u.Id);
    }

    [MySqlFact]
    public async Task Rotate_IssuesNewToken_AndStoresOnlyHashes()
    {
        var (s, uid) = await NewUserAsync();
        var svc = s.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var first = await svc.IssueAsync(uid, "1.1.1.1");
        var rotated = await svc.RotateAsync(first.Token, "1.1.1.1");
        Assert.True(rotated.Succeeded);
        Assert.NotEqual(first.Token, rotated.Issued!.Token);

        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(await db.RefreshTokens.Select(t => t.TokenHash).ToListAsync(), h => h == first.Token || h == rotated.Issued.Token);
    }

    [MySqlFact]
    public async Task ReusedToken_RevokesWholeFamily()
    {
        var (s, uid) = await NewUserAsync();
        var svc = s.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var first = await svc.IssueAsync(uid, null);
        var second = (await svc.RotateAsync(first.Token, null)).Issued!;

        Assert.False((await svc.RotateAsync(first.Token, null)).Succeeded);   // replay of rotated token
        Assert.False((await svc.RotateAsync(second.Token, null)).Succeeded);  // descendant now revoked too
    }

    [MySqlFact]
    public async Task UnknownToken_Fails()
    {
        var (s, _) = await NewUserAsync();
        Assert.False((await s.ServiceProvider.GetRequiredService<IRefreshTokenService>().RotateAsync("nope", null)).Succeeded);
    }

    [MySqlFact]
    public async Task ExpiredToken_Fails()
    {
        var (s, uid) = await NewUserAsync();
        var svc = s.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var t = await svc.IssueAsync(uid, null);
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.RefreshTokens.Where(x => x.UserId == uid).ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.False((await svc.RotateAsync(t.Token, null)).Succeeded);
    }

    [MySqlFact]
    public async Task Revoke_ByOtherUser_IsIgnored_ByOwner_Works()
    {
        var (s, uid) = await NewUserAsync();
        var svc = s.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var t = await svc.IssueAsync(uid, null);
        await svc.RevokeAsync(t.Token, Guid.NewGuid());
        var again = await svc.RotateAsync(t.Token, null);
        Assert.True(again.Succeeded);
        await svc.RevokeAsync(again.Issued!.Token, uid);
        Assert.False((await svc.RotateAsync(again.Issued.Token, null)).Succeeded);
    }

    [MySqlFact]
    public async Task ConcurrentRotation_OnlyOneWins()
    {
        var (s, uid) = await NewUserAsync();
        var t = await s.ServiceProvider.GetRequiredService<IRefreshTokenService>().IssueAsync(uid, null);
        var attempts = Enumerable.Range(0, 5).Select(async _ =>
        {
            using var scope = fx.Scope();
            return (await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().RotateAsync(t.Token, null)).Succeeded;
        });
        var results = await Task.WhenAll(attempts);
        Assert.True(results.Count(r => r) <= 1);
    }
}
