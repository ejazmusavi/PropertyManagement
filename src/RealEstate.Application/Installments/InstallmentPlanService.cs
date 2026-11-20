using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Installments;

namespace RealEstate.Application.Installments;

/// <summary>
/// Installment plan templates (standard project, property category, property-specific, customer-specific and cash) and their components.
/// Draft plans are editable; activation validates the plan (customer-specific plans need a second user holding installments.approve);
/// active plans are frozen and only retired, so agreements built from them stay explainable.
/// </summary>
public class InstallmentPlanService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, CustomerService customers, InstallmentSupport support,
    IValidator<CreateInstallmentPlanRequest> createValidator, IValidator<UpdateInstallmentPlanRequest> updateValidator, IValidator<PlanComponentInput> componentValidator,
    IValidator<RetirePlanRequest> retireValidator, IValidator<CopyPlanRequest> copyValidator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");

    public async Task<PagedResult<InstallmentPlanListItemDto>> ListAsync(InstallmentPlanListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.InstallmentPlans.AsNoTracking().Where(p => projects.Any(x => x.Id == p.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(p => p.ProjectId == pid);
        if (r.PlanType is { } t) q = q.Where(p => p.PlanType == t);
        if (r.Status is { } s) q = q.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var term = r.Search.Trim(); q = q.Where(p => p.Code.Contains(term) || p.Name.Contains(term)); }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("name", false) => q.OrderBy(p => p.Name).ThenBy(p => p.Id), ("name", true) => q.OrderByDescending(p => p.Name).ThenBy(p => p.Id),
            ("type", false) => q.OrderBy(p => p.PlanType).ThenBy(p => p.Code), ("type", true) => q.OrderByDescending(p => p.PlanType).ThenBy(p => p.Code),
            ("status", false) => q.OrderBy(p => p.Status).ThenBy(p => p.Code), ("status", true) => q.OrderByDescending(p => p.Status).ThenBy(p => p.Code),
            ("code", true) => q.OrderByDescending(p => p.Code), _ => q.OrderBy(p => p.Code).ThenBy(p => p.Id),
        };
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(p => new
        {
            p.Id, p.ProjectId, p.Code, p.Name, p.PlanType, p.Status, p.PropertyCategoryId, p.PropertyId, p.CustomerId, Count = p.Components.Count, p.CreatedAtUtc, p.CreatedBy,
        }).ToListAsync(ct);
        var names = await TargetNamesAsync(rows.Select(x => (x.ProjectId, x.PropertyCategoryId, x.PropertyId, x.CustomerId)).ToList(), ct);
        return new PagedResult<InstallmentPlanListItemDto>(rows.Select(x => new InstallmentPlanListItemDto(x.Id, x.ProjectId, names.Projects.GetValueOrDefault(x.ProjectId, ""), x.Code, x.Name,
            x.PlanType, x.Status, Target(x.PlanType, x.PropertyCategoryId, x.PropertyId, x.CustomerId, names), x.Count, x.CreatedAtUtc, x.CreatedBy)).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<InstallmentPlanDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await EnsurePlanAccessAsync(id, ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Active plans that can be applied to the agreement (project, payment mode, category, property and customer must match).</summary>
    public async Task<IReadOnlyList<InstallmentPlanListItemDto>> ApplicablePlansAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var ctx = await AgreementContextAsync(agreementId, ct);
        var plans = await db.InstallmentPlans.AsNoTracking().Include(p => p.Components).Where(p => p.ProjectId == ctx.ProjectId && p.Status == InstallmentPlanStatus.Active).OrderBy(p => p.PlanType).ThenBy(p => p.Code).ToListAsync(ct);
        var ok = plans.Where(p => Applicability(p, ctx) is null).ToList();
        var names = await TargetNamesAsync(ok.Select(x => (x.ProjectId, x.PropertyCategoryId, x.PropertyId, x.CustomerId)).ToList(), ct);
        return ok.Select(x => new InstallmentPlanListItemDto(x.Id, x.ProjectId, names.Projects.GetValueOrDefault(x.ProjectId, ""), x.Code, x.Name, x.PlanType, x.Status,
            Target(x.PlanType, x.PropertyCategoryId, x.PropertyId, x.CustomerId, names), x.Components.Count, x.CreatedAtUtc, x.CreatedBy)).ToList();
    }

    public async Task<InstallmentPlanDetailDto> CreateAsync(CreateInstallmentPlanRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await access.EnsureAccessAsync(r.ProjectId, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == r.ProjectId, ct);
        if (project.IsReadOnly) throw new DomainException("Archived projects cannot be changed.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.InstallmentPlans.AnyAsync(p => p.ProjectId == r.ProjectId && p.Code == code, ct)) throw PricingSupport.Invalid("Code", "A plan with this code already exists in the project.");
        await ValidateTargetAsync(r.ProjectId, r.PlanType, r.PropertyCategoryId, r.PropertyId, r.CustomerId, ct);
        var plan = new InstallmentPlan
        {
            ProjectId = r.ProjectId, Code = code, Name = r.Name.Trim(), Description = PricingSupport.N(r.Description), PlanType = r.PlanType,
            PropertyCategoryId = r.PropertyCategoryId, PropertyId = r.PropertyId, CustomerId = r.CustomerId,
        };
        var i = 0;
        foreach (var c in r.Components ?? [])
        {
            var comp = new InstallmentPlanComponent();
            await ApplyComponentAsync(comp, c, plan, ct);
            comp.SortOrder = c.SortOrder ?? ++i * 10;
            plan.Components.Add(comp);
        }
        EnsureSingleRemainder(plan);
        db.InstallmentPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.Created", "InstallmentPlan", plan.Id.ToString(), NewValues: new
        {
            plan.ProjectId, plan.Code, plan.Name, PlanType = plan.PlanType.ToString(), plan.PropertyCategoryId, plan.PropertyId, plan.CustomerId, Components = plan.Components.Count,
        }), ct);
        return await BuildAsync(plan.Id, ct);
    }

    public async Task<InstallmentPlanDetailDto> UpdateAsync(long id, UpdateInstallmentPlanRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        await updateValidator.ValidateOrThrowAsync(r, ct);
        var plan = await DraftAsync(id, ct);
        await ValidateTargetAsync(plan.ProjectId, plan.PlanType, r.PropertyCategoryId, r.PropertyId, r.CustomerId, ct);
        db.UseRowVersion(plan, r.RowVersion);
        var old = new { plan.Name, plan.Description, plan.PropertyCategoryId, plan.PropertyId, plan.CustomerId };
        plan.Name = r.Name.Trim(); plan.Description = PricingSupport.N(r.Description);
        plan.PropertyCategoryId = r.PropertyCategoryId; plan.PropertyId = r.PropertyId; plan.CustomerId = r.CustomerId;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.Updated", "InstallmentPlan", id.ToString(), old, new { plan.Name, plan.Description, plan.PropertyCategoryId, plan.PropertyId, plan.CustomerId }), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<InstallmentPlanDetailDto> AddComponentAsync(long id, PlanComponentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        await componentValidator.ValidateOrThrowAsync(r.Component, ct);
        var plan = await DraftAsync(id, ct);
        if (plan.Components.Count >= 50) throw new DomainException("A plan can have at most 50 components.");
        db.UseRowVersion(plan, r.RowVersion);
        var comp = new InstallmentPlanComponent();
        await ApplyComponentAsync(comp, r.Component, plan, ct);
        comp.SortOrder = r.Component.SortOrder ?? (plan.Components.Count == 0 ? 10 : plan.Components.Max(c => c.SortOrder) + 10);
        plan.Components.Add(comp);
        EnsureSingleRemainder(plan);
        Touch(plan);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.ComponentAdded", "InstallmentPlan", id.ToString(), NewValues: ComponentSnapshot(comp)), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<InstallmentPlanDetailDto> UpdateComponentAsync(long id, long componentId, PlanComponentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        await componentValidator.ValidateOrThrowAsync(r.Component, ct);
        var plan = await DraftAsync(id, ct);
        var comp = plan.Components.FirstOrDefault(c => c.Id == componentId) ?? throw new NotFoundException("Component not found.");
        db.UseRowVersion(plan, r.RowVersion);
        var old = ComponentSnapshot(comp);
        await ApplyComponentAsync(comp, r.Component, plan, ct);
        if (r.Component.SortOrder is { } so) comp.SortOrder = so;
        EnsureSingleRemainder(plan);
        Touch(plan);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.ComponentUpdated", "InstallmentPlan", id.ToString(), old, ComponentSnapshot(comp)), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<InstallmentPlanDetailDto> RemoveComponentAsync(long id, long componentId, PlanRowVersionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        var plan = await DraftAsync(id, ct);
        var comp = plan.Components.FirstOrDefault(c => c.Id == componentId) ?? throw new NotFoundException("Component not found.");
        db.UseRowVersion(plan, r.RowVersion);
        var old = ComponentSnapshot(comp);
        plan.Components.Remove(comp);
        db.InstallmentPlanComponents.Remove(comp); // draft templates only: nothing financial refers to a draft component
        Touch(plan);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.ComponentRemoved", "InstallmentPlan", id.ToString(), old), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<InstallmentPlanDetailDto> ActivateAsync(long id, PlanRowVersionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        var userId = RequireUser();
        await EnsurePlanAccessAsync(id, ct);
        var plan = await DraftAsync(id, ct);
        if (plan.PlanType == InstallmentPlanType.CustomerSpecific)
        {
            if (!await permissions.HasPermissionAsync(userId, Permissions.InstallmentsApprove, ct)) throw new ForbiddenException("Activating a customer-specific plan requires the installments.approve permission.");
            if (plan.CreatedBy == user.UserName) throw new DomainException("A customer-specific plan must be activated by a different user than the one who created it.");
        }
        ValidateForActivation(plan);
        db.UseRowVersion(plan, r.RowVersion);
        plan.Status = InstallmentPlanStatus.Active; plan.ActivatedByUserId = userId; plan.ActivatedBy = user.UserName; plan.ActivatedAtUtc = Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.Activated", "InstallmentPlan", id.ToString(), new { Status = "Draft" }, new { Status = "Active" }, UserId: userId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<InstallmentPlanDetailDto> RetireAsync(long id, RetirePlanRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        await retireValidator.ValidateOrThrowAsync(r, ct);
        var plan = await db.InstallmentPlans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan not found.");
        if (plan.Status == InstallmentPlanStatus.Retired) throw new DomainException("The plan is already retired.");
        db.UseRowVersion(plan, r.RowVersion);
        var old = plan.Status;
        plan.Status = InstallmentPlanStatus.Retired; plan.RetiredBy = user.UserName; plan.RetiredAtUtc = Now; plan.RetireReason = r.Reason.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.Retired", "InstallmentPlan", id.ToString(), new { Status = old.ToString() }, new { Status = "Retired" }, r.Reason.Trim()), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Copies a plan (any status) into a new Draft plan of the same project, the normal way to change an active plan.</summary>
    public async Task<InstallmentPlanDetailDto> CopyAsync(long id, CopyPlanRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsManage, ct);
        await EnsurePlanAccessAsync(id, ct);
        await copyValidator.ValidateOrThrowAsync(r, ct);
        var src = await db.InstallmentPlans.AsNoTracking().Include(p => p.Components).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan not found.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.InstallmentPlans.AnyAsync(p => p.ProjectId == src.ProjectId && p.Code == code, ct)) throw PricingSupport.Invalid("Code", "A plan with this code already exists in the project.");
        var plan = new InstallmentPlan
        {
            ProjectId = src.ProjectId, Code = code, Name = r.Name.Trim(), Description = src.Description, PlanType = src.PlanType, PropertyCategoryId = src.PropertyCategoryId,
            PropertyId = src.PropertyId, CustomerId = src.CustomerId,
            Components = src.Components.Select(c => new InstallmentPlanComponent
            {
                SortOrder = c.SortOrder, ChargeTypeId = c.ChargeTypeId, Name = c.Name, Category = c.Category, AmountBasis = c.AmountBasis, Amount = c.Amount, Percent = c.Percent, Frequency = c.Frequency,
                NumberOfInstallments = c.NumberOfInstallments, InstallmentAmount = c.InstallmentAmount, FirstDueOffsetMonths = c.FirstDueOffsetMonths, FirstDueOffsetDays = c.FirstDueOffsetDays,
                DueDayOfMonth = c.DueDayOfMonth, CustomScheduleJson = c.CustomScheduleJson, GracePeriodDays = c.GracePeriodDays, LateFeeRuleId = c.LateFeeRuleId, WaiverAllowed = c.WaiverAllowed,
                MaxWaiverPercent = c.MaxWaiverPercent, TaxTreatment = c.TaxTreatment, TaxRatePercent = c.TaxRatePercent, RoundingUnit = c.RoundingUnit, Remarks = c.Remarks,
            }).ToList(),
        };
        db.InstallmentPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("InstallmentPlan.Copied", "InstallmentPlan", plan.Id.ToString(), NewValues: new { From = src.Code, plan.Code, plan.Name }), ct);
        return await BuildAsync(plan.Id, ct);
    }

    /// <summary>Resolves the plan for a sample price and shows the schedule it would produce (nothing is stored).</summary>
    public async Task<PlanPreviewDto> PreviewAsync(long id, PlanPreviewRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await EnsurePlanAccessAsync(id, ct);
        var policy = await support.PolicyAsync(ct);
        if (r.Price <= 0 || policy.Round(r.Price) != r.Price) throw PricingSupport.Invalid("Price", "Enter a positive price.");
        if (r.BookingAmount < 0 || r.BookingAmount > r.Price || policy.Round(r.BookingAmount) != r.BookingAmount) throw PricingSupport.Invalid("BookingAmount", "The booking amount must be between 0 and the price.");
        var plan = await db.InstallmentPlans.AsNoTracking().Include(p => p.Components).ThenInclude(c => c.ChargeType).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan not found.");
        var downPaymentType = await DownPaymentTypeIdAsync(ct);
        var resolved = PlanResolver.Resolve(plan.Components.Select(ToSpec).ToList(), r.Price, r.BookingAmount, r.StartDate, r.StartDate, policy, downPaymentType);
        var specs = resolved.Select((c, i) => new ChargeSpec(-(i + 1), c.SortOrder, c.Description, c.Category, c.Amount, c.Frequency, c.NumberOfInstallments, c.InstallmentAmount,
            c.FirstDueDate, c.DueDayOfMonth, c.CustomEntries, c.GracePeriodDays, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit)).ToList();
        var schedule = AgreementScheduleService.BuildPreview(specs, policy, r.Price, await support.TodayAsync(ct));
        return new PlanPreviewDto(resolved.Select(c => new ResolvedChargeDto(c.SortOrder, c.Description, c.Category, c.Amount, c.Frequency, c.NumberOfInstallments, c.InstallmentAmount,
            c.FirstDueDate, c.DueDayOfMonth, c.GracePeriodDays, c.TaxTreatment, c.TaxRatePercent)).ToList(), schedule);
    }

    // =====================================================================================================
    // shared with the schedule service
    // =====================================================================================================
    internal sealed record AgreementContext(long AgreementId, long ProjectId, PaymentMode PaymentMode, IReadOnlyList<long> PropertyIds, IReadOnlyList<long> CategoryIds, IReadOnlyList<long> CustomerIds);

    internal async Task<AgreementContext> AgreementContextAsync(long agreementId, CancellationToken ct)
    {
        var a = await (from ag in db.CustomerAgreements.AsNoTracking() join b in db.Bookings.AsNoTracking() on ag.BookingId equals b.Id where ag.Id == agreementId
                       select new { b.Id, b.ProjectId, b.PaymentMode }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Agreement not found.");
        var props = await db.BookingProperties.AsNoTracking().Where(p => p.BookingId == a.Id).Select(p => new { p.PropertyId, p.Property!.PropertyType!.CategoryId }).ToListAsync(ct);
        var owners = await db.BookingCustomers.AsNoTracking().Where(c => c.BookingId == a.Id && c.IsActive).Select(c => c.CustomerId).ToListAsync(ct);
        return new AgreementContext(agreementId, a.ProjectId, a.PaymentMode, props.Select(p => p.PropertyId).ToList(), props.Select(p => p.CategoryId).Distinct().ToList(), owners);
    }

    /// <summary>Null when the plan applies to the agreement, otherwise the reason it does not.</summary>
    internal static string? Applicability(InstallmentPlan p, AgreementContext a)
    {
        if (p.Status != InstallmentPlanStatus.Active) return "The plan is not active.";
        if (p.ProjectId != a.ProjectId) return "The plan belongs to another project.";
        if (p.PlanType == InstallmentPlanType.Cash && a.PaymentMode != PaymentMode.Cash) return "A cash plan applies only to cash sales.";
        if (p.PlanType != InstallmentPlanType.Cash && a.PaymentMode != PaymentMode.Installment) return "Installment plans apply only to installment sales; use a cash plan.";
        return p.PlanType switch
        {
            InstallmentPlanType.PropertyCategory when a.CategoryIds.Count != 1 || a.CategoryIds[0] != p.PropertyCategoryId => "The plan is for another property category.",
            InstallmentPlanType.PropertySpecific when !a.PropertyIds.Contains(p.PropertyId ?? 0) => "The plan is for another property.",
            InstallmentPlanType.CustomerSpecific when !a.CustomerIds.Contains(p.CustomerId ?? 0) => "The plan is for another customer.",
            InstallmentPlanType.CustomerSpecific when p.PropertyId is { } pp && !a.PropertyIds.Contains(pp) => "The plan is for another property.",
            _ => null,
        };
    }

    internal static ComponentSpec ToSpec(InstallmentPlanComponent c) => new(c.Id, c.SortOrder, c.ChargeTypeId, c.ChargeType?.Kind ?? ChargeComponentKind.CustomCharge, c.Name, c.Category,
        c.AmountBasis, c.Amount, c.Percent, c.Frequency, c.NumberOfInstallments, c.InstallmentAmount, c.FirstDueOffsetMonths, c.FirstDueOffsetDays, c.DueDayOfMonth,
        InstallmentSupport.ParseTemplate(c.CustomScheduleJson).Select(e => new TemplateEntry(e.OffsetMonths, e.SharePercent)).ToList(), c.GracePeriodDays, c.LateFeeRuleId,
        c.WaiverAllowed, c.MaxWaiverPercent, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit, c.Remarks);

    internal async Task<long> DownPaymentTypeIdAsync(CancellationToken ct) =>
        await db.ChargeTypes.AsNoTracking().Where(t => t.Code == ChargeTypeCodes.DownPayment).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
        ?? throw new DomainException("The standard DOWN_PAYMENT charge type is missing; run the database initializer.");

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task EnsurePlanAccessAsync(long id, CancellationToken ct)
    {
        var pid = await db.InstallmentPlans.AsNoTracking().Where(p => p.Id == id).Select(p => (long?)p.ProjectId).FirstOrDefaultAsync(ct);
        if (pid is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this plan.");
            throw new NotFoundException("Plan not found.");
        }
        await access.EnsureAccessAsync(pid.Value, ct);
    }

    private async Task<InstallmentPlan> DraftAsync(long id, CancellationToken ct)
    {
        var plan = await db.InstallmentPlans.Include(p => p.Components).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan not found.");
        if (plan.Status != InstallmentPlanStatus.Draft) throw new DomainException("Only draft plans can be changed. Copy the plan to make a new version.");
        return plan;
    }

    private void Touch(InstallmentPlan plan) => db.Entry(plan).Property(p => p.Name).IsModified = true; // bumps the row version so concurrent component edits conflict

    private async Task ValidateTargetAsync(long projectId, InstallmentPlanType type, long? categoryId, long? propertyId, long? customerId, CancellationToken ct)
    {
        if (type == InstallmentPlanType.PropertyCategory && (categoryId is null || !await db.PropertyCategories.AnyAsync(c => c.Id == categoryId, ct))) throw PricingSupport.Invalid("PropertyCategoryId", "Property category not found.");
        if (type != InstallmentPlanType.PropertyCategory && categoryId is not null) throw PricingSupport.Invalid("PropertyCategoryId", "A property category applies only to property-category plans.");
        if (type == InstallmentPlanType.PropertySpecific && propertyId is null) throw PricingSupport.Invalid("PropertyId", "Choose the property.");
        if (type is not (InstallmentPlanType.PropertySpecific or InstallmentPlanType.CustomerSpecific) && propertyId is not null) throw PricingSupport.Invalid("PropertyId", "A property applies only to property- or customer-specific plans.");
        if (propertyId is { } p && !await db.Properties.AnyAsync(x => x.Id == p && x.ProjectId == projectId, ct)) throw PricingSupport.Invalid("PropertyId", "The property does not belong to this project.");
        if (type == InstallmentPlanType.CustomerSpecific)
        {
            if (customerId is not { } c) throw PricingSupport.Invalid("CustomerId", "Choose the customer.");
            await customers.EnsureAccessAsync(c, ct);
        }
        else if (customerId is not null) throw PricingSupport.Invalid("CustomerId", "A customer applies only to customer-specific plans.");
    }

    private async Task ApplyComponentAsync(InstallmentPlanComponent comp, PlanComponentInput c, InstallmentPlan plan, CancellationToken ct)
    {
        if (!await db.ChargeTypes.AnyAsync(t => t.Id == c.ChargeTypeId && t.IsActive, ct)) throw PricingSupport.Invalid("ChargeTypeId", "The charge type was not found or is inactive.");
        if (c.LateFeeRuleId is { } lr && !await db.LateFeeRules.AnyAsync(r => r.Id == lr && r.IsActive && (r.ProjectId == null || r.ProjectId == plan.ProjectId), ct))
            throw PricingSupport.Invalid("LateFeeRuleId", "The late fee rule was not found, is inactive or belongs to another project.");
        if (plan.PlanType == InstallmentPlanType.Cash && c.Frequency != ScheduleFrequency.OneTime) throw PricingSupport.Invalid("Frequency", "A cash plan can contain only one-time payments.");
        comp.ChargeTypeId = c.ChargeTypeId; comp.Name = c.Name.Trim(); comp.Category = c.Category; comp.AmountBasis = c.AmountBasis; comp.Amount = c.Amount; comp.Percent = c.Percent;
        comp.Frequency = c.Frequency; comp.NumberOfInstallments = c.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : c.NumberOfInstallments;
        comp.InstallmentAmount = c.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : c.InstallmentAmount;
        comp.FirstDueOffsetMonths = c.FirstDueOffsetMonths; comp.FirstDueOffsetDays = c.FirstDueOffsetDays;
        comp.DueDayOfMonth = ScheduleGenerationService.IsRegular(c.Frequency) ? c.DueDayOfMonth : null;
        comp.CustomScheduleJson = c.Frequency == ScheduleFrequency.CustomDates ? InstallmentSupport.SerializeTemplate(c.CustomEntries) : null;
        comp.GracePeriodDays = c.GracePeriodDays; comp.LateFeeRuleId = c.LateFeeRuleId; comp.WaiverAllowed = c.WaiverAllowed; comp.MaxWaiverPercent = c.WaiverAllowed ? c.MaxWaiverPercent : 0m;
        comp.TaxTreatment = c.TaxTreatment; comp.TaxRatePercent = c.TaxRatePercent; comp.RoundingUnit = c.RoundingUnit; comp.Remarks = PricingSupport.N(c.Remarks);
    }

    private static void EnsureSingleRemainder(InstallmentPlan plan)
    {
        if (plan.Components.Count(c => c.AmountBasis == AmountBasis.Remainder) > 1) throw PricingSupport.Invalid("AmountBasis", "A plan can have only one remainder component.");
    }

    internal static void ValidateForActivation(InstallmentPlan plan)
    {
        if (plan.Components.Count == 0) throw new DomainException("Add at least one component before activating the plan.");
        EnsureSingleRemainder(plan);
        var sc = plan.Components.Where(c => c.Category == ChargeCategory.SaleConsideration).ToList();
        if (sc.Count == 0) throw new DomainException("The plan needs at least one sale-consideration component (down payment, installments, possession ...).");
        var pct = sc.Where(c => c.AmountBasis == AmountBasis.PercentOfPrice).Sum(c => c.Percent ?? 0m);
        if (pct > 100m) throw new DomainException($"Sale-consideration percentages total {pct:0.####}%, more than 100%.");
        var hasRemainder = sc.Any(c => c.AmountBasis == AmountBasis.Remainder);
        if (!hasRemainder && (pct != 100m || sc.Any(c => c.AmountBasis == AmountBasis.FixedAmount)))
            throw new DomainException("The plan must cover the whole price: add a remainder component, or use percentages of the price that total exactly 100%.");
        if (plan.PlanType == InstallmentPlanType.Cash && plan.Components.Any(c => c.Frequency != ScheduleFrequency.OneTime)) throw new DomainException("A cash plan can contain only one-time payments.");
    }

    private static object ComponentSnapshot(InstallmentPlanComponent c) => new
    {
        c.Id, c.Name, c.ChargeTypeId, Category = c.Category.ToString(), Basis = c.AmountBasis.ToString(), c.Amount, c.Percent, Frequency = c.Frequency.ToString(), c.NumberOfInstallments,
        c.InstallmentAmount, c.FirstDueOffsetMonths, c.FirstDueOffsetDays, c.DueDayOfMonth, c.CustomScheduleJson, c.GracePeriodDays, c.LateFeeRuleId, c.WaiverAllowed, c.MaxWaiverPercent,
        Tax = c.TaxTreatment.ToString(), c.TaxRatePercent, c.RoundingUnit,
    };

    private sealed record Names(Dictionary<long, string> Projects, Dictionary<long, string> Categories, Dictionary<long, string> Properties, Dictionary<long, string> Customers);

    private async Task<Names> TargetNamesAsync(IReadOnlyList<(long ProjectId, long? Cat, long? Prop, long? Cust)> rows, CancellationToken ct)
    {
        var pids = rows.Select(r => r.ProjectId).Distinct().ToList();
        var cats = rows.Where(r => r.Cat != null).Select(r => r.Cat!.Value).Distinct().ToList();
        var props = rows.Where(r => r.Prop != null).Select(r => r.Prop!.Value).Distinct().ToList();
        var custs = rows.Where(r => r.Cust != null).Select(r => r.Cust!.Value).Distinct().ToList();
        return new Names(
            await db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct),
            await db.PropertyCategories.AsNoTracking().Where(p => cats.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct),
            await db.Properties.AsNoTracking().Where(p => props.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PropertyCode, ct),
            await db.Customers.AsNoTracking().Where(p => custs.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => $"{p.CustomerCode} {p.FullName}", ct));
    }

    private static string? Target(InstallmentPlanType t, long? cat, long? prop, long? cust, Names n) => t switch
    {
        InstallmentPlanType.PropertyCategory => cat is { } c ? n.Categories.GetValueOrDefault(c) : null,
        InstallmentPlanType.PropertySpecific => prop is { } p ? n.Properties.GetValueOrDefault(p) : null,
        InstallmentPlanType.CustomerSpecific => (cust is { } cu ? n.Customers.GetValueOrDefault(cu) : null) + (prop is { } pp ? $" / {n.Properties.GetValueOrDefault(pp)}" : ""),
        _ => null,
    };

    private async Task<InstallmentPlanDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var p = await db.InstallmentPlans.AsNoTracking().Include(x => x.Components).ThenInclude(c => c.ChargeType).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Plan not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == p.ProjectId, ct);
        var names = await TargetNamesAsync([(p.ProjectId, p.PropertyCategoryId, p.PropertyId, p.CustomerId)], ct);
        var ruleIds = p.Components.Where(c => c.LateFeeRuleId != null).Select(c => c.LateFeeRuleId!.Value).Distinct().ToList();
        var rules = await db.LateFeeRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var comps = p.Components.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Select(c => new PlanComponentDto(c.Id, c.SortOrder, c.ChargeTypeId, c.ChargeType?.Name ?? "", c.ChargeType?.Kind ?? ChargeComponentKind.CustomCharge,
            c.Name, c.Category, c.AmountBasis, c.Amount, c.Percent, c.Frequency, c.NumberOfInstallments, c.InstallmentAmount, c.FirstDueOffsetMonths, c.FirstDueOffsetDays, c.DueDayOfMonth,
            InstallmentSupport.ParseTemplate(c.CustomScheduleJson), c.GracePeriodDays, c.LateFeeRuleId, c.LateFeeRuleId is { } r ? rules.GetValueOrDefault(r) : null, c.WaiverAllowed,
            c.MaxWaiverPercent, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit, c.Remarks)).ToList();
        var sc = p.Components.Where(c => c.Category == ChargeCategory.SaleConsideration).ToList();
        return new InstallmentPlanDetailDto(p.Id, p.ProjectId, project.ProjectCode, project.ProjectName, p.Code, p.Name, p.Description, p.PlanType, p.PropertyCategoryId,
            p.PropertyCategoryId is { } pc ? names.Categories.GetValueOrDefault(pc) : null, p.PropertyId, p.PropertyId is { } pp ? names.Properties.GetValueOrDefault(pp) : null,
            p.CustomerId, p.CustomerId is { } cu ? names.Customers.GetValueOrDefault(cu) : null, p.Status, p.ActivatedBy, p.ActivatedAtUtc, p.RetiredBy, p.RetiredAtUtc, p.RetireReason, comps,
            sc.Where(c => c.AmountBasis == AmountBasis.PercentOfPrice).Sum(c => c.Percent ?? 0m), sc.Any(c => c.AmountBasis == AmountBasis.Remainder),
            p.PlanType == InstallmentPlanType.CustomerSpecific, p.CreatedAtUtc, p.CreatedBy, p.RowVersion.ToToken());
    }
}

/// <summary>Codes of the seeded standard charge types.</summary>
public static class ChargeTypeCodes
{
    public const string DownPayment = "DOWN_PAYMENT";
    public const string Confirmation = "CONFIRMATION";
    public const string Monthly = "MONTHLY";
    public const string Quarterly = "QUARTERLY";
    public const string HalfYearly = "HALF_YEARLY";
    public const string Annual = "ANNUAL";
    public const string Possession = "POSSESSION";
    public const string Development = "DEVELOPMENT";
    public const string Utility = "UTILITY";
    public const string GovernmentFee = "GOVT_FEE";
    public const string Custom = "CUSTOM";
}
