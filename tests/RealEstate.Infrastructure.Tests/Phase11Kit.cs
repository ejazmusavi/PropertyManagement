using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Shared set-up for the Phase 11 integration tests: a project with staff, customers, confirmed installment bookings and posted payments.</summary>
public sealed class Phase11Kit(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;

    public sealed record World(Guid Admin, Guid Sales, Guid Director, Guid Accountant, Guid Support, Guid Cashier, long ProjectId, DateOnly Today);
    public sealed record Deal(long BookingId, long AgreementId, long CustomerId, long PropertyId);

    public Phase1Fixture Fx => fx;
    public static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

    public void As(Guid id) { fx.Act(id); lock (Names) fx.Actor.UserName = Names[id]; }

    public async Task<Guid> UserAsync(params string[] roles)
    {
        var id = await fx.CreateUserAsync(roles);
        lock (Names) Names[id] = fx.Actor.UserName!;
        return id;
    }

    public async Task<T> Run<T>(Guid actor, Func<IServiceScope, Task<T>> f)
    {
        As(actor);
        using var s = fx.Scope();
        return await f(s);
    }

    public async Task Do(Guid actor, Func<IServiceScope, Task> f)
    {
        As(actor);
        using var s = fx.Scope();
        await f(s);
    }

    /// <summary>Runs as nobody (a system/background caller).</summary>
    public async Task<T> RunAnonymous<T>(Func<IServiceScope, Task<T>> f)
    {
        fx.Actor.UserId = null; fx.Actor.UserName = null;
        using var s = fx.Scope();
        return await f(s);
    }

    public async Task<World> WorldAsync(long? existingProject = null)
    {
        var admin = await UserAsync(RoleNames.Administrator);
        As(admin);
        long pid; DateOnly today;
        if (existingProject is { } e) pid = e;
        else using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        var support = await UserAsync(RoleNames.CustomerSupport);
        var cashier = await UserAsync(RoleNames.Cashier);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { sales, accountant, support, cashier }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
            if (existingProject is null)
                await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                    null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, accountant, support, cashier, pid, today);
    }

    public async Task<long> CustomerAsync(World w, string? email = "c@example.com", string mobile = "0300-1234567") =>
        await Run(w.Sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
            IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), mobile, email, "House 1, Murree", null,
            [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed installment booking (5 marla = 5,000,000) with a generated schedule; optionally moved back in time so installments are overdue.</summary>
    public async Task<Deal> DealAsync(World w, int monthsBack = 0, long? customerId = null, string? email = "c@example.com", string mobile = "0300-1234567")
    {
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"PX-{Guid.NewGuid():N}"[..14], "X" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
        var c = customerId ?? await CustomerAsync(w, email, mobile);
        var b = await Run(w.Sales, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, PaymentMode.Installment, [new BookingPropertyInput(propertyId, 0m, null)],
            [new OwnerInput(c, 100m)], null, 500_000m, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        var agreementId = b.AgreementId!.Value;
        var types = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ChargeTypes.ToDictionaryAsync(t => t.Code, t => t.Id));
        PlanComponentInput Comp(string type, string name, AmountBasis basis, decimal? amount, decimal? pct, ScheduleFrequency f, int? count, int offset, ChargeCategory cat = ChargeCategory.SaleConsideration) =>
            new(types[type], name, cat, basis, amount, pct, f, count, null, offset, 0, null, null, 5, null, true, 50m, TaxTreatment.NotApplicable, 0m, null, null);
        var code = "P11-" + Interlocked.Increment(ref counter);
        var plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, code, "Plan " + code, null, InstallmentPlanType.StandardProject,
            null, null, null,
            [
                Comp("DOWN_PAYMENT", "Down payment", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 0),
                Comp("MONTHLY", "Monthly installment", AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, 1),
                Comp("POSSESSION", "Possession", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 30),
                Comp("DEVELOPMENT", "Development charges", AmountBasis.FixedAmount, 100_000m, null, ScheduleFrequency.OneTime, null, 12, ChargeCategory.DevelopmentCharges),
            ])));
        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(plan.RowVersion)));
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(plan.Id, null, null)));
        await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null)));
        if (monthsBack > 0)
            await Run(w.Admin, async s =>
            {
                var db = Svc<AppDbContext>(s);
                var items = await (from i in db.PaymentScheduleItems join sc in db.PaymentSchedules on i.ScheduleId equals sc.Id where sc.AgreementId == agreementId select i).ToListAsync();
                foreach (var i in items) { i.DueDate = i.DueDate.AddMonths(-monthsBack); i.GraceEndDate = i.GraceEndDate.AddMonths(-monthsBack); }
                return await db.SaveChangesAsync();
            });
        return new Deal(b.Id, agreementId, c, propertyId);
    }

    /// <summary>Collects cash at the office and approves it (posted payment).</summary>
    public async Task<PaymentDetailDto> PostPaymentAsync(World w, Deal d, decimal amount)
    {
        var cash = await Run(w.Admin, async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == "CASH").Select(m => m.Id).FirstAsync());
        var p = await Run(w.Cashier, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, null, cash, PaymentSource.Office, amount, w.Today, null, null, null, null, null,
            AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N"))));
        return await Run(w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(p.Id, new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion)));
    }
}
