using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

[Collection("phase1")]
public class Phase02Tests(Phase1Fixture fx)
{
    private int counter;

    private async Task<(Guid Admin, long ProjectId)> ProjectAsync()
    {
        var admin = await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var p = await s.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(fx.NewProject());
        return (admin, p.Id);
    }

    private static async Task<long> TypeIdAsync(IServiceScope s, string code)
    {
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PropertyTypes.Where(t => t.Code == code).Select(t => t.Id).FirstAsync();
    }

    private async Task<PropertyDetailDto> NewPropertyAsync(long projectId, string? code = null, string typeCode = "RES-PLOT", decimal size = 5m, int? floor = null, long? block = null)
    {
        using var s = fx.Scope();
        var type = await TypeIdAsync(s, typeCode);
        var n = Interlocked.Increment(ref counter);
        return await s.ServiceProvider.GetRequiredService<PropertyService>().CreateAsync(new CreatePropertyRequest(projectId, code ?? $"PR-{Guid.NewGuid():N}"[..14], n.ToString(), block, null, null, type,
            size, MeasurementUnit.Marla, "Near park", floor, true, true, false, DevelopmentStatus.Developed, null,
            [new DimensionDto("Length", 50, "ft"), new DimensionDto("Width", 30, "ft")], [new FeatureDto("Gas", null)]));
    }

