using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

[Collection("phase1")]
public class Phase04Tests(Phase1Fixture fx)
{
    private int counter;
    private static readonly Dictionary<Guid, string> Names = new();
    private static Guid adminId;
    private record World(Guid Admin, Guid Sales, Guid Director, long ProjectId);

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

    private void As(Guid id) { fx.Act(id); lock (Names) fx.Actor.UserName = Names[id]; }

    private async Task<Guid> UserAsync(params string[] roles)
    {
        var id = await fx.CreateUserAsync(roles);
        lock (Names) Names[id] = fx.Actor.UserName!;
        return id;
    }

    private async Task<World> WorldAsync()
    {
        var admin = await UserAsync(RoleNames.Administrator);
        adminId = admin;
        As(admin);
        long pid;
        using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        As(admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, sales);
        using (var s = fx.Scope())
        {
            var today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, pid);
    }

    /// <summary>Properties are created by an administrator; the previous acting user is restored afterwards.</summary>
    private async Task<PropertyDetailDto> PropertyAsync(long projectId, decimal size = 5m)
    {
        var prev = fx.Actor.UserId; var prevName = fx.Actor.UserName;
        As(adminId);
        try { return await CreatePropertyCoreAsync(projectId, size); }
        finally { fx.Actor.UserId = prev; fx.Actor.UserName = prevName; }
    }

