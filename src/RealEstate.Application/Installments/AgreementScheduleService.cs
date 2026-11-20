using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Installments;

namespace RealEstate.Application.Installments;

/// <summary>
/// Customer agreement installment structure: applying a plan template, building/customizing agreement charges, previewing and generating the
/// payment schedule (version 1), and reading schedules and their history. Changes after generation go through <see cref="AgreementAmendmentService"/>.
/// </summary>
public class AgreementScheduleService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, InstallmentSupport support,
    InstallmentPlanService plans, BookingService bookings, IValidator<AgreementChargeInput> chargeValidator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");

    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<InstallmentAgreementListItemDto>> ListAsync(InstallmentAgreementListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = from a in db.CustomerAgreements.AsNoTracking()
                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                where projects.Any(p => p.Id == b.ProjectId)
                select new { a, b };
        if (r.ProjectId is { } pid) q = q.Where(x => x.b.ProjectId == pid);
        if (r.Status is { } s) q = q.Where(x => x.a.Status == s);
        if (r.PaymentMode is { } pm) q = q.Where(x => x.b.PaymentMode == pm);
        if (r.HasSchedule is { } hs) q = q.Where(x => db.PaymentSchedules.Any(ps => ps.AgreementId == x.a.Id) == hs);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(x => x.a.AgreementNumber.Contains(t) || x.b.BookingNumber.Contains(t) || x.b.Customers.Any(c => c.IsActive && (c.Customer!.FullName.Contains(t) || c.Customer.CustomerCode.Contains(t))));
        }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("number", false) => q.OrderBy(x => x.a.AgreementNumber), ("number", true) => q.OrderByDescending(x => x.a.AgreementNumber),
            ("amount", false) => q.OrderBy(x => x.b.TotalPayable).ThenBy(x => x.a.Id), ("amount", true) => q.OrderByDescending(x => x.b.TotalPayable).ThenBy(x => x.a.Id),
            (_, false) => q.OrderByDescending(x => x.a.Id), _ => q.OrderBy(x => x.a.Id),
        };
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new
        {
            x.a.Id, x.a.AgreementNumber, BookingId = x.b.Id, x.b.BookingNumber, x.b.ProjectId, x.b.PaymentMode, x.a.Status, x.b.TotalPayable,
            Primary = x.b.Customers.Where(c => c.IsActive && c.IsPrimary).Select(c => c.Customer!.FullName).FirstOrDefault(),
            Charges = db.AgreementCharges.Where(c => c.AgreementId == x.a.Id && c.Status == AgreementChargeStatus.Active).Sum(c => (decimal?)c.Amount) ?? 0m,
            Version = db.PaymentSchedules.Where(ps => ps.ActiveAgreementId == x.a.Id).Select(ps => (int?)ps.VersionNo).FirstOrDefault(),
            Outstanding = db.PaymentScheduleItems.Where(i => db.PaymentSchedules.Any(ps => ps.Id == i.ScheduleId && ps.ActiveAgreementId == x.a.Id)).Sum(i => (decimal?)(i.Amount - i.AmountPaid - i.AmountWaived)),
            Pending = db.AgreementAmendments.Any(m => m.PendingAgreementId == x.a.Id),
        }).ToListAsync(ct);
        var codes = await db.Projects.AsNoTracking().Where(p => rows.Select(x => x.ProjectId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        return new PagedResult<InstallmentAgreementListItemDto>(rows.Select(x => new InstallmentAgreementListItemDto(x.Id, x.AgreementNumber, x.BookingId, x.BookingNumber, x.ProjectId,
            codes.GetValueOrDefault(x.ProjectId, ""), x.Primary, x.PaymentMode, x.Status, x.TotalPayable, x.Charges, x.Version, x.Version is null ? null : x.Outstanding ?? 0m, x.Pending)).ToList(),
            r.Page, r.PageSize, total);
    }

    public async Task<AgreementInstallmentDto> GetAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        return await BuildAsync(agreementId, ct);
    }

    public async Task<PaymentScheduleDto> GetScheduleAsync(long agreementId, int? versionNo = null, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var ag = await db.CustomerAgreements.AsNoTracking().FirstAsync(a => a.Id == agreementId, ct);
        var all = await db.PaymentSchedules.AsNoTracking().Include(s => s.Items).Where(s => s.AgreementId == agreementId).OrderBy(s => s.VersionNo).ToListAsync(ct);
        if (all.Count == 0) throw new NotFoundException("No payment schedule has been generated for this agreement yet.");
        var s = versionNo is { } v ? all.FirstOrDefault(x => x.VersionNo == v) ?? throw new NotFoundException("Schedule version not found.")
                                   : all.FirstOrDefault(x => x.Status == PaymentScheduleStatus.Active) ?? all[^1];
        var policy = await support.PolicyAsync(ct);
        var today = await support.TodayAsync(ct);
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId).ToDictionaryAsync(c => c.Id, ct);
        var ruleIds = charges.Values.Where(c => c.LateFeeRuleId != null).Select(c => c.LateFeeRuleId!.Value).Distinct().ToList();
        var rules = await db.LateFeeRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var lines = s.Items.OrderBy(i => i.LineNo).Select(i => InstallmentSupport.Line(i, charges, rules, today, policy)).ToList();
        var chargeIds = s.Items.Select(i => i.AgreementChargeId).Distinct().ToHashSet();
        var reconciliation = ScheduleGenerationService.Reconcile(charges.Values.Where(c => chargeIds.Contains(c.Id) || (s.Status == PaymentScheduleStatus.Active && c.Status == AgreementChargeStatus.Active))
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Select(c => (c.Id, c.Description, c.Amount)), s.Items.Select(i => (i.AgreementChargeId, i.Amount)))
            .Select(r => new ReconciliationDto(r.ChargeKey, r.Description, r.ChargeAmount, r.ScheduledAmount, r.Difference)).ToList();
        var hash = ScheduleGenerationService.Hash(s.Items.Select(i => (i.LineNo, i.AgreementChargeId, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount)));
        return new PaymentScheduleDto(s.Id, agreementId, ag.AgreementNumber, s.VersionNo, s.Status, s.Reason, s.GeneratedAtUtc, s.GeneratedBy, s.SupersededAtUtc, s.SourceAmendmentId,
            s.TotalAmount, s.TotalTax, s.Items.Sum(i => i.AmountPaid), s.Items.Sum(i => i.AmountWaived), s.Items.Sum(i => i.Outstanding), lines.Sum(l => l.LateFeeDue), s.ItemCount, s.ContentHash,
            hash == s.ContentHash, reconciliation.All(r => r.Difference == 0m) && s.Items.Sum(i => i.Amount) == s.TotalAmount, lines, reconciliation,
            all.Select(x => InstallmentSupport.Summary(x, today)).ToList(), await support.CurrencyAsync(ct));
    }

    public async Task<AgreementHistoryDto> GetHistoryAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var ag = await db.CustomerAgreements.AsNoTracking().Include(a => a.Versions).FirstAsync(a => a.Id == agreementId, ct);
        var booking = await db.Bookings.AsNoTracking().Where(b => b.Id == ag.BookingId).Select(b => b.BookingNumber).FirstAsync(ct);
        var today = await support.TodayAsync(ct);
        var schedules = await db.PaymentSchedules.AsNoTracking().Include(s => s.Items).Where(s => s.AgreementId == agreementId).OrderBy(s => s.VersionNo).ToListAsync(ct);
        var versionOf = schedules.ToDictionary(s => s.Id, s => s.VersionNo);
        var amendments = await db.AgreementAmendments.AsNoTracking().Where(a => a.AgreementId == agreementId).OrderBy(a => a.Id).ToListAsync(ct);
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId).ToDictionaryAsync(c => c.Id, c => c.Description, ct);
        var amendmentIds = amendments.Select(a => a.Id).ToList();
        var changes = await db.ScheduleAmendments.AsNoTracking().Where(c => amendmentIds.Contains(c.AmendmentId)).OrderBy(c => c.AmendmentId).ThenBy(c => c.Id).ToListAsync(ct);
        var typeOf = amendments.ToDictionary(a => a.Id, a => a.AmendmentType);
        var waivers = await WaiversAsync(db.Waivers.AsNoTracking().Where(w => w.AgreementId == agreementId), ct);
        return new AgreementHistoryDto(agreementId, ag.AgreementNumber, ag.BookingId, booking,
            schedules.Select(s => InstallmentSupport.Summary(s, today)).ToList(),
            amendments.Select(a => new AmendmentSummaryDto(a.Id, a.AmendmentType, a.Status, a.Reason, a.RequestedBy ?? a.ApprovedBy, a.Status == AmendmentStatus.Approved ? a.ApprovedBy : null,
                a.CreatedAtUtc, a.DecidedAtUtc ?? (a.Status == AmendmentStatus.Approved ? a.CreatedAtUtc : null), a.FromVersionNo, a.ToVersionNo,
                a.BaseScheduleId is { } b ? versionOf.GetValueOrDefault(b) : null, a.ResultScheduleId is { } rs ? versionOf.GetValueOrDefault(rs) : null)).ToList(),
            changes.Select(c => new ScheduleChangeDto(c.AmendmentId, typeOf[c.AmendmentId], versionOf.GetValueOrDefault(c.FromScheduleId), versionOf.GetValueOrDefault(c.ToScheduleId), c.Operation,
                c.AgreementChargeId, c.AgreementChargeId is { } ch ? charges.GetValueOrDefault(ch) : null, c.FromItemId, c.ToItemId, c.OldDueDate, c.NewDueDate, c.OldAmount, c.NewAmount, c.Note)).ToList(),
            ag.Versions.OrderBy(v => v.VersionNo).Select(v => new AgreementVersionDto(v.VersionNo, v.Reason, v.CreatedAtUtc, v.CreatedBy, v.ContentSha256)).ToList(), waivers);
    }

    internal async Task<IReadOnlyList<WaiverDto>> WaiversAsync(IQueryable<Waiver> q, CancellationToken ct)
    {
        var rows = await q.OrderByDescending(w => w.Id).ToListAsync(ct);
        var itemIds = rows.Select(w => w.ScheduleItemId).Distinct().ToList();
        var items = await db.PaymentScheduleItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Description, ct);
        var agIds = rows.Select(w => w.AgreementId).Distinct().ToList();
        var numbers = await db.CustomerAgreements.AsNoTracking().Where(a => agIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.AgreementNumber, ct);
        return rows.Select(w => new WaiverDto(w.Id, w.AgreementId, numbers.GetValueOrDefault(w.AgreementId, ""), w.AmendmentId, w.ScheduleItemId, items.GetValueOrDefault(w.ScheduleItemId, ""),
            w.Kind, w.Amount, w.Reason, w.Status, w.RequestedBy, w.DecidedBy, w.DecidedAtUtc, w.DecisionComments, w.CreatedAtUtc)).ToList();
    }

    // =====================================================================================================
    // schedule builder (before the first schedule)
    // =====================================================================================================
    public async Task<AgreementInstallmentDto> ApplyPlanAsync(long agreementId, ApplyPlanRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsSchedule, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var (ag, booking) = await EditableAsync(agreementId, ct);
        var plan = await db.InstallmentPlans.AsNoTracking().Include(p => p.Components).ThenInclude(c => c.ChargeType).FirstOrDefaultAsync(p => p.Id == r.PlanId, ct)
                   ?? throw PricingSupport.Invalid("PlanId", "Plan not found.");
        var ctx = await plans.AgreementContextAsync(agreementId, ct);
        if (InstallmentPlanService.Applicability(plan, ctx) is { } why) throw PricingSupport.Invalid("PlanId", why);
        var start = r.StartDate ?? booking.BookingDate;
        if (start < booking.BookingDate) throw PricingSupport.Invalid("StartDate", "The schedule cannot start before the booking date.");
        var policy = await support.PolicyAsync(ct);
        var resolved = PlanResolver.Resolve(plan.Components.Select(InstallmentPlanService.ToSpec).ToList(), booking.TotalPayable, booking.BookingAmount, booking.BookingDate, start, policy,
            await plans.DownPaymentTypeIdAsync(ct));
        db.UseRowVersion(ag, r.RowVersion);
        var existing = await db.AgreementCharges.Where(c => c.AgreementId == agreementId).ToListAsync(ct);
        var old = existing.Select(c => new { c.Description, c.Amount }).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AgreementCharges.RemoveRange(existing); // working charges of a schedule that was never generated; replaced as a whole and audited
        var i = 0;
        foreach (var c in resolved)
            db.AgreementCharges.Add(new AgreementCharge
            {
                AgreementId = agreementId, SortOrder = ++i * 10, ChargeTypeId = c.ChargeTypeId, Description = c.Description, Category = c.Category, Amount = c.Amount, Frequency = c.Frequency,
                NumberOfInstallments = c.NumberOfInstallments, InstallmentAmount = c.InstallmentAmount, FirstDueDate = c.FirstDueDate, DueDayOfMonth = c.DueDayOfMonth,
                CustomScheduleJson = InstallmentSupport.SerializeEntries(c.CustomEntries?.Select(e => new ScheduleEntryInput(e.DueDate, e.Amount)).ToList()),
                GracePeriodDays = c.GracePeriodDays, LateFeeRuleId = c.LateFeeRuleId, WaiverAllowed = c.WaiverAllowed, MaxWaiverPercent = c.MaxWaiverPercent,
                TaxTreatment = c.TaxTreatment, TaxRatePercent = c.TaxRatePercent, RoundingUnit = c.RoundingUnit, Remarks = c.Remarks, SourcePlanComponentId = c.SourceComponentId,
            });
        ag.InstallmentPlanId = plan.Id; ag.ScheduleStartDate = start;
        db.Entry(ag).Property(a => a.InstallmentPlanId).IsModified = true;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.PlanApplied", "CustomerAgreement", agreementId.ToString(), new { Charges = old },
            new { Plan = plan.Code, StartDate = start, Charges = resolved.Select(c => new { c.Description, c.Amount, Frequency = c.Frequency.ToString() }) }), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(agreementId, ct);
    }

    public async Task<AgreementInstallmentDto> AddChargeAsync(long agreementId, SaveAgreementChargeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsSchedule, ct);
        var projectId = await support.EnsureAgreementAccessAsync(agreementId, ct);
        await chargeValidator.ValidateOrThrowAsync(r.Charge, ct);
        var (ag, _) = await EditableAsync(agreementId, ct);
        var count = await db.AgreementCharges.CountAsync(c => c.AgreementId == agreementId, ct);
        if (count >= 50) throw new DomainException("An agreement can have at most 50 charges.");
        var charge = new AgreementCharge { AgreementId = agreementId };
        await support.ApplyInputAsync(charge, r.Charge, projectId, ct);
        if (r.Charge.SortOrder is null) charge.SortOrder = (await db.AgreementCharges.Where(c => c.AgreementId == agreementId).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 10;
        ScheduleGenerationService.Validate(InstallmentSupport.ToSpec(charge), await support.PolicyAsync(ct));
        db.UseRowVersion(ag, r.RowVersion);
        db.Entry(ag).Property(a => a.InstallmentPlanId).IsModified = true;
        db.AgreementCharges.Add(charge);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.ChargeAdded", "CustomerAgreement", agreementId.ToString(), NewValues: Snapshot(charge)), ct);
        return await BuildAsync(agreementId, ct);
    }

    public async Task<AgreementInstallmentDto> UpdateChargeAsync(long agreementId, long chargeId, SaveAgreementChargeRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsSchedule, ct);
        var projectId = await support.EnsureAgreementAccessAsync(agreementId, ct);
        await chargeValidator.ValidateOrThrowAsync(r.Charge, ct);
        var (ag, _) = await EditableAsync(agreementId, ct);
        var charge = await db.AgreementCharges.FirstOrDefaultAsync(c => c.Id == chargeId && c.AgreementId == agreementId, ct) ?? throw new NotFoundException("Charge not found.");
        var old = Snapshot(charge);
        await support.ApplyInputAsync(charge, r.Charge, projectId, ct);
        ScheduleGenerationService.Validate(InstallmentSupport.ToSpec(charge), await support.PolicyAsync(ct));
        db.UseRowVersion(ag, r.RowVersion);
        db.Entry(ag).Property(a => a.InstallmentPlanId).IsModified = true;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.ChargeUpdated", "CustomerAgreement", agreementId.ToString(), old, Snapshot(charge)), ct);
        return await BuildAsync(agreementId, ct);
    }

    public async Task<AgreementInstallmentDto> RemoveChargeAsync(long agreementId, long chargeId, AgreementRowVersionRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsSchedule, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var (ag, _) = await EditableAsync(agreementId, ct);
        var charge = await db.AgreementCharges.FirstOrDefaultAsync(c => c.Id == chargeId && c.AgreementId == agreementId, ct) ?? throw new NotFoundException("Charge not found.");
        db.UseRowVersion(ag, r.RowVersion);
        db.Entry(ag).Property(a => a.InstallmentPlanId).IsModified = true;
        var old = Snapshot(charge);
        db.AgreementCharges.Remove(charge); // never scheduled: no schedule line refers to it
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.ChargeRemoved", "CustomerAgreement", agreementId.ToString(), old), ct);
        return await BuildAsync(agreementId, ct);
    }

    /// <summary>The schedule the current charges would produce, with reconciliation and any problems (nothing is stored).</summary>
    public async Task<SchedulePreviewDto> PreviewAsync(long agreementId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var ag = await db.CustomerAgreements.AsNoTracking().FirstAsync(a => a.Id == agreementId, ct);
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == ag.BookingId, ct);
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active).ToListAsync(ct);
        var preview = BuildPreview(charges.Select(InstallmentSupport.ToSpec).ToList(), await support.PolicyAsync(ct), booking.TotalPayable, await support.TodayAsync(ct));
        if (await db.PaymentSchedules.AnyAsync(s => s.AgreementId == agreementId, ct))
            preview = preview with { CanGenerate = false, Problems = [.. preview.Problems, "A payment schedule already exists; change it through an amendment."] };
        else if (ag.Status != AgreementStatus.Active)
            preview = preview with { CanGenerate = false, Problems = [.. preview.Problems, "The agreement is not active."] };
        return preview;
    }

    /// <summary>Pure preview: schedules every charge it can, reports problems instead of throwing, and checks the sale consideration against the booking total.</summary>
    public static SchedulePreviewDto BuildPreview(IReadOnlyList<ChargeSpec> specs, MoneyPolicy policy, decimal totalPayable, DateOnly today)
    {
        var problems = new List<string>();
        var valid = new List<ChargeSpec>();
        foreach (var s in specs)
        {
            try { ScheduleGenerationService.Validate(s, policy); ScheduleGenerationService.ScheduleCharge(s, policy); valid.Add(s); }
            catch (DomainException ex) { problems.Add(ex.Message); }
        }
        var result = ScheduleGenerationService.Generate(valid, policy);
        var sale = specs.Where(s => s.Category == ChargeCategory.SaleConsideration).Sum(s => s.Amount);
        var diff = totalPayable - sale;
        if (specs.Count == 0) problems.Add("Add at least one charge or apply an installment plan.");
        if (diff != 0m) problems.Add($"The sale-consideration charges total {sale:N2} but the booking's total payable is {totalPayable:N2} (difference {diff:N2}).");
        var lines = result.Items.Select(i => new ScheduleLineDto(null, i.LineNo, i.ChargeKey, i.InstallmentNo, i.InstallmentCount, i.Description, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount,
            i.IsPartial, 0m, 0m, 0m, i.Amount, ScheduleItemStatus.Pending, 0m, today > i.GraceEndDate, false, null)).ToList();
        var rec = ScheduleGenerationService.Reconcile(specs.Select(s => (s.Key, s.Description, s.Amount)), result.Items.Select(i => (i.ChargeKey, i.Amount)))
            .Select(r => new ReconciliationDto(r.ChargeKey, r.Description, r.ChargeAmount, r.ScheduledAmount, r.Difference)).ToList();
        var reconciled = rec.All(r => r.Difference == 0m);
        return new SchedulePreviewDto(lines, rec, result.TotalAmount, result.TotalTax, specs.Sum(s => s.Amount), sale, totalPayable, diff, reconciled,
            problems.Count == 0 && reconciled && diff == 0m && specs.Count > 0, problems);
    }

    public async Task<PaymentScheduleDto> GenerateAsync(long agreementId, GenerateScheduleRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsSchedule, ct);
        var userId = RequireUser();
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var (ag, booking) = await EditableAsync(agreementId, ct);
        var charges = await db.AgreementCharges.Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        if (charges.Count == 0) throw new DomainException("Add at least one charge or apply an installment plan before generating the schedule.");
        var policy = await support.PolicyAsync(ct);
        var sale = charges.Where(c => c.Category == ChargeCategory.SaleConsideration).Sum(c => c.Amount);
        if (sale != booking.TotalPayable)
            throw new DomainException($"The sale-consideration charges total {sale:N2} but the booking's total payable is {booking.TotalPayable:N2}; the schedule must reconcile with the agreement.");
        var result = ScheduleGenerationService.Generate(charges.Select(InstallmentSupport.ToSpec), policy);
        if (!result.IsReconciled || result.TotalAmount != charges.Sum(c => c.Amount)) throw new DomainException("The generated schedule does not reconcile with the agreement charges.");
        db.UseRowVersion(ag, r.RowVersion);
        var now = Now;
        var reason = PricingSupport.N(r.Remarks) ?? "Initial payment schedule";
        var schedule = new PaymentSchedule
        {
            AgreementId = agreementId, VersionNo = 1, Status = PaymentScheduleStatus.Active, ActiveAgreementId = agreementId, Reason = reason, TotalAmount = result.TotalAmount,
            TotalTax = result.TotalTax, ItemCount = result.Items.Count, GeneratedAtUtc = now, GeneratedBy = user.UserName ?? "unknown",
            ContentHash = ScheduleGenerationService.Hash(result.Items.Select(i => (i.LineNo, i.ChargeKey, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount))),
            Items = result.Items.Select(i => new PaymentScheduleItem
            {
                AgreementChargeId = i.ChargeKey, LineNo = i.LineNo, InstallmentNo = i.InstallmentNo, InstallmentCount = i.InstallmentCount, Description = i.Description, DueDate = i.DueDate,
                GraceEndDate = i.GraceEndDate, Amount = i.Amount, TaxAmount = i.TaxAmount, IsPartial = i.IsPartial,
            }).ToList(),
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PaymentSchedules.Add(schedule);
        db.Entry(ag).Property(a => a.InstallmentPlanId).IsModified = true;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw new DomainException("A payment schedule was just generated for this agreement by someone else."); }
        await bookings.AddAgreementVersionAsync(ag, $"Payment schedule v1 generated: {reason}", now, ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.ScheduleGenerated", "CustomerAgreement", agreementId.ToString(), NewValues: new
        {
            ScheduleVersion = 1, schedule.TotalAmount, schedule.TotalTax, schedule.ItemCount, schedule.ContentHash, AgreementVersion = ag.CurrentVersionNo,
        }, Reason: reason, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await GetScheduleAsync(agreementId, 1, ct);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task<(CustomerAgreement Agreement, Booking Booking)> EditableAsync(long agreementId, CancellationToken ct)
    {
        var ag = await db.CustomerAgreements.FirstOrDefaultAsync(a => a.Id == agreementId, ct) ?? throw new NotFoundException("Agreement not found.");
        if (ag.Status != AgreementStatus.Active) throw new DomainException("The agreement is not active.");
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == ag.BookingId, ct);
        if (booking.Status != BookingStatus.Confirmed) throw new DomainException("Only confirmed bookings have editable agreements.");
        if (await db.PaymentSchedules.AnyAsync(s => s.AgreementId == agreementId, ct))
            throw new DomainException("A payment schedule has already been generated; agreement charges now change only through approved amendments.");
        return (ag, booking);
    }

    private async Task<AgreementInstallmentDto> BuildAsync(long agreementId, CancellationToken ct)
    {
        var ag = await db.CustomerAgreements.AsNoTracking().FirstAsync(a => a.Id == agreementId, ct);
        var b = await db.Bookings.AsNoTracking().Include(x => x.Customers).ThenInclude(c => c.Customer).Include(x => x.Properties).ThenInclude(p => p.Property).FirstAsync(x => x.Id == ag.BookingId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == b.ProjectId, ct);
        var charges = await db.AgreementCharges.AsNoTracking().Include(c => c.ChargeType).Where(c => c.AgreementId == agreementId).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        var ruleIds = charges.Where(c => c.LateFeeRuleId != null).Select(c => c.LateFeeRuleId!.Value).Distinct().ToList();
        var rules = await db.LateFeeRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var plan = ag.InstallmentPlanId is { } pid ? await db.InstallmentPlans.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Code + " - " + p.Name).FirstOrDefaultAsync(ct) : null;
        var active = await db.PaymentSchedules.AsNoTracking().Include(s => s.Items).FirstOrDefaultAsync(s => s.ActiveAgreementId == agreementId, ct);
        var generated = active is not null || await db.PaymentSchedules.AnyAsync(s => s.AgreementId == agreementId, ct);
        var pending = await db.AgreementAmendments.AsNoTracking().Where(a => a.PendingAgreementId == agreementId).Select(a => (long?)a.Id).FirstOrDefaultAsync(ct);
        var canEdit = !generated && ag.Status == AgreementStatus.Active && b.Status == BookingStatus.Confirmed && user.UserId is { } uid
                      && await permissions.HasPermissionAsync(uid, Permissions.InstallmentsSchedule, ct);
        var activeCharges = charges.Where(c => c.Status == AgreementChargeStatus.Active).ToList();
        var sale = activeCharges.Where(c => c.Category == ChargeCategory.SaleConsideration).Sum(c => c.Amount);
        var today = await support.TodayAsync(ct);
        var owners = b.Customers.Where(c => c.IsActive).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Id).ToList();
        return new AgreementInstallmentDto(ag.Id, ag.AgreementNumber, ag.Status, ag.CurrentVersionNo, b.Id, b.BookingNumber, b.BookingDate, b.ProjectId, project.ProjectCode, project.ProjectName,
            b.PaymentMode, string.Join("; ", owners.Select(c => $"{c.Customer!.FullName} ({c.SharePercent:0.####}%)")), string.Join(", ", b.Properties.Select(p => p.Property!.PropertyCode)),
            owners.Select(o => o.CustomerId).ToList(), b.TotalPayable, b.BookingAmount, ag.InstallmentPlanId, plan, ag.ScheduleStartDate,
            charges.Select(c => new AgreementChargeDto(c.Id, c.SortOrder, c.ChargeTypeId, c.ChargeType?.Name ?? "", c.Description, c.Category, c.Amount, c.Frequency, c.NumberOfInstallments,
                c.InstallmentAmount, c.FirstDueDate, c.DueDayOfMonth, InstallmentSupport.ParseEntries(c.CustomScheduleJson), c.GracePeriodDays, c.LateFeeRuleId,
                c.LateFeeRuleId is { } r ? rules.GetValueOrDefault(r) : null, c.WaiverAllowed, c.MaxWaiverPercent, c.TaxTreatment, c.TaxRatePercent, c.RoundingUnit, c.Remarks, c.Status,
                c.AddedByAmendmentId)).ToList(),
            sale, activeCharges.Sum(c => c.Amount) - sale, activeCharges.Sum(c => c.Amount), b.TotalPayable - sale, generated, canEdit,
            active is null ? null : InstallmentSupport.Summary(active, today), pending, ag.RowVersion.ToToken(), await support.CurrencyAsync(ct));
    }

    private static object Snapshot(AgreementCharge c) => new
    {
        c.Id, c.Description, Category = c.Category.ToString(), c.Amount, Frequency = c.Frequency.ToString(), c.NumberOfInstallments, c.InstallmentAmount, c.FirstDueDate, c.DueDayOfMonth,
        c.CustomScheduleJson, c.GracePeriodDays, c.LateFeeRuleId, c.WaiverAllowed, c.MaxWaiverPercent, Tax = c.TaxTreatment.ToString(), c.TaxRatePercent, c.RoundingUnit,
    };

    internal static bool IsDuplicate(DbUpdateException ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
