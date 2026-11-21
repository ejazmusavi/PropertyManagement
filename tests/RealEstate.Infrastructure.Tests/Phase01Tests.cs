using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RealEstate.Application;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Companies;
using RealEstate.Application.Documents;
using RealEstate.Application.LandManagement;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Real container on a migrated MySQL database where the acting user can be switched per test step.</summary>
public sealed class Phase1Fixture : IAsyncLifetime
{
    private readonly MySqlTestDatabase db = new();
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), "realestate_test_storage_" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Root { get; private set; } = default!;
    public FakeCurrentUser Actor { get; } = new();
    public string StorageRoot => storageRoot;
    public const string Password = "Str0ng#Passw0rd!";

    public async Task InitializeAsync()
    {
        await db.InitializeAsync();
        if (!MySqlTestDatabase.IsAvailable) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.ConnectionString,
            ["Jwt:SigningKey"] = "test-signing-key-test-signing-key-123456",
            ["FileStorage:RootPath"] = storageRoot,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection();
        services.AddApplication();
        services.AddInfrastructure(config);
        services.AddSingleton<ICurrentUser>(Actor);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
        Root = services.BuildServiceProvider();
        using var scope = Root.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Root is not null) await Root.DisposeAsync();
        await db.DisposeAsync();
        if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, true);
    }

    public IServiceScope Scope() => Root.CreateScope();

    /// <summary>Creates a user with the given roles and makes them the acting user.</summary>
    public async Task<Guid> ActAsAsync(params string[] roles)
    {
        var id = await CreateUserAsync(roles);
        Actor.UserId = id;
        return id;
    }

    public async Task<Guid> CreateUserAsync(params string[] roles)
    {
        using var s = Scope();
        var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"u{Guid.NewGuid():N}@example.com";
        var u = new ApplicationUser { UserName = email, Email = email, FullName = "User " + email[..6], IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        Assert.True((await users.CreateAsync(u, Password)).Succeeded);
        if (roles.Length > 0) Assert.True((await users.AddToRolesAsync(u, roles)).Succeeded);
        Actor.UserName = email;
        return u.Id;
    }

    public void Act(Guid userId) { Actor.UserId = userId; }

    private int counter;
    public CreateProjectRequest NewProject(decimal area = 100m, Guid? manager = null) =>
        new($"P{Interlocked.Increment(ref counter)}{Guid.NewGuid():N}"[..14], "Serene Test", null, "Rawalpindi", null, null, null, area, MeasurementUnit.Kanal, manager, null);
}

[CollectionDefinition("phase1")]
public class Phase1Collection : ICollectionFixture<Phase1Fixture>;

[Collection("phase1")]
public class Phase01Tests(Phase1Fixture fx)
{
    private async Task<(Guid Admin, long ProjectId)> ProjectAsync(decimal area = 100m)
    {
        var admin = await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var p = await s.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(fx.NewProject(area));
        return (admin, p.Id);
    }

