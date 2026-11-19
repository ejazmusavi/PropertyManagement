using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

[Collection("phase1")]
public class Phase03Tests(Phase1Fixture fx)
{
    private int counter;
    private record World(Guid Admin, Guid Sales, Guid Director, long ProjectId);

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

    private async Task<DateOnly> TodayAsync() { using var s = fx.Scope(); return await Svc<PricingSupport>(s).TodayAsync(); }

    private async Task<World> WorldAsync()
    {
        var admin = await fx.ActAsAsync(RoleNames.Administrator);
        long pid;
        using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await fx.CreateUserAsync(RoleNames.SalesManager);
        var director = await fx.CreateUserAsync(RoleNames.Director);
        fx.Act(admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, sales);
        return new World(admin, sales, director, pid);
    }

    private async Task<PropertyDetailDto> PropertyAsync(long projectId, decimal size = 5m, bool corner = false, string type = "RES-PLOT")
    {
        using var s = fx.Scope();
        var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == type).Select(t => t.Id).FirstAsync();
        var n = Interlocked.Increment(ref counter);
        return await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(projectId, $"PP-{Guid.NewGuid():N}"[..14], n.ToString(), null, null, null, typeId, size, MeasurementUnit.Marla,
            null, null, corner, false, false, DevelopmentStatus.Developed, null, null, null));
    }

    private static CreatePricingRuleRequest Rule(long projectId, DateOnly from, PricingRuleKind kind = PricingRuleKind.BaseRate, PricingMethod method = PricingMethod.PerUnit, decimal value = 1_000_000m,
        string code = "BASE", PricingFactor factor = PricingFactor.Size, PricingScope scope = PricingScope.Project, long? property = null, long? customer = null, int priority = 0) =>
        new(projectId, code, code + " rule", null, kind, factor, scope, null, null, null, property, customer, null, method, value, method == PricingMethod.PerUnit ? MeasurementUnit.Marla : null, priority, from, null, null);

    private async Task<PricingRuleDto> AddRuleAsync(CreatePricingRuleRequest r) { using var s = fx.Scope(); return await Svc<PricingRuleService>(s).CreateRuleAsync(r); }
    private async Task<PriceCalculationDto> CalcAsync(long propertyId, long? customer = null, DateOnly? asOf = null) { using var s = fx.Scope(); return await Svc<PricingCalculationService>(s).CalculateAsync(new CalculatePriceRequest(propertyId, customer, asOf)); }
    private async Task<PublishOutcomeDto> PublishAsync(long propertyId) { using var s = fx.Scope(); return await Svc<PropertyPricingService>(s).PublishAsync(propertyId, new PublishPriceRequest("test")); }
    private async Task<ApprovalDto> GetApprovalAsync(long id) { using var s = fx.Scope(); return await Svc<PricingApprovalService>(s).GetAsync(id); }

    private async Task<ApprovalDto> RequestAsync(long propertyId, PricingApprovalType type, decimal value, long? customer = null, PricingMethod? method = null, AdjustmentKind? kind = null)
    {
        using var s = fx.Scope();
        return await Svc<PricingApprovalService>(s).RequestAsync(new RequestPricingApprovalRequest(type, propertyId, customer, customer is null ? null : "Customer " + customer, kind, method, value, null, null, "Negotiated by management"));
    }

    private async Task<ApprovalDto> DecideAsync(ApprovalDto a, bool approve, string? comment = null)
    {
        using var s = fx.Scope();
        return await Svc<PricingApprovalService>(s).DecideAsync(a.Id, new DecidePricingApprovalRequest(approve, comment, a.RowVersion));
    }

    [MySqlFact]
    public async Task PricingPermissions_AreCataloged_AndGrantedToDefaultRoles()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        Assert.Contains(Permissions.PricingApprove, await Perms(RoleNames.Director));
        var sales = await Perms(RoleNames.SalesManager);
        Assert.Contains(Permissions.PricingRequest, sales);
        Assert.DoesNotContain(Permissions.PricingApprove, sales);
        Assert.DoesNotContain(Permissions.PricingManage, sales);
        Assert.Equal([Permissions.PricingView], (await Perms(RoleNames.Accountant)).Where(p => p.StartsWith("pricing.")).ToList());
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
    }

    [MySqlFact]
    public async Task ConfigurablePricing_BaseRatePremiumsDiscountsAndCharges_ProduceTheFinalPrice()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId, 5m, corner: true);
        var created = await AddRuleAsync(Rule(w.ProjectId, today));
        Assert.Equal(PricingRuleStatus.Active, created.Status);
        await AddRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.Premium, PricingMethod.Percentage, 10m, "CORNER", PricingFactor.Corner));
        await AddRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.AdditionalCharge, PricingMethod.FixedAmount, 10_000m, "TRANSFER", PricingFactor.Other));
        using (var s = fx.Scope())
            await Svc<PricingRuleService>(s).CreateDiscountAsync(new CreateDiscountRuleRequest(w.ProjectId, "EARLY", "Early bird", null, PricingScope.Project, null, null, null, null, PricingMethod.Percentage, 5m, true, 0, today, null));
        var c = await CalcAsync(p.Id);
        Assert.Equal(5_000_000m, c.SizeBasedPrice);
        Assert.Equal(500_000m, c.TotalPremiums);
        Assert.Equal(275_000m, c.TotalDiscounts);
        Assert.Equal(10_000m, c.TotalCharges);
        Assert.Equal(5_235_000m, c.FinalPrice);
        Assert.Equal(4, c.AppliedRules.Count);
        using var s2 = fx.Scope();
        var db = Svc<AppDbContext>(s2);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "PricingRule.Created" && a.EntityId == created.Id.ToString()));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "DiscountRule.Created"));
    }

    [MySqlFact]
    public async Task MoreSpecificRule_Overrides_ProjectRule_ForThatPropertyOnly()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var a = await PropertyAsync(w.ProjectId); var b = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await AddRuleAsync(Rule(w.ProjectId, today, value: 1_500_000m, scope: PricingScope.Property, property: a.Id));
        Assert.Equal(7_500_000m, (await CalcAsync(a.Id)).FinalPrice);
        Assert.Equal(5_000_000m, (await CalcAsync(b.Id)).FinalPrice);
    }

    [MySqlFact]
    public async Task RuleValidation_PastStart_WrongTargets_AndMissingCondition()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        using var s = fx.Scope(); var svc = Svc<PricingRuleService>(s);
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateRuleAsync(Rule(w.ProjectId, today.AddDays(-1))));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateRuleAsync(Rule(w.ProjectId, today, scope: PricingScope.Property)));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateRuleAsync(Rule(w.ProjectId, today, scope: PricingScope.Property, property: 987654321)));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateRuleAsync(Rule(w.ProjectId, today, property: 5)));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.Premium, PricingMethod.Percentage, 5m, "DEV", PricingFactor.DevelopmentStatus)));
    }

    [MySqlFact]
    public async Task CustomerNegotiatedRule_NeedsApproval_ThenAppliesOnlyToThatCustomer()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        var rule = await AddRuleAsync(Rule(w.ProjectId, today, value: 900_000m, scope: PricingScope.Customer, customer: 77));
        Assert.Equal(PricingRuleStatus.PendingApproval, rule.Status);
        Assert.NotNull(rule.ApprovalId);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id, 77)).FinalPrice);
        var approval = await GetApprovalAsync(rule.ApprovalId!.Value);
        await Assert.ThrowsAsync<DomainException>(() => DecideAsync(approval, true));
        fx.Act(w.Director);
        await DecideAsync(approval, true, "agreed");
        Assert.Equal(4_500_000m, (await CalcAsync(p.Id, 77)).FinalPrice);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id, 78)).FinalPrice);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id)).FinalPrice);
    }

    [MySqlFact]
    public async Task RejectedCustomerRule_NeverApplies_AndRejectNeedsAComment()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        var rule = await AddRuleAsync(Rule(w.ProjectId, today, value: 500_000m, scope: PricingScope.Customer, customer: 5));
        var approval = await GetApprovalAsync(rule.ApprovalId!.Value);
        fx.Act(w.Director);
        await Assert.ThrowsAsync<RequestValidationException>(() => DecideAsync(approval, false, " "));
        var rejected = await DecideAsync(approval, false, "too low");
        Assert.Equal(PricingApprovalStatus.Rejected, rejected.Status);
        using (var s = fx.Scope()) Assert.Equal(PricingRuleStatus.Rejected, (await Svc<PricingRuleService>(s).GetRuleAsync(rule.Id)).Status);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id, 5)).FinalPrice);
    }

    [MySqlFact]
    public async Task PriceOverride_Workflow_IsEnforced_AndUpdatesThePublishedPrice()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await PublishAsync(p.Id);
        fx.Act(w.Sales);
        var req = await RequestAsync(p.Id, PricingApprovalType.PriceOverride, 4_200_000m);
        Assert.Equal(PricingApprovalStatus.Pending, req.Status);
        Assert.Equal(5_000_000m, req.OriginalPrice);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id)).FinalPrice);
        await Assert.ThrowsAsync<ForbiddenException>(() => DecideAsync(req, true));
        await Assert.ThrowsAsync<DomainException>(() => RequestAsync(p.Id, PricingApprovalType.PriceOverride, 4_100_000m));
        fx.Act(w.Director);
        var done = await DecideAsync(req, true, "ok");
        Assert.Equal(PricingApprovalStatus.Approved, done.Status);
        Assert.NotNull(done.AdjustmentId);
        var c = await CalcAsync(p.Id);
        Assert.True(c.IsOverridden);
        Assert.Equal(4_200_000m, c.FinalPrice);
        using var s = fx.Scope();
        var view = await Svc<PropertyPricingService>(s).GetPriceAsync(p.Id, null, null);
        Assert.Equal(4_200_000m, view.Published!.FinalPrice);
        var hist = await Svc<PropertyPricingService>(s).ListHistoryAsync(new PriceHistoryRequest { PropertyId = p.Id });
        Assert.Equal(2, hist.TotalCount);
        Assert.Contains(hist.Items, h => h.Source == PriceChangeSource.ApprovalApplied && h.PreviousPrice == 5_000_000m && h.NewPrice == 4_200_000m && h.ApprovalId == req.Id);
        var db = Svc<AppDbContext>(s);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "PricingApproval.Approved" && a.EntityId == req.Id.ToString()));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "PricingApproval.Requested" && a.EntityId == req.Id.ToString()));
    }

    [MySqlFact]
    public async Task CustomerDiscount_AfterApproval_AppliesOnlyForThatCustomer_AndPublishedPriceIsUnchanged()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await PublishAsync(p.Id);
        fx.Act(w.Sales);
        var req = await RequestAsync(p.Id, PricingApprovalType.CustomerDiscount, 10m, 42, PricingMethod.Percentage);
        fx.Act(w.Director);
        await DecideAsync(req, true);
        Assert.Equal(4_500_000m, (await CalcAsync(p.Id, 42)).FinalPrice);
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id, 43)).FinalPrice);
        using var s = fx.Scope();
        Assert.Equal(5_000_000m, (await Svc<PropertyPricingService>(s).GetPriceAsync(p.Id, null, null)).Published!.FinalPrice);
    }

    [MySqlFact]
    public async Task PricingException_AndSpecialPricing_Work_AndAdjustmentCanBeRevoked()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        fx.Act(w.Sales);
        var ex = await RequestAsync(p.Id, PricingApprovalType.PricingException, 100_000m, null, PricingMethod.FixedAmount, AdjustmentKind.Discount);
        var special = await RequestAsync(p.Id, PricingApprovalType.SpecialPricing, 3_900_000m, 9);
        fx.Act(w.Director);
        var ok = await DecideAsync(ex, true);
        await DecideAsync(special, true);
        Assert.Equal(4_900_000m, (await CalcAsync(p.Id)).FinalPrice);
        Assert.Equal(3_900_000m, (await CalcAsync(p.Id, 9)).FinalPrice);
        using (var s = fx.Scope())
        {
            var svc = Svc<PropertyPricingService>(s);
            var adj = (await svc.ListAdjustmentsAsync(p.Id)).Single(a => a.ApprovalId == ok.Id);
            await Assert.ThrowsAsync<DomainException>(() => svc.RevokeAdjustmentAsync(adj.Id, new RetireRequest("", adj.RowVersion)));
            var revoked = await svc.RevokeAdjustmentAsync(adj.Id, new RetireRequest("granted in error", adj.RowVersion));
            Assert.Equal(AdjustmentStatus.Revoked, revoked.Status);
        }
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id)).FinalPrice);
    }

    [MySqlFact]
    public async Task CancelRequest_ByRequester_AndOnlyWhenPending()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        fx.Act(w.Sales);
        var req = await RequestAsync(p.Id, PricingApprovalType.PriceOverride, 1_000_000m);
        var other = await fx.CreateUserAsync(RoleNames.SalesManager);
        fx.Act(w.Admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, other);
        fx.Act(other);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingApprovalService>(s).CancelAsync(req.Id, new CancelPricingApprovalRequest("x", req.RowVersion)));
        fx.Act(w.Sales);
        using (var s = fx.Scope()) Assert.Equal(PricingApprovalStatus.Cancelled, (await Svc<PricingApprovalService>(s).CancelAsync(req.Id, new CancelPricingApprovalRequest("changed", req.RowVersion))).Status);
        fx.Act(w.Director);
        await Assert.ThrowsAsync<DomainException>(() => DecideAsync(req, true));
        Assert.Equal(5_000_000m, (await CalcAsync(p.Id)).FinalPrice);
    }

    [MySqlFact]
    public async Task ConcurrentDecisions_OnlyOneWins_AndOnlyOneAdjustmentExists()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        fx.Act(w.Sales);
        var req = await RequestAsync(p.Id, PricingApprovalType.PriceOverride, 4_000_000m);
        fx.Act(w.Director);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await DecideAsync(req, true); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, results.Count(r => r));
        using var s = fx.Scope();
        Assert.Single(await Svc<PropertyPricingService>(s).ListAdjustmentsAsync(p.Id));
    }

    [MySqlFact]
    public async Task Publish_IsIdempotent_RecordsHistory_AndSnapshotVerifies()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await Assert.ThrowsAsync<DomainException>(() => PublishAsync(p.Id));
        await AddRuleAsync(Rule(w.ProjectId, today));
        var first = await PublishAsync(p.Id); var second = await PublishAsync(p.Id);
        Assert.True(first.Changed);
        Assert.False(second.Changed);
        using var s = fx.Scope(); var svc = Svc<PropertyPricingService>(s);
        var hist = await svc.ListHistoryAsync(new PriceHistoryRequest { PropertyId = p.Id });
        Assert.Single(hist.Items);
        var snap = await svc.GetHistorySnapshotAsync(hist.Items[0].Id);
        Assert.True(PricingSnapshot.Verify(snap.Json, snap.Hash));
        Assert.Equal(5_000_000m, snap.Calculation.FinalPrice);
        Assert.True(await Svc<AppDbContext>(s).AuditLogs.AnyAsync(a => a.Action == "PropertyPrice.Published" && a.EntityId == p.Id.ToString()));
        await Svc<AppDbContext>(s).Database.ExecuteSqlRawAsync("UPDATE PropertyPriceHistories SET SnapshotJson = CONCAT(SnapshotJson, ' ') WHERE Id = {0}", hist.Items[0].Id);
        await Assert.ThrowsAsync<DomainException>(() => svc.GetHistorySnapshotAsync(hist.Items[0].Id));
    }

    [MySqlFact]
    public async Task Calculations_AreReproducible_AndLaterRulesDoNotAlterStoredSnapshots()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId, 7.25m, corner: true);
        await AddRuleAsync(Rule(w.ProjectId, today, value: 1_234_567.8912m));
        await AddRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.Premium, PricingMethod.Percentage, 7.5m, "CORNER", PricingFactor.Corner));
        using var s0 = fx.Scope();
        var calc = Svc<PricingCalculationService>(s0);
        var a = await calc.CreateSnapshotAsync(new CalculatePriceRequest(p.Id, null, null));
        var b = await calc.CreateSnapshotAsync(new CalculatePriceRequest(p.Id, null, null));
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Json, b.Json);
        await PublishAsync(p.Id);
        await AddRuleAsync(Rule(w.ProjectId, today, value: 2_000_000m, scope: PricingScope.Property, property: p.Id));
        Assert.NotEqual(a.Calculation.FinalPrice, (await CalcAsync(p.Id)).FinalPrice);
        using var s = fx.Scope();
        var view = await Svc<PropertyPricingService>(s).GetPriceAsync(p.Id, null, null);
        Assert.Equal(a.Calculation.FinalPrice, view.Published!.FinalPrice);
        var stored = await Svc<AppDbContext>(s).PropertyPrices.AsNoTracking().FirstAsync(x => x.PropertyId == p.Id);
        Assert.Equal(a.Calculation.FinalPrice, PricingSnapshot.Deserialize(stored.SnapshotJson).FinalPrice);
    }

    [MySqlFact]
    public async Task RetiredRule_KeepsPastCalculationsStable()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        long ruleId;
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var r = new PricingRule { ProjectId = w.ProjectId, Code = "BASE", Name = "Old rate", Kind = PricingRuleKind.BaseRate, Factor = PricingFactor.Size, Scope = PricingScope.Project, Method = PricingMethod.PerUnit,
                Value = 800_000m, Unit = MeasurementUnit.Marla, EffectiveFrom = today.AddDays(-30), Status = PricingRuleStatus.Active };
            db.PricingRules.Add(r);
            await db.SaveChangesAsync();
            ruleId = r.Id;
        }
        Assert.Equal(4_000_000m, (await CalcAsync(p.Id, null, today.AddDays(-5))).FinalPrice);
        using (var s = fx.Scope())
        {
            var svc = Svc<PricingRuleService>(s); var rule = await svc.GetRuleAsync(ruleId);
            using (var stale = fx.Scope()) await Assert.ThrowsAsync<ConcurrencyException>(() => Svc<PricingRuleService>(stale).RetireRuleAsync(ruleId, new RetireRequest("stale", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))));
            var retired = await svc.RetireRuleAsync(ruleId, new RetireRequest("replaced", rule.RowVersion));
            Assert.Equal(PricingRuleStatus.Retired, retired.Status);
            Assert.Equal(today.AddDays(-1), retired.EffectiveTo);
            await Assert.ThrowsAsync<DomainException>(() => svc.RetireRuleAsync(ruleId, new RetireRequest("again", retired.RowVersion)));
        }
        Assert.Equal(4_000_000m, (await CalcAsync(p.Id, null, today.AddDays(-5))).FinalPrice);
        await Assert.ThrowsAsync<DomainException>(() => CalcAsync(p.Id));
    }

    [MySqlFact]
    public async Task BookedProperties_AreFrozen_SoExistingAgreementsAreUnaffected()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId); var free = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await PublishAsync(p.Id);
        using (var s = fx.Scope()) await Svc<PropertyService>(s).ChangeStatusAsync(p.Id, new ChangePropertyStatusRequest(PropertyStatus.Booked, "Booked by customer", null));
        await AddRuleAsync(Rule(w.ProjectId, today, value: 2_000_000m, scope: PricingScope.Property, property: p.Id));
        await Assert.ThrowsAsync<DomainException>(() => PublishAsync(p.Id));
        fx.Act(w.Sales);
        await Assert.ThrowsAsync<DomainException>(() => RequestAsync(p.Id, PricingApprovalType.PriceOverride, 1_000_000m));
        fx.Act(w.Admin);
        using var s2 = fx.Scope(); var svc = Svc<PropertyPricingService>(s2);
        var result = await svc.PublishProjectAsync(w.ProjectId, new PublishPriceRequest(null));
        Assert.Equal(1, result.Frozen);
        Assert.True(result.Published >= 1);
        var view = await svc.GetPriceAsync(p.Id, null, null);
        Assert.True(view.PriceFrozen);
        Assert.Equal(5_000_000m, view.Published!.FinalPrice);
        Assert.Equal(5_000_000m, (await svc.GetPriceAsync(free.Id, null, null)).Published!.FinalPrice);
    }

    [MySqlFact]
    public async Task ConcurrentFirstPublish_CreatesExactlyOnePrice()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { return (await PublishAsync(p.Id)).Changed; } catch (DomainException) { return false; }
        }));
        Assert.Equal(1, results.Count(r => r));
        using var s = fx.Scope(); var db = Svc<AppDbContext>(s);
        Assert.Equal(1, await db.PropertyPrices.CountAsync(x => x.PropertyId == p.Id));
        Assert.Equal(1, await db.PropertyPriceHistories.CountAsync(x => x.PropertyId == p.Id));
    }

    [MySqlFact]
    public async Task Discounts_ExclusiveVsStackable_Retire_AndCapAtThePrice()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        using var s = fx.Scope(); var svc = Svc<PricingRuleService>(s);
        CreateDiscountRuleRequest D(string code, PricingMethod m, decimal v, bool stack) => new(w.ProjectId, code, code, null, PricingScope.Project, null, null, null, null, m, v, stack, 0, today, null);
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateDiscountAsync(D("C", PricingMethod.Percentage, 5m, false) with { Scope = PricingScope.Customer }));
        var a = await svc.CreateDiscountAsync(D("EX1", PricingMethod.Percentage, 5m, false));
        await svc.CreateDiscountAsync(D("EX2", PricingMethod.Percentage, 8m, false) with { Priority = 5 });
        Assert.Equal(400_000m, (await CalcAsync(p.Id)).TotalDiscounts);
        await svc.CreateDiscountAsync(D("ST1", PricingMethod.FixedAmount, 99_000_000m, true));
        var c = await CalcAsync(p.Id);
        Assert.Equal(5_000_000m, c.TotalDiscounts);
        Assert.Equal(0m, c.FinalPrice);
        var retired = await svc.RetireDiscountAsync(a.Id, new RetireRequest("not needed", a.RowVersion));
        Assert.Equal(PricingRuleStatus.Retired, retired.Status);
        var list = await svc.ListDiscountsAsync(new DiscountListRequest { ProjectId = w.ProjectId, Status = PricingRuleStatus.Active });
        Assert.DoesNotContain(list.Items, x => x.Id == a.Id);
    }

    [MySqlFact]
    public async Task RateCard_ListsRulesInForce_AndRespectsTheDate()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        await AddRuleAsync(Rule(w.ProjectId, today));
        await AddRuleAsync(Rule(w.ProjectId, today.AddDays(10), value: 1_100_000m, priority: 1));
        await AddRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.Premium, PricingMethod.Percentage, 10m, "CORNER", PricingFactor.Corner));
        using var s = fx.Scope(); var svc = Svc<PricingRuleService>(s);
        var now = await svc.GetRateCardAsync(w.ProjectId, null);
        Assert.Single(now.BaseRates);
        Assert.Single(now.Premiums);
        Assert.Empty(now.Charges);
        var later = await svc.GetRateCardAsync(w.ProjectId, today.AddDays(10));
        Assert.Equal(2, later.BaseRates.Count);
        var rules = await svc.ListRulesAsync(new PricingRuleListRequest { ProjectId = w.ProjectId, Kind = PricingRuleKind.BaseRate, PageSize = 1 });
        Assert.Equal(2, rules.TotalCount);
        Assert.Single(rules.Items);
    }

    [MySqlFact]
    public async Task PublishProject_AndPriceList_ReportPricedAndUnpriced()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        await PropertyAsync(w.ProjectId); await PropertyAsync(w.ProjectId); await PropertyAsync(w.ProjectId, 5m, true);
        await AddRuleAsync(Rule(w.ProjectId, today));
        using var s = fx.Scope(); var svc = Svc<PropertyPricingService>(s);
        Assert.Equal(3, (await svc.ListPropertyPricesAsync(new PropertyPriceListRequest { ProjectId = w.ProjectId, Published = false })).TotalCount);
        var r = await svc.PublishProjectAsync(w.ProjectId, new PublishPriceRequest("bulk"));
        Assert.Equal(3, r.Published);
        Assert.Equal(0, r.Frozen);
        Assert.Empty(r.Skipped);
        Assert.Equal(3, (await svc.ListPropertyPricesAsync(new PropertyPriceListRequest { ProjectId = w.ProjectId, Published = true })).TotalCount);
        var again = await svc.PublishProjectAsync(w.ProjectId, new PublishPriceRequest("bulk"));
        Assert.Equal(0, again.Published);
        Assert.Equal(3, again.Unchanged);
        Assert.Equal(3, (await svc.ListHistoryAsync(new PriceHistoryRequest { ProjectId = w.ProjectId })).TotalCount);
    }

    [MySqlFact]
    public async Task Authorization_PermissionsAndProjectScope_AreEnforced()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        long otherProject;
        using (var s = fx.Scope()) otherProject = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var theirs = await PropertyAsync(otherProject);

        fx.Act(w.Sales);
        using (var s = fx.Scope())
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingRuleService>(s).CreateRuleAsync(Rule(w.ProjectId, today)));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PropertyPricingService>(s).PublishAsync(p.Id, new PublishPriceRequest(null)));
            Assert.Equal(5_000_000m, (await Svc<PricingCalculationService>(s).CalculateAsync(new CalculatePriceRequest(p.Id, null, null))).FinalPrice);
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingCalculationService>(s).CalculateAsync(new CalculatePriceRequest(theirs.Id, null, null)));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingCalculationService>(s).CalculateAsync(new CalculatePriceRequest(987654321, null, null)));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingRuleService>(s).GetRateCardAsync(otherProject, null));
        }
        var acct = await fx.CreateUserAsync(RoleNames.Accountant);
        fx.Act(w.Admin);
        using (var s = fx.Scope()) await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, acct);
        fx.Act(acct);
        using (var s = fx.Scope())
        {
            Assert.NotNull(await Svc<PricingCalculationService>(s).CalculateAsync(new CalculatePriceRequest(p.Id, null, null)));
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingApprovalService>(s).RequestAsync(new RequestPricingApprovalRequest(PricingApprovalType.PriceOverride, p.Id, null, null, null, null, 1m, null, null, "Reason here")));
        }
        var cashier = await fx.CreateUserAsync(RoleNames.Cashier);
        fx.Act(cashier);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<ForbiddenException>(() => Svc<PricingRuleService>(s).ListRulesAsync(new PricingRuleListRequest()));
    }

    [MySqlFact]
    public async Task ArchivedProject_RejectsNewRules()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        await AddRuleAsync(Rule(w.ProjectId, today));
        using (var s = fx.Scope()) await Svc<ProjectService>(s).ChangeStatusAsync(w.ProjectId, new ChangeProjectStatusRequest(ProjectStatus.Archived, "Closed", null));
        await Assert.ThrowsAsync<DomainException>(() => AddRuleAsync(Rule(w.ProjectId, today, code: "B2")));
    }

    [MySqlFact]
    public async Task Database_EnforcesPriceUniquenessAndValueChecks()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await PublishAsync(p.Id);
        using var s = fx.Scope(); var db = Svc<AppDbContext>(s);
        db.PropertyPrices.Add(new PropertyPrice { PropertyId = p.Id, ProjectId = w.ProjectId, SnapshotJson = "{}", SnapshotHash = "x", FinalPrice = 1m, PublishedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.PricingRules.Add(new PricingRule { ProjectId = w.ProjectId, Code = "NEG", Name = "n", Kind = PricingRuleKind.Premium, Factor = PricingFactor.Other, Scope = PricingScope.Project, Method = PricingMethod.FixedAmount,
            Value = -5m, EffectiveFrom = today, Status = PricingRuleStatus.Active });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task Snapshot_ForBooking_CarriesHashAndFullBreakdown()
    {
        var w = await WorldAsync(); var today = await TodayAsync();
        var p = await PropertyAsync(w.ProjectId, 5m, true);
        await AddRuleAsync(Rule(w.ProjectId, today));
        await AddRuleAsync(Rule(w.ProjectId, today, PricingRuleKind.Premium, PricingMethod.Percentage, 10m, "CORNER", PricingFactor.Corner));
        using var s = fx.Scope();
        var snap = await Svc<PricingCalculationService>(s).CreateSnapshotAsync(new CalculatePriceRequest(p.Id, 5, null));
        Assert.True(PricingSnapshot.Verify(snap.Json, snap.Hash));
        Assert.Equal(5_500_000m, snap.Calculation.FinalPrice);
        Assert.Equal(5, snap.Calculation.CustomerId);
        Assert.Equal(2, snap.Calculation.AppliedRules.Count);
    }
}
