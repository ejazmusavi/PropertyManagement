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
/// Agreement amendments (spec 5.6): rescheduling, deferment, installment reduction, additional installments, due-date changes, penalty changes,
/// waivers and additional charges. Every change is requested as an amendment record (Pending) and applied only when a different user holding
/// agreements.amend.approve approves it. Approval creates a new schedule version (the previous one is kept as Superseded), records each changed line
/// in ScheduleAmendment, and adds a new agreement version. One pending amendment per agreement; a request based on an older schedule cannot be approved.
/// </summary>
public class AgreementAmendmentService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, InstallmentSupport support,
    AgreementScheduleService schedules, BookingService bookings, IValidator<CreateAmendmentRequest> createValidator, IValidator<RejectAmendmentRequest> rejectValidator,
    TimeProvider clock)
{
    private const long PreviewChargeKey = -1;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");

    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<AmendmentListItemDto>> ListAsync(AmendmentListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = from m in db.AgreementAmendments.AsNoTracking()
                join a in db.CustomerAgreements.AsNoTracking() on m.AgreementId equals a.Id
                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                where projects.Any(p => p.Id == b.ProjectId)
                select new { m, a, b };
        if (r.ProjectId is { } pid) q = q.Where(x => x.b.ProjectId == pid);
        if (r.AgreementId is { } aid) q = q.Where(x => x.a.Id == aid);
        if (r.Status is { } s) q = q.Where(x => x.m.Status == s);
        if (r.Type is { } t) q = q.Where(x => x.m.AmendmentType == t);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var term = r.Search.Trim(); q = q.Where(x => x.a.AgreementNumber.Contains(term) || x.b.BookingNumber.Contains(term) || x.m.Reason.Contains(term)); }
        var total = await q.CountAsync(ct);
        q = r.Descending || string.IsNullOrEmpty(r.SortBy) ? q.OrderByDescending(x => x.m.Id) : q.OrderBy(x => x.m.Id);
        var rows = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new
        {
            x.m.Id, AgreementId = x.a.Id, x.a.AgreementNumber, x.b.BookingNumber, x.b.ProjectId, x.m.AmendmentType, x.m.Status, x.m.Reason, x.m.RequestedBy, x.m.CreatedAtUtc,
            x.m.ApprovedBy, x.m.DecidedAtUtc, Waiver = db.Waivers.Where(w => w.AmendmentId == x.m.Id).Sum(w => (decimal?)w.Amount),
        }).ToListAsync(ct);
        var codes = await db.Projects.AsNoTracking().Where(p => rows.Select(x => x.ProjectId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);
        return new PagedResult<AmendmentListItemDto>(rows.Select(x => new AmendmentListItemDto(x.Id, x.AgreementId, x.AgreementNumber, x.BookingNumber, codes.GetValueOrDefault(x.ProjectId, ""),
            x.AmendmentType, x.Status, x.Reason, x.RequestedBy ?? x.ApprovedBy, x.CreatedAtUtc, x.Status == AmendmentStatus.Pending ? null : x.ApprovedBy, x.DecidedAtUtc, x.Waiver)).ToList(),
            r.Page, r.PageSize, total);
    }

    public async Task<PagedResult<WaiverDto>> ListWaiversAsync(WaiverStatus? status, long? projectId, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = from w in db.Waivers.AsNoTracking()
                join a in db.CustomerAgreements.AsNoTracking() on w.AgreementId equals a.Id
                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                where projects.Any(p => p.Id == b.ProjectId) && (projectId == null || b.ProjectId == projectId)
                select w;
        if (status is { } s) q = q.Where(w => w.Status == s);
        var total = await q.CountAsync(ct);
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, PagedRequest.MaxPageSize);
        var ids = await q.OrderByDescending(w => w.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(w => w.Id).ToListAsync(ct);
        var items = await schedules.WaiversAsync(db.Waivers.AsNoTracking().Where(w => ids.Contains(w.Id)), ct);
        return new PagedResult<WaiverDto>(items, page, pageSize, total);
    }

    public async Task<AmendmentDetailDto> GetAsync(long agreementId, long amendmentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.InstallmentsView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        return await BuildAsync(agreementId, amendmentId, ct);
    }

    // =====================================================================================================
    // request
    // =====================================================================================================
    public async Task<AmendmentDetailDto> CreateAsync(long agreementId, CreateAmendmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsAmend, ct);
        var userId = RequireUser();
        var projectId = await support.EnsureAgreementAccessAsync(agreementId, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var key = PricingSupport.N(r.IdempotencyKey);
        if (key is not null)
        {
            var existing = await db.AgreementAmendments.AsNoTracking().Where(a => a.IdempotencyKey == key).Select(a => new { a.Id, a.AgreementId, a.RequestedByUserId }).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.RequestedByUserId != userId || existing.AgreementId != agreementId) throw PricingSupport.Invalid("IdempotencyKey", "This idempotency key was already used.");
                return await BuildAsync(agreementId, existing.Id, ct);
            }
        }
        var ag = await db.CustomerAgreements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agreementId, ct) ?? throw new NotFoundException("Agreement not found.");
        if (ag.Status != AgreementStatus.Active) throw new DomainException("Only active agreements can be amended.");
        var schedule = await ActiveScheduleAsync(agreementId, tracked: false, ct) ?? throw new DomainException("Generate the payment schedule before requesting amendments.");
        if (await db.AgreementAmendments.AnyAsync(a => a.PendingAgreementId == agreementId, ct)) throw new DomainException("This agreement already has a pending amendment; decide it first.");
        if (r.AdditionalCharge is { } extra) await support.ApplyInputAsync(new AgreementCharge(), extra, projectId, ct); // reference checks only
        if (r.PenaltyChange?.LateFeeRuleId is { } lr && !await db.LateFeeRules.AnyAsync(x => x.Id == lr && x.IsActive && (x.ProjectId == null || x.ProjectId == projectId), ct))
            throw PricingSupport.Invalid("PenaltyChange.LateFeeRuleId", "The late fee rule was not found, is inactive or belongs to another project.");

        // Validate by running the engine now: a request that cannot be applied is rejected immediately.
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active).ToListAsync(ct);
        var policy = await support.PolicyAsync(ct);
        var today = await support.TodayAsync(ct);
        var command = await CommandAsync(r, schedule, charges, PreviewChargeKey, policy, today, ct);
        ScheduleAmendmentEngine.Apply(charges.Select(InstallmentSupport.ToState).ToList(), schedule.Items.Select(ScheduleAmendmentEngine.ToState).ToList(), command, policy, today);

        var amendment = new AgreementAmendment
        {
            AgreementId = agreementId, AmendmentType = r.Type, Status = AmendmentStatus.Pending, FromVersionNo = ag.CurrentVersionNo, Reason = r.Reason.Trim(),
            Details = InstallmentSupport.Serialize(r with { Reason = r.Reason.Trim(), IdempotencyKey = null }), RequestedByUserId = userId, RequestedBy = user.UserName,
            BaseScheduleId = schedule.Id, PendingAgreementId = agreementId, IdempotencyKey = key,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AgreementAmendments.Add(amendment);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AgreementScheduleService.IsDuplicate(ex)) { throw new DomainException("Another amendment was just requested for this agreement; decide it first."); }
        if (r.Waiver is { } wv)
        {
            var item = schedule.Items.First(i => i.Id == wv.ItemId);
            db.Waivers.Add(new Waiver
            {
                AgreementId = agreementId, AmendmentId = amendment.Id, ScheduleItemId = item.Id, AgreementChargeId = item.AgreementChargeId, Kind = wv.Kind, Amount = wv.Amount,
                Reason = r.Reason.Trim(), Status = WaiverStatus.Pending, RequestedBy = user.UserName ?? "unknown",
            });
            await db.SaveChangesAsync(ct);
        }
        await audit.RecordAsync(new AuditEntry("Agreement.AmendmentRequested", "AgreementAmendment", amendment.Id.ToString(), NewValues: new
        {
            agreementId, ag.AgreementNumber, Type = r.Type.ToString(), BaseScheduleVersion = schedule.VersionNo, amendment.Details,
        }, Reason: amendment.Reason, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(agreementId, amendment.Id, ct);
    }

    // =====================================================================================================
    // decide
    // =====================================================================================================
    public async Task<AmendmentDetailDto> ApproveAsync(long agreementId, long amendmentId, DecideAmendmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsAmendApprove, ct);
        var userId = RequireUser();
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var m = await db.AgreementAmendments.FirstOrDefaultAsync(a => a.Id == amendmentId && a.AgreementId == agreementId, ct) ?? throw new NotFoundException("Amendment not found.");
        if (m.Status != AmendmentStatus.Pending) throw new DomainException($"The amendment is already {m.Status}.");
        if (m.RequestedByUserId == userId) throw new DomainException("An amendment cannot be approved by the user who requested it.");
        var ag = await db.CustomerAgreements.FirstAsync(a => a.Id == agreementId, ct);
        if (ag.Status != AgreementStatus.Active) throw new DomainException("The agreement is not active.");
        var current = await ActiveScheduleAsync(agreementId, tracked: true, ct) ?? throw new DomainException("The agreement has no active schedule.");
        if (current.Id != m.BaseScheduleId) throw new DomainException("The schedule has changed since this amendment was requested; reject it and request it again.");
        db.UseRowVersion(m, r.RowVersion);
        var request = InstallmentSupport.Deserialize<CreateAmendmentRequest>(m.Details) ?? throw new DomainException("The amendment details cannot be read.");
        var policy = await support.PolicyAsync(ct);
        var today = await support.TodayAsync(ct);
        var now = Now;
        var comments = PricingSupport.N(r.Comments);
        var projectId = await support.EnsureAgreementAccessAsync(agreementId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // An additional charge becomes a real agreement charge first so the new schedule lines can reference it.
        AgreementCharge? added = null;
        if (request.AdditionalCharge is { } extra)
        {
            added = new AgreementCharge { AgreementId = agreementId, AddedByAmendmentId = m.Id };
            await support.ApplyInputAsync(added, extra, projectId, ct);
            if (extra.SortOrder is null) added.SortOrder = (await db.AgreementCharges.Where(c => c.AgreementId == agreementId).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 10;
            db.AgreementCharges.Add(added);
            await db.SaveChangesAsync(ct);
        }
        var charges = await db.AgreementCharges.Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active).ToListAsync(ct);
        var command = await CommandAsync(request, current, charges.Where(c => c != added).ToList(), added?.Id ?? PreviewChargeKey, policy, today, ct);
        var outcome = ScheduleAmendmentEngine.Apply(charges.Where(c => c != added).Select(InstallmentSupport.ToState).ToList(),
            current.Items.Select(ScheduleAmendmentEngine.ToState).ToList(), command, policy, today);

        // --- new schedule version; the old one is kept unchanged as Superseded ---
        current.Supersede(now);
        await db.SaveWithConcurrencyAsync(ct); // frees the unique active marker
        var next = new PaymentSchedule
        {
            AgreementId = agreementId, VersionNo = current.VersionNo + 1, Status = PaymentScheduleStatus.Active, ActiveAgreementId = agreementId,
            Reason = $"{m.AmendmentType}: {m.Reason}"[..Math.Min(1000, $"{m.AmendmentType}: {m.Reason}".Length)], SourceAmendmentId = m.Id,
            TotalAmount = outcome.TotalAmount, TotalTax = outcome.Items.Sum(i => i.TaxAmount), ItemCount = outcome.Items.Count, GeneratedAtUtc = now, GeneratedBy = user.UserName ?? "unknown",
            ContentHash = ScheduleGenerationService.Hash(outcome.Items.Select(i => (i.LineNo, i.ChargeKey, i.InstallmentNo, i.DueDate, i.GraceEndDate, i.Amount, i.TaxAmount))),
        };
        var newItems = outcome.Items.Select(i =>
        {
            var e = new PaymentScheduleItem
            {
                AgreementChargeId = i.ChargeKey, LineNo = i.LineNo, InstallmentNo = i.InstallmentNo, InstallmentCount = i.InstallmentCount, Description = i.Description, DueDate = i.DueDate,
                GraceEndDate = i.GraceEndDate, Amount = i.Amount, TaxAmount = i.TaxAmount, IsPartial = i.IsPartial, AmountPaid = i.Paid, AmountWaived = i.Waived, LateFeeWaived = i.LateFeeWaived,
                LastPaymentDate = i.LastPaymentDate, PreviousItemId = i.PreviousItemId,
            };
            e.RefreshStatus();
            return e;
        }).ToList();
        next.Items = newItems;
        db.PaymentSchedules.Add(next);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AgreementScheduleService.IsDuplicate(ex)) { throw new ConcurrencyException(); }

        foreach (var c in outcome.Changes)
            db.ScheduleAmendments.Add(new ScheduleAmendment
            {
                AmendmentId = m.Id, FromScheduleId = current.Id, ToScheduleId = next.Id, Operation = c.Operation, AgreementChargeId = c.ChargeKey, FromItemId = c.FromItemId,
                ToItemId = c.ToItemIndex is { } ix ? newItems[ix].Id : null, OldDueDate = c.OldDueDate, NewDueDate = c.NewDueDate, OldAmount = c.OldAmount, NewAmount = c.NewAmount, Note = c.Note,
            });

        // --- charge terms that the amendment changed ---
        foreach (var state in outcome.Charges)
        {
            var c = charges.FirstOrDefault(x => x.Id == state.Key);
            if (c is null) continue;
            if (request.PenaltyChange is { } p && p.ChargeId == c.Id) { c.GracePeriodDays = p.GracePeriodDays; c.LateFeeRuleId = p.LateFeeRuleId; }
            if (request.Reschedule is { } rs && rs.ChargeId == c.Id)
            {
                c.Frequency = rs.Frequency; c.DueDayOfMonth = rs.DueDayOfMonth; c.InstallmentAmount = rs.InstallmentAmount;
                c.NumberOfInstallments = rs.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : newItems.Count(i => i.AgreementChargeId == c.Id);
                c.CustomScheduleJson = rs.Frequency == ScheduleFrequency.CustomDates ? InstallmentSupport.SerializeEntries(rs.CustomEntries) : null;
            }
            if (request.AdditionalInstallments is { } ai && ai.ChargeId == c.Id) c.NumberOfInstallments = newItems.Count(i => i.AgreementChargeId == c.Id);
        }
        foreach (var w in await db.Waivers.Where(w => w.AmendmentId == m.Id && w.Status == WaiverStatus.Pending).ToListAsync(ct))
        { w.Status = WaiverStatus.Approved; w.DecidedBy = user.UserName; w.DecidedAtUtc = now; w.DecisionComments = comments; }

        m.Status = AmendmentStatus.Approved; m.ApprovedByUserId = userId; m.ApprovedBy = user.UserName; m.DecidedAtUtc = now; m.DecisionComments = comments;
        m.ResultScheduleId = next.Id; m.PendingAgreementId = null;
        await db.SaveWithConcurrencyAsync(ct);
        await bookings.AddAgreementVersionAsync(ag, $"Amendment {m.AmendmentType} (schedule v{next.VersionNo}): {m.Reason}", now, ct);
        m.ToVersionNo = ag.CurrentVersionNo;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Agreement.AmendmentApproved", "AgreementAmendment", m.Id.ToString(),
            new { Status = "Pending", ScheduleVersion = current.VersionNo, current.TotalAmount },
            new { Status = "Approved", ScheduleVersion = next.VersionNo, next.TotalAmount, AgreementVersion = ag.CurrentVersionNo, Changes = outcome.Changes.Count }, comments, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildAsync(agreementId, m.Id, ct);
    }

    public async Task<AmendmentDetailDto> RejectAsync(long agreementId, long amendmentId, RejectAmendmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsAmendApprove, ct);
        var userId = RequireUser();
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var m = await db.AgreementAmendments.FirstOrDefaultAsync(a => a.Id == amendmentId && a.AgreementId == agreementId, ct) ?? throw new NotFoundException("Amendment not found.");
        if (m.Status != AmendmentStatus.Pending) throw new DomainException($"The amendment is already {m.Status}.");
        if (m.RequestedByUserId == userId) throw new DomainException("An amendment cannot be rejected by the user who requested it; cancel it instead.");
        await CloseAsync(m, AmendmentStatus.Rejected, WaiverStatus.Rejected, r.Comments.Trim(), r.RowVersion, "Agreement.AmendmentRejected", userId, ct);
        return await BuildAsync(agreementId, amendmentId, ct);
    }

    /// <summary>The requester withdraws a pending request.</summary>
    public async Task<AmendmentDetailDto> CancelAsync(long agreementId, long amendmentId, DecideAmendmentRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsAmend, ct);
        var userId = RequireUser();
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        var m = await db.AgreementAmendments.FirstOrDefaultAsync(a => a.Id == amendmentId && a.AgreementId == agreementId, ct) ?? throw new NotFoundException("Amendment not found.");
        if (m.Status != AmendmentStatus.Pending) throw new DomainException($"The amendment is already {m.Status}.");
        if (m.RequestedByUserId != userId) throw new DomainException("Only the user who requested the amendment can cancel it.");
        await CloseAsync(m, AmendmentStatus.Cancelled, WaiverStatus.Cancelled, PricingSupport.N(r.Comments) ?? "Withdrawn by requester", r.RowVersion, "Agreement.AmendmentCancelled", userId, ct);
        return await BuildAsync(agreementId, amendmentId, ct);
    }

    private async Task CloseAsync(AgreementAmendment m, AmendmentStatus status, WaiverStatus waiverStatus, string comments, string? rowVersion, string action, Guid userId, CancellationToken ct)
    {
        db.UseRowVersion(m, rowVersion);
        var now = Now;
        m.Status = status; m.PendingAgreementId = null; m.DecidedAtUtc = now; m.DecisionComments = comments;
        if (status == AmendmentStatus.Rejected) { m.ApprovedByUserId = userId; m.ApprovedBy = user.UserName; }
        foreach (var w in await db.Waivers.Where(w => w.AmendmentId == m.Id && w.Status == WaiverStatus.Pending).ToListAsync(ct))
        { w.Status = waiverStatus; w.DecidedBy = user.UserName; w.DecidedAtUtc = now; w.DecisionComments = comments; }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(action, "AgreementAmendment", m.Id.ToString(), new { Status = "Pending" }, new { Status = status.ToString() }, comments, UserId: userId), ct);
        await tx.CommitAsync(ct);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task<PaymentSchedule?> ActiveScheduleAsync(long agreementId, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.PaymentSchedules : db.PaymentSchedules.AsNoTracking();
        return await q.Include(s => s.Items).FirstOrDefaultAsync(s => s.ActiveAgreementId == agreementId, ct);
    }

    /// <summary>Turns the request into an engine command. Item and charge ids must belong to the given schedule / agreement.</summary>
    private async Task<AmendmentCommand> CommandAsync(CreateAmendmentRequest r, PaymentSchedule schedule, IReadOnlyList<AgreementCharge> charges, long newChargeKey, MoneyPolicy policy,
        DateOnly today, CancellationToken ct)
    {
        var itemIds = schedule.Items.Select(i => i.Id).ToHashSet();
        var chargeIds = charges.Select(c => c.Id).ToHashSet();
        void Item(long id, string field) { if (!itemIds.Contains(id)) throw PricingSupport.Invalid(field, "The installment is not part of the agreement's current schedule."); }
        void Charge(long id, string field) { if (!chargeIds.Contains(id)) throw PricingSupport.Invalid(field, "The charge is not an active charge of this agreement."); }
        switch (r.Type)
        {
            case AmendmentType.Reschedule:
                var rs = r.Reschedule!; Charge(rs.ChargeId, "Reschedule.ChargeId");
                var entries = rs.Frequency == ScheduleFrequency.CustomDates ? rs.CustomEntries?.Select(e => new ScheduleEntry(e.DueDate, e.Amount)).ToList() : null;
                var first = entries is { Count: > 0 } ? entries[0].DueDate : rs.FirstDueDate;
                return new RescheduleCommand(rs.ChargeId, rs.Frequency, rs.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : rs.NumberOfInstallments,
                    rs.Frequency is ScheduleFrequency.OneTime or ScheduleFrequency.CustomDates ? null : rs.InstallmentAmount, first, rs.DueDayOfMonth, entries);
            case AmendmentType.Deferment:
                foreach (var id in r.Deferment!.ItemIds) Item(id, "Deferment.ItemIds");
                return new DefermentCommand(r.Deferment.ItemIds, r.Deferment.Months, r.Deferment.CascadeSubsequent);
            case AmendmentType.InstallmentReduction:
                Item(r.Reduction!.ItemId, "Reduction.ItemId");
                return new ReductionCommand(r.Reduction.ItemId, r.Reduction.NewAmount, r.Reduction.Redistribution, r.Reduction.NewDueDate);
            case AmendmentType.AdditionalInstallments:
                Charge(r.AdditionalInstallments!.ChargeId, "AdditionalInstallments.ChargeId");
                return new AdditionalInstallmentsCommand(r.AdditionalInstallments.ChargeId, r.AdditionalInstallments.AdditionalCount);
            case AmendmentType.DueDateChange:
                foreach (var d in r.DueDateChanges!) Item(d.ItemId, "DueDateChanges");
                return new DueDateChangeCommand(r.DueDateChanges!.Select(d => new DueDateChange(d.ItemId, d.NewDueDate)).ToList());
            case AmendmentType.PenaltyChange:
                Charge(r.PenaltyChange!.ChargeId, "PenaltyChange.ChargeId");
                return new PenaltyChangeCommand(r.PenaltyChange.ChargeId, r.PenaltyChange.LateFeeRuleId, r.PenaltyChange.GracePeriodDays);
            case AmendmentType.Waiver:
                var wv = r.Waiver!; Item(wv.ItemId, "Waiver.ItemId");
                var fee = 0m;
                if (wv.Kind == WaiverKind.LateFee)
                {
                    var item = schedule.Items.First(i => i.Id == wv.ItemId);
                    var charge = charges.FirstOrDefault(c => c.Id == item.AgreementChargeId);
                    if (charge?.LateFeeRuleId is { } ruleId && await db.LateFeeRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ruleId, ct) is { } rule)
                        fee = LateFeeCalculator.Compute(InstallmentSupport.Terms(rule), item.Amount, item.Outstanding, item.GraceEndDate, today, policy);
                }
                return new WaiverCommand(wv.ItemId, wv.Kind, wv.Amount, fee);
            case AmendmentType.AdditionalCharge:
                var extra = r.AdditionalCharge!;
                var sort = extra.SortOrder ?? (charges.Count == 0 ? 10 : charges.Max(c => c.SortOrder) + 10);
                return new AdditionalChargeCommand(InstallmentSupport.ToSpec(extra, newChargeKey, sort), extra.LateFeeRuleId, extra.WaiverAllowed, extra.WaiverAllowed ? extra.MaxWaiverPercent : 0m);
            default:
                throw PricingSupport.Invalid("Type", "Choose a schedule amendment type.");
        }
    }

    private async Task<AmendmentDetailDto> BuildAsync(long agreementId, long amendmentId, CancellationToken ct)
    {
        var m = await db.AgreementAmendments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == amendmentId && a.AgreementId == agreementId, ct) ?? throw new NotFoundException("Amendment not found.");
        var ag = await db.CustomerAgreements.AsNoTracking().FirstAsync(a => a.Id == agreementId, ct);
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == ag.BookingId, ct);
        var versions = await db.PaymentSchedules.AsNoTracking().Where(s => s.AgreementId == agreementId).ToDictionaryAsync(s => s.Id, s => s.VersionNo, ct);
        var chargeNames = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId).ToDictionaryAsync(c => c.Id, c => c.Description, ct);
        var active = await ActiveScheduleAsync(agreementId, tracked: false, ct);
        var stale = m.Status == AmendmentStatus.Pending && (active is null || active.Id != m.BaseScheduleId);
        var changes = new List<ScheduleChangeDto>();
        SchedulePreviewDto? preview = null;
        string? previewError = null;
        CreateAmendmentRequest? request = null;
        if (m.AmendmentType >= AmendmentType.Reschedule) request = InstallmentSupport.Deserialize<CreateAmendmentRequest>(m.Details);
        if (m.Status == AmendmentStatus.Approved && m.ResultScheduleId is not null)
        {
            var rows = await db.ScheduleAmendments.AsNoTracking().Where(c => c.AmendmentId == m.Id).OrderBy(c => c.Id).ToListAsync(ct);
            changes = rows.Select(c => new ScheduleChangeDto(m.Id, m.AmendmentType, versions.GetValueOrDefault(c.FromScheduleId), versions.GetValueOrDefault(c.ToScheduleId), c.Operation,
                c.AgreementChargeId, c.AgreementChargeId is { } ch ? chargeNames.GetValueOrDefault(ch) : null, c.FromItemId, c.ToItemId, c.OldDueDate, c.NewDueDate, c.OldAmount, c.NewAmount, c.Note)).ToList();
        }
        else if (m.Status == AmendmentStatus.Pending && !stale && request is not null && active is not null)
        {
            try
            {
                var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == agreementId && c.Status == AgreementChargeStatus.Active).ToListAsync(ct);
                var policy = await support.PolicyAsync(ct);
                var today = await support.TodayAsync(ct);
                var command = await CommandAsync(request, active, charges, PreviewChargeKey, policy, today, ct);
                var outcome = ScheduleAmendmentEngine.Apply(charges.Select(InstallmentSupport.ToState).ToList(), active.Items.Select(ScheduleAmendmentEngine.ToState).ToList(), command, policy, today);
                if (request.AdditionalCharge is { } extra) chargeNames[PreviewChargeKey] = extra.Description;
                changes = outcome.Changes.Select(c => new ScheduleChangeDto(m.Id, m.AmendmentType, active.VersionNo, active.VersionNo + 1, c.Operation, c.ChargeKey,
                    chargeNames.GetValueOrDefault(c.ChargeKey), c.FromItemId, null, c.OldDueDate, c.NewDueDate, c.OldAmount, c.NewAmount, c.Note)).ToList();
                var lines = outcome.Items.Select(i => new ScheduleLineDto(null, i.LineNo, i.ChargeKey, i.InstallmentNo, i.InstallmentCount, i.Description, i.DueDate, i.GraceEndDate, i.Amount,
                    i.TaxAmount, i.IsPartial, i.Paid, i.Waived, i.LateFeeWaived, i.Outstanding, i.Status, 0m, i.Outstanding > 0m && today > i.GraceEndDate, false, i.PreviousItemId)).ToList();
                var rec = outcome.Reconciliation.Select(x => new ReconciliationDto(x.ChargeKey, x.Description, x.ChargeAmount, x.ScheduledAmount, x.Difference)).ToList();
                var sale = outcome.Charges.Where(c => c.Category == ChargeCategory.SaleConsideration).Sum(c => c.Amount);
                preview = new SchedulePreviewDto(lines, rec, outcome.TotalAmount, lines.Sum(l => l.TaxAmount), outcome.Charges.Sum(c => c.Amount), sale, booking.TotalPayable,
                    booking.TotalPayable - sale, outcome.IsReconciled, false, []);
            }
            catch (DomainException ex) { previewError = ex.Message; }
        }
        var waiver = (await schedules.WaiversAsync(db.Waivers.AsNoTracking().Where(w => w.AmendmentId == m.Id), ct)).FirstOrDefault();
        var uid = user.UserId;
        var canDecide = m.Status == AmendmentStatus.Pending && uid is { } u && m.RequestedByUserId != u && await permissions.HasPermissionAsync(u, Permissions.AgreementsAmendApprove, ct);
        var canCancel = m.Status == AmendmentStatus.Pending && uid is { } u2 && m.RequestedByUserId == u2;
        return new AmendmentDetailDto(m.Id, agreementId, ag.AgreementNumber, booking.Id, booking.BookingNumber, m.AmendmentType, m.Status, m.Reason, Summarize(m, request), m.Details,
            m.RequestedBy ?? m.ApprovedBy, m.CreatedAtUtc, m.Status == AmendmentStatus.Pending ? null : m.ApprovedBy, m.DecidedAtUtc, m.DecisionComments, m.FromVersionNo, m.ToVersionNo,
            m.BaseScheduleId is { } bs ? versions.GetValueOrDefault(bs) : null, m.ResultScheduleId is { } rs ? versions.GetValueOrDefault(rs) : null, stale, changes, preview, previewError,
            waiver, canDecide && !stale, canCancel, m.RowVersion.ToToken(), await support.CurrencyAsync(ct));
    }

    private static string Summarize(AgreementAmendment m, CreateAmendmentRequest? r) => (m.AmendmentType, r) switch
    {
        (AmendmentType.Reschedule, { Reschedule: { } x }) => $"Reschedule charge #{x.ChargeId}: {x.Frequency}" + (x.NumberOfInstallments is { } n ? $", {n} installments" : "") +
                                                             (x.InstallmentAmount is { } a ? $", {a:N2} each" : "") + $", first due {x.FirstDueDate:yyyy-MM-dd}",
        (AmendmentType.Deferment, { Deferment: { } x }) => $"Defer {x.ItemIds.Count} installment(s) by {x.Months} month(s)" + (x.CascadeSubsequent ? " with all later installments" : ""),
        (AmendmentType.InstallmentReduction, { Reduction: { } x }) => $"Reduce installment #{x.ItemId} to {x.NewAmount:N2} ({x.Redistribution})",
        (AmendmentType.AdditionalInstallments, { AdditionalInstallments: { } x }) => $"Add {x.AdditionalCount} installment(s) to charge #{x.ChargeId}",
        (AmendmentType.DueDateChange, { DueDateChanges: { } x }) => $"Change {x.Count} due date(s)",
        (AmendmentType.PenaltyChange, { PenaltyChange: { } x }) => $"Charge #{x.ChargeId}: late fee rule {(x.LateFeeRuleId?.ToString() ?? "none")}, grace {x.GracePeriodDays} day(s)",
        (AmendmentType.Waiver, { Waiver: { } x }) => $"Waive {x.Amount:N2} {(x.Kind == WaiverKind.LateFee ? "late fee" : "principal")} on installment #{x.ItemId}",
        (AmendmentType.AdditionalCharge, { AdditionalCharge: { } x }) => $"Add charge '{x.Description}' {x.Amount:N2} ({x.Category}, {x.Frequency})",
        _ => m.AmendmentType.ToString(),
    };
}