    private static AdjustLandRequest Adjust(Guid approver, LandAdjustmentType t, decimal area, string? rv = null) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow), t, area, "Survey correction", "Deed 42", approver, rv);

    [MySqlFact]
    public async Task StandardRoles_AreSeeded_WithExpectedPermissions()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.Roles.Select(r => r.Name).ToListAsync();
        foreach (var n in new[] { "Administrator", "Director", "Project Manager", "Sales Manager", "Accountant", "Cashier", "Recovery Officer", "Inventory Manager", "Customer Support" })
            Assert.Contains(n, names);
        var perms = async (string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        Assert.Contains(Permissions.LandApprove, await perms(RoleNames.Director));
        Assert.DoesNotContain(Permissions.UsersManage, await perms(RoleNames.Cashier));
        Assert.Equal(Permissions.All.Count, (await perms(RoleNames.Administrator)).Count);
    }

    [MySqlFact]
    public async Task CreateProject_WritesInitialHistory_Distribution_AndAudit()
    {
        var (_, id) = await ProjectAsync(200m);
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.ProjectAreaHistories.Where(h => h.ProjectId == id).ToListAsync());
        Assert.Single(await db.ProjectStatusHistories.Where(h => h.ProjectId == id).ToListAsync());
        var dist = await db.LandDistributions.Where(d => d.ProjectId == id).ToListAsync();
        Assert.Equal(8, dist.Count);
        Assert.Equal(200m, dist.Single(d => d.Category == LandCategory.Unclassified).Area);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Project.Created" && a.EntityId == id.ToString()));
        var overview = await s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id);
        Assert.True(overview.IsReconciled);
    }

    [MySqlFact]
    public async Task CreateProject_DuplicateCode_IsRejected()
    {
        await fx.ActAsAsync(RoleNames.Administrator);
        var req = fx.NewProject();
        using (var s = fx.Scope()) await s.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(req);
        using var s2 = fx.Scope();
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => s2.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(req with { ProjectCode = req.ProjectCode.ToLowerInvariant() }));
        Assert.Contains("ProjectCode", ex.Errors.Keys);
    }

    [MySqlFact]
    public async Task Acquire_IncreasesArea_WithHistory_AndKeepsDistributionReconciled()
    {
        var (_, id) = await ProjectAsync(100m);
        using var s = fx.Scope();
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        await land.AcquireAsync(id, new AcquireLandRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Seller A", 25.5m, 1000000m, "REG-1", null, null, null));
        var o = await land.GetOverviewAsync(id);
        Assert.Equal(125.5m, o.TotalProjectArea);
        Assert.Equal(25.5m, o.AcquiredArea);
        Assert.Equal(125.5m, o.UnclassifiedArea);
        Assert.True(o.IsReconciled);
        Assert.Contains(o.Parcels, p => p.ParcelType == LandParcelType.Acquired && p.Area == 25.5m);
        var h = await land.GetAreaHistoryAsync(id, new PagedRequest());
        Assert.Equal(2, h.TotalCount);
        Assert.Equal(AreaChangeSource.Acquisition, h.Items[0].Source);
        Assert.Equal(100m, h.Items[0].PreviousArea);
    }

    [MySqlFact]
    public async Task Adjust_Addition_And_Reduction_RecordFullHistory()
    {
        var (admin, id) = await ProjectAsync(100m);
        using var s = fx.Scope();
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        await land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 10m));
        await land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Reduction, 4m));
        var o = await land.GetOverviewAsync(id);
        Assert.Equal(106m, o.TotalProjectArea);
        Assert.Equal(10m, o.AddedArea);
        Assert.Equal(4m, o.ReducedArea);
        Assert.True(o.IsReconciled);
        var last = o.Adjustments[0];
        Assert.Equal(110m, last.PreviousArea);
        Assert.Equal(-4m, last.AdjustmentArea);
        Assert.Equal(106m, last.NewArea);
        Assert.Equal("Deed 42", last.SupportingReference);
        Assert.False(string.IsNullOrEmpty(last.ApprovedByName));
    }

    [MySqlFact]
    public async Task Adjust_Validation_Approver_AndFloor()
    {
        var (admin, id) = await ProjectAsync(100m);
        using var s = fx.Scope();
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        await Assert.ThrowsAsync<RequestValidationException>(() => land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 1m) with { SupportingReference = " " }));
        await Assert.ThrowsAsync<RequestValidationException>(() => land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, -1m)));
        var nobody = await fx.CreateUserAsync(RoleNames.Cashier);
        fx.Act(admin);
        await Assert.ThrowsAsync<RequestValidationException>(() => land.AdjustAsync(id, Adjust(nobody, LandAdjustmentType.Addition, 1m)));
        // Distribute 60 then try to reduce below it.
        await land.SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Residential, 60m)], "Plan", (await land.GetOverviewAsync(id)).DistributionVersion));
        await Assert.ThrowsAsync<RequestValidationException>(() => land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Reduction, 50m)));
        await land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Reduction, 40m));
        Assert.Equal(60m, (await land.GetOverviewAsync(id)).TotalProjectArea);
        Assert.Equal(0m, (await land.GetOverviewAsync(id)).UnclassifiedArea);
    }

    [MySqlFact]
    public async Task Distribution_MustReconcile_AndRecordsHistory()
    {
        var (_, id) = await ProjectAsync(100m);
        using var s = fx.Scope();
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        var version = (await land.GetOverviewAsync(id)).DistributionVersion;
        await Assert.ThrowsAsync<DomainException>(() => land.SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Residential, 80m), new(LandCategory.Roads, 30m)], "Too much", version)));
        var o = await land.SetDistributionAsync(id, new SetLandDistributionRequest(
            [new(LandCategory.Residential, 50m), new(LandCategory.Commercial, 10m), new(LandCategory.Roads, 15m), new(LandCategory.Parks, 5m)], "Master plan", version));
        Assert.NotEqual(version, o.DistributionVersion);
        Assert.Equal(60m, o.SaleableArea);
        Assert.Equal(80m, o.ClassifiedArea);
        Assert.Equal(20m, o.UnclassifiedArea);
        Assert.True(o.IsReconciled);
        var hist = await land.GetDistributionHistoryAsync(id, new PagedRequest { PageSize = 100 });
        Assert.Contains(hist.Items, x => x.Category == LandCategory.Residential && x.NewArea == 50m && x.Reason == "Master plan");
        Assert.Contains(hist.Items, x => x.Category == LandCategory.Unclassified && x.NewArea == 20m);
        await Assert.ThrowsAsync<RequestValidationException>(() => land.SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Unclassified, 5m)], "x", o.DistributionVersion)));
    }

    [MySqlFact]
    public async Task Distribution_StaleVersion_IsRejected_AndNothingIsOverwritten()
    {
        var (admin, id) = await ProjectAsync(100m);
        string stale;
        using (var s = fx.Scope()) stale = (await s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id)).DistributionVersion;

        // User A saves first.
        using (var s = fx.Scope())
            await s.ServiceProvider.GetRequiredService<LandService>().SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Residential, 40m)], "User A", stale));

        // User B submits a form loaded before A's save: rejected, A's value is kept.
        using (var s = fx.Scope())
            await Assert.ThrowsAsync<ConcurrencyException>(() => s.ServiceProvider.GetRequiredService<LandService>()
                .SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Residential, 10m), new(LandCategory.Roads, 5m)], "User B", stale)));

        // An area change also invalidates an open distribution form (the unclassified remainder moved).
        string beforeAdjust;
        using (var s = fx.Scope()) beforeAdjust = (await s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id)).DistributionVersion;
        using (var s = fx.Scope()) await s.ServiceProvider.GetRequiredService<LandService>().AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 10m));
        using (var s = fx.Scope())
            await Assert.ThrowsAsync<ConcurrencyException>(() => s.ServiceProvider.GetRequiredService<LandService>()
                .SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Roads, 5m)], "Stale after adjust", beforeAdjust)));

        using var check = fx.Scope();
        var land = check.ServiceProvider.GetRequiredService<LandService>();
        var o = await land.GetOverviewAsync(id);
        Assert.Equal(40m, o.Distribution.Single(d => d.Category == LandCategory.Residential).Area);
        Assert.Equal(0m, o.Distribution.Single(d => d.Category == LandCategory.Roads).Area);
        Assert.Equal(70m, o.UnclassifiedArea);
        Assert.True(o.IsReconciled);
        var hist = await land.GetDistributionHistoryAsync(id, new PagedRequest { PageSize = 100 });
        Assert.DoesNotContain(hist.Items, h => h.Reason.StartsWith("User B") || h.Reason.StartsWith("Stale"));
    }

    [MySqlFact]
    public async Task ConcurrentDistributionSaves_FromSameVersion_OnlyOneWins()
    {
        var (_, id) = await ProjectAsync(100m);
        string version;
        using (var s = fx.Scope()) version = (await s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id)).DistributionVersion;
        var tasks = new[] { 30m, 50m }.Select(area => Task.Run(async () =>
        {
            using var s = fx.Scope();
            try
            {
                await s.ServiceProvider.GetRequiredService<LandService>().SetDistributionAsync(id, new SetLandDistributionRequest([new(LandCategory.Residential, area)], $"Concurrent {area}", version));
                return true;
            }
            catch (ConcurrencyException) { return false; }
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r);
        using var check = fx.Scope();
        var o = await check.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id);
        var residential = o.Distribution.Single(d => d.Category == LandCategory.Residential).Area;
        Assert.Contains(residential, new[] { 30m, 50m });
        Assert.Equal(100m - residential, o.UnclassifiedArea);
        Assert.True(o.IsReconciled);
    }

    [MySqlFact]
    public async Task ConcurrentAdjustments_NeverLoseAnUpdate()
    {
        var (admin, id) = await ProjectAsync(100m);
        string rv;
        using (var s = fx.Scope()) rv = (await s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id)).RowVersion;
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            using var s = fx.Scope();
            try { await s.ServiceProvider.GetRequiredService<LandService>().AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 10m, rv)); return true; }
            catch (ConcurrencyException) { return false; }
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Contains(true, results);
        Assert.Contains(false, results); // both started from the same version, so exactly one must be rejected
        using var check = fx.Scope();
        var o = await check.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id);
        Assert.Equal(110m, o.TotalProjectArea);
        Assert.Single(o.Adjustments);
        Assert.True(o.IsReconciled);
    }

    [MySqlFact]
    public async Task ProjectAccess_RestrictsUnassignedUsers_AndListsOnlyAssigned()
    {
        var (admin, id) = await ProjectAsync();
        var cashier = await fx.CreateUserAsync(RoleNames.Cashier);
        using (var s = fx.Scope())
        {
            fx.Act(cashier);
            var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => projects.GetAsync(id));
            Assert.DoesNotContain((await projects.ListAsync(new ProjectListRequest { PageSize = 100 })).Items, p => p.Id == id);
            await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<LandService>().GetOverviewAsync(id)); // cashier has no land.view either
        }
        fx.Act(admin);
        using (var s = fx.Scope()) await s.ServiceProvider.GetRequiredService<ProjectAccessAdminService>().AssignAsync(id, cashier);
        fx.Act(cashier);
        using (var s = fx.Scope())
        {
            var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
            Assert.Equal(id, (await projects.GetAsync(id)).Id);
            Assert.Contains((await projects.ListAsync(new ProjectListRequest { PageSize = 100 })).Items, p => p.Id == id);
        }
        fx.Act(admin);
        using (var s = fx.Scope()) await s.ServiceProvider.GetRequiredService<ProjectAccessAdminService>().RevokeAsync(id, cashier, "Moved");
        fx.Act(cashier);
        using var last = fx.Scope();
        await Assert.ThrowsAsync<ForbiddenException>(() => last.ServiceProvider.GetRequiredService<ProjectService>().GetAsync(id));
    }

    [MySqlFact]
    public async Task ProjectManager_ReceivesAccess_AndHistoryIsKept()
    {
        var admin = await fx.ActAsAsync(RoleNames.Administrator);
        var m1 = await fx.CreateUserAsync(RoleNames.ProjectManager);
        var m2 = await fx.CreateUserAsync(RoleNames.ProjectManager);
        fx.Act(admin);
        long id;
        using (var s = fx.Scope())
        {
            var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
            var p = await projects.CreateAsync(fx.NewProject(manager: m1));
            id = p.Id;
            var upd = await projects.UpdateAsync(id, new UpdateProjectRequest(p.ProjectName, p.Description, p.City, p.Address, null, null, m2, null, p.RowVersion));
            Assert.Equal(m2, upd.ProjectManagerId);
            Assert.Equal(2, upd.ManagerHistory.Count);
            Assert.Single(upd.ManagerHistory, h => h.AssignedToUtc == null);
        }
        fx.Act(m1);
        using var s1 = fx.Scope();
        Assert.Equal(id, (await s1.ServiceProvider.GetRequiredService<ProjectService>().GetAsync(id)).Id); // retains access until revoked
    }

    [MySqlFact]
    public async Task StatusChange_RequiresReason_ValidTransition_AndBlocksLandWhenCompleted()
    {
        var (admin, id) = await ProjectAsync();
        using var s = fx.Scope();
        var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
        var d = await projects.GetAsync(id);
        await Assert.ThrowsAsync<RequestValidationException>(() => projects.ChangeStatusAsync(id, new ChangeProjectStatusRequest(ProjectStatus.Active, "", d.RowVersion)));
        await Assert.ThrowsAsync<DomainException>(() => projects.ChangeStatusAsync(id, new ChangeProjectStatusRequest(ProjectStatus.Completed, "skip", d.RowVersion)));
        d = await projects.ChangeStatusAsync(id, new ChangeProjectStatusRequest(ProjectStatus.Active, "Launch", d.RowVersion));
        d = await projects.ChangeStatusAsync(id, new ChangeProjectStatusRequest(ProjectStatus.Completed, "Done", d.RowVersion));
        Assert.Equal(3, d.StatusHistory.Count);
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        await Assert.ThrowsAsync<DomainException>(() => land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 1m)));
        await Assert.ThrowsAsync<DomainException>(() => land.AcquireAsync(id, new AcquireLandRequest(DateOnly.FromDateTime(DateTime.UtcNow), "S", 1m, null, null, null, null, null)));
    }

    [MySqlFact]
    public async Task StaleRowVersion_OnProjectUpdate_IsRejected()
    {
        var (_, id) = await ProjectAsync();
        using var s = fx.Scope();
        var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
        var d = await projects.GetAsync(id);
        await projects.UpdateAsync(id, new UpdateProjectRequest("Renamed", null, d.City, null, null, null, null, null, d.RowVersion));
        using var s2 = fx.Scope();
        await Assert.ThrowsAsync<ConcurrencyException>(() => s2.ServiceProvider.GetRequiredService<ProjectService>()
            .UpdateAsync(id, new UpdateProjectRequest("Stale", null, d.City, null, null, null, null, null, d.RowVersion)));
    }

    [MySqlFact]
    public async Task Documents_AreStoredOutsideWebRoot_Downloadable_AndSoftDeleted()
    {
        var (_, id) = await ProjectAsync();
        using var s = fx.Scope();
        var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 test");
        var doc = await projects.UploadDocumentAsync(id, ProjectDocumentType.Legal, "NOC", null, new FileUpload("noc.pdf", "application/pdf", bytes.Length, new MemoryStream(bytes)));
        Assert.True(Directory.EnumerateFiles(fx.StorageRoot, "*.pdf", SearchOption.AllDirectories).Any());
        var f = await projects.DownloadDocumentAsync(id, doc.Id);
        using var ms = new MemoryStream(); await f.Content.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
        await Assert.ThrowsAsync<DomainException>(() => projects.UploadDocumentAsync(id, ProjectDocumentType.Other, null, null, new FileUpload("evil.exe", "application/octet-stream", 3, new MemoryStream([1, 2, 3]))));
        await Assert.ThrowsAsync<DomainException>(() => projects.DeleteDocumentAsync(id, doc.Id, " "));
        await projects.DeleteDocumentAsync(id, doc.Id, "Superseded");
        Assert.Empty(await projects.ListDocumentsAsync(id));
        await Assert.ThrowsAsync<NotFoundException>(() => projects.DownloadDocumentAsync(id, doc.Id));
    }

    [MySqlFact]
    public async Task LandDocument_LinkedToAdjustment_IsStored()
    {
        var (admin, id) = await ProjectAsync();
        using var s = fx.Scope();
        var land = s.ServiceProvider.GetRequiredService<LandService>();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var adj = await land.AdjustAsync(id, Adjust(admin, LandAdjustmentType.Addition, 5m),
            [new LandDocumentUpload(new FileUpload("proof.pdf", "application/pdf", bytes.Length, new MemoryStream(bytes)), LandDocumentType.AdjustmentSupport, "Proof")]);
        var docs = await land.ListDocumentsAsync(id);
        Assert.Single(docs, d => d.AdjustmentId == adj.Id && d.Title == "Proof");
    }

    [MySqlFact]
    public async Task RoleManagement_ProtectsSystemRoles_AndSupportsCustomRoles()
    {
        await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var roles = s.ServiceProvider.GetRequiredService<RoleService>();
        var all = await roles.ListAsync();
        var admin = all.Single(r => r.Name == "Administrator");
        var cashier = all.Single(r => r.Name == "Cashier");
        await Assert.ThrowsAsync<DomainException>(() => roles.DeleteAsync(cashier.Id));
        await Assert.ThrowsAsync<DomainException>(() => roles.UpdateAsync(cashier.Id, new UpdateRoleRequest("Teller", null, [])));
        await Assert.ThrowsAsync<DomainException>(() => roles.UpdateAsync(admin.Id, new UpdateRoleRequest("Administrator", null, [Permissions.UsersView])));

        var created = await roles.CreateAsync(new CreateRoleRequest("Auditor " + Guid.NewGuid().ToString("N")[..6], "Read only", [Permissions.ProjectsView, Permissions.LandView]));
        Assert.Equal(2, created.PermissionCodes.Count);
        await Assert.ThrowsAsync<RequestValidationException>(() => roles.CreateAsync(new CreateRoleRequest("Bad", null, ["nope.nothing"])));
        var updated = await roles.UpdateAsync(created.Id, new UpdateRoleRequest(created.Name, "Changed", [Permissions.ProjectsView]));
        Assert.Single(updated.PermissionCodes);

        await roles.SaveMatrixAsync(new SavePermissionMatrixRequest(new Dictionary<Guid, IReadOnlyList<string>> { [created.Id] = [Permissions.ProjectsView, Permissions.CompanyView] }));
        Assert.Equal(2, (await roles.GetAsync(created.Id)).PermissionCodes.Count);
        await roles.DeleteAsync(created.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => roles.GetAsync(created.Id));
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Role.Created"));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Role.PermissionsChanged"));
    }

    [MySqlFact]
    public async Task UserAdministration_EnforcesPrivilegeRules()
    {
        var admin = await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var users = s.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var email = $"n{Guid.NewGuid():N}@example.com";
        var created = await users.CreateAsync(new CreateUserRequest(email, "New Person", null, Phase1Fixture.Password, true, [RoleNames.ProjectManager]));
        Assert.Equal([RoleNames.ProjectManager], created.Roles);
        await Assert.ThrowsAsync<RequestValidationException>(() => users.CreateAsync(new CreateUserRequest(email, "Dup", null, Phase1Fixture.Password, true, [])));
        await Assert.ThrowsAsync<RequestValidationException>(() => users.CreateAsync(new CreateUserRequest("w@example.com", "Weak", null, "short", true, [])));
        await Assert.ThrowsAsync<RequestValidationException>(() => users.CreateAsync(new CreateUserRequest($"r{Guid.NewGuid():N}@example.com", "Role", null, Phase1Fixture.Password, true, ["Ghost"])));

        var updated = await users.UpdateAsync(created.Id, new UpdateUserRequest("Renamed", null, true, [RoleNames.Director, RoleNames.Cashier]));
        Assert.Equal("Renamed", updated.FullName);
        Assert.Equal(2, updated.Roles.Count);
        await Assert.ThrowsAsync<DomainException>(() => users.UpdateAsync(admin, new UpdateUserRequest("Me", null, false, [RoleNames.Administrator]))); // self-deactivation / last admin

        // A user manager who is not an Administrator cannot grant the Administrator role.
        var hr = await fx.CreateUserAsync();
        using (var s2 = fx.Scope())
        {
            var role = s2.ServiceProvider.GetRequiredService<RoleService>();
            fx.Act(admin);
            var r = await role.CreateAsync(new CreateRoleRequest("UserManagers " + Guid.NewGuid().ToString("N")[..6], null, [Permissions.UsersView, Permissions.UsersManage, Permissions.RolesView]));
            using var s3 = fx.Scope();
            var store = s3.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await store.AddToRoleAsync((await store.FindByIdAsync(hr.ToString()))!, r.Name);
        }
        fx.Act(hr);
        using var s4 = fx.Scope();
        await Assert.ThrowsAsync<ForbiddenException>(() => s4.ServiceProvider.GetRequiredService<UserAdministrationService>()
            .UpdateAsync(created.Id, new UpdateUserRequest("Renamed", null, true, [RoleNames.Administrator])));
    }

    [MySqlFact]
    public async Task Company_UpdateRoundTrips_AndRejectsStaleVersion()
    {
        await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<CompanyService>();
        var c = await svc.GetAsync();
        UpdateCompanyRequest Req(string name, string rv) => new(name, "Legal", "NTN-1", "info@example.com", "0300", null, "Addr", "Rawalpindi", "Pakistan", "46000",
            "PKR", "Rs", 2, RoundingMode.Bankers, 7, "Asia/Karachi", 15, "pdf,png,docx", rv);
        var saved = await svc.UpdateAsync(Req("Serene Developments", c.RowVersion));
        Assert.Equal(RoundingMode.Bankers, saved.RoundingMode);
        Assert.Equal("pdf,png,docx", saved.AllowedFileExtensions);
        await Assert.ThrowsAsync<ConcurrencyException>(() => svc.UpdateAsync(Req("Stale", c.RowVersion)));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.UpdateAsync(Req("X", saved.RowVersion) with { TimeZoneId = "Mars/Base" }));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.UpdateAsync(Req("X", saved.RowVersion) with { FiscalYearStartMonth = 13 }));
    }

    [MySqlFact]
    public async Task Users_WithoutPermission_AreForbidden_AtServiceLevel()
    {
        await fx.ActAsAsync(RoleNames.Cashier);
        using var s = fx.Scope();
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<CompanyService>().GetAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(fx.NewProject()));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<RoleService>().ListAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<UserAdministrationService>().CreateAsync(new CreateUserRequest("a@example.com", "A", null, Phase1Fixture.Password, true, [])));
    }

    [MySqlFact]
    public async Task Migrations_CreatePhase1Tables_AndConstraints()
    {
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var (admin, id) = await ProjectAsync();
        // DB constraint: negative distribution areas are rejected even if application checks are bypassed.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE LandDistributions SET Area = -1 WHERE ProjectId = {id} AND Category = 'Roads'"));
        // Unique constraint on (project, category).
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"INSERT INTO LandDistributions (ProjectId, Category, Area, CreatedAtUtc) VALUES ({id}, 'Roads', 1, NOW())"));
        _ = admin;
    }
}
