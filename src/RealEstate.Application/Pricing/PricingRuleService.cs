using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Pricing;

/// <summary>Pricing rules, discount configuration and rate cards. Pricing-defining fields are immutable: rules are retired and replaced, never edited.</summary>
public class PricingRuleService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, PricingSupport support, TimeProvider clock,
    IValidator<CreatePricingRuleRequest> ruleValidator, IValidator<CreateDiscountRuleRequest> discountValidator)
{
    // ---------- rules ----------
    public async Task<PagedResult<PricingRuleDto>> ListRulesAsync(PricingRuleListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.PricingRules.AsNoTracking().Where(x => projects.Any(p => p.Id == x.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(x => x.ProjectId == pid);
        if (r.Scope is { } sc) q = q.Where(x => x.Scope == sc);
        if (r.Kind is { } k) q = q.Where(x => x.Kind == k);
        if (r.Status is { } st) q = q.Where(x => x.Status == st);
        if (r.ActiveOn is { } d) q = q.Where(x => (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= d && (x.EffectiveTo == null || x.EffectiveTo >= d));
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.Code.Contains(t) || x.Name.Contains(t)); }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("name", false) => q.OrderBy(x => x.Name).ThenBy(x => x.Id),
            ("name", true) => q.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            ("scope", false) => q.OrderBy(x => x.Scope).ThenBy(x => x.Id),
            ("scope", true) => q.OrderByDescending(x => x.Scope).ThenBy(x => x.Id),
            ("from", false) => q.OrderBy(x => x.EffectiveFrom).ThenBy(x => x.Id),
            (_, false) => q.OrderByDescending(x => x.Id),
            _ => q.OrderBy(x => x.Id),
        };
        var page = await q.Include(x => x.Conditions).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<PricingRuleDto>(await ToDtosAsync(page, ct), r.Page, r.PageSize, total);
    }

    public async Task<PricingRuleDto> GetRuleAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var rule = await LoadRuleAsync(id, false, ct);
        await access.EnsureAccessAsync(rule.ProjectId, ct);
        return (await ToDtosAsync([rule], ct))[0];
    }

    public async Task<PricingRuleDto> CreateRuleAsync(CreatePricingRuleRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        await access.EnsureAccessAsync(r.ProjectId, ct);
        await ruleValidator.ValidateOrThrowAsync(r, ct);
        await support.WritableProjectAsync(r.ProjectId, ct);
        var today = await support.TodayAsync(ct);
        if (r.EffectiveFrom < today) throw PricingSupport.Invalid("EffectiveFrom", "A rule cannot start in the past: past prices must stay reproducible. Choose today or a later date.");
        await PricingTargets.ValidateAsync(db, r.ProjectId, r.Scope, r.BlockId, r.StreetId, r.CategoryId, r.PropertyId, r.CustomerId, ct);
        await PricingTargets.ValidateConditionsAsync(db, r, ct);
        if (r.Kind == PricingRuleKind.BaseRate && r.Factor != PricingFactor.Size) { /* base rates are always the size factor */ }

        var now = clock.GetUtcNow().UtcDateTime;
        var customer = r.Scope == PricingScope.Customer;
        var rule = new PricingRule
        {
            ProjectId = r.ProjectId, Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), Description = PricingSupport.N(r.Description), Kind = r.Kind,
            Factor = r.Kind == PricingRuleKind.BaseRate ? PricingFactor.Size : r.Factor, Scope = r.Scope, BlockId = r.BlockId, StreetId = r.StreetId, CategoryId = r.CategoryId, PropertyId = r.PropertyId,
            CustomerId = r.CustomerId, CustomerReference = PricingSupport.N(r.CustomerReference), Method = r.Method, Value = r.Value, Unit = r.Unit, Priority = r.Priority,
            EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo, Status = customer ? PricingRuleStatus.PendingApproval : PricingRuleStatus.Active, StatusChangedAtUtc = now,
        };
        foreach (var c in r.Conditions ?? []) rule.Conditions.Add(new PricingRuleCondition { Field = c.Field, Value = c.Value.Trim() });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PricingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        long? approvalId = null;
        if (customer)
        {
            var a = new PricingApproval
            {
                Type = PricingApprovalType.SpecialPricing, ProjectId = rule.ProjectId, PricingRuleId = rule.Id, CustomerId = rule.CustomerId, CustomerReference = rule.CustomerReference,
                Method = rule.Method, Value = rule.Value, EffectiveFrom = rule.EffectiveFrom, EffectiveTo = rule.EffectiveTo,
                Reason = rule.Description ?? $"Customer-negotiated pricing rule {rule.Code}", RequestedByUserId = user.UserId!.Value, RequestedByName = user.UserName, RequestedAtUtc = now,
            };
            db.PricingApprovals.Add(a);
            await db.SaveChangesAsync(ct);
            approvalId = a.Id;
            await audit.RecordAsync(new AuditEntry("PricingApproval.Requested", "PricingApproval", a.Id.ToString(), NewValues: new { a.Type, a.PricingRuleId, a.CustomerId, a.Value, a.Reason }), ct);
        }
        await audit.RecordAsync(new AuditEntry("PricingRule.Created", "PricingRule", rule.Id.ToString(), NewValues: Snapshot(rule)), ct);
        await tx.CommitAsync(ct);
        var dto = (await ToDtosAsync([rule], ct))[0];
        return dto with { ApprovalId = approvalId };
    }

    /// <summary>Only the descriptive fields can change; anything that affects prices requires a new rule.</summary>
    public async Task<PricingRuleDto> UpdateRuleDetailsAsync(long id, UpdateRuleDetailsRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw PricingSupport.Invalid("Name", "Name is required (max 150 characters).");
        var rule = await LoadRuleAsync(id, true, ct);
        await access.EnsureAccessAsync(rule.ProjectId, ct);
        await support.WritableProjectAsync(rule.ProjectId, ct);
        var before = new { rule.Name, rule.Description };
        db.UseRowVersion(rule, r.RowVersion);
        rule.Name = r.Name.Trim(); rule.Description = PricingSupport.N(r.Description);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PricingRule.Updated", "PricingRule", id.ToString(), before, new { rule.Name, rule.Description }), ct);
        return (await ToDtosAsync([rule], ct))[0];
    }

    public async Task<PricingRuleDto> RetireRuleAsync(long id, RetireRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        var rule = await LoadRuleAsync(id, true, ct);
        await access.EnsureAccessAsync(rule.ProjectId, ct);
        await support.WritableProjectAsync(rule.ProjectId, ct);
        db.UseRowVersion(rule, r.RowVersion);
        var before = new { Status = rule.Status.ToString(), rule.EffectiveTo };
        rule.Retire(r.Reason ?? "", await support.TodayAsync(ct), clock.GetUtcNow().UtcDateTime);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PricingRule.Retired", "PricingRule", id.ToString(), before, new { Status = rule.Status.ToString(), rule.EffectiveTo }, rule.StatusReason), ct);
        return (await ToDtosAsync([rule], ct))[0];
    }

    private async Task<PricingRule> LoadRuleAsync(long id, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.PricingRules.Include(x => x.Conditions) : db.PricingRules.AsNoTracking().Include(x => x.Conditions);
        var rule = await q.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (rule is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this rule.");
            throw new NotFoundException("Pricing rule not found.");
        }
        return rule;
    }

    // ---------- discounts ----------
    public async Task<PagedResult<DiscountRuleDto>> ListDiscountsAsync(DiscountListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.DiscountRules.AsNoTracking().Where(x => projects.Any(p => p.Id == x.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(x => x.ProjectId == pid);
        if (r.Status is { } st) q = q.Where(x => x.Status == st);
        if (r.ActiveOn is { } d) q = q.Where(x => (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= d && (x.EffectiveTo == null || x.EffectiveTo >= d));
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.Code.Contains(t) || x.Name.Contains(t)); }
        var total = await q.CountAsync(ct);
        var page = await q.OrderByDescending(x => x.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<DiscountRuleDto>(await ToDtosAsync(page, ct), r.Page, r.PageSize, total);
    }

    public async Task<DiscountRuleDto> GetDiscountAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        var d = await LoadDiscountAsync(id, false, ct);
        await access.EnsureAccessAsync(d.ProjectId, ct);
        return (await ToDtosAsync([d], ct))[0];
    }

    public async Task<DiscountRuleDto> CreateDiscountAsync(CreateDiscountRuleRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        await access.EnsureAccessAsync(r.ProjectId, ct);
        await discountValidator.ValidateOrThrowAsync(r, ct);
        await support.WritableProjectAsync(r.ProjectId, ct);
        var today = await support.TodayAsync(ct);
        if (r.EffectiveFrom < today) throw PricingSupport.Invalid("EffectiveFrom", "A discount cannot start in the past: past prices must stay reproducible. Choose today or a later date.");
        await PricingTargets.ValidateAsync(db, r.ProjectId, r.Scope, r.BlockId, r.StreetId, r.CategoryId, r.PropertyId, null, ct);
        var d = new DiscountRule
        {
            ProjectId = r.ProjectId, Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), Description = PricingSupport.N(r.Description), Scope = r.Scope, BlockId = r.BlockId, StreetId = r.StreetId,
            CategoryId = r.CategoryId, PropertyId = r.PropertyId, Method = r.Method, Value = r.Value, IsStackable = r.IsStackable, Priority = r.Priority, EffectiveFrom = r.EffectiveFrom,
            EffectiveTo = r.EffectiveTo, Status = PricingRuleStatus.Active, StatusChangedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.DiscountRules.Add(d);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("DiscountRule.Created", "DiscountRule", d.Id.ToString(), NewValues: new { d.ProjectId, d.Code, d.Name, Scope = d.Scope.ToString(), Method = d.Method.ToString(), d.Value, d.IsStackable, d.Priority, d.EffectiveFrom, d.EffectiveTo }), ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync([d], ct))[0];
    }

    public async Task<DiscountRuleDto> UpdateDiscountDetailsAsync(long id, UpdateRuleDetailsRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw PricingSupport.Invalid("Name", "Name is required (max 150 characters).");
        var d = await LoadDiscountAsync(id, true, ct);
        await access.EnsureAccessAsync(d.ProjectId, ct);
        await support.WritableProjectAsync(d.ProjectId, ct);
        var before = new { d.Name, d.Description };
        db.UseRowVersion(d, r.RowVersion);
        d.Name = r.Name.Trim(); d.Description = PricingSupport.N(r.Description);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("DiscountRule.Updated", "DiscountRule", id.ToString(), before, new { d.Name, d.Description }), ct);
        return (await ToDtosAsync([d], ct))[0];
    }

    public async Task<DiscountRuleDto> RetireDiscountAsync(long id, RetireRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingManage, ct);
        var d = await LoadDiscountAsync(id, true, ct);
        await access.EnsureAccessAsync(d.ProjectId, ct);
        await support.WritableProjectAsync(d.ProjectId, ct);
        db.UseRowVersion(d, r.RowVersion);
        var before = new { Status = d.Status.ToString(), d.EffectiveTo };
        d.Retire(r.Reason ?? "", await support.TodayAsync(ct), clock.GetUtcNow().UtcDateTime);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("DiscountRule.Retired", "DiscountRule", id.ToString(), before, new { Status = d.Status.ToString(), d.EffectiveTo }, d.StatusReason), ct);
        return (await ToDtosAsync([d], ct))[0];
    }

    private async Task<DiscountRule> LoadDiscountAsync(long id, bool tracked, CancellationToken ct)
    {
        var d = await (tracked ? db.DiscountRules : db.DiscountRules.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this discount.");
            throw new NotFoundException("Discount not found.");
        }
        return d;
    }

    // ---------- rate card ----------
    /// <summary>The rate card of a project on a date: every base rate, premium, charge and discount in force.</summary>
    public async Task<RateCardDto> GetRateCardAsync(long projectId, DateOnly? asOf, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PricingView, ct);
        await access.EnsureAccessAsync(projectId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var date = asOf ?? await support.TodayAsync(ct);
        var rules = await db.PricingRules.AsNoTracking().Include(x => x.Conditions)
            .Where(x => x.ProjectId == projectId && (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= date && (x.EffectiveTo == null || x.EffectiveTo >= date))
            .OrderByDescending(x => x.Scope).ThenByDescending(x => x.Priority).ThenBy(x => x.Code).ToListAsync(ct);
        var discounts = await db.DiscountRules.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (x.Status == PricingRuleStatus.Active || x.Status == PricingRuleStatus.Retired) && x.EffectiveFrom <= date && (x.EffectiveTo == null || x.EffectiveTo >= date))
            .OrderByDescending(x => x.Scope).ThenBy(x => x.Code).ToListAsync(ct);
        var dtos = await ToDtosAsync(rules, ct);
        return new RateCardDto(project.Id, project.ProjectCode, project.ProjectName, date,
            dtos.Where(d => d.Kind == PricingRuleKind.BaseRate).ToList(), dtos.Where(d => d.Kind == PricingRuleKind.Premium).ToList(), dtos.Where(d => d.Kind == PricingRuleKind.AdditionalCharge).ToList(),
            await ToDtosAsync(discounts, ct));
    }

    // ---------- mapping ----------
    private async Task<(Dictionary<long, string> Projects, Dictionary<long, string> Blocks, Dictionary<long, string> Streets, Dictionary<long, string> Categories, Dictionary<long, string> Properties)>
        NamesAsync(IEnumerable<long> projectIds, IEnumerable<long?> blockIds, IEnumerable<long?> streetIds, IEnumerable<long?> categoryIds, IEnumerable<long?> propertyIds, CancellationToken ct)
    {
        var pr = projectIds.Distinct().ToList(); var bl = blockIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList(); var st = streetIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var ca = categoryIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList(); var pp = propertyIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        return (await db.Projects.AsNoTracking().Where(x => pr.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ProjectCode, ct),
            await db.ProjectBlocks.AsNoTracking().Where(x => bl.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            await db.ProjectStreets.AsNoTracking().Where(x => st.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            await db.PropertyCategories.AsNoTracking().Where(x => ca.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            await db.Properties.AsNoTracking().Where(x => pp.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.PropertyCode, ct));
    }

    private async Task<IReadOnlyList<PricingRuleDto>> ToDtosAsync(IReadOnlyList<PricingRule> rules, CancellationToken ct)
    {
        var n = await NamesAsync(rules.Select(x => x.ProjectId), rules.Select(x => x.BlockId), rules.Select(x => x.StreetId), rules.Select(x => x.CategoryId), rules.Select(x => x.PropertyId), ct);
        var ruleIds = rules.Select(x => x.Id).ToList();
        var approvals = await db.PricingApprovals.AsNoTracking().Where(a => a.PricingRuleId != null && ruleIds.Contains(a.PricingRuleId!.Value)).Select(a => new { a.Id, RuleId = a.PricingRuleId!.Value }).ToListAsync(ct);
        return rules.Select(x => new PricingRuleDto(x.Id, x.ProjectId, n.Projects.GetValueOrDefault(x.ProjectId, ""), x.Code, x.Name, x.Description, x.Kind, x.Factor, x.Scope,
            x.BlockId, x.BlockId is { } b ? n.Blocks.GetValueOrDefault(b) : null, x.StreetId, x.StreetId is { } s ? n.Streets.GetValueOrDefault(s) : null,
            x.CategoryId, x.CategoryId is { } c ? n.Categories.GetValueOrDefault(c) : null, x.PropertyId, x.PropertyId is { } p ? n.Properties.GetValueOrDefault(p) : null,
            x.CustomerId, x.CustomerReference, x.Method, x.Value, x.Unit, x.Priority, x.EffectiveFrom, x.EffectiveTo, x.Status, x.StatusReason,
            x.Conditions.OrderBy(c2 => c2.Field).Select(c2 => new ConditionDto(c2.Field, c2.Value)).ToList(), x.RowVersion.ToToken(), x.CreatedAtUtc, x.CreatedBy,
            approvals.Where(a => a.RuleId == x.Id).Select(a => (long?)a.Id).FirstOrDefault())).ToList();
    }

    private async Task<IReadOnlyList<DiscountRuleDto>> ToDtosAsync(IReadOnlyList<DiscountRule> rules, CancellationToken ct)
    {
        var n = await NamesAsync(rules.Select(x => x.ProjectId), rules.Select(x => x.BlockId), rules.Select(x => x.StreetId), rules.Select(x => x.CategoryId), rules.Select(x => x.PropertyId), ct);
        return rules.Select(x => new DiscountRuleDto(x.Id, x.ProjectId, n.Projects.GetValueOrDefault(x.ProjectId, ""), x.Code, x.Name, x.Description, x.Scope,
            x.BlockId, x.BlockId is { } b ? n.Blocks.GetValueOrDefault(b) : null, x.StreetId, x.StreetId is { } s ? n.Streets.GetValueOrDefault(s) : null,
            x.CategoryId, x.CategoryId is { } c ? n.Categories.GetValueOrDefault(c) : null, x.PropertyId, x.PropertyId is { } p ? n.Properties.GetValueOrDefault(p) : null,
            x.Method, x.Value, x.IsStackable, x.Priority, x.EffectiveFrom, x.EffectiveTo, x.Status, x.StatusReason, x.RowVersion.ToToken(), x.CreatedAtUtc, x.CreatedBy)).ToList();
    }

    private static object Snapshot(PricingRule r) => new
    {
        r.ProjectId, r.Code, r.Name, Kind = r.Kind.ToString(), Factor = r.Factor.ToString(), Scope = r.Scope.ToString(), r.BlockId, r.StreetId, r.CategoryId, r.PropertyId, r.CustomerId,
        Method = r.Method.ToString(), r.Value, Unit = r.Unit?.ToString(), r.Priority, r.EffectiveFrom, r.EffectiveTo, Status = r.Status.ToString(),
        Conditions = r.Conditions.Select(c => $"{c.Field}={c.Value}").ToArray(),
    };
}
