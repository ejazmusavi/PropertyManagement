using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Accounting;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>
/// Configurable posting rules (spec 8.4, spec-issues #7). A rule is drafted, submitted, and becomes active only when a second user with accounting.rules.approve
/// approves it - this is the accounting policy approval required before production. Active rules are immutable; change policy by retiring a rule and approving
/// a replacement (a later effective date wins).
/// </summary>
public class PostingRuleService(IAppDbContext db, AccountingSupport support, IAuditService audit, IValidator<SavePostingRuleRequest> validator)
{
    public IReadOnlyList<EventCatalogDto> Catalog() =>
        PostingCatalog.All.Select(d => new EventCatalogDto(d.Type, d.Label, d.Source, d.AmountKeys, d.ConditionFields, d.AccountKeys, d.Reverses is not null, d.SpecItems)).ToList();

    public async Task<PagedResult<PostingRuleDto>> ListAsync(PostingRuleListRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var q = db.PostingRules.AsNoTracking().AsQueryable();
        if (r.EventType is { } e) q = q.Where(x => x.EventType == e);
        if (r.Status is { } s) q = q.Where(x => x.Status == s);
        if (r.ProjectId is { } p) q = q.Where(x => x.ProjectId == p || x.ProjectId == null);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.Name.Contains(t)); }
        var total = await q.CountAsync(ct);
        var ids = await q.OrderBy(x => x.EventType).ThenBy(x => x.Status).ThenBy(x => x.Priority).ThenBy(x => x.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => x.Id).ToListAsync(ct);
        var items = new List<PostingRuleDto>();
        foreach (var id in ids) items.Add(await BuildAsync(id, ct));
        return new PagedResult<PostingRuleDto>(items, r.Page, r.PageSize, total);
    }

    public async Task<PostingRuleDto> GetAsync(long id, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        return await BuildAsync(id, ct);
    }

    private async Task<PostingRuleDto> BuildAsync(long id, CancellationToken ct)
    {
        var r = await db.PostingRules.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        var accountIds = r.Lines.Where(l => l.AccountId != null).Select(l => l.AccountId!.Value).ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var project = r.ProjectId is { } p ? await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.ProjectCode).FirstOrDefaultAsync(ct) : null;
        var journals = await db.JournalEntries.AsNoTracking().CountAsync(j => j.PostingRuleId == id, ct);
        return new PostingRuleDto(r.Id, r.Name, r.EventType, PostingCatalog.Get(r.EventType).Label, r.ProjectId, project, r.ConditionField, r.ConditionValue, r.Priority, r.EffectiveFrom, r.Status,
            r.Description, r.CreatedBy, r.SubmittedBy, r.SubmittedAtUtc, r.ApprovedBy, r.ApprovedAtUtc, r.DecisionComment, r.RetiredBy, journals,
            r.Lines.OrderBy(l => l.LineNo).Select(l => new PostingRuleLineDto(l.LineNo, l.Side, l.AmountKey, l.AccountId, l.AccountId is { } a ? accounts.GetValueOrDefault(a)?.Code : null,
                l.AccountId is { } b ? accounts.GetValueOrDefault(b)?.Name : null, l.AccountKey, l.CostCenterId, l.Description)).ToList(),
            r.RowVersion.ToToken());
    }

    public async Task<PostingRuleDto> CreateAsync(SavePostingRuleRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingRulesManage, ct);
        await validator.ValidateOrThrowAsync(r, ct);
        var rule = new PostingRule();
        await ApplyAsync(rule, r, ct);
        db.PostingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Created", "PostingRule", rule.Id.ToString(), NewValues: Snapshot(rule)), ct);
        return await BuildAsync(rule.Id, ct);
    }

    public async Task<PostingRuleDto> UpdateAsync(long id, SavePostingRuleRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingRulesManage, ct);
        await validator.ValidateOrThrowAsync(r, ct);
        var rule = await db.PostingRules.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        db.UseRowVersion(rule, r.RowVersion);
        if (rule.Status is not (PostingRuleStatus.Draft or PostingRuleStatus.Rejected)) throw new DomainException("Only draft or rejected rules can be edited; retire an active rule and create a new one.");
        var old = Snapshot(rule);
        db.PostingRuleLines.RemoveRange(rule.Lines);
        rule.Lines = [];
        await ApplyAsync(rule, r, ct);
        rule.Status = PostingRuleStatus.Draft; rule.DecisionComment = null;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Updated", "PostingRule", id.ToString(), old, Snapshot(rule)), ct);
        return await BuildAsync(id, ct);
    }

    private async Task ApplyAsync(PostingRule rule, SavePostingRuleRequest r, CancellationToken ct)
    {
        var field = AccountingSupport.TrimOrNull(r.ConditionField, 40);
        var lines = r.Lines.Select((l, i) => new PostingRuleLine
        {
            LineNo = i + 1, Side = l.Side, AmountKey = l.AmountKey.Trim(), AccountId = l.AccountId, AccountKey = AccountingSupport.TrimOrNull(l.AccountKey, 40), CostCenterId = l.CostCenterId,
            Description = AccountingSupport.TrimOrNull(l.Description, 200),
        }).ToList();
        var errors = PostingRuleEngine.ValidateDefinition(r.EventType, lines, field);
        if (errors.Count > 0) throw AccountingSupport.Invalid("Lines", string.Join(" ", errors));
        if (r.ProjectId is { } p) await support.EnsureScopeAsync(p, ct); else await support.EnsureScopeAsync(null, ct);
        var ids = lines.Where(l => l.AccountId != null).Select(l => l.AccountId!.Value).Distinct().ToList();
        var accounts = await db.ChartOfAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != ids.Count) throw AccountingSupport.Invalid("Lines", "A line refers to an unknown account.");
        if (accounts.FirstOrDefault(a => !a.IsActive) is { } inactive) throw AccountingSupport.Invalid("Lines", $"Account {inactive.Code} is inactive.");
        if (await db.ChartOfAccounts.AnyAsync(a => a.ParentAccountId != null && ids.Contains(a.ParentAccountId.Value), ct)) throw AccountingSupport.Invalid("Lines", "Rules post to sub-accounts, not to parent accounts.");
        var ccs = lines.Where(l => l.CostCenterId != null).Select(l => l.CostCenterId!.Value).Distinct().ToList();
        if (ccs.Count > 0 && await db.CostCenters.CountAsync(c => ccs.Contains(c.Id) && c.IsActive, ct) != ccs.Count) throw AccountingSupport.Invalid("Lines", "A line refers to an unknown or inactive cost center.");
        rule.Name = r.Name.Trim(); rule.EventType = r.EventType; rule.ProjectId = r.ProjectId; rule.ConditionField = field;
        rule.ConditionValue = field is null ? null : AccountingSupport.TrimOrNull(r.ConditionValue, 100); rule.Priority = r.Priority; rule.EffectiveFrom = r.EffectiveFrom;
        rule.Description = AccountingSupport.TrimOrNull(r.Description, 1000);
        rule.Lines = lines;
    }

    public async Task<PostingRuleDto> SubmitAsync(long id, RuleDecisionRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingRulesManage, ct);
        var rule = await db.PostingRules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        db.UseRowVersion(rule, r.RowVersion);
        if (rule.Status is not (PostingRuleStatus.Draft or PostingRuleStatus.Rejected)) throw new DomainException("Only draft or rejected rules can be submitted.");
        rule.Status = PostingRuleStatus.PendingApproval; rule.SubmittedByUserId = userId; rule.SubmittedBy = support.Actor; rule.SubmittedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Submitted", "PostingRule", id.ToString(), new { Status = "Draft" }, new { Status = "PendingApproval" }, r.Comments), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Policy approval (maker-checker): the approver must differ from the creator and the submitter.</summary>
    public async Task<PostingRuleDto> ApproveAsync(long id, RuleDecisionRequest r, CancellationToken ct = default)
    {
        var userId = support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingRulesApprove, ct);
        var rule = await db.PostingRules.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        db.UseRowVersion(rule, r.RowVersion);
        if (rule.Status != PostingRuleStatus.PendingApproval) throw new DomainException("Only rules pending approval can be approved.");
        if (rule.SubmittedByUserId == userId || string.Equals(rule.CreatedBy, support.Actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("A posting rule must be approved by a different user than the one who created or submitted it.");
        var errors = PostingRuleEngine.ValidateDefinition(rule.EventType, rule.Lines, rule.ConditionField);
        if (errors.Count > 0) throw new DomainException(string.Join(" ", errors));
        rule.Status = PostingRuleStatus.Active; rule.ApprovedByUserId = userId; rule.ApprovedBy = support.Actor; rule.ApprovedAtUtc = support.Now; rule.DecisionComment = AccountingSupport.TrimOrNull(r.Comments, 500);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Approved", "PostingRule", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Active", Rule = Snapshot(rule) }, rule.DecisionComment), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<PostingRuleDto> RejectAsync(long id, RuleDecisionRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingRulesApprove, ct);
        if (string.IsNullOrWhiteSpace(r.Comments) || r.Comments.Trim().Length < 5) throw AccountingSupport.Invalid("Comments", "Give a rejection comment of at least 5 characters.");
        var rule = await db.PostingRules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        db.UseRowVersion(rule, r.RowVersion);
        if (rule.Status != PostingRuleStatus.PendingApproval) throw new DomainException("Only rules pending approval can be rejected.");
        rule.Status = PostingRuleStatus.Rejected; rule.DecisionComment = r.Comments.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Rejected", "PostingRule", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Rejected" }, rule.DecisionComment), ct);
        return await BuildAsync(id, ct);
    }

    /// <summary>Retires an active rule (needs policy approval rights): new events no longer use it; journals already posted keep their link.</summary>
    public async Task<PostingRuleDto> RetireAsync(long id, RuleDecisionRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingRulesApprove, ct);
        if (string.IsNullOrWhiteSpace(r.Comments) || r.Comments.Trim().Length < 5) throw AccountingSupport.Invalid("Comments", "Give a reason of at least 5 characters.");
        var rule = await db.PostingRules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Posting rule not found.");
        db.UseRowVersion(rule, r.RowVersion);
        if (rule.Status is not (PostingRuleStatus.Active or PostingRuleStatus.Draft or PostingRuleStatus.Rejected)) throw new DomainException("Only active, draft or rejected rules can be retired.");
        var from = rule.Status;
        rule.Status = PostingRuleStatus.Retired; rule.RetiredBy = support.Actor; rule.RetiredAtUtc = support.Now; rule.DecisionComment = r.Comments.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("PostingRule.Retired", "PostingRule", id.ToString(), new { Status = from.ToString() }, new { Status = "Retired" }, rule.DecisionComment), ct);
        return await BuildAsync(id, ct);
    }

    private static object Snapshot(PostingRule r) => new
    {
        r.Name, EventType = r.EventType.ToString(), r.ProjectId, r.ConditionField, r.ConditionValue, r.Priority, r.EffectiveFrom, Status = r.Status.ToString(),
        Lines = r.Lines.OrderBy(l => l.LineNo).Select(l => new { Side = l.Side.ToString(), l.AmountKey, l.AccountId, l.AccountKey, l.CostCenterId }),
    };
}
