using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
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

/// <summary>Phase 5 integration tests against MySQL: plans, agreement charges, schedule generation, amendments, history, authorization and concurrency.</summary>
[Collection("phase1")]
public class Phase05Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Sales, Guid Director, Guid Accountant, long ProjectId, DateOnly Today);

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();
    private void As(Guid id) { fx.Act(id); lock (Names) fx.Actor.UserName = Names[id]; }

    private async Task<Guid> UserAsync(params string[] roles)
    {
        var id = await fx.CreateUserAsync(roles);
        lock (Names) Names[id] = fx.Actor.UserName!;
        return id;
    }

    private async Task<T> Run<T>(Guid actor, Func<IServiceScope, Task<T>> f)
    {
        As(actor);
        using var s = fx.Scope();
        return await f(s);
    }

    private async Task<World> WorldAsync()
    {
        var admin = await UserAsync(RoleNames.Administrator);
        As(admin);
        long pid; DateOnly today;
        using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        As(admin);
        using (var s = fx.Scope())
        {
            await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, sales);
            await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, accountant);
            today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, accountant, pid, today);
    }

    private async Task<long> PropertyAsync(World w)
    {
        return await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"IN-{Guid.NewGuid():N}"[..14], "I" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
    }

    private async Task<long> CustomerAsync(World w) => await Run(w.Sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
        [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed booking (5 marla x 1,000,000 = 5,000,000) and its agreement id.</summary>
    private async Task<(long BookingId, long AgreementId, long CustomerId)> AgreementAsync(World w, PaymentMode mode = PaymentMode.Installment, decimal bookingAmount = 500_000m, long? customerId = null)
    {
        var p = await PropertyAsync(w);
        var c = customerId ?? await CustomerAsync(w);
        var b = await Run(w.Sales, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, mode, [new BookingPropertyInput(p, 0m, null)], [new OwnerInput(c, 100m)],
            null, mode == PaymentMode.Cash ? 5_000_000m : bookingAmount, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        return (b.Id, b.AgreementId!.Value, c);
    }

    private static PlanComponentInput Comp(long typeId, string name, AmountBasis basis, decimal? amount, decimal? pct, ScheduleFrequency f, int? count, int offsetMonths,
        ChargeCategory cat = ChargeCategory.SaleConsideration, bool waiver = false, long? lateFee = null, int grace = 5) =>
        new(typeId, name, cat, basis, amount, pct, f, count, null, offsetMonths, 0, null, null, grace, lateFee, waiver, waiver ? 50m : 0m, TaxTreatment.NotApplicable, 0m, null, null);

    private async Task<Dictionary<string, long>> TypesAsync() { using var s = fx.Scope(); return await Svc<AppDbContext>(s).ChargeTypes.ToDictionaryAsync(t => t.Code, t => t.Id); }

    /// <summary>Standard plan: down payment 10% (replaced by the booking amount), 24 monthly installments taking the remainder, possession 10% at month 30, development 100,000 at month 12.</summary>
    private async Task<InstallmentPlanDetailDto> ActivePlanAsync(World w, InstallmentPlanType type = InstallmentPlanType.StandardProject, long? customerId = null, bool activate = true, long? lateFee = null)
    {
        var t = await TypesAsync();
        var comps = type == InstallmentPlanType.Cash
            ? new List<PlanComponentInput> { Comp(t["DOWN_PAYMENT"], "Full payment", AmountBasis.Remainder, null, null, ScheduleFrequency.OneTime, null, 0) }
            : [
                Comp(t["DOWN_PAYMENT"], "Down payment", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 0),
                Comp(t["MONTHLY"], "Monthly installment", AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, 1, waiver: true, lateFee: lateFee),
                Comp(t["POSSESSION"], "Possession", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 30),
                Comp(t["DEVELOPMENT"], "Development charges", AmountBasis.FixedAmount, 100_000m, null, ScheduleFrequency.OneTime, null, 12, ChargeCategory.DevelopmentCharges),
            ];
        var code = "P" + Interlocked.Increment(ref counter);
        var plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, code, "Plan " + code, null, type, null, null, customerId, comps)));
        if (!activate) return plan;
        var approver = type == InstallmentPlanType.CustomerSpecific ? w.Admin : w.Director;
        return await Run(approver, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(plan.RowVersion)));
    }

    private async Task<PaymentScheduleDto> GeneratedAsync(World w, long agreementId, long planId)
    {
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(planId, null, null)));
        return await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null)));
    }

    // ------------------------------------------------------------------------------------------------ catalog
    [MySqlFact]
    public async Task Phase5Permissions_AndChargeTypes_AreSeeded()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        var director = await Perms(RoleNames.Director);
        foreach (var p in new[] { Permissions.InstallmentsManage, Permissions.InstallmentsApprove, Permissions.InstallmentsSchedule, Permissions.AgreementsAmend, Permissions.AgreementsAmendApprove })
            Assert.Contains(p, director);
        var sales = await Perms(RoleNames.SalesManager);
        Assert.Contains(Permissions.AgreementsAmend, sales);
        Assert.DoesNotContain(Permissions.AgreementsAmendApprove, sales);
        Assert.Contains(Permissions.AgreementsAmendApprove, await Perms(RoleNames.Accountant));
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
        var types = await db.ChargeTypes.ToListAsync();
        Assert.Equal(11, types.Count(t => t.IsSystem));
        Assert.Equal(Enum.GetValues<ChargeComponentKind>().Length, types.Where(t => t.IsSystem).Select(t => t.Kind).Distinct().Count());
    }

    // ------------------------------------------------------------------------------------------------ plans
    [MySqlFact]
    public async Task Plan_Lifecycle_DraftEditsActivateRetireCopy()
    {
        var w = await WorldAsync();
        var t = await TypesAsync();
        var plan = await ActivePlanAsync(w, activate: false);
        Assert.Equal(InstallmentPlanStatus.Draft, plan.Status);
        Assert.Equal(4, plan.Components.Count);
        Assert.True(plan.HasRemainder);

        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).AddComponentAsync(plan.Id, new PlanComponentRequest(
            Comp(t["UTILITY"], "Utility", AmountBasis.FixedAmount, 25_000m, null, ScheduleFrequency.OneTime, null, 24, ChargeCategory.UtilityCharges), plan.RowVersion)));
        Assert.Equal(5, plan.Components.Count);
        var stale = plan.RowVersion;
        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).RemoveComponentAsync(plan.Id, plan.Components.Last().Id, new PlanRowVersionRequest(plan.RowVersion)));
        await Assert.ThrowsAsync<ConcurrencyException>(() => Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(stale))));

        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(plan.RowVersion)));
        Assert.Equal(InstallmentPlanStatus.Active, plan.Status);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<InstallmentPlanService>(s).AddComponentAsync(plan.Id, new PlanComponentRequest(
            Comp(t["UTILITY"], "Utility", AmountBasis.FixedAmount, 1m, null, ScheduleFrequency.OneTime, null, 1, ChargeCategory.UtilityCharges), plan.RowVersion))));

        var copy = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CopyAsync(plan.Id, new CopyPlanRequest("COPY-" + plan.Code, "Copy")));
        Assert.Equal(InstallmentPlanStatus.Draft, copy.Status);
        Assert.Equal(plan.Components.Count, copy.Components.Count);
        var retired = await Run(w.Director, s => Svc<InstallmentPlanService>(s).RetireAsync(plan.Id, new RetirePlanRequest("Replaced by new plan", plan.RowVersion)));
        Assert.Equal(InstallmentPlanStatus.Retired, retired.Status);

        var preview = await Run(w.Director, s => Svc<InstallmentPlanService>(s).PreviewAsync(copy.Id, new PlanPreviewRequest(10_000_000m, 1_000_000m, w.Today)));
        Assert.True(preview.Schedule.IsReconciled);
        Assert.Equal(10_100_000m, preview.Schedule.TotalAmount); // price + development charges
        Assert.Equal(27, preview.Schedule.Items.Count);
        Assert.Equal(0m, preview.Schedule.PriceDifference);

        using var scope = fx.Scope();
        Assert.Contains(await Svc<AppDbContext>(scope).AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "InstallmentPlan.Activated");
    }

    [MySqlFact]
    public async Task Plan_Activation_RequiresFullPriceCoverage_AndCustomerPlansNeedSecondApprover()
    {
        var w = await WorldAsync();
        var t = await TypesAsync();
        var incomplete = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, "INC" + Interlocked.Increment(ref counter), "Incomplete", null,
            InstallmentPlanType.StandardProject, null, null, null, [Comp(t["MONTHLY"], "Monthly", AmountBasis.PercentOfPrice, null, 60m, ScheduleFrequency.Monthly, 12, 1)])));
        var ex = await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(incomplete.Id, new PlanRowVersionRequest(incomplete.RowVersion))));
        Assert.Contains("whole price", ex.Message);

        var customer = await CustomerAsync(w);
        var custPlan = await ActivePlanAsync(w, InstallmentPlanType.CustomerSpecific, customer, activate: false);
        Assert.True(custPlan.RequiresApproval);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(custPlan.Id, new PlanRowVersionRequest(custPlan.RowVersion))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<InstallmentPlanService>(s).ActivateAsync(custPlan.Id, new PlanRowVersionRequest(custPlan.RowVersion))));
        var active = await Run(w.Admin, s => Svc<InstallmentPlanService>(s).ActivateAsync(custPlan.Id, new PlanRowVersionRequest(custPlan.RowVersion)));
        Assert.Equal(InstallmentPlanStatus.Active, active.Status);
        Assert.Equal(Names[w.Admin], active.ActivatedBy);
    }

    [MySqlFact]
    public async Task Plans_AreProjectScoped()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var outsider = await UserAsync(RoleNames.SalesManager); // not assigned to the project
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<InstallmentPlanService>(s).GetAsync(plan.Id)));
        var list = await Run(outsider, s => Svc<InstallmentPlanService>(s).ListAsync(new InstallmentPlanListRequest { ProjectId = w.ProjectId }));
        Assert.Empty(list.Items);
        var cashier = await UserAsync(RoleNames.Cashier);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(cashier, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, "X1", "X", null,
            InstallmentPlanType.StandardProject, null, null, null, null))));
    }

    [MySqlFact]
    public async Task CompanyWideLateFeeRules_CanBeChangedOnlyWithAllProjectAccess()
    {
        var w = await WorldAsync();
        var manager = await UserAsync(RoleNames.ProjectManager); // installments.manage, but no projects.access.all
        As(w.Admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, manager);

        var global = await Run(w.Director, s => Svc<InstallmentSetupService>(s).CreateLateFeeRuleAsync(new SaveLateFeeRuleRequest("LF" + Interlocked.Increment(ref counter), "Company rule",
            null, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, null)));
        // Readable, but neither renaming nor deactivating is allowed for a project-scoped manager.
        Assert.Equal(global.Id, (await Run(manager, s => Svc<InstallmentSetupService>(s).GetLateFeeRuleAsync(global.Id))).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(manager, s => Svc<InstallmentSetupService>(s).UpdateLateFeeRuleAsync(global.Id,
            new SaveLateFeeRuleRequest(global.Code, "Hijacked", null, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, false, global.RowVersion))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(manager, s => Svc<InstallmentSetupService>(s).CreateLateFeeRuleAsync(new SaveLateFeeRuleRequest(
            "LF" + Interlocked.Increment(ref counter), "Company rule 2", null, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, null))));
        var unchanged = await Run(w.Director, s => Svc<InstallmentSetupService>(s).GetLateFeeRuleAsync(global.Id));
        Assert.Equal("Company rule", unchanged.Name);
        Assert.True(unchanged.IsActive);

        // Project rules of an assigned project stay manageable by the project-scoped manager.
        var local = await Run(manager, s => Svc<InstallmentSetupService>(s).CreateLateFeeRuleAsync(new SaveLateFeeRuleRequest("LF" + Interlocked.Increment(ref counter), "Project rule",
            w.ProjectId, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, null)));
        var renamed = await Run(manager, s => Svc<InstallmentSetupService>(s).UpdateLateFeeRuleAsync(local.Id,
            new SaveLateFeeRuleRequest(local.Code, "Project rule (renamed)", w.ProjectId, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, local.RowVersion)));
        Assert.Equal("Project rule (renamed)", renamed.Name);
    }

    // ------------------------------------------------------------------------------------------------ builder and generation
    [MySqlFact]
    public async Task ApplyPlan_Generate_ReconcilesWithAgreement_AndAttachesScheduleToAgreementText()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (bookingId, agreementId, _) = await AgreementAsync(w);
        var applicable = await Run(w.Sales, s => Svc<InstallmentPlanService>(s).ApplicablePlansAsync(agreementId));
        Assert.Contains(applicable, p => p.Id == plan.Id);

        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(plan.Id, null, null)));
        Assert.Equal(4, ag.Charges.Count);
        Assert.Equal(500_000m, ag.Charges[0].Amount); // booking amount wins over the 10% template amount
        Assert.Equal(4_000_000m, ag.Charges[1].Amount); // remainder: 5,000,000 - 500,000 - 500,000
        Assert.Equal(5_000_000m, ag.SaleConsiderationTotal);
        Assert.Equal(100_000m, ag.AdditionalChargesTotal);
        Assert.Equal(0m, ag.PriceDifference);
        Assert.True(ag.CanEditCharges);

        var preview = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).PreviewAsync(agreementId));
        Assert.True(preview.CanGenerate);
        Assert.Equal(27, preview.Items.Count);

        var schedule = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, "Signed plan")));
        Assert.Equal(1, schedule.VersionNo);
        Assert.Equal(PaymentScheduleStatus.Active, schedule.Status);
        Assert.Equal(5_100_000m, schedule.TotalAmount);
        Assert.True(schedule.HashVerified);
        Assert.True(schedule.IsReconciled);
        Assert.All(schedule.Reconciliation, r => Assert.Equal(0m, r.Difference));
        Assert.Equal(166_666.67m, schedule.Items.First(i => i.Description.StartsWith("Monthly installment 1/")).Amount);
        Assert.Equal(166_666.59m, schedule.Items.Single(i => i.Description == "Monthly installment 24/24").Amount);
        Assert.Equal(w.Today.AddMonths(1), schedule.Items.First(i => i.Description.StartsWith("Monthly installment 1/")).DueDate);

        var agreement = await Run(w.Sales, s => Svc<BookingService>(s).GetAgreementAsync(bookingId, null));
        Assert.Equal(2, agreement.CurrentVersionNo);
        Assert.Contains("PAYMENT SCHEDULE (version 1)", agreement.Content);
        Assert.True(agreement.ContentVerified);
        var v1 = await Run(w.Sales, s => Svc<BookingService>(s).GetAgreementAsync(bookingId, 1));
        Assert.DoesNotContain("PAYMENT SCHEDULE", v1.Content); // original version untouched
        Assert.True(v1.ContentVerified);

        // after generation the builder is closed
        var after = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId));
        Assert.False(after.CanEditCharges);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(null, null))));
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementScheduleService>(s).RemoveChargeAsync(agreementId, after.Charges[0].Id, new AgreementRowVersionRequest(null))));
    }

    [MySqlFact]
    public async Task ManualCustomCharges_MustReconcileBeforeGeneration()
    {
        var w = await WorldAsync();
        var t = await TypesAsync();
        var (_, agreementId, _) = await AgreementAsync(w, bookingAmount: 0m);
        AgreementChargeInput Charge(string name, decimal amount, ScheduleFrequency f, int? count, IReadOnlyList<ScheduleEntryInput>? entries = null) =>
            new(t["CUSTOM"], name, ChargeCategory.SaleConsideration, amount, f, count, null, w.Today.AddDays(10), null, entries, 0, null, false, 0m, TaxTreatment.NotApplicable, 0m, null, null);
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).AddChargeAsync(agreementId, new SaveAgreementChargeRequest(Charge("Quarterly", 3_000_000m, ScheduleFrequency.Quarterly, 6), null)));
        var preview = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).PreviewAsync(agreementId));
        Assert.False(preview.CanGenerate);
        Assert.Equal(2_000_000m, preview.PriceDifference);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null))));

        // irregular custom schedule covering the rest
        var d = w.Today;
        ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).AddChargeAsync(agreementId, new SaveAgreementChargeRequest(Charge("Irregular", 2_000_000m, ScheduleFrequency.CustomDates, null,
            [new(d.AddDays(45), 250_000m), new(d.AddMonths(5), 1_000_000m), new(d.AddMonths(11), 750_000m)]), ag.RowVersion)));
        Assert.Equal(0m, ag.PriceDifference);
        var schedule = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null)));
        Assert.Equal(9, schedule.ItemCount);
        Assert.Equal(5_000_000m, schedule.TotalAmount);
        Assert.Contains(schedule.Items, i => i.DueDate == d.AddMonths(5) && i.Amount == 1_000_000m);
    }

    [MySqlFact]
    public async Task CustomerSpecificPlan_AppliesOnlyToThatCustomer_AndCashPlanOnlyToCashSales()
    {
        var w = await WorldAsync();
        var vip = await CustomerAsync(w);
        var plan = await ActivePlanAsync(w, InstallmentPlanType.CustomerSpecific, vip);
        var cash = await ActivePlanAsync(w, InstallmentPlanType.Cash);
        var (_, vipAgreement, _) = await AgreementAsync(w, customerId: vip);
        var (_, otherAgreement, _) = await AgreementAsync(w);
        Assert.Contains(await Run(w.Sales, s => Svc<InstallmentPlanService>(s).ApplicablePlansAsync(vipAgreement)), p => p.Id == plan.Id);
        Assert.DoesNotContain(await Run(w.Sales, s => Svc<InstallmentPlanService>(s).ApplicablePlansAsync(otherAgreement)), p => p.Id == plan.Id);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(otherAgreement, new ApplyPlanRequest(plan.Id, null, null))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(otherAgreement, new ApplyPlanRequest(cash.Id, null, null))));
        Assert.Equal(5_100_000m, (await GeneratedAsync(w, vipAgreement, plan.Id)).TotalAmount);

        var (_, cashAgreement, _) = await AgreementAsync(w, PaymentMode.Cash);
        var cashSchedule = await GeneratedAsync(w, cashAgreement, cash.Id);
        Assert.Single(cashSchedule.Items);
        Assert.Equal(5_000_000m, cashSchedule.TotalAmount);
    }

    [MySqlFact]
    public async Task ConcurrentGeneration_ProducesExactlyOneSchedule()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (_, agreementId, _) = await AgreementAsync(w);
        await Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(plan.Id, null, null)));
        As(w.Sales);
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(null, null)); return true; }
            catch (DomainException) { return false; }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
        using var scope = fx.Scope();
        Assert.Equal(1, await Svc<AppDbContext>(scope).PaymentSchedules.CountAsync(x => x.AgreementId == agreementId));
    }

    // ------------------------------------------------------------------------------------------------ amendments
    [MySqlFact]
    public async Task Deferment_RequiresSecondApprover_CreatesVersion2_AndPreservesVersion1()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (_, agreementId, _) = await AgreementAsync(w);
        var v1 = await GeneratedAsync(w, agreementId, plan.Id);
        var target = v1.Items.First(i => i.Description == "Monthly installment 3/24");

        var request = new CreateAmendmentRequest(AmendmentType.Deferment, "Customer travelling abroad", "key-" + Guid.NewGuid(), Deferment: new DefermentInput([target.Id!.Value], 2, true));
        var m = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, request));
        Assert.Equal(AmendmentStatus.Pending, m.Status);
        Assert.NotNull(m.Preview);
        Assert.Equal(22, m.Changes.Count); // installments 3..24
        var again = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, request)); // idempotent retry
        Assert.Equal(m.Id, again.Id);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, request with { IdempotencyKey = null }))); // one pending
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, m.Id, new DecideAmendmentRequest(null, m.RowVersion))));
        Assert.False((await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).GetAsync(agreementId, m.Id))).CanDecide);

        var approved = await Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, m.Id, new DecideAmendmentRequest("Approved per policy", m.RowVersion)));
        Assert.Equal(AmendmentStatus.Approved, approved.Status);
        Assert.Equal(2, approved.ResultScheduleVersion);
        Assert.Equal(3, approved.ToVersionNo); // agreement v1 booking, v2 schedule, v3 amendment
        Assert.Equal(22, approved.Changes.Count);
        Assert.All(approved.Changes, c => Assert.NotNull(c.ToItemId));

        var v2 = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        Assert.Equal(2, v2.VersionNo);
        Assert.Equal(v1.TotalAmount, v2.TotalAmount);
        Assert.True(v2.HashVerified);
        Assert.Equal(target.DueDate.AddMonths(2), v2.Items.Single(i => i.PreviousItemId == target.Id).DueDate);
        var old = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId, 1));
        Assert.Equal(PaymentScheduleStatus.Superseded, old.Status);
        Assert.Equal(v1.ContentHash, old.ContentHash);
        Assert.True(old.HashVerified); // superseded lines are never modified
        Assert.Equal(target.DueDate, old.Items.Single(i => i.Id == target.Id).DueDate);

        var history = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetHistoryAsync(agreementId));
        Assert.Equal(2, history.Schedules.Count);
        Assert.Equal(22, history.Changes.Count);
        Assert.Equal(3, history.AgreementVersions.Count);
        Assert.Contains(history.Amendments, a => a.Id == m.Id && a.ResultScheduleVersion == 2 && a.BaseScheduleVersion == 1);
    }

    [MySqlFact]
    public async Task SelfApproval_IsRefused_AndRejectionNeedsComment_AndRequesterCanCancel()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (_, agreementId, _) = await AgreementAsync(w);
        var v1 = await GeneratedAsync(w, agreementId, plan.Id);
        var item = v1.Items.Last();
        var m = await Run(w.Director, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId,
            new CreateAmendmentRequest(AmendmentType.DueDateChange, "Correct the date", DueDateChanges: [new DueDateChangeInput(item.Id!.Value, item.DueDate.AddDays(3))])));
        Assert.False(m.CanDecide);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, m.Id, new DecideAmendmentRequest(null, m.RowVersion))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).RejectAsync(agreementId, m.Id, new RejectAmendmentRequest("", m.RowVersion))));
        var cancelled = await Run(w.Director, s => Svc<AgreementAmendmentService>(s).CancelAsync(agreementId, m.Id, new DecideAmendmentRequest(null, m.RowVersion)));
        Assert.Equal(AmendmentStatus.Cancelled, cancelled.Status);

        var m2 = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId,
            new CreateAmendmentRequest(AmendmentType.DueDateChange, "Correct the date", DueDateChanges: [new DueDateChangeInput(item.Id!.Value, item.DueDate.AddDays(3))])));
        var rejected = await Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).RejectAsync(agreementId, m2.Id, new RejectAmendmentRequest("Not justified", m2.RowVersion)));
        Assert.Equal(AmendmentStatus.Rejected, rejected.Status);
        var schedule = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        Assert.Equal(1, schedule.VersionNo); // nothing applied
    }

    [MySqlFact]
    public async Task Waiver_And_AdditionalCharge_And_Reduction_AreAppliedOnApproval()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var t = await TypesAsync();
        var (_, agreementId, _) = await AgreementAsync(w);
        var v1 = await GeneratedAsync(w, agreementId, plan.Id);
        var monthly = v1.Items.First(i => i.Description == "Monthly installment 1/24");

        async Task<AmendmentDetailDto> RequestAndApprove(CreateAmendmentRequest r)
        {
            var m = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, r));
            return await Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, m.Id, new DecideAmendmentRequest(null, m.RowVersion)));
        }

        // waiver beyond the charge's 50% limit is refused at request time
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId,
            new CreateAmendmentRequest(AmendmentType.Waiver, "Hardship waiver", Waiver: new WaiverInput(monthly.Id!.Value, WaiverKind.Principal, 100_000m)))));
        var waived = await RequestAndApprove(new CreateAmendmentRequest(AmendmentType.Waiver, "Hardship waiver", Waiver: new WaiverInput(monthly.Id!.Value, WaiverKind.Principal, 10_000m)));
        Assert.Equal(WaiverStatus.Approved, waived.Waiver!.Status);
        var v2 = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        var line = v2.Items.Single(i => i.PreviousItemId == monthly.Id);
        Assert.Equal(10_000m, line.AmountWaived);
        Assert.Equal(ScheduleItemStatus.PartiallyPaid, line.Status);
        Assert.Equal(v1.TotalAmount - 10_000m, v2.TotalOutstanding);

        var added = await RequestAndApprove(new CreateAmendmentRequest(AmendmentType.AdditionalCharge, "Electricity connection", AdditionalCharge:
            new AgreementChargeInput(t["UTILITY"], "Electricity connection", ChargeCategory.UtilityCharges, 60_000m, ScheduleFrequency.Monthly, 3, null, w.Today.AddMonths(2), null, null, 0, null,
                false, 0m, TaxTreatment.NotApplicable, 0m, null, null)));
        Assert.Equal(3, added.ResultScheduleVersion);
        var v3 = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        Assert.Equal(v1.TotalAmount + 60_000m, v3.TotalAmount);
        Assert.True(v3.IsReconciled);
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId));
        Assert.Contains(ag.Charges, c => c.Description == "Electricity connection" && c.AddedByAmendmentId == added.Id);

        var line2 = v3.Items.First(i => i.Description == "Monthly installment 2/24");
        var reduced = await RequestAndApprove(new CreateAmendmentRequest(AmendmentType.InstallmentReduction, "Temporary relief",
            Reduction: new ReductionInput(line2.Id!.Value, 66_666.67m, ReductionRedistribution.AddToLast, null)));
        var v4 = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        Assert.Equal(4, v4.VersionNo);
        Assert.Equal(v3.TotalAmount, v4.TotalAmount);
        Assert.Equal(266_666.59m, v4.Items.Single(i => i.Description == "Monthly installment 24/24").Amount);
        Assert.Equal(4, (await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetHistoryAsync(agreementId))).Schedules.Count);
        Assert.Equal(AmendmentStatus.Approved, reduced.Status);
    }

    [MySqlFact]
    public async Task RescheduleAndPenaltyChange_UpdateChargeTerms()
    {
        var w = await WorldAsync();
        var rule = await Run(w.Director, s => Svc<InstallmentSetupService>(s).CreateLateFeeRuleAsync(new SaveLateFeeRuleRequest("LF" + Interlocked.Increment(ref counter), "1% per month",
            null, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, null)));
        var plan = await ActivePlanAsync(w, lateFee: rule.Id);
        var (_, agreementId, _) = await AgreementAsync(w);
        await GeneratedAsync(w, agreementId, plan.Id);
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId));
        var monthly = ag.Charges.Single(c => c.Frequency == ScheduleFrequency.Monthly);
        Assert.Equal(rule.Id, monthly.LateFeeRuleId);

        var m = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, new CreateAmendmentRequest(AmendmentType.Reschedule, "Switch to quarterly",
            Reschedule: new RescheduleInput(monthly.Id, ScheduleFrequency.Quarterly, 8, null, w.Today.AddMonths(3), null, null))));
        await Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, m.Id, new DecideAmendmentRequest(null, m.RowVersion)));
        var schedule = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        var quarterly = schedule.Items.Where(i => i.ChargeId == monthly.Id).ToList();
        Assert.Equal(8, quarterly.Count);
        Assert.All(quarterly, i => Assert.Equal(500_000m, i.Amount));
        ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId));
        Assert.Equal(ScheduleFrequency.Quarterly, ag.Charges.Single(c => c.Id == monthly.Id).Frequency);

        var p = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId, new CreateAmendmentRequest(AmendmentType.PenaltyChange, "Remove penalty",
            PenaltyChange: new PenaltyChangeInput(monthly.Id, null, 30))));
        await Run(w.Accountant, s => Svc<AgreementAmendmentService>(s).ApproveAsync(agreementId, p.Id, new DecideAmendmentRequest(null, p.RowVersion)));
        ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId));
        Assert.Null(ag.Charges.Single(c => c.Id == monthly.Id).LateFeeRuleId);
        Assert.Equal(30, ag.Charges.Single(c => c.Id == monthly.Id).GracePeriodDays);

        // a late fee rule in use cannot change its terms
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<InstallmentSetupService>(s).UpdateLateFeeRuleAsync(rule.Id,
            new SaveLateFeeRuleRequest(rule.Code, rule.Name, null, LateFeeMethod.PercentPerMonth, 2m, null, 10m, null, true, rule.RowVersion))));
        var renamed = await Run(w.Director, s => Svc<InstallmentSetupService>(s).UpdateLateFeeRuleAsync(rule.Id,
            new SaveLateFeeRuleRequest(rule.Code, "Renamed", null, LateFeeMethod.PercentPerMonth, 1m, null, 10m, null, true, rule.RowVersion)));
        Assert.True(renamed.IsInUse);
        var fee = await Run(w.Sales, s => Svc<InstallmentSetupService>(s).PreviewLateFeeAsync(new LateFeePreviewRequest(rule.Id, 100_000m, 100_000m, new DateOnly(2025, 1, 31), new DateOnly(2025, 3, 1))));
        Assert.Equal(2_000m, fee.LateFee);
    }

    [MySqlFact]
    public async Task CancellingTheBooking_CancelsScheduleAndPendingAmendment_KeepingHistory()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (bookingId, agreementId, _) = await AgreementAsync(w);
        var v1 = await GeneratedAsync(w, agreementId, plan.Id);
        var m = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId,
            new CreateAmendmentRequest(AmendmentType.Waiver, "Goodwill waiver", Waiver: new WaiverInput(v1.Items[1].Id!.Value, WaiverKind.Principal, 1_000m))));
        var b = await Run(w.Director, s => Svc<BookingService>(s).GetAsync(bookingId));
        await Run(w.Director, s => Svc<BookingService>(s).CancelAsync(bookingId, new CancelBookingRequest("Customer withdrew", null, null, b.RowVersion)));
        var schedule = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GetScheduleAsync(agreementId));
        Assert.Equal(PaymentScheduleStatus.Cancelled, schedule.Status);
        Assert.Equal(v1.ItemCount, schedule.ItemCount);
        var amendment = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).GetAsync(agreementId, m.Id));
        Assert.Equal(AmendmentStatus.Cancelled, amendment.Status);
        Assert.Equal(WaiverStatus.Cancelled, amendment.Waiver!.Status);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(agreementId,
            new CreateAmendmentRequest(AmendmentType.Waiver, "Goodwill waiver", Waiver: new WaiverInput(v1.Items[1].Id!.Value, WaiverKind.Principal, 1_000m)))));
    }

    [MySqlFact]
    public async Task Agreements_AreProjectScoped()
    {
        var w = await WorldAsync();
        var (_, agreementId, _) = await AgreementAsync(w);
        var outsider = await UserAsync(RoleNames.SalesManager);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<AgreementScheduleService>(s).GetAsync(agreementId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<AgreementScheduleService>(s).GetHistoryAsync(agreementId)));
        var list = await Run(outsider, s => Svc<AgreementScheduleService>(s).ListAsync(new InstallmentAgreementListRequest()));
        Assert.DoesNotContain(list.Items, a => a.AgreementId == agreementId);
        var cashier = await UserAsync(RoleNames.Cashier);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(cashier, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(null, null))));
    }

    // ------------------------------------------------------------------------------------------------ database
    [MySqlFact]
    public async Task Database_AllowsOnlyOneActiveSchedulePerAgreement_AndRejectsInvalidAmounts()
    {
        var w = await WorldAsync();
        var plan = await ActivePlanAsync(w);
        var (_, agreementId, _) = await AgreementAsync(w);
        var v1 = await GeneratedAsync(w, agreementId, plan.Id);
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        db.PaymentSchedules.Add(new PaymentSchedule
        {
            AgreementId = agreementId, VersionNo = 99, ActiveAgreementId = agreementId, Reason = "x", TotalAmount = 1m, ItemCount = 0, ContentHash = "x", GeneratedAtUtc = DateTime.UtcNow, GeneratedBy = "t",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE PaymentScheduleItems SET AmountPaid = Amount + 1 WHERE Id = {v1.Items[0].Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE AgreementCharges SET Amount = 0 WHERE AgreementId = {agreementId}"));
    }
}