    private static ReservePropertyRequest Reserve(Guid approver, string rv, string who = "Ali Khan", int days = 7) =>
        new(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today).AddDays(days), 5000m, null, who, "0300-1234567", approver, rv);

    [MySqlFact]
    public async Task Catalog_IsSeeded_WithAllRequiredPropertyTypes()
    {
        await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var types = await s.ServiceProvider.GetRequiredService<PropertySetupService>().ListTypesAsync();
        foreach (var name in new[] { "Residential Plot", "Commercial Plot", "House", "Apartment", "Shop", "Office" })
            Assert.Contains(types, t => t.Name == name && t.IsSystem);
        Assert.True(types.Single(t => t.Code == "APARTMENT").RequiresFloor);
        var cats = await s.ServiceProvider.GetRequiredService<PropertySetupService>().ListCategoriesAsync();
        Assert.Contains(cats, c => c.Code == "RESIDENTIAL");
        Assert.Contains(cats, c => c.Code == "COMMERCIAL");
    }

    [MySqlFact]
    public async Task CustomCategoryAndType_CanBeCreated_AndUsedForProperties()
    {
        var (_, projectId) = await ProjectAsync();
        long typeId;
        using (var s = fx.Scope())
        {
            var setup = s.ServiceProvider.GetRequiredService<PropertySetupService>();
            var code = "IND" + Guid.NewGuid().ToString("N")[..6];
            var cat = await setup.CreateCategoryAsync(new SaveCategoryRequest(code, "Industrial " + code, null, true));
            var type = await setup.CreateTypeAsync(new SavePropertyTypeRequest("WH" + code, "Warehouse " + code, cat.Id, PropertyForm.Other, false, true));
            typeId = type.Id;
            await Assert.ThrowsAsync<RequestValidationException>(() => setup.CreateCategoryAsync(new SaveCategoryRequest(code, "Another", null, true)));
            await Assert.ThrowsAsync<RequestValidationException>(() => setup.CreateTypeAsync(new SavePropertyTypeRequest("X" + code, type.Name, cat.Id, PropertyForm.Other, false, true)));
        }
        using var s2 = fx.Scope();
        var p = await s2.ServiceProvider.GetRequiredService<PropertyService>().CreateAsync(new CreatePropertyRequest(projectId, "WH-" + Guid.NewGuid().ToString("N")[..8], "1", null, null, null, typeId,
            100m, MeasurementUnit.SquareFeet, null, null, false, false, false, DevelopmentStatus.Undeveloped, null, null, null));
        Assert.Equal(PropertyStatus.Available, p.Status);
    }

    [MySqlFact]
    public async Task SystemTypes_CannotBeDeactivatedOrReclassified()
    {
        await fx.ActAsAsync(RoleNames.Administrator);
        using var s = fx.Scope();
        var setup = s.ServiceProvider.GetRequiredService<PropertySetupService>();
        var shop = (await setup.ListTypesAsync()).Single(t => t.Code == "SHOP");
        await Assert.ThrowsAsync<DomainException>(() => setup.UpdateTypeAsync(shop.Id, new SavePropertyTypeRequest("SHOP", "Shop", shop.CategoryId, shop.Form, shop.RequiresFloor, false)));
    }

    [MySqlFact]
    public async Task Property_CreatesWithDimensionsFeaturesAndInitialHistory_AndAudit()
    {
        var (_, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        Assert.Equal(PropertyStatus.Available, p.Status);
        Assert.Equal(2, p.Dimensions.Count);
        Assert.Single(p.Features);
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var history = await db.PropertyStatusHistories.Where(h => h.PropertyId == p.Id).ToListAsync();
        Assert.Single(history);
        Assert.Null(history[0].FromStatus);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Property.Created" && a.EntityId == p.Id.ToString()));
    }

    [MySqlFact]
    public async Task PropertyCode_IsUniqueWithinProject_ButReusableAcrossProjects()
    {
        var (_, p1) = await ProjectAsync();
        var (_, p2) = await ProjectAsync();
        await NewPropertyAsync(p1, "DUP-1");
        await Assert.ThrowsAsync<RequestValidationException>(() => NewPropertyAsync(p1, "dup-1"));
        var other = await NewPropertyAsync(p2, "DUP-1");
        Assert.Equal(p2, other.ProjectId);
    }

    [MySqlFact]
    public async Task Property_RequiresValidProject_AndZonesOfThatProject()
    {
        var (_, p1) = await ProjectAsync();
        var (_, p2) = await ProjectAsync();
        long foreignBlock;
        using (var s = fx.Scope())
            foreignBlock = (await s.ServiceProvider.GetRequiredService<PropertySetupService>().CreateZoneAsync(ZoneKind.Block, p2, new SaveZoneRequest("B1", "Block A", true))).Id;
        await Assert.ThrowsAsync<RequestValidationException>(() => NewPropertyAsync(p1, block: foreignBlock));
        await Assert.ThrowsAsync<NotFoundException>(() => NewPropertyAsync(999_999_999));
    }

    [MySqlFact]
    public async Task Apartment_RequiresFloorNumber()
    {
        var (_, projectId) = await ProjectAsync();
        await Assert.ThrowsAsync<RequestValidationException>(() => NewPropertyAsync(projectId, typeCode: "APARTMENT"));
        var ok = await NewPropertyAsync(projectId, typeCode: "APARTMENT", floor: 3);
        Assert.Equal(3, ok.FloorNumber);
    }

    [MySqlFact]
    public async Task PropertyNumber_IsUniqueWithinSameBlockSectorStreet()
    {
        var (_, projectId) = await ProjectAsync();
        long type;
        using (var s = fx.Scope()) type = await TypeIdAsync(s, "HOUSE");
        using var s2 = fx.Scope();
        var svc = s2.ServiceProvider.GetRequiredService<PropertyService>();
        CreatePropertyRequest Req(string code) => new(projectId, code, "77", null, null, null, type, 5, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Undeveloped, null, null, null);
        await svc.CreateAsync(Req("N-" + Guid.NewGuid().ToString("N")[..8]));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateAsync(Req("N-" + Guid.NewGuid().ToString("N")[..8])));
    }

    [MySqlFact]
    public async Task Update_ChangesDetails_ReplacesDimensions_AndDetectsStaleEdit()
    {
        var (_, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        UpdatePropertyRequest Upd(string rv, decimal size) => new(p.PropertyNumber, null, null, null, p.PropertyTypeId, size, MeasurementUnit.Kanal, "New place", null, false, false, true,
            DevelopmentStatus.UnderDevelopment, "note", [new DimensionDto("Depth", 12, "ft")], [], rv);
        var updated = await svc.UpdateAsync(p.Id, Upd(p.RowVersion, 7.5m));
        Assert.Equal(7.5m, updated.Size);
        Assert.Equal("Depth", Assert.Single(updated.Dimensions).Name);
        Assert.Empty(updated.Features);
        await Assert.ThrowsAsync<ConcurrencyException>(() => svc.UpdateAsync(p.Id, Upd(p.RowVersion, 9m)));
        using var s2 = fx.Scope();
        var db = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Property.Updated" && a.EntityId == p.Id.ToString()));
    }

    [MySqlFact]
    public async Task Reserve_Release_RoundTrip_KeepsHistory()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        var reserved = await svc.ReserveAsync(p.Id, Reserve(admin, p.RowVersion));
        Assert.Equal(PropertyStatus.Reserved, reserved.Status);
        Assert.NotNull(reserved.ActiveReservation);
        Assert.Equal(5000m, reserved.ActiveReservation!.Amount);
        Assert.Equal(admin, reserved.ActiveReservation.ApprovedByUserId);

        var released = await svc.ReleaseReservationAsync(p.Id, new ReleaseReservationRequest("Customer withdrew", reserved.RowVersion));
        Assert.Equal(PropertyStatus.Available, released.Status);
        Assert.Null(released.ActiveReservation);

        var again = await svc.ReserveAsync(p.Id, Reserve(admin, released.RowVersion, "Second Customer"));
        Assert.Equal("Second Customer", again.ActiveReservation!.ReservedForName);

        var reservations = await svc.ListReservationsAsync(p.Id);
        Assert.Equal(2, reservations.Count);
        Assert.Contains(reservations, r => r.Status == ReservationStatus.Released && r.CloseReason == "Customer withdrew");
        var history = await svc.ListHistoryAsync(new InventoryHistoryRequest { PropertyId = p.Id, PageSize = 50 });
        Assert.Equal(4, history.TotalCount); // created, reserved, released, reserved
        Assert.All(history.Items, h => Assert.False(string.IsNullOrWhiteSpace(h.Reason)));
    }

    [MySqlFact]
    public async Task Reserve_NonAvailableProperty_IsRejected()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        var booked = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "Booked via sales", p.RowVersion));
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ReserveAsync(p.Id, Reserve(admin, booked.RowVersion)));
        Assert.Contains("Booked", ex.Message);
    }

    [MySqlFact]
    public async Task Reserve_Validation_ApproverDatesAndPermission()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        var cashier = await fx.CreateUserAsync(RoleNames.Cashier); // holds no reservation permission
        fx.Act(admin);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.ReserveAsync(p.Id, Reserve(cashier, p.RowVersion)));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.ReserveAsync(p.Id, Reserve(Guid.NewGuid(), p.RowVersion)));
        var past = new ReservePropertyRequest(DateOnly.FromDateTime(DateTime.Today).AddDays(-10), DateOnly.FromDateTime(DateTime.Today).AddDays(-1), 0, null, "X", null, admin, null);
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.ReserveAsync(p.Id, past));
        Assert.Equal(PropertyStatus.Available, (await svc.GetAsync(p.Id)).Status);
    }

    [MySqlFact]
    public async Task DoubleBooking_ConcurrentReservations_OnlyOneSucceeds()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        var tasks = Enumerable.Range(0, 6).Select(async i =>
        {
            using var s = fx.Scope();
            try { await s.ServiceProvider.GetRequiredService<PropertyService>().ReserveAsync(p.Id, Reserve(admin, p.RowVersion, "Customer " + i)); return true; }
            catch (Exception ex) when (ex is DomainException) { return false; }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
        using var s2 = fx.Scope();
        var db = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PropertyReservations.CountAsync(r => r.PropertyId == p.Id && r.Status == ReservationStatus.Active));
        Assert.Equal(PropertyStatus.Reserved, (await db.Properties.AsNoTracking().FirstAsync(x => x.Id == p.Id)).Status);
    }

    [MySqlFact]
    public async Task UniqueIndex_RejectsTwoActiveReservations_AtDatabaseLevel()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var d = DateOnly.FromDateTime(DateTime.Today);
        db.PropertyReservations.Add(RealEstate.Domain.Entities.PropertyReservation.Create(p.Id, d, d.AddDays(3), 0, "A", null, admin, "x"));
        db.PropertyReservations.Add(RealEstate.Domain.Entities.PropertyReservation.Create(p.Id, d, d.AddDays(3), 0, "B", null, admin, "x"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task ReservationExpiry_ReturnsPropertyToAvailable_WithHistoryAndAudit()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using (var s = fx.Scope())
            await s.ServiceProvider.GetRequiredService<PropertyService>().ReserveAsync(p.Id, Reserve(admin, p.RowVersion, days: 0));
        // Not yet expired: expiry date is today.
        using (var s = fx.Scope())
            Assert.Equal(PropertyStatus.Reserved, (await s.ServiceProvider.GetRequiredService<PropertyService>().GetAsync(p.Id)).Status);
        // Move the expiry into the past.
        using (var s = fx.Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PropertyReservations.Where(r => r.PropertyId == p.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.ExpiryDate, DateOnly.FromDateTime(DateTime.Today).AddDays(-2))
                .SetProperty(r => r.ReservationDate, DateOnly.FromDateTime(DateTime.Today).AddDays(-9)));
        }
        using var s2 = fx.Scope();
        var svc = s2.ServiceProvider.GetRequiredService<PropertyService>();
        var available = await svc.ListAvailableAsync(new PropertyListRequest { ProjectId = projectId, PageSize = 50 });
        Assert.Contains(available.Items, x => x.Id == p.Id);
        var detail = await svc.GetAsync(p.Id);
        Assert.Equal(PropertyStatus.Available, detail.Status);
        var res = (await svc.ListReservationsAsync(p.Id)).Single();
        Assert.Equal(ReservationStatus.Expired, res.Status);
        var history = await svc.ListHistoryAsync(new InventoryHistoryRequest { PropertyId = p.Id, PageSize = 50 });
        Assert.Contains(history.Items, h => h.ToStatus == PropertyStatus.Available && h.Reason.Contains("expired") && h.ChangedBy == "system");
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db2.AuditLogs.AnyAsync(a => a.Action == "Property.ReservationExpired" && a.EntityId == p.Id.ToString()));
        // Reserving again after expiry works.
        var again = await svc.ReserveAsync(p.Id, Reserve(admin, detail.RowVersion, "New customer"));
        Assert.Equal(PropertyStatus.Reserved, again.Status);
    }

    [MySqlFact]
    public async Task ChangeStatus_FollowsStateMachine_AndClosesReservationOnBooking()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        var reserved = await svc.ReserveAsync(p.Id, Reserve(admin, p.RowVersion));
        await Assert.ThrowsAsync<DomainException>(() => svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Sold, "skip", reserved.RowVersion)));
        await Assert.ThrowsAsync<DomainException>(() => svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Available, "use release", reserved.RowVersion)));
        await Assert.ThrowsAsync<DomainException>(() => svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Reserved, "again", reserved.RowVersion)));
        var booked = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "Customer confirmed", reserved.RowVersion));
        Assert.Equal(PropertyStatus.Booked, booked.Status);
        Assert.Null(booked.ActiveReservation);
        Assert.Equal(ReservationStatus.Converted, (await svc.ListReservationsAsync(p.Id)).Single().Status);
        var sold = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Sold, "Sale completed", booked.RowVersion));
        var allocated = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Allocated, "Allocated", sold.RowVersion));
        var ready = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.PossessionReady, "Ready", allocated.RowVersion));
        var delivered = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.PossessionDelivered, "Handed over", ready.RowVersion));
        var transferred = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Transferred, "Registry transfer", delivered.RowVersion));
        Assert.Equal(PropertyStatus.Transferred, transferred.Status);
        await Assert.ThrowsAsync<DomainException>(() => svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Available, "undo", transferred.RowVersion)));
        var history = await svc.ListHistoryAsync(new InventoryHistoryRequest { PropertyId = p.Id, PageSize = 50 });
        Assert.Equal(8, history.TotalCount);
    }

    [MySqlFact]
    public async Task SoldProperty_PhysicalDetailsLocked_ButRemarksEditable()
    {
        var (_, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        var booked = await svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "Booked", p.RowVersion));
        UpdatePropertyRequest Upd(decimal size, string remarks) => new(p.PropertyNumber, null, null, null, p.PropertyTypeId, size, MeasurementUnit.Marla, null, null, false, false, false,
            DevelopmentStatus.Developed, remarks, null, null, booked.RowVersion);
        await Assert.ThrowsAsync<DomainException>(() => svc.UpdateAsync(p.Id, Upd(99m, "x")));
        var ok = await svc.UpdateAsync(p.Id, Upd(p.Size, "Remarks after booking"));
        Assert.Equal("Remarks after booking", ok.Remarks);
    }

    [MySqlFact]
    public async Task ProjectAccess_IsEnforced_ForListGetAndMutations()
    {
        var (admin, projectId) = await ProjectAsync();
        var mine = await NewPropertyAsync(projectId);
        var (_, otherProject) = await ProjectAsync();
        var theirs = await NewPropertyAsync(otherProject);

        var manager = await fx.CreateUserAsync(RoleNames.InventoryManager);
        fx.Act(admin);
        using (var s = fx.Scope()) await s.ServiceProvider.GetRequiredService<ProjectAccessAdminService>().AssignAsync(projectId, manager);

        fx.Act(manager);
        using var s2 = fx.Scope();
        var svc = s2.ServiceProvider.GetRequiredService<PropertyService>();
        var list = await svc.ListAsync(new PropertyListRequest { PageSize = 100 });
        Assert.Contains(list.Items, x => x.Id == mine.Id);
        Assert.DoesNotContain(list.Items, x => x.Id == theirs.Id);
        Assert.Equal(mine.Id, (await svc.GetAsync(mine.Id)).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetAsync(theirs.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetAsync(999_999_999)); // unknown ids cannot be probed
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ReserveAsync(theirs.Id, Reserve(manager, theirs.RowVersion)));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ChangeStatusAsync(theirs.Id, new ChangePropertyStatusRequest(PropertyStatus.Cancelled, "no", theirs.RowVersion)));
        await Assert.ThrowsAsync<ForbiddenException>(() => NewPropertyAsync(otherProject));
        var report = await s2.ServiceProvider.GetRequiredService<InventoryReportService>().GetReportAsync(null);
        await Assert.ThrowsAsync<ForbiddenException>(() => s2.ServiceProvider.GetRequiredService<InventoryReportService>().GetProjectSummaryAsync(otherProject));
    }

    [MySqlFact]
    public async Task Permissions_AreRequired_AtServiceLevel()
    {
        var (_, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        await fx.ActAsAsync(RoleNames.Cashier);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAsync(new PropertyListRequest()));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetAsync(p.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<PropertySetupService>().ListTypesAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => s.ServiceProvider.GetRequiredService<PropertySetupService>().CreateCategoryAsync(new SaveCategoryRequest("Q", "Q", null, true)));

        await fx.ActAsAsync(RoleNames.CustomerSupport); // view only
        using var s2 = fx.Scope();
        var view = s2.ServiceProvider.GetRequiredService<PropertyService>();
        await Assert.ThrowsAsync<ForbiddenException>(() => view.ReserveAsync(p.Id, Reserve(Guid.NewGuid(), p.RowVersion)));
        await Assert.ThrowsAsync<ForbiddenException>(() => view.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "no", p.RowVersion)));
    }

    [MySqlFact]
    public async Task Zones_CreateUpdateDeactivate_AreUniquePerProject_AndAudited()
    {
        var (_, projectId) = await ProjectAsync();
        var (_, other) = await ProjectAsync();
        using var s = fx.Scope();
        var setup = s.ServiceProvider.GetRequiredService<PropertySetupService>();
        foreach (var kind in Enum.GetValues<ZoneKind>())
        {
            var z = await setup.CreateZoneAsync(kind, projectId, new SaveZoneRequest("Z1", "Zone One", true));
            await Assert.ThrowsAsync<RequestValidationException>(() => setup.CreateZoneAsync(kind, projectId, new SaveZoneRequest("z1", "Different", true)));
            await Assert.ThrowsAsync<RequestValidationException>(() => setup.CreateZoneAsync(kind, projectId, new SaveZoneRequest("Z2", "zone one", true)));
            await setup.CreateZoneAsync(kind, other, new SaveZoneRequest("Z1", "Zone One", true)); // same code in another project is fine
            var renamed = await setup.UpdateZoneAsync(kind, projectId, z.Id, new SaveZoneRequest("Z1", "Renamed", false));
            Assert.False(renamed.IsActive);
            Assert.Single(await setup.ListZonesAsync(kind, projectId, activeOnly: false));
            Assert.Empty(await setup.ListZonesAsync(kind, projectId, activeOnly: true));
            await Assert.ThrowsAsync<NotFoundException>(() => setup.UpdateZoneAsync(kind, projectId, 999_999_999, new SaveZoneRequest("Z", "N", true)));
        }
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "ProjectBlock.Created"));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "ProjectStreet.Updated"));
    }

    [MySqlFact]
    public async Task InactiveZone_CannotBeAssignedToNewProperty()
    {
        var (_, projectId) = await ProjectAsync();
        long zone;
        using (var s = fx.Scope())
        {
            var setup = s.ServiceProvider.GetRequiredService<PropertySetupService>();
            var z = await setup.CreateZoneAsync(ZoneKind.Sector, projectId, new SaveZoneRequest("S1", "Sector 1", true));
            await setup.UpdateZoneAsync(ZoneKind.Sector, projectId, z.Id, new SaveZoneRequest("S1", "Sector 1", false));
            zone = z.Id;
        }
        using var s2 = fx.Scope();
        var type = await TypeIdAsync(s2, "HOUSE");
        await Assert.ThrowsAsync<RequestValidationException>(() => s2.ServiceProvider.GetRequiredService<PropertyService>().CreateAsync(
            new CreatePropertyRequest(projectId, "SZ-" + Guid.NewGuid().ToString("N")[..8], "1", null, zone, null, type, 5, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Undeveloped, null, null, null)));
    }

    [MySqlFact]
    public async Task ListFilters_Paging_Sorting_AndAvailableOnly()
    {
        var (admin, projectId) = await ProjectAsync();
        long block;
        using (var s = fx.Scope()) block = (await s.ServiceProvider.GetRequiredService<PropertySetupService>().CreateZoneAsync(ZoneKind.Block, projectId, new SaveZoneRequest("BA", "Block A", true))).Id;
        var a = await NewPropertyAsync(projectId, "F-A", size: 5m, block: block);
        var b = await NewPropertyAsync(projectId, "F-B", size: 10m);
        var c = await NewPropertyAsync(projectId, "F-C", "SHOP", 20m, floor: 0);
        using var s2 = fx.Scope();
        var svc = s2.ServiceProvider.GetRequiredService<PropertyService>();
        await svc.ReserveAsync(b.Id, Reserve(admin, b.RowVersion));

        Assert.Equal(3, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId })).TotalCount);
        Assert.Equal(1, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, BlockId = block })).TotalCount);
        Assert.Equal(1, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, Status = PropertyStatus.Reserved })).TotalCount);
        Assert.Equal(2, (await svc.ListAvailableAsync(new PropertyListRequest { ProjectId = projectId })).TotalCount);
        Assert.Equal(2, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, MinSize = 10m })).TotalCount);
        Assert.Equal(1, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, Search = "F-C" })).TotalCount);
        var typeId = c.PropertyTypeId;
        Assert.Equal(1, (await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, TypeId = typeId })).TotalCount);
        var page = await svc.ListAsync(new PropertyListRequest { ProjectId = projectId, PageSize = 2, Page = 2, SortBy = "size", Descending = true });
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(a.Id, Assert.Single(page.Items).Id);
    }

    [MySqlFact]
    public async Task Summary_AndReport_CountByStatusTypeBlockAndSize()
    {
        var (admin, projectId) = await ProjectAsync();
        await NewPropertyAsync(projectId, size: 5m);
        var b = await NewPropertyAsync(projectId, size: 5m);
        var c = await NewPropertyAsync(projectId, "SHOP-1", "SHOP", 12m, floor: 1);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        await svc.ReserveAsync(b.Id, Reserve(admin, b.RowVersion));
        var booked = await svc.ChangeStatusAsync(c.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "Booked", c.RowVersion));
        await svc.ChangeStatusAsync(c.Id, new ChangePropertyStatusRequest(PropertyStatus.Sold, "Sold", booked.RowVersion));

        var reports = s.ServiceProvider.GetRequiredService<InventoryReportService>();
        var sum = await reports.GetProjectSummaryAsync(projectId);
        Assert.Equal(3, sum.Total);
        Assert.Equal(1, sum.ByStatus.Single(x => x.Status == PropertyStatus.Available).Count);
        Assert.Equal(1, sum.ByStatus.Single(x => x.Status == PropertyStatus.Reserved).Count);
        Assert.Equal(1, sum.ByStatus.Single(x => x.Status == PropertyStatus.Sold).Count);
        Assert.Equal(2, sum.ByType.Single(x => x.Label == "Residential Plot").Total);
        Assert.Equal(1, sum.ByType.Single(x => x.Label == "Shop").Sold);
        Assert.Equal(3, sum.ByBlock.Single(x => x.Label == "No block").Total);

        var report = await reports.GetReportAsync(projectId);
        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.ByProject.Single().Total);
        Assert.Equal(2, report.BySize.Single(x => x.Size == 5m).Total);
        Assert.Equal(1, report.BySize.Single(x => x.Size == 12m).Sold);
    }

    [MySqlFact]
    public async Task History_FiltersByDateStatusAndProject()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        await svc.ReserveAsync(p.Id, Reserve(admin, p.RowVersion));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var all = await svc.ListHistoryAsync(new InventoryHistoryRequest { ProjectId = projectId, From = today.AddDays(-1), To = today.AddDays(1) });
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(1, (await svc.ListHistoryAsync(new InventoryHistoryRequest { ProjectId = projectId, Status = PropertyStatus.Reserved })).TotalCount);
        Assert.Equal(0, (await svc.ListHistoryAsync(new InventoryHistoryRequest { ProjectId = projectId, To = today.AddDays(-5) })).TotalCount);
    }

    [MySqlFact]
    public async Task Documents_UploadDownloadRemove_AreSecuredAndAudited()
    {
        var (_, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using var s = fx.Scope();
        var svc = s.ServiceProvider.GetRequiredService<PropertyService>();
        var doc = await svc.UploadDocumentAsync(p.Id, PropertyDocumentType.Plan, "Floor plan", new FileUpload("plan.pdf", "application/pdf", 5, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-"))));
        Assert.Single(await svc.ListDocumentsAsync(p.Id));
        var file = await svc.DownloadDocumentAsync(p.Id, doc.Id);
        using (var reader = new StreamReader(file.Content)) Assert.Equal("%PDF-", await reader.ReadToEndAsync());
        await Assert.ThrowsAsync<DomainException>(() => svc.UploadDocumentAsync(p.Id, PropertyDocumentType.Other, null, new FileUpload("run.exe", "application/octet-stream", 3, new MemoryStream([1, 2, 3]))));
        await Assert.ThrowsAsync<DomainException>(() => svc.DeleteDocumentAsync(p.Id, doc.Id, " "));
        await svc.DeleteDocumentAsync(p.Id, doc.Id, "Superseded");
        Assert.Empty(await svc.ListDocumentsAsync(p.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.DownloadDocumentAsync(p.Id, doc.Id));
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.PropertyDocuments.AnyAsync(d => d.Id == doc.Id && d.IsDeleted && d.DeleteReason == "Superseded")); // soft delete, retained
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "PropertyDocument.Removed"));
    }

    [MySqlFact]
    public async Task ArchivedProject_BlocksPropertyChanges()
    {
        var (admin, projectId) = await ProjectAsync();
        var p = await NewPropertyAsync(projectId);
        using (var s = fx.Scope())
        {
            var projects = s.ServiceProvider.GetRequiredService<ProjectService>();
            var detail = await projects.GetAsync(projectId);
            var planning = await projects.ChangeStatusAsync(projectId, new ChangeProjectStatusRequest(ProjectStatus.Archived, "Closed", detail.RowVersion));
            Assert.Equal(ProjectStatus.Archived, planning.Status);
        }
        using var s2 = fx.Scope();
        var svc = s2.ServiceProvider.GetRequiredService<PropertyService>();
        await Assert.ThrowsAsync<DomainException>(() => NewPropertyAsync(projectId));
        await Assert.ThrowsAsync<DomainException>(() => svc.ReserveAsync(p.Id, Reserve(admin, p.RowVersion)));
        await Assert.ThrowsAsync<DomainException>(() => svc.ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Cancelled, "xyz", p.RowVersion)));
    }
}