    private async Task<PropertyDetailDto> CreatePropertyCoreAsync(long projectId, decimal size)
    {
        using var s = fx.Scope();
        var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
        var n = Interlocked.Increment(ref counter);
        return await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(projectId, $"BK-{Guid.NewGuid():N}"[..14], n.ToString(), null, null, null, typeId, size, MeasurementUnit.Marla,
            null, null, false, false, false, DevelopmentStatus.Developed, null, null, null));
    }

    private async Task<CustomerDetailDto> CustomerAsync(string? name = null, IReadOnlyList<NomineeDto>? nominees = null)
    {
        using var s = fx.Scope();
        return await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest(name ?? "Customer " + Guid.NewGuid().ToString("N")[..6], IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
            [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], nominees));
    }

    private static CreateBookingRequest Book(long projectId, IEnumerable<long> propertyIds, IEnumerable<(long Customer, decimal Share)> owners, PaymentMode mode = PaymentMode.Installment,
        decimal amount = 500_000m, bool submit = false, string? key = null, decimal discount = 0m, IReadOnlyList<ChargeInput>? charges = null, bool authorize = false, string? authReason = null) =>
        new(projectId, null, mode, propertyIds.Select(p => new BookingPropertyInput(p, discount, discount > 0 ? "Negotiated discount" : null)).ToList(),
            owners.Select(o => new OwnerInput(o.Customer, o.Share)).ToList(), charges, amount, authorize, authReason, null, submit, key);

    private async Task<BookingDetailDto> CreateBookingAsync(World w, CreateBookingRequest r, Guid? actor = null)
    {
        As(actor ?? w.Sales);
        using var s = fx.Scope();
        return await Svc<BookingService>(s).CreateAsync(r);
    }

    private async Task<BookingDetailDto> NewSubmittedAsync(World w, long? propertyId = null, long? customerId = null, PaymentMode mode = PaymentMode.Installment, decimal amount = 500_000m)
    {
        var p = propertyId ?? (await PropertyAsync(w.ProjectId)).Id;
        As(w.Sales);
        var c = customerId ?? (await CustomerAsync()).Id;
        return await CreateBookingAsync(w, Book(w.ProjectId, [p], [(c, 100m)], mode, amount, submit: true));
    }

    private async Task<BookingDetailDto> ApproveAsync(BookingDetailDto b, Guid approver)
    {
        As(approver);
        using var s = fx.Scope();
        return await Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion));
    }

    private async Task<BookingDetailDto> GetAsync(long id) { using var s = fx.Scope(); return await Svc<BookingService>(s).GetAsync(id); }
    private async Task<PropertyStatus> StatusAsync(long propertyId) { using var s = fx.Scope(); return await Svc<AppDbContext>(s).Properties.Where(p => p.Id == propertyId).Select(p => p.Status).FirstAsync(); }

    // ------------------------------------------------------------------------------------------------ permissions
    [MySqlFact]
    public async Task Phase4Permissions_AreCataloged_AndGrantedToDefaultRoles()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        var director = await Perms(RoleNames.Director);
        foreach (var p in new[] { Permissions.BookingsApprove, Permissions.BookingsTransfer, Permissions.BookingsRefund, Permissions.BookingsAuthorizeBalance, Permissions.CustomersManage })
            Assert.Contains(p, director);
        var sales = await Perms(RoleNames.SalesManager);
        Assert.Contains(Permissions.BookingsCreate, sales);
        Assert.DoesNotContain(Permissions.BookingsApprove, sales);
        Assert.DoesNotContain(Permissions.BookingsAuthorizeBalance, sales);
        Assert.DoesNotContain(Permissions.BookingsCreate, await Perms(RoleNames.Cashier));
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
    }

    // ------------------------------------------------------------------------------------------------ customers
    [MySqlFact]
    public async Task Customer_Registration_GeneratesCode_NormalizesIdentity_AndAudits()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var cnic = "35202-1234567-1";
        CustomerDetailDto c;
        using (var s = fx.Scope())
            c = await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("  Ali Khan ", IdentityDocumentType.Cnic, cnic, "0300-1111111", "ali@example.com", "Murree", "VIP",
                [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], [new ContactDto("Wife", "Spouse", "0301-2222222", null)], [new NomineeDto("Sara Khan", "Wife", "3520212345678", "0301-2222222", 60m), new NomineeDto("Omar Khan", "Son", null, null, 40m)]));
        Assert.StartsWith("CUS-", c.CustomerCode);
        Assert.Equal("Ali Khan", c.FullName);
        Assert.Equal("3520212345671", c.IdentityNumber);
        Assert.Single(c.Addresses); Assert.Single(c.Contacts); Assert.Equal(2, c.Nominees.Count);
        using var s2 = fx.Scope();
        Assert.True(await Svc<AppDbContext>(s2).AuditLogs.AnyAsync(a => a.Action == "Customer.Created" && a.EntityId == c.Id.ToString()));
        // duplicate identity (any formatting) is rejected
        var ex = await Assert.ThrowsAsync<RequestValidationException>(async () => { using var s = fx.Scope(); await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Dup", IdentityDocumentType.Cnic, "3520212345671", "0300-1111111", null, null, null, null, null, null)); });
        Assert.Contains("IdentityNumber", ex.Errors.Keys);
    }

    [MySqlFact]
    public async Task Customer_InvalidInput_IsRejected()
    {
        var w = await WorldAsync();
        As(w.Sales);
        async Task<RequestValidationException> Fail(CreateCustomerRequest r) { using var s = fx.Scope(); return await Assert.ThrowsAsync<RequestValidationException>(() => Svc<CustomerService>(s).CreateAsync(r)); }
        Assert.Contains("IdentityNumber", (await Fail(new("A", IdentityDocumentType.Cnic, "123", "0300-1234567", null, null, null, null, null, null))).Errors.Keys);
        Assert.Contains("FullName", (await Fail(new("", IdentityDocumentType.Passport, "AB12345", "0300-1234567", null, null, null, null, null, null))).Errors.Keys);
        Assert.Contains("Mobile", (await Fail(new("A", IdentityDocumentType.Passport, "AB12345", "x", null, null, null, null, null, null))).Errors.Keys);
        Assert.Contains("Email", (await Fail(new("A", IdentityDocumentType.Passport, "AB12345", "0300-1234567", "not-an-email", null, null, null, null, null))).Errors.Keys);
        Assert.Contains("Nominees", (await Fail(new("A", IdentityDocumentType.Passport, "AB12345", "0300-1234567", null, null, null, null, null, [new("N1", "Son", null, null, 70m), new("N2", "Son", null, null, 40m)]))).Errors.Keys);
    }

    [MySqlFact]
    public async Task Customer_Update_StatusChangeNeedsReason_AndStaleEditIsRejected()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var c = await CustomerAsync();
        UpdateCustomerRequest Upd(CustomerStatus st, string? reason, string? rv) => new(c.FullName, c.IdentityType, c.IdentityNumber, c.Mobile, c.Email, c.Address, null, st, reason, null, null, null, rv);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<CustomerService>(s).UpdateAsync(c.Id, Upd(CustomerStatus.Inactive, null, c.RowVersion)));
        CustomerDetailDto updated;
        using (var s = fx.Scope()) updated = await Svc<CustomerService>(s).UpdateAsync(c.Id, Upd(CustomerStatus.Inactive, "Moved abroad", c.RowVersion));
        Assert.Equal(CustomerStatus.Inactive, updated.Status);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ConcurrencyException>(() => Svc<CustomerService>(s).UpdateAsync(c.Id, Upd(CustomerStatus.Active, "Back", c.RowVersion)));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<CustomerService>(s).UpdateAsync(c.Id, Upd(CustomerStatus.Blocked, null, updated.RowVersion)));
        using var s2 = fx.Scope();
        Assert.True(await Svc<AppDbContext>(s2).AuditLogs.AnyAsync(a => a.Action == "Customer.StatusChanged" && a.EntityId == c.Id.ToString() && a.Reason == "Moved abroad"));
    }

    [MySqlFact]
    public async Task Customer_Access_IsLimitedToOwnRegistrationsAndAssignedProjects()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var mine = await CustomerAsync("Mine");
        var outsider = await UserAsync(RoleNames.SalesManager);
        As(outsider);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<CustomerService>(s).GetAsync(mine.Id));
        using (var s = fx.Scope()) Assert.DoesNotContain(await Svc<CustomerService>(s).ListAsync(new CustomerListRequest { PageSize = 100 }) is { } p ? p.Items : [], x => x.Id == mine.Id);
        // a booking in an assigned project makes the customer visible to project members
        As(w.Admin);
        var other = await UserAsync(RoleNames.SalesManager);
        As(w.Admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, other);
        var prop = await PropertyAsync(w.ProjectId);
        await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(mine.Id, 100m)]));
        As(other);
        using (var s = fx.Scope()) Assert.Equal(mine.Id, (await Svc<CustomerService>(s).GetAsync(mine.Id)).Id);
        // director/admin hold projects.access.all
        As(w.Director);
        using (var s = fx.Scope()) Assert.Equal(mine.Id, (await Svc<CustomerService>(s).GetAsync(mine.Id)).Id);
    }

    [MySqlFact]
    public async Task Customer_Relationships_AreLinked_AndRejectedWhenInvalid()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var a = await CustomerAsync("A"); var b = await CustomerAsync("B");
        CustomerDetailDto detail;
        using (var s = fx.Scope()) detail = await Svc<CustomerService>(s).AddRelationshipAsync(a.Id, new AddRelationshipRequest(b.Id, CustomerRelationshipType.Spouse, "married"));
        Assert.Single(detail.Relationships);
        using (var s = fx.Scope()) Assert.Single((await Svc<CustomerService>(s).GetAsync(b.Id)).Relationships);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<CustomerService>(s).AddRelationshipAsync(a.Id, new AddRelationshipRequest(a.Id, CustomerRelationshipType.Other, null)));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<CustomerService>(s).AddRelationshipAsync(b.Id, new AddRelationshipRequest(a.Id, CustomerRelationshipType.Spouse, null)));
        using (var s = fx.Scope()) detail = await Svc<CustomerService>(s).RemoveRelationshipAsync(a.Id, detail.Relationships[0].Id, "Entered by mistake");
        Assert.Empty(detail.Relationships);
    }

    [MySqlFact]
    public async Task Customer_Documents_UploadDownloadRemove()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var c = await CustomerAsync();
        CustomerDocumentDto doc;
        using (var s = fx.Scope()) doc = await Svc<CustomerService>(s).UploadDocumentAsync(c.Id, CustomerDocumentType.Cnic, "CNIC front", new FileUpload("cnic.pdf", "application/pdf", 5, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-"))));
        using (var s = fx.Scope()) Assert.Single(await Svc<CustomerService>(s).ListDocumentsAsync(c.Id));
        using (var s = fx.Scope()) { var f = await Svc<CustomerService>(s).DownloadDocumentAsync(c.Id, doc.Id); using var ms = new MemoryStream(); await f.Content.CopyToAsync(ms); Assert.Equal(5, ms.Length); }
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<CustomerService>(s).UploadDocumentAsync(c.Id, CustomerDocumentType.Other, null, new FileUpload("x.exe", "application/octet-stream", 5, new MemoryStream(new byte[5]))));
        using (var s = fx.Scope()) await Svc<CustomerService>(s).DeleteDocumentAsync(c.Id, doc.Id, "Wrong file");
        using (var s = fx.Scope()) Assert.Empty(await Svc<CustomerService>(s).ListDocumentsAsync(c.Id));
    }

    // ------------------------------------------------------------------------------------------------ booking creation
    [MySqlFact]
    public async Task Booking_Create_StoresPriceSnapshot_Totals_AndHoldsProperty()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], amount: 500_000m,
            charges: [new ChargeInput(BookingChargeType.Registration, "Registration fee", 25_000m)]));
        Assert.StartsWith("BK-", b.BookingNumber);
        Assert.Equal(BookingStatus.Draft, b.Status);
        Assert.Equal(5_000_000m, b.TotalPrice);
        Assert.Equal(25_000m, b.TotalCharges);
        Assert.Equal(5_025_000m, b.TotalPayable);
        Assert.Equal(4_525_000m, b.BalanceAfterBooking);
        var line = Assert.Single(b.Properties);
        Assert.Equal(5_000_000m, line.FinalPrice);
        Assert.Equal(PropertyStatus.Booked, await StatusAsync(prop.Id));
        using var s = fx.Scope();
        var snap = await Svc<AppDbContext>(s).BookingPricingSnapshots.SingleAsync(x => x.BookingPropertyId == line.Id);
        Assert.True(PricingSnapshot.Verify(snap.SnapshotJson, snap.SnapshotHash));
        Assert.Equal(prop.Id, PricingSnapshot.Deserialize(snap.SnapshotJson).PropertyId);
        Assert.True(await Svc<AppDbContext>(s).PropertyStatusHistories.AnyAsync(h => h.PropertyId == prop.Id && h.ToStatus == PropertyStatus.Booked));
        Assert.True(await Svc<AppDbContext>(s).AuditLogs.AnyAsync(a => a.Action == "Booking.Created" && a.EntityId == b.Id.ToString()));
    }

    [MySqlFact]
    public async Task Booking_PriceSnapshot_IsNotRecalculated_WhenPricingChanges()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]));
        As(w.Admin);
        using (var s = fx.Scope())
        {
            var today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(w.ProjectId, "BASE2", "Higher base", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Property,
                null, null, null, prop.Id, null, null, PricingMethod.PerUnit, 2_000_000m, MeasurementUnit.Marla, 5, today, null, null));
        }
        var after = await GetAsync(b.Id);
        Assert.Equal(5_000_000m, after.Properties[0].FinalPrice);
        Assert.Equal(5_000_000m, after.TotalPayable);
    }

    [MySqlFact]
    public async Task Booking_ExtraDiscount_RequiresPermissionAndReason_AndReducesTotal()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        await Assert.ThrowsAsync<ForbiddenException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], discount: 100_000m)));
        Assert.Equal(PropertyStatus.Available, await StatusAsync(prop.Id));
        var noReason = Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]) with { Properties = [new BookingPropertyInput(prop.Id, 100m, null)] };
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, noReason, w.Admin));
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], discount: 100_000m), w.Admin);
        Assert.Equal(100_000m, b.TotalExtraDiscount);
        Assert.Equal(4_900_000m, b.TotalPayable);
        await Assert.ThrowsAsync<DomainException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [(await PropertyAsync(w.ProjectId)).Id], [(c.Id, 100m)], discount: 9_000_000m), w.Admin));
    }

    [MySqlFact]
    public async Task Booking_JointOwnership_MustTotalExactly100_AndOwnersAreRecorded()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var a = await CustomerAsync("Owner A"); var b = await CustomerAsync("Owner B");
        var ex = await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 60m), (b.Id, 30m)])));
        Assert.Contains("Owners", ex.Errors.Keys);
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 60m), (b.Id, 50m)])));
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 50m), (a.Id, 50m)])));
        var booking = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 60.5m), (b.Id, 39.5m)]));
        Assert.Equal(2, booking.Customers.Count);
        Assert.Equal(100m, booking.Customers.Sum(x => x.SharePercent));
        Assert.True(booking.Customers.Single(x => x.CustomerId == a.Id).IsPrimary);
    }

    [MySqlFact]
    public async Task Booking_MultipleProperties_AreBookedTogether_AndOneCustomerCanOwnMany()
    {
        var w = await WorldAsync();
        var p1 = await PropertyAsync(w.ProjectId); var p2 = await PropertyAsync(w.ProjectId, 10m);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [p1.Id, p2.Id], [(c.Id, 100m)], amount: 1_000_000m));
        Assert.Equal(15_000_000m, b.TotalPayable);
        Assert.Equal(2, b.Properties.Count);
        var b2 = await CreateBookingAsync(w, Book(w.ProjectId, [(await PropertyAsync(w.ProjectId)).Id], [(c.Id, 100m)]));
        Assert.NotEqual(b.Id, b2.Id);
        var approved = await ApproveAsync(await SubmitAsync(b), w.Director);
        await ApproveAsync(await SubmitAsync(b2), w.Director);
        As(w.Director);
        using var s = fx.Scope();
        var detail = await Svc<CustomerService>(s).GetAsync(c.Id);
        Assert.Equal(3, detail.Ownerships.Count(o => o.IsActive));
        Assert.All(detail.Ownerships, o => Assert.Equal(100m, o.SharePercent));
        Assert.Equal(BookingStatus.Confirmed, approved.Status);
    }

    private async Task<BookingDetailDto> SubmitAsync(BookingDetailDto b)
    {
        As(Names.Single(x => x.Value == b.CreatedBy).Key);
        using var s = fx.Scope();
        return await Svc<BookingService>(s).SubmitAsync(b.Id, new SubmitBookingRequest(b.RowVersion));
    }

    // ------------------------------------------------------------------------------------------------ conflicts and idempotency
    [MySqlFact]
    public async Task Booking_Conflict_SecondBookingOfSameProperty_IsRejected()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c1 = await CustomerAsync(); var c2 = await CustomerAsync();
        await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c1.Id, 100m)]));
        var ex = await Assert.ThrowsAsync<DomainException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c2.Id, 100m)])));
        Assert.Contains("Booked", ex.Message);
    }

    [MySqlFact]
    public async Task Booking_ConcurrentAttempts_ExactlyOneSucceeds_AndDatabaseHoldsOneLiveBooking()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var customers = new List<long>();
        for (var i = 0; i < 6; i++) customers.Add((await CustomerAsync()).Id);
        var tasks = customers.Select(async cid =>
        {
            try { using var s = fx.Scope(); await Svc<BookingService>(s).CreateAsync(Book(w.ProjectId, [prop.Id], [(cid, 100m)])); return true; }
            catch (DomainException) { return false; }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
        using var s2 = fx.Scope();
        Assert.Equal(1, await Svc<AppDbContext>(s2).BookingProperties.CountAsync(x => x.PropertyId == prop.Id && x.ActivePropertyId != null));
        Assert.Equal(1, await Svc<AppDbContext>(s2).BookingProperties.CountAsync(x => x.PropertyId == prop.Id));
        Assert.Equal(PropertyStatus.Booked, await StatusAsync(prop.Id));
    }

    [MySqlFact]
    public async Task Booking_DatabaseIndex_RejectsTwoLiveBookingsForOneProperty()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]));
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        db.Bookings.Add(new Booking { BookingNumber = "DUP-" + Guid.NewGuid().ToString("N")[..10], ProjectId = w.ProjectId, BookingDate = DateOnly.FromDateTime(DateTime.UtcNow), PaymentMode = PaymentMode.Cash,
            Properties = [new BookingProperty { PropertyId = prop.Id, ActivePropertyId = prop.Id, LinePrice = 1m, PricingSnapshot = new BookingPricingSnapshot { SnapshotJson = "{}", SnapshotHash = new string('a', 64), Unit = MeasurementUnit.Marla, CapturedAtUtc = DateTime.UtcNow } }] });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotEqual(0, b.Id);
    }

    [MySqlFact]
    public async Task Booking_Idempotency_SameKeyReturnsSameBooking_AndDoesNotDoubleBook()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var key = Guid.NewGuid().ToString("N");
        var first = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], key: key));
        var second = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], key: key));
        Assert.Equal(first.Id, second.Id);
        using var s = fx.Scope();
        Assert.Equal(1, await Svc<AppDbContext>(s).Bookings.CountAsync(b => b.IdempotencyKey == key));
        // another user cannot hijack the key
        var other = await UserAsync(RoleNames.Director);
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)], key: key), other));
    }

    [MySqlFact]
    public async Task Booking_ReservedProperty_CanOnlyBeBookedByTheReservingCustomer_AndReservationIsConverted()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var holder = await CustomerAsync("Holder"); var other = await CustomerAsync("Other");
        As(w.Admin);
        using (var s = fx.Scope())
        {
            var today = await Svc<PropertyService>(s).TodayAsync();
            await Svc<PropertyService>(s).ReserveAsync(prop.Id, new ReservePropertyRequest(today, today.AddDays(5), 1000m, holder.Id, holder.FullName, null, w.Admin, prop.RowVersion));
        }
        var ex = await Assert.ThrowsAsync<DomainException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(other.Id, 100m)])));
        Assert.Contains("reserved", ex.Message);
        await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(holder.Id, 100m)]));
        using var s2 = fx.Scope();
        Assert.Equal(ReservationStatus.Converted, await Svc<AppDbContext>(s2).PropertyReservations.Where(r => r.PropertyId == prop.Id).Select(r => r.Status).SingleAsync());
        Assert.Equal(PropertyStatus.Booked, await StatusAsync(prop.Id));
    }

    [MySqlFact]
    public async Task Booking_InactiveOrBlockedCustomer_AndWrongProjectProperty_AreRejected()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        using (var s = fx.Scope())
            await Svc<CustomerService>(s).UpdateAsync(c.Id, new UpdateCustomerRequest(c.FullName, c.IdentityType, c.IdentityNumber, c.Mobile, c.Email, c.Address, null, CustomerStatus.Inactive, "On hold", null, null, null, c.RowVersion));
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)])));
        var active = await CustomerAsync();
        As(w.Admin);
        long otherProject;
        using (var s = fx.Scope()) otherProject = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var foreign = await PropertyAsync(otherProject);
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [foreign.Id], [(active.Id, 100m)]), w.Admin));
    }

    [MySqlFact]
    public async Task Booking_UserWithoutProjectAccess_CannotBookOrView()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]));
        var outsider = await UserAsync(RoleNames.SalesManager);
        await Assert.ThrowsAsync<ForbiddenException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [(await PropertyAsync(w.ProjectId)).Id], [(c.Id, 100m)]), outsider));
        As(outsider);
        using var s = fx.Scope();
        await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s).GetAsync(b.Id));
        var list = await Svc<BookingService>(s).ListAsync(new BookingListRequest { PageSize = 100 });
        Assert.DoesNotContain(list.Items, x => x.Id == b.Id);
        var cashier = await UserAsync(RoleNames.Cashier);
        As(cashier);
        using var s2 = fx.Scope();
        await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s2).CreateAsync(Book(w.ProjectId, [prop.Id], [(c.Id, 100m)])));
    }

    // ------------------------------------------------------------------------------------------------ cash sales
    [MySqlFact]
    public async Task CashSale_FullPaymentAtBooking_IsAllowed_PartialRequiresAuthorization()
    {
        var w = await WorldAsync();
        var p1 = await PropertyAsync(w.ProjectId); var p2 = await PropertyAsync(w.ProjectId); var p3 = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var full = await CreateBookingAsync(w, Book(w.ProjectId, [p1.Id], [(c.Id, 100m)], PaymentMode.Cash, 5_000_000m));
        Assert.Equal(0m, full.BalanceAfterBooking); Assert.Null(full.BalanceAuthorizedBy);
        // no authorization flag
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [p2.Id], [(c.Id, 100m)], PaymentMode.Cash, 1_000_000m)));
        // flag set but the user lacks bookings.balance.authorize
        await Assert.ThrowsAsync<ForbiddenException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [p2.Id], [(c.Id, 100m)], PaymentMode.Cash, 1_000_000m, authorize: true, authReason: "Customer will pay after sale of land")));
        // authorized by a user holding the permission, with a reason
        await Assert.ThrowsAsync<RequestValidationException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [p2.Id], [(c.Id, 100m)], PaymentMode.Cash, 1_000_000m, authorize: true, authReason: "x"), w.Director));
        var partial = await CreateBookingAsync(w, Book(w.ProjectId, [p2.Id], [(c.Id, 100m)], PaymentMode.Cash, 1_000_000m, authorize: true, authReason: "Customer will pay after sale of land"), w.Director);
        Assert.Equal(4_000_000m, partial.BalanceAfterBooking);
        Assert.NotNull(partial.BalanceAuthorizedBy);
        await Assert.ThrowsAsync<DomainException>(async () => await CreateBookingAsync(w, Book(w.ProjectId, [p3.Id], [(c.Id, 100m)], PaymentMode.Cash, 9_000_000m), w.Director));
        Assert.Equal(PropertyStatus.Available, await StatusAsync(p3.Id));
    }

    // ------------------------------------------------------------------------------------------------ approval and agreement
    [MySqlFact]
    public async Task Approval_IsMakerChecker_ConfirmsBooking_GeneratesAgreementAndOwnership()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var a = await CustomerAsync("Joint A"); var b = await CustomerAsync("Joint B");
        var booking = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 70m), (b.Id, 30m)], submit: true));
        Assert.Equal(BookingStatus.PendingApproval, booking.Status);
        // submitter lacks the permission; an admin who also submitted would be refused too
        As(w.Sales);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s).ApproveAsync(booking.Id, new ApproveBookingRequest(null, booking.RowVersion)));
        var maker = await UserAsync(RoleNames.Administrator);
        var adminBooking = await CreateBookingAsync(w, Book(w.ProjectId, [(await PropertyAsync(w.ProjectId)).Id], [(a.Id, 100m)], submit: true), maker);
        As(maker);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).ApproveAsync(adminBooking.Id, new ApproveBookingRequest(null, adminBooking.RowVersion)));

        var approved = await ApproveAsync(booking, w.Director);
        Assert.Equal(BookingStatus.Confirmed, approved.Status);
        Assert.NotNull(approved.AgreementNumber);
        Assert.StartsWith("AGR-", approved.AgreementNumber);
        Assert.Equal(1, approved.AgreementVersion);
        Assert.Equal(PropertyStatus.Sold, await StatusAsync(prop.Id));
        Assert.Single(approved.Approvals);
        Assert.Contains(approved.History, h => h.ToStatus == BookingStatus.Confirmed);
        using (var s = fx.Scope())
        {
            var owns = await Svc<AppDbContext>(s).CustomerOwnerships.Where(o => o.PropertyId == prop.Id && o.IsActive).ToListAsync();
            Assert.Equal(2, owns.Count); Assert.Equal(100m, owns.Sum(o => o.SharePercent));
        }
        // second approval is refused
        As(w.Director);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).ApproveAsync(booking.Id, new ApproveBookingRequest(null, null)));
        // agreement content
        using var s2 = fx.Scope();
        var agreement = await Svc<BookingService>(s2).GetAgreementAsync(booking.Id, null);
        Assert.True(agreement.ContentVerified);
        Assert.Contains("Joint A", agreement.Content); Assert.Contains("70%", agreement.Content); Assert.Contains(prop.PropertyCode, agreement.Content);
        Assert.Contains("BOOKING CONFIRMATION RECEIPT", agreement.ReceiptContent);
        Assert.Single(agreement.Versions);
    }

    [MySqlFact]
    public async Task Approval_ConcurrentApprovals_OnlyOneWins()
    {
        var w = await WorldAsync();
        var booking = await NewSubmittedAsync(w);
        var d2 = await UserAsync(RoleNames.Director);
        var results = await Task.WhenAll(new[] { w.Director, d2 }.Select(async approver =>
        {
            try { using var s = fx.Scope(); fx.Act(approver); await Svc<BookingService>(s).ApproveAsync(booking.Id, new ApproveBookingRequest(null, booking.RowVersion)); return true; }
            catch (DomainException) { return false; }
        }));
        // the shared fake actor makes the identity racy; what matters is exactly one confirmation and one agreement
        Assert.True(results.Count(r => r) >= 1);
        using var s2 = fx.Scope();
        Assert.Equal(1, await Svc<AppDbContext>(s2).CustomerAgreements.CountAsync(a => a.BookingId == booking.Id));
        Assert.Equal(1, await Svc<AppDbContext>(s2).BookingApprovals.CountAsync(a => a.BookingId == booking.Id));
    }

    [MySqlFact]
    public async Task Rejection_NeedsComment_ReleasesProperty_AndIsTerminal()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        var booking = await NewSubmittedAsync(w, prop.Id);
        As(w.Director);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<BookingService>(s).RejectAsync(booking.Id, new RejectBookingRequest("", booking.RowVersion)));
        BookingDetailDto rejected;
        using (var s = fx.Scope()) rejected = await Svc<BookingService>(s).RejectAsync(booking.Id, new RejectBookingRequest("Price not agreed", booking.RowVersion));
        Assert.Equal(BookingStatus.Rejected, rejected.Status);
        Assert.Equal(PropertyStatus.Available, await StatusAsync(prop.Id));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).ApproveAsync(booking.Id, new ApproveBookingRequest(null, null)));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).CancelAsync(booking.Id, new CancelBookingRequest("Cancel after reject", null, null, null)));
        // the property can be booked again
        As(w.Sales);
        var again = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [((await CustomerAsync()).Id, 100m)]));
        Assert.Equal(BookingStatus.Draft, again.Status);
        using var s2 = fx.Scope();
        Assert.Equal(2, await Svc<AppDbContext>(s2).BookingProperties.CountAsync(x => x.PropertyId == prop.Id));
    }

    // ------------------------------------------------------------------------------------------------ cancellation
    [MySqlFact]
    public async Task Cancel_Draft_ReleasesProperty_WithReasonRequired()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<BookingService>(s).CancelAsync(b.Id, new CancelBookingRequest("no", null, null, b.RowVersion)));
        BookingDetailDto cancelled;
        using (var s = fx.Scope()) cancelled = await Svc<BookingService>(s).CancelAsync(b.Id, new CancelBookingRequest("Customer changed mind", null, null, b.RowVersion));
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(PropertyStatus.Available, await StatusAsync(prop.Id));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).CancelAsync(b.Id, new CancelBookingRequest("Cancel again", null, null, null)));
        using var s2 = fx.Scope();
        var hist = await Svc<AppDbContext>(s2).PropertyStatusHistories.Where(h => h.PropertyId == prop.Id).OrderBy(h => h.Id).Select(h => h.ToStatus).ToListAsync();
        Assert.Equal([PropertyStatus.Available, PropertyStatus.Booked, PropertyStatus.Cancelled, PropertyStatus.Available], hist);
        Assert.Equal(0, await Svc<AppDbContext>(s2).BookingProperties.CountAsync(x => x.PropertyId == prop.Id && x.ActivePropertyId != null));
    }

    [MySqlFact]
    public async Task Cancel_ConfirmedAgreement_PreservesHistory_ClosesOwnership_AndRequestsRefundWithMakerChecker()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        var booking = await NewSubmittedAsync(w, prop.Id);
        var approved = await ApproveAsync(booking, w.Director);
        // the refund requires bookings.refund (sales manager lacks it)
        As(w.Sales);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s).CancelAsync(approved.Id, new CancelBookingRequest("Customer withdrew", 100_000m, null, approved.RowVersion)));
        Assert.Equal(PropertyStatus.Sold, await StatusAsync(prop.Id));
        As(w.Director);
        BookingDetailDto cancelled;
        using (var s = fx.Scope()) cancelled = await Svc<BookingService>(s).CancelAsync(approved.Id, new CancelBookingRequest("Customer withdrew", 400_000m, "Refund of booking amount", approved.RowVersion));
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(AgreementStatus.Cancelled, cancelled.AgreementStatus);
        Assert.Equal(PropertyStatus.Available, await StatusAsync(prop.Id));
        var refund = Assert.Single(cancelled.Refunds);
        Assert.Equal(RefundStatus.Requested, refund.Status);
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var owns = await db.CustomerOwnerships.Where(o => o.PropertyId == prop.Id).ToListAsync();
            Assert.NotEmpty(owns); Assert.All(owns, o => { Assert.False(o.IsActive); Assert.NotNull(o.EndedAtUtc); Assert.Contains("cancelled", o.EndReason); });
            Assert.True(await db.AgreementVersions.AnyAsync(v => v.AgreementId == cancelled.AgreementId));
            Assert.Equal(1, await db.BookingApprovals.CountAsync(a => a.BookingId == booking.Id));
        }
        // the requester cannot approve their own refund; another approver can
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).DecideRefundAsync(cancelled.Id, refund.Id, new DecideRefundRequest(true, null, refund.RowVersion)));
        var d2 = await UserAsync(RoleNames.Director);
        As(d2);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Svc<BookingService>(s).DecideRefundAsync(cancelled.Id, refund.Id, new DecideRefundRequest(false, null, refund.RowVersion)));
        BookingDetailDto after;
        using (var s = fx.Scope()) after = await Svc<BookingService>(s).DecideRefundAsync(cancelled.Id, refund.Id, new DecideRefundRequest(true, "Approved by finance", refund.RowVersion));
        Assert.Equal(RefundStatus.Approved, after.Refunds[0].Status);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).DecideRefundAsync(cancelled.Id, refund.Id, new DecideRefundRequest(true, null, null)));
        Assert.Equal(PropertyStatus.Available, await StatusAsync(prop.Id));
    }

    [MySqlFact]
    public async Task Cancel_Refund_CannotExceedTotalPayable()
    {
        var w = await WorldAsync();
        var booking = await NewSubmittedAsync(w);
        As(w.Director);
        using var s = fx.Scope();
        await Assert.ThrowsAsync<RequestValidationException>(() => Svc<BookingService>(s).CancelAsync(booking.Id, new CancelBookingRequest("Customer withdrew", 99_000_000m, null, booking.RowVersion)));
    }

    // ------------------------------------------------------------------------------------------------ transfer
    [MySqlFact]
    public async Task Transfer_ToNewCustomer_PreservesOriginalOwnership_AndCreatesAgreementVersion()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var original = await CustomerAsync("Original Owner"); var buyer = await CustomerAsync("New Owner");
        var booking = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(original.Id, 100m)], submit: true));
        var approved = await ApproveAsync(booking, w.Director);
        As(w.Sales);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s).TransferAsync(approved.Id, new TransferBookingRequest("Sold to relative", [new OwnerInput(buyer.Id, 100m)], approved.RowVersion)));
        As(w.Director);
        BookingDetailDto transferred;
        using (var s = fx.Scope()) transferred = await Svc<BookingService>(s).TransferAsync(approved.Id, new TransferBookingRequest("Sold to relative", [new OwnerInput(buyer.Id, 100m)], approved.RowVersion));
        Assert.Equal(2, transferred.AgreementVersion);
        Assert.Equal(1, transferred.OwnershipRevision);
        Assert.Equal(2, transferred.Customers.Count);
        Assert.False(transferred.Customers.Single(c => c.CustomerId == original.Id).IsActive);
        Assert.NotNull(transferred.Customers.Single(c => c.CustomerId == original.Id).EffectiveToUtc);
        Assert.True(transferred.Customers.Single(c => c.CustomerId == buyer.Id).IsActive);
        using var s2 = fx.Scope();
        var db = Svc<AppDbContext>(s2);
        var owns = await db.CustomerOwnerships.Where(o => o.PropertyId == prop.Id).ToListAsync();
        Assert.Equal(2, owns.Count);
        Assert.Single(owns, o => o.IsActive && o.CustomerId == buyer.Id);
        var old = owns.Single(o => o.CustomerId == original.Id);
        Assert.False(old.IsActive); Assert.Contains("Transfer", old.EndReason);
        var versions = await db.AgreementVersions.Where(v => v.AgreementId == transferred.AgreementId).OrderBy(v => v.VersionNo).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Contains("Original Owner", versions[0].Content); Assert.Contains("New Owner", versions[1].Content); Assert.DoesNotContain("Original Owner", versions[1].Content);
        var amendment = await db.AgreementAmendments.SingleAsync(a => a.AgreementId == transferred.AgreementId);
        Assert.Equal(AmendmentType.Transfer, amendment.AmendmentType);
        Assert.Equal((1, 2), (amendment.FromVersionNo, amendment.ToVersionNo));
        Assert.Equal(PropertyStatus.Sold, await StatusAsync(prop.Id));
        var oldView = await Svc<BookingService>(s2).GetAgreementAsync(approved.Id, 1);
        Assert.True(oldView.ContentVerified); Assert.Contains("Original Owner", oldView.Content);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Booking.Transfer" && a.EntityId == approved.Id.ToString() && a.Reason == "Sold to relative"));
        // the previous owner still sees the history; the new owner holds the active share
        Assert.Contains((await Svc<CustomerService>(s2).GetAsync(original.Id)).Ownerships, o => !o.IsActive);
        Assert.Contains((await Svc<CustomerService>(s2).GetAsync(buyer.Id)).Ownerships, o => o.IsActive);
    }

    [MySqlFact]
    public async Task Transfer_OwnershipPercentageChange_KeepsHistory_AndRejectsInvalidShares()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var a = await CustomerAsync("Share A"); var b = await CustomerAsync("Share B");
        var booking = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(a.Id, 50m), (b.Id, 50m)], submit: true));
        var approved = await ApproveAsync(booking, w.Director);
        As(w.Director);
        async Task<Exception> Fail(IReadOnlyList<OwnerInput> o) { using var s = fx.Scope(); return await Assert.ThrowsAnyAsync<Exception>(() => Svc<BookingService>(s).TransferAsync(approved.Id, new TransferBookingRequest("Adjust shares", o, approved.RowVersion))); }
        Assert.IsType<RequestValidationException>(await Fail([new(a.Id, 60m), new(b.Id, 50m)]));
        Assert.IsType<RequestValidationException>(await Fail([new(a.Id, 60m), new(b.Id, 30m)]));
        Assert.IsType<DomainException>(await Fail([new(a.Id, 50m), new(b.Id, 50m)])); // identical
        BookingDetailDto result;
        using (var s = fx.Scope()) result = await Svc<BookingService>(s).TransferAsync(approved.Id, new TransferBookingRequest("Adjust shares", [new(a.Id, 80m), new(b.Id, 20m)], approved.RowVersion));
        Assert.Equal(2, result.AgreementVersion);
        using var s2 = fx.Scope();
        var db = Svc<AppDbContext>(s2);
        var active = await db.CustomerOwnerships.Where(o => o.PropertyId == prop.Id && o.IsActive).ToListAsync();
        Assert.Equal(100m, active.Sum(o => o.SharePercent));
        Assert.Equal(80m, active.Single(o => o.CustomerId == a.Id).SharePercent);
        Assert.Equal(4, await db.CustomerOwnerships.CountAsync(o => o.PropertyId == prop.Id));
        Assert.Equal(AmendmentType.OwnershipChange, (await db.AgreementAmendments.SingleAsync(x => x.AgreementId == result.AgreementId)).AmendmentType);
    }

    [MySqlFact]
    public async Task Transfer_RequiresConfirmedBooking_AndConcurrentTransfersConflict()
    {
        var w = await WorldAsync();
        var draft = await NewSubmittedAsync(w);
        As(w.Director);
        var x = await CustomerAsync();
        using (var s = fx.Scope()) await Assert.ThrowsAsync<DomainException>(() => Svc<BookingService>(s).TransferAsync(draft.Id, new TransferBookingRequest("Not yet approved", [new(x.Id, 100m)], draft.RowVersion)));

        var approved = await ApproveAsync(await NewSubmittedAsync(w), w.Director);
        As(w.Director);
        var c1 = await CustomerAsync(); var c2 = await CustomerAsync();
        var results = await Task.WhenAll(new[] { c1.Id, c2.Id }.Select(async cid =>
        {
            try { using var s = fx.Scope(); await Svc<BookingService>(s).TransferAsync(approved.Id, new TransferBookingRequest("Concurrent transfer", [new(cid, 100m)], approved.RowVersion)); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, results.Count(r => r));
        using var s2 = fx.Scope();
        var db = Svc<AppDbContext>(s2);
        Assert.Equal(100m, await db.CustomerOwnerships.Where(o => o.BookingId == approved.Id && o.IsActive).SumAsync(o => o.SharePercent));
        Assert.Equal(2, await db.AgreementVersions.CountAsync(v => v.AgreementId == approved.AgreementId));
    }

    [MySqlFact]
    public async Task Ownership_DatabaseConstraints_PreventDuplicateActiveRows()
    {
        var w = await WorldAsync();
        var approved = await ApproveAsync(await NewSubmittedAsync(w), w.Director);
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        var existing = await db.CustomerOwnerships.FirstAsync(o => o.BookingId == approved.Id);
        db.CustomerOwnerships.Add(new CustomerOwnership
        {
            CustomerId = existing.CustomerId, PropertyId = existing.PropertyId, BookingId = existing.BookingId, AgreementId = existing.AgreementId, SharePercent = 10m, IsActive = true,
            ActiveKey = CustomerOwnership.KeyFor(existing.PropertyId, existing.CustomerId), StartedAtUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ------------------------------------------------------------------------------------------------ listing, documents, quote
    [MySqlFact]
    public async Task Booking_List_FiltersSearchesAndPages()
    {
        var w = await WorldAsync();
        As(w.Sales);
        var c = await CustomerAsync("Searchable Sam");
        for (var i = 0; i < 3; i++) await CreateBookingAsync(w, Book(w.ProjectId, [(await PropertyAsync(w.ProjectId)).Id], [(c.Id, 100m)], submit: i == 0));
        As(w.Sales);
        using var s = fx.Scope();
        var svc = Svc<BookingService>(s);
        var all = await svc.ListAsync(new BookingListRequest { ProjectId = w.ProjectId, PageSize = 2 });
        Assert.Equal(3, all.TotalCount); Assert.Equal(2, all.Items.Count);
        Assert.Equal(1, (await svc.ListAsync(new BookingListRequest { ProjectId = w.ProjectId, Status = BookingStatus.PendingApproval })).TotalCount);
        var found = await svc.ListAsync(new BookingListRequest { ProjectId = w.ProjectId, Search = "Searchable" });
        Assert.Equal(3, found.TotalCount); Assert.All(found.Items, i => Assert.Equal("Searchable Sam", i.PrimaryCustomer));
        Assert.Equal(3, (await svc.ListAsync(new BookingListRequest { CustomerId = c.Id })).TotalCount);
        Assert.Equal(0, (await svc.ListAsync(new BookingListRequest { ProjectId = w.ProjectId, Search = "no-such-thing" })).TotalCount);
    }

    [MySqlFact]
    public async Task Agreement_Documents_UploadDownloadRemove_WithPermission()
    {
        var w = await WorldAsync();
        var approved = await ApproveAsync(await NewSubmittedAsync(w), w.Director);
        As(w.Sales);
        AgreementDocumentDto doc;
        using (var s = fx.Scope()) doc = await Svc<BookingService>(s).UploadAgreementDocumentAsync(approved.Id, AgreementDocumentType.Signed, "Signed copy", new FileUpload("signed.pdf", "application/pdf", 5, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-"))));
        using (var s = fx.Scope()) Assert.Single((await Svc<BookingService>(s).GetAgreementAsync(approved.Id, null)).Documents);
        using (var s = fx.Scope()) { var f = await Svc<BookingService>(s).DownloadAgreementDocumentAsync(approved.Id, doc.Id); Assert.Equal("signed.pdf", f.FileName); }
        var cashier = await UserAsync(RoleNames.Accountant);
        As(w.Admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, cashier);
        As(cashier);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<BookingService>(s).UploadAgreementDocumentAsync(approved.Id, AgreementDocumentType.Other, null, new FileUpload("a.pdf", "application/pdf", 5, new MemoryStream(new byte[5]))));
        As(w.Sales);
        using (var s = fx.Scope()) await Svc<BookingService>(s).DeleteAgreementDocumentAsync(approved.Id, doc.Id, "Uploaded wrong scan");
        using (var s = fx.Scope()) Assert.Empty((await Svc<BookingService>(s).GetAgreementAsync(approved.Id, null)).Documents);
    }

    [MySqlFact]
    public async Task Agreement_BeforeApproval_IsNotFound_AndQuoteMatchesBookingPrice()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        As(w.Sales);
        var c = await CustomerAsync();
        PriceCalculationDto quote;
        using (var s = fx.Scope()) quote = await Svc<BookingService>(s).QuoteAsync(prop.Id, c.Id);
        var b = await CreateBookingAsync(w, Book(w.ProjectId, [prop.Id], [(c.Id, 100m)]));
        Assert.Equal(quote.FinalPrice, b.Properties[0].FinalPrice);
        using var s2 = fx.Scope();
        await Assert.ThrowsAsync<NotFoundException>(() => Svc<BookingService>(s2).GetAgreementAsync(b.Id, null));
    }

    [MySqlFact]
    public async Task Booking_Retention_NoPhysicalDeleteOfApprovedHistory_AuditTrailIsComplete()
    {
        var w = await WorldAsync();
        var prop = await PropertyAsync(w.ProjectId);
        var booking = await NewSubmittedAsync(w, prop.Id);
        var approved = await ApproveAsync(booking, w.Director);
        As(w.Director);
        using (var s = fx.Scope()) await Svc<BookingService>(s).CancelAsync(approved.Id, new CancelBookingRequest("Customer withdrew", null, null, approved.RowVersion));
        using var s2 = fx.Scope();
        var db = Svc<AppDbContext>(s2);
        var actions = await db.AuditLogs.Where(a => a.EntityId == approved.Id.ToString() && a.EntityType == "Booking").Select(a => a.Action).ToListAsync();
        Assert.Contains("Booking.Created", actions); Assert.Contains("Booking.Approved", actions); Assert.Contains("Agreement.Cancelled", actions);
        Assert.True(await db.Bookings.AnyAsync(b => b.Id == approved.Id));
        Assert.Equal(4, await db.BookingStatusHistories.CountAsync(h => h.BookingId == approved.Id));
        Assert.True(await db.BookingPricingSnapshots.AnyAsync(x => x.BookingPropertyId == approved.Properties[0].Id));
        var approval = await db.AuditLogs.FirstAsync(a => a.Action == "Booking.Approved" && a.EntityId == approved.Id.ToString());
        Assert.Equal(w.Director, approval.UserId);
    }
}
