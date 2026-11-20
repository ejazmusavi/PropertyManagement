using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

/// <summary>Account groups (hierarchical), chart of accounts (with sub-accounts) and cost centers (spec 8.1/8.2/8.5).</summary>
public class ChartOfAccountsService(IAppDbContext db, AccountingSupport support, IAuditService audit,
    IValidator<SaveAccountGroupRequest> groupValidator, IValidator<SaveAccountRequest> accountValidator, IValidator<SaveCostCenterRequest> costCenterValidator)
{
    /// <summary>Which classifications belong to which nature (a cash group under liabilities would corrupt the statements).</summary>
    public static AccountNature? NatureOf(AccountClassification c) => (int)c switch
    {
        0 => null,
        < 20 => AccountNature.Asset,
        < 40 => AccountNature.Liability,
        < 60 => AccountNature.Equity,
        < 80 => AccountNature.Revenue,
        _ => AccountNature.Expense,
    };

    // =====================================================================================================
    // groups
    // =====================================================================================================
    public async Task<IReadOnlyList<AccountGroupDto>> ListGroupsAsync(CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var groups = await db.AccountGroups.AsNoTracking().ToListAsync(ct);
        var counts = await db.ChartOfAccounts.AsNoTracking().GroupBy(a => a.AccountGroupId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var result = new List<AccountGroupDto>();
        void Walk(long? parent, int depth)
        {
            foreach (var g in groups.Where(g => g.ParentGroupId == parent).OrderBy(g => g.SortOrder).ThenBy(g => g.Code))
            {
                result.Add(new AccountGroupDto(g.Id, g.Code, g.Name, g.Nature, g.Classification, g.ParentGroupId, groups.FirstOrDefault(p => p.Id == g.ParentGroupId)?.Name,
                    g.CashFlowSection, g.SortOrder, g.IsSystem, g.IsActive, g.Description, depth, counts.GetValueOrDefault(g.Id), g.RowVersion.ToToken()));
                Walk(g.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return result;
    }

    public async Task<AccountGroupDto> CreateGroupAsync(SaveAccountGroupRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await groupValidator.ValidateOrThrowAsync(r, ct);
        var parent = await ParentGroupAsync(r.ParentGroupId!.Value, ct);
        CheckClassification(r.Classification, parent.Nature);
        var g = new AccountGroup
        {
            Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), Nature = parent.Nature, Classification = r.Classification == AccountClassification.None ? parent.Classification : r.Classification,
            ParentGroupId = parent.Id, CashFlowSection = r.CashFlowSection, SortOrder = r.SortOrder, Description = AccountingSupport.TrimOrNull(r.Description, 500), IsActive = r.IsActive,
        };
        db.AccountGroups.Add(g);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "An account group with this code already exists."); }
        await audit.RecordAsync(new AuditEntry("AccountGroup.Created", "AccountGroup", g.Id.ToString(), NewValues: new { g.Code, g.Name, g.Nature, g.Classification, g.ParentGroupId }), ct);
        return (await ListGroupsAsync(ct)).First(x => x.Id == g.Id);
    }

    public async Task<AccountGroupDto> UpdateGroupAsync(long id, SaveAccountGroupRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await groupValidator.ValidateOrThrowAsync(r, ct);
        var g = await db.AccountGroups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Account group not found.");
        db.UseRowVersion(g, r.RowVersion);
        var old = new { g.Code, g.Name, g.Classification, g.ParentGroupId, g.CashFlowSection, g.IsActive };
        if (g.IsSystem)
        {
            if (!string.Equals(r.Code.Trim(), g.Code, StringComparison.OrdinalIgnoreCase) || r.ParentGroupId != g.ParentGroupId || (r.Classification != g.Classification && g.ParentGroupId is not null))
                throw new DomainException("The code, parent and classification of a standard group cannot be changed.");
            if (!r.IsActive) throw new DomainException("Standard groups cannot be deactivated.");
        }
        else
        {
            var parent = await ParentGroupAsync(r.ParentGroupId!.Value, ct);
            if (parent.Nature != g.Nature) throw new DomainException("A group cannot move to another nature.");
            if (await IsDescendantGroupAsync(parent.Id, g.Id, ct)) throw new DomainException("A group cannot be placed below itself.");
            CheckClassification(r.Classification, g.Nature);
            g.Code = r.Code.Trim().ToUpperInvariant(); g.ParentGroupId = parent.Id;
            g.Classification = r.Classification == AccountClassification.None ? parent.Classification : r.Classification;
            if (!r.IsActive && await db.ChartOfAccounts.AnyAsync(a => a.AccountGroupId == id && a.IsActive, ct)) throw new DomainException("Deactivate or move the group's active accounts first.");
            g.IsActive = r.IsActive;
        }
        g.Name = r.Name.Trim(); g.CashFlowSection = r.CashFlowSection; g.SortOrder = r.SortOrder; g.Description = AccountingSupport.TrimOrNull(r.Description, 500);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "An account group with this code already exists."); }
        await audit.RecordAsync(new AuditEntry("AccountGroup.Updated", "AccountGroup", id.ToString(), old, new { g.Code, g.Name, g.Classification, g.ParentGroupId, g.CashFlowSection, g.IsActive }), ct);
        return (await ListGroupsAsync(ct)).First(x => x.Id == id);
    }

    private async Task<AccountGroup> ParentGroupAsync(long id, CancellationToken ct) =>
        await db.AccountGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AccountingSupport.Invalid("ParentGroupId", "The parent group was not found.");

    private static void CheckClassification(AccountClassification c, AccountNature nature)
    {
        if (NatureOf(c) is { } n && n != nature) throw AccountingSupport.Invalid("Classification", $"Classification {c} belongs to {n} groups, not {nature}.");
    }

    private async Task<bool> IsDescendantGroupAsync(long candidate, long ancestor, CancellationToken ct)
    {
        var all = await db.AccountGroups.AsNoTracking().Select(g => new { g.Id, g.ParentGroupId }).ToListAsync(ct);
        for (long? cur = candidate; cur is not null; cur = all.FirstOrDefault(g => g.Id == cur)?.ParentGroupId)
            if (cur == ancestor) return true;
        return false;
    }

    // =====================================================================================================
    // accounts
    // =====================================================================================================
    public async Task<PagedResult<AccountDto>> ListAccountsAsync(AccountListRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var all = await AllAccountsAsync(ct);
        IEnumerable<AccountDto> q = all;
        if (!string.IsNullOrWhiteSpace(r.Search)) { var s = r.Search.Trim(); q = q.Where(a => a.Code.Contains(s, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(s, StringComparison.OrdinalIgnoreCase)); }
        if (r.GroupId is { } gid) q = q.Where(a => a.AccountGroupId == gid);
        if (r.Nature is { } n) q = q.Where(a => a.Nature == n);
        if (r.Active is { } act) q = q.Where(a => a.IsActive == act);
        if (r.PostableOnly) q = q.Where(a => a.IsPostable && a.IsActive);
        var list = q.ToList();
        return new PagedResult<AccountDto>(list.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).ToList(), r.Page, r.PageSize, list.Count);
    }

    /// <summary>The chart in tree order (group order, then code, sub-accounts below their parent).</summary>
    public async Task<IReadOnlyList<AccountDto>> AllAccountsAsync(CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var groups = await db.AccountGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        var accounts = await db.ChartOfAccounts.AsNoTracking().ToListAsync(ct);
        var parents = accounts.Where(a => a.ParentAccountId is not null).Select(a => a.ParentAccountId!.Value).ToHashSet();
        var groupOrder = (await ListGroupsAsync(ct)).Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var result = new List<AccountDto>();
        void Walk(long? parent, int depth, IEnumerable<ChartOfAccount> pool)
        {
            foreach (var a in pool.Where(a => a.ParentAccountId == parent).OrderBy(a => parent is null ? groupOrder.GetValueOrDefault(a.AccountGroupId) : 0).ThenBy(a => a.Code))
            {
                var g = groups[a.AccountGroupId];
                result.Add(new AccountDto(a.Id, a.Code, a.Name, a.AccountGroupId, g.Name, g.Classification, a.Nature, a.ParentAccountId, accounts.FirstOrDefault(p => p.Id == a.ParentAccountId)?.Code,
                    !parents.Contains(a.Id), a.IsActive, a.AllowManualEntries, a.Description, depth, a.RowVersion.ToToken()));
                Walk(a.Id, depth + 1, accounts);
            }
        }
        Walk(null, 0, accounts);
        return result;
    }

    public async Task<IReadOnlyList<OptionDto>> PostableOptionsAsync(AccountClassification[]? only = null, CancellationToken ct = default)
    {
        var all = await AllAccountsAsync(ct);
        return all.Where(a => a.IsPostable && a.IsActive && (only is null || only.Contains(a.Classification))).Select(a => new OptionDto(a.Id, a.Code, a.Name)).ToList();
    }

    public async Task<AccountDetailDto> GetAccountAsync(long id, CancellationToken ct = default)
    {
        var a = (await AllAccountsAsync(ct)).FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Account not found.");
        var all = await support.HasAllAccessAsync(ct);
        var q = db.GeneralLedgerEntries.AsNoTracking().Where(e => e.AccountId == id);
        if (!all)
        {
            var uid = support.RequireUser();
            q = q.Where(e => e.ProjectId != null && db.UserProjects.Any(up => up.UserId == uid && up.IsActive && up.ProjectId == e.ProjectId));
        }
        var sums = await q.GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit), N = g.Count() }).FirstOrDefaultAsync(ct);
        var d = sums?.D ?? 0m; var c = sums?.C ?? 0m;
        return new AccountDetailDto(a, d, c, Domain.Accounting.AccountingMath.NormalBalance(a.Nature, d, c), sums?.N ?? 0, await support.CurrencyAsync(ct));
    }

    public async Task<AccountDetailDto> CreateAccountAsync(SaveAccountRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await accountValidator.ValidateOrThrowAsync(r, ct);
        var group = await ActiveGroupAsync(r.AccountGroupId, ct);
        await CheckParentAsync(r.ParentAccountId, null, group, ct);
        var a = new ChartOfAccount
        {
            Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), AccountGroupId = group.Id, ParentAccountId = r.ParentAccountId, Nature = group.Nature,
            AllowManualEntries = r.AllowManualEntries, Description = AccountingSupport.TrimOrNull(r.Description, 500), IsActive = r.IsActive,
        };
        db.ChartOfAccounts.Add(a);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "An account with this code already exists."); }
        await audit.RecordAsync(new AuditEntry("Account.Created", "ChartOfAccount", a.Id.ToString(), NewValues: new { a.Code, a.Name, a.AccountGroupId, a.ParentAccountId, a.Nature, a.AllowManualEntries }), ct);
        return await GetAccountAsync(a.Id, ct);
    }

    public async Task<AccountDetailDto> UpdateAccountAsync(long id, SaveAccountRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await accountValidator.ValidateOrThrowAsync(r, ct);
        var a = await db.ChartOfAccounts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Account not found.");
        db.UseRowVersion(a, r.RowVersion);
        var old = new { a.Code, a.Name, a.AccountGroupId, a.ParentAccountId, a.IsActive, a.AllowManualEntries };
        var hasPostings = await db.GeneralLedgerEntries.AnyAsync(e => e.AccountId == id, ct) || await db.JournalEntryLines.AnyAsync(l => l.AccountId == id, ct);
        var group = await ActiveGroupAsync(r.AccountGroupId, ct, allowInactiveId: a.AccountGroupId);
        if (group.Nature != a.Nature) throw new DomainException("An account cannot move to a group of another nature.");
        if (hasPostings && !string.Equals(r.Code.Trim(), a.Code, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The code of an account with journal lines cannot be changed.");
        await CheckParentAsync(r.ParentAccountId, id, group, ct);
        if (!r.IsActive && a.IsActive)
        {
            var bal = await db.GeneralLedgerEntries.Where(e => e.AccountId == id).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
            if (bal != 0m) throw new DomainException("An account with a balance cannot be deactivated.");
            if (await db.BankAccounts.AnyAsync(b => b.GlAccountId == id && b.IsActive, ct) || await db.CashAccounts.AnyAsync(c => c.GlAccountId == id && c.IsActive, ct))
                throw new DomainException("The account is linked to an active bank or cash account.");
            if (await db.PostingRuleLines.AnyAsync(l => l.AccountId == id && db.PostingRules.Any(p => p.Id == l.PostingRuleId && (p.Status == PostingRuleStatus.Active || p.Status == PostingRuleStatus.PendingApproval)), ct))
                throw new DomainException("The account is used by an active or pending posting rule.");
        }
        a.Code = r.Code.Trim().ToUpperInvariant(); a.Name = r.Name.Trim(); a.AccountGroupId = group.Id; a.ParentAccountId = r.ParentAccountId;
        a.AllowManualEntries = r.AllowManualEntries; a.Description = AccountingSupport.TrimOrNull(r.Description, 500); a.IsActive = r.IsActive;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "An account with this code already exists."); }
        await audit.RecordAsync(new AuditEntry("Account.Updated", "ChartOfAccount", id.ToString(), old, new { a.Code, a.Name, a.AccountGroupId, a.ParentAccountId, a.IsActive, a.AllowManualEntries }), ct);
        return await GetAccountAsync(id, ct);
    }

    private async Task<AccountGroup> ActiveGroupAsync(long id, CancellationToken ct, long? allowInactiveId = null)
    {
        var g = await db.AccountGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AccountingSupport.Invalid("AccountGroupId", "The account group was not found.");
        if (!g.IsActive && g.Id != allowInactiveId) throw AccountingSupport.Invalid("AccountGroupId", "The account group is inactive.");
        if (g.ParentGroupId is null) throw AccountingSupport.Invalid("AccountGroupId", "Choose a group below the nature (e.g. Cash, Bank, Salaries).");
        return g;
    }

    private async Task CheckParentAsync(long? parentId, long? selfId, AccountGroup group, CancellationToken ct)
    {
        if (parentId is not { } pid) return;
        if (pid == selfId) throw AccountingSupport.Invalid("ParentAccountId", "An account cannot be its own parent.");
        var parent = await db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw AccountingSupport.Invalid("ParentAccountId", "The parent account was not found.");
        if (parent.Nature != group.Nature) throw AccountingSupport.Invalid("ParentAccountId", "The parent account has another nature.");
        if (selfId is { } sid)
        {
            var all = await db.ChartOfAccounts.AsNoTracking().Select(a => new { a.Id, a.ParentAccountId }).ToListAsync(ct);
            for (long? cur = pid; cur is not null; cur = all.FirstOrDefault(a => a.Id == cur)?.ParentAccountId)
                if (cur == sid) throw AccountingSupport.Invalid("ParentAccountId", "An account cannot be placed below its own sub-account.");
        }
        if (await db.JournalEntryLines.AnyAsync(l => l.AccountId == pid, ct))
            throw AccountingSupport.Invalid("ParentAccountId", "The parent account already has journal lines and cannot receive sub-accounts.");
    }

    // =====================================================================================================
    // cost centers
    // =====================================================================================================
    public async Task<IReadOnlyList<CostCenterDto>> ListCostCentersAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var projects = (await support.ProjectOptionsAsync(ct)).ToDictionary(p => p.Id);
        var all = await support.HasAllAccessAsync(ct);
        var list = await (from c in db.CostCenters.AsNoTracking() where !activeOnly || c.IsActive orderby c.Code select c).ToListAsync(ct);
        return list.Where(c => c.ProjectId is null ? true : projects.ContainsKey(c.ProjectId.Value))
            .Where(c => all || c.ProjectId is not null)
            .Select(c => new CostCenterDto(c.Id, c.Code, c.Name, c.ProjectId, c.ProjectId is { } p ? projects[p].ProjectCode : null, c.IsActive, c.Description, c.RowVersion.ToToken())).ToList();
    }

    public async Task<CostCenterDto> SaveCostCenterAsync(long? id, SaveCostCenterRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await costCenterValidator.ValidateOrThrowAsync(r, ct);
        await support.EnsureScopeAsync(r.ProjectId, ct);
        CostCenter c;
        object? old = null;
        if (id is { } cid)
        {
            c = await db.CostCenters.FirstOrDefaultAsync(x => x.Id == cid, ct) ?? throw new NotFoundException("Cost center not found.");
            await support.EnsureScopeAsync(c.ProjectId, ct);
            db.UseRowVersion(c, r.RowVersion);
            old = new { c.Code, c.Name, c.ProjectId, c.IsActive };
            if (c.ProjectId != r.ProjectId && await db.JournalEntryLines.AnyAsync(l => l.CostCenterId == cid, ct)) throw new DomainException("A cost center with journal lines cannot move to another project.");
        }
        else { c = new CostCenter(); db.CostCenters.Add(c); }
        c.Code = r.Code.Trim().ToUpperInvariant(); c.Name = r.Name.Trim(); c.ProjectId = r.ProjectId; c.Description = AccountingSupport.TrimOrNull(r.Description, 500); c.IsActive = r.IsActive;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "A cost center with this code already exists."); }
        await audit.RecordAsync(new AuditEntry(id is null ? "CostCenter.Created" : "CostCenter.Updated", "CostCenter", c.Id.ToString(), old, new { c.Code, c.Name, c.ProjectId, c.IsActive }), ct);
        return (await ListCostCentersAsync(false, ct)).First(x => x.Id == c.Id);
    }

    public async Task<AccountingLookupsDto> LookupsAsync(CancellationToken ct = default) =>
        new(await support.ProjectOptionsAsync(ct), await PostableOptionsAsync(null, ct),
            (await ListCostCentersAsync(true, ct)).Select(c => new OptionDto(c.Id, c.Code, c.Name)).ToList());
}
