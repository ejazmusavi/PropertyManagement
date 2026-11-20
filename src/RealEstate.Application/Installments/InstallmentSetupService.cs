using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Installments;

namespace RealEstate.Application.Installments;

/// <summary>Charge type catalog and late fee configuration.</summary>
public class InstallmentSetupService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, InstallmentSupport support,
    IValidator<SaveChargeTypeRequest> chargeTypeValidator, IValidator<SaveLateFeeRuleRequest> ruleValidator)
{
    // ------------------------------------------------------------------ charge types
    public async Task<IReadOnlyList<ChargeTypeDto>> ListChargeTypesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var q = db.ChargeTypes.AsNoTracking();
        if (activeOnly) q = q.Where(t => t.IsActive);
        return await q.OrderBy(t => t.Kind).ThenBy(t => t.Name)
            .Select(t => new ChargeTypeDto(t.Id, t.Code, t.Name, t.Kind, t.DefaultCategory, t.DefaultFrequency, t.Description, t.IsSystem, t.IsActive)).ToListAsync(ct);
    }

    public async Task<ChargeTypeDto> CreateChargeTypeAsync(SaveChargeTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await chargeTypeValidator.ValidateOrThrowAsync(r, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.ChargeTypes.AnyAsync(t => t.Code == code, ct)) throw PricingSupport.Invalid("Code", "A charge type with this code already exists.");
        var t = new ChargeType { Code = code, Name = r.Name.Trim(), Kind = r.Kind, DefaultCategory = r.DefaultCategory, DefaultFrequency = r.DefaultFrequency, Description = PricingSupport.N(r.Description), IsActive = r.IsActive };
        db.ChargeTypes.Add(t);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ChargeType.Created", "ChargeType", t.Id.ToString(), NewValues: new { t.Code, t.Name, Kind = t.Kind.ToString() }), ct);
        return Map(t);
    }

    public async Task<ChargeTypeDto> UpdateChargeTypeAsync(long id, SaveChargeTypeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await chargeTypeValidator.ValidateOrThrowAsync(r, ct);
        var t = await db.ChargeTypes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Charge type not found.");
        var old = new { t.Name, t.Description, t.IsActive, Kind = t.Kind.ToString(), Category = t.DefaultCategory.ToString(), Frequency = t.DefaultFrequency.ToString() };
        if (!string.Equals(t.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase)) throw PricingSupport.Invalid("Code", "The code cannot be changed.");
        if (t.IsSystem && (r.Kind != t.Kind || r.DefaultCategory != t.DefaultCategory || r.DefaultFrequency != t.DefaultFrequency || !r.IsActive))
            throw new DomainException("Standard charge types can only be renamed or re-described; they cannot be re-classified or deactivated.");
        t.Name = r.Name.Trim(); t.Description = PricingSupport.N(r.Description); t.Kind = r.Kind; t.DefaultCategory = r.DefaultCategory; t.DefaultFrequency = r.DefaultFrequency; t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("ChargeType.Updated", "ChargeType", t.Id.ToString(), old, new { t.Name, t.Description, t.IsActive, Kind = t.Kind.ToString() }), ct);
        return Map(t);
    }

    private static ChargeTypeDto Map(ChargeType t) => new(t.Id, t.Code, t.Name, t.Kind, t.DefaultCategory, t.DefaultFrequency, t.Description, t.IsSystem, t.IsActive);

    // ------------------------------------------------------------------ late fee rules
    public async Task<PagedResult<LateFeeRuleDto>> ListLateFeeRulesAsync(LateFeeRuleListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.LateFeeRules.AsNoTracking().Where(x => x.ProjectId == null || projects.Any(p => p.Id == x.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(x => x.ProjectId == null || x.ProjectId == pid);
        if (r.IsActive is { } a) q = q.Where(x => x.IsActive == a);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var s = r.Search.Trim(); q = q.Where(x => x.Code.Contains(s) || x.Name.Contains(s)); }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("name", false) => q.OrderBy(x => x.Name), ("name", true) => q.OrderByDescending(x => x.Name),
            ("code", true) => q.OrderByDescending(x => x.Code), _ => q.OrderBy(x => x.Code),
        };
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToListAsync(ct);
        return new PagedResult<LateFeeRuleDto>(await MapAsync(rows, ct), r.Page, r.PageSize, total);
    }

    public async Task<LateFeeRuleDto> GetLateFeeRuleAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var rule = await LoadRuleAsync(id, tracked: false, ct);
        return (await MapAsync([rule], ct))[0];
    }

    public async Task<LateFeeRuleDto> CreateLateFeeRuleAsync(SaveLateFeeRuleRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await ruleValidator.ValidateOrThrowAsync(r, ct);
        if (r.ProjectId is { } pid) await access.EnsureAccessAsync(pid, ct);
        else if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Only users with access to all projects can create company-wide late fee rules.");
        LateFeeCalculator.Validate(new(r.Method, r.Value, r.MaxAmount, r.MaxPercentOfInstallment));
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.LateFeeRules.AnyAsync(x => x.Code == code, ct)) throw PricingSupport.Invalid("Code", "A late fee rule with this code already exists.");
        var rule = new LateFeeRule
        {
            Code = code, Name = r.Name.Trim(), ProjectId = r.ProjectId, Method = r.Method, Value = r.Value, MaxAmount = r.MaxAmount, MaxPercentOfInstallment = r.MaxPercentOfInstallment,
            Description = PricingSupport.N(r.Description), IsActive = r.IsActive,
        };
        db.LateFeeRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("LateFeeRule.Created", "LateFeeRule", rule.Id.ToString(), NewValues: Snapshot(rule)), ct);
        return (await MapAsync([rule], ct))[0];
    }

    /// <summary>Name, description and active flag can always change; calculation terms only while no plan component or agreement charge uses the rule.</summary>
    public async Task<LateFeeRuleDto> UpdateLateFeeRuleAsync(long id, SaveLateFeeRuleRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await ruleValidator.ValidateOrThrowAsync(r, ct);
        var rule = await LoadRuleAsync(id, tracked: true, ct);
        // Same rule as creation: company-wide rules affect every project, so only users with access to all projects may change them.
        if (rule.ProjectId is null && !await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Only users with access to all projects can change company-wide late fee rules.");
        if (!string.Equals(rule.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase)) throw PricingSupport.Invalid("Code", "The code cannot be changed.");
        if (rule.ProjectId != r.ProjectId) throw PricingSupport.Invalid("ProjectId", "The project of a late fee rule cannot be changed.");
        LateFeeCalculator.Validate(new(r.Method, r.Value, r.MaxAmount, r.MaxPercentOfInstallment));
        var termsChanged = rule.Method != r.Method || rule.Value != r.Value || rule.MaxAmount != r.MaxAmount || rule.MaxPercentOfInstallment != r.MaxPercentOfInstallment;
        if (termsChanged && await IsInUseAsync(id, ct)) throw new DomainException("This rule is used by plans or agreements; its calculation cannot change. Create a new rule and change the penalty through an amendment.");
        db.UseRowVersion(rule, r.RowVersion);
        var old = Snapshot(rule);
        rule.Name = r.Name.Trim(); rule.Description = PricingSupport.N(r.Description); rule.IsActive = r.IsActive;
        rule.Method = r.Method; rule.Value = r.Value; rule.MaxAmount = r.MaxAmount; rule.MaxPercentOfInstallment = r.MaxPercentOfInstallment;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("LateFeeRule.Updated", "LateFeeRule", rule.Id.ToString(), old, Snapshot(rule)), ct);
        return (await MapAsync([rule], ct))[0];
    }

    public async Task<LateFeePreviewDto> PreviewLateFeeAsync(LateFeePreviewRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var rule = await LoadRuleAsync(r.RuleId, tracked: false, ct);
        if (r.InstallmentAmount <= 0 || r.Outstanding < 0 || r.Outstanding > r.InstallmentAmount) throw PricingSupport.Invalid("Outstanding", "Enter a positive installment amount and an outstanding amount not above it.");
        var policy = await support.PolicyAsync(ct);
        return new LateFeePreviewDto(LateFeeCalculator.Compute(InstallmentSupport.Terms(rule), r.InstallmentAmount, r.Outstanding, r.GraceEndDate, r.AsOf, policy),
            LateFeeCalculator.OverdueDays(r.GraceEndDate, r.AsOf), LateFeeCalculator.StartedMonths(r.GraceEndDate, r.AsOf));
    }

    private async Task<LateFeeRule> LoadRuleAsync(long id, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.LateFeeRules : db.LateFeeRules.AsNoTracking();
        var rule = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Late fee rule not found.");
        if (rule.ProjectId is { } pid) await access.EnsureAccessAsync(pid, ct);
        return rule;
    }

    private async Task<bool> IsInUseAsync(long id, CancellationToken ct) =>
        await db.InstallmentPlanComponents.AnyAsync(c => c.LateFeeRuleId == id, ct) || await db.AgreementCharges.AnyAsync(c => c.LateFeeRuleId == id, ct);

    private async Task<IReadOnlyList<LateFeeRuleDto>> MapAsync(IReadOnlyList<LateFeeRule> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var used = (await db.InstallmentPlanComponents.AsNoTracking().Where(c => c.LateFeeRuleId != null && ids.Contains(c.LateFeeRuleId.Value)).Select(c => c.LateFeeRuleId!.Value).ToListAsync(ct))
            .Concat(await db.AgreementCharges.AsNoTracking().Where(c => c.LateFeeRuleId != null && ids.Contains(c.LateFeeRuleId.Value)).Select(c => c.LateFeeRuleId!.Value).ToListAsync(ct)).ToHashSet();
        var pids = rows.Where(r => r.ProjectId != null).Select(r => r.ProjectId!.Value).Distinct().ToList();
        var codes = await db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        return rows.Select(r => new LateFeeRuleDto(r.Id, r.Code, r.Name, r.ProjectId, r.ProjectId is { } p ? codes.GetValueOrDefault(p) : null, r.Method, r.Value, r.MaxAmount,
            r.MaxPercentOfInstallment, r.Description, r.IsActive, used.Contains(r.Id), r.RowVersion.ToToken(), r.CreatedAtUtc, r.CreatedBy)).ToList();
    }

    private static object Snapshot(LateFeeRule r) => new { r.Code, r.Name, r.ProjectId, Method = r.Method.ToString(), r.Value, r.MaxAmount, r.MaxPercentOfInstallment, r.IsActive };
}
