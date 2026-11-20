using System.Globalization;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Accounting;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Banking;

/// <summary>
/// Bank and cash accounts (each linked to one ledger account of a Bank/Cash group) and bank reconciliation (spec 8.7): statement import with duplicate
/// detection, automatic and manual matching of statement lines to book entries, unmatched items on both sides, reconciliation as at a statement date and its
/// history. Completed reconciliations are never changed.
/// </summary>
public class BankingService(IAppDbContext db, AccountingSupport support, IAuditService audit, IValidator<SaveBankAccountRequest> bankValidator,
    IValidator<SaveCashAccountRequest> cashValidator, IValidator<ImportStatementRequest> importValidator, IValidator<StartReconciliationRequest> startValidator)
{
    private const int MatchWindowDays = 7;

    // =====================================================================================================
    // bank and cash accounts
    // =====================================================================================================
    public async Task<IReadOnlyList<BankAccountDto>> ListBankAccountsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        await support.EnsureScopeAsync(null, ct);
        var list = await db.BankAccounts.AsNoTracking().Where(b => !activeOnly || b.IsActive).OrderBy(b => b.Code).ToListAsync(ct);
        var result = new List<BankAccountDto>();
        foreach (var b in list) result.Add(await MapBankAsync(b, ct));
        return result;
    }

    public async Task<BankAccountDto> GetBankAccountAsync(long id, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        await support.EnsureScopeAsync(null, ct);
        var b = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Bank account not found.");
        return await MapBankAsync(b, ct);
    }

    private async Task<BankAccountDto> MapBankAsync(BankAccount b, CancellationToken ct)
    {
        var gl = await db.ChartOfAccounts.AsNoTracking().FirstAsync(a => a.Id == b.GlAccountId, ct);
        var project = b.ProjectId is { } p ? await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.ProjectCode).FirstOrDefaultAsync(ct) : null;
        var balance = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.AccountId == b.GlAccountId).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var unmatched = await db.BankTransactions.AsNoTracking().CountAsync(t => t.BankAccountId == b.Id && t.Status == BankTransactionStatus.Unmatched, ct);
        return new BankAccountDto(b.Id, b.Code, b.Name, b.BankName, b.Branch, b.AccountTitle, b.AccountNumber, b.Iban, b.CurrencyCode, b.GlAccountId, gl.Code, gl.Name, b.ProjectId, project,
            b.IsActive, balance, unmatched, b.RowVersion.ToToken());
    }

    public async Task<BankAccountDto> SaveBankAccountAsync(long? id, SaveBankAccountRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await support.EnsureScopeAsync(null, ct);
        await bankValidator.ValidateOrThrowAsync(r, ct);
        if (r.ProjectId is { } pid && !await db.Projects.AnyAsync(p => p.Id == pid, ct)) throw AccountingSupport.Invalid("ProjectId", "The project was not found.");
        await CheckLedgerAccountAsync(r.GlAccountId, AccountClassification.Bank, ct);
        BankAccount b; object? old = null;
        if (id is { } bid)
        {
            b = await db.BankAccounts.FirstOrDefaultAsync(x => x.Id == bid, ct) ?? throw new NotFoundException("Bank account not found.");
            db.UseRowVersion(b, r.RowVersion);
            old = new { b.Code, b.Name, b.BankName, b.AccountNumber, b.GlAccountId, b.IsActive };
            if (b.GlAccountId != r.GlAccountId && (await db.BankTransactions.AnyAsync(t => t.BankAccountId == bid, ct) || await db.GeneralLedgerEntries.AnyAsync(e => e.AccountId == b.GlAccountId, ct)))
                throw new DomainException("The ledger account of a bank account with postings or statement lines cannot change.");
        }
        else { b = new BankAccount(); db.BankAccounts.Add(b); }
        b.Code = r.Code.Trim().ToUpperInvariant(); b.Name = r.Name.Trim(); b.BankName = r.BankName.Trim(); b.Branch = AccountingSupport.TrimOrNull(r.Branch, 150);
        b.AccountTitle = r.AccountTitle.Trim(); b.AccountNumber = r.AccountNumber.Trim(); b.Iban = AccountingSupport.TrimOrNull(r.Iban, 50); b.CurrencyCode = r.CurrencyCode.Trim().ToUpperInvariant();
        b.GlAccountId = r.GlAccountId; b.ProjectId = r.ProjectId; b.IsActive = r.IsActive;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "The code or the ledger account is already used by another bank account."); }
        await audit.RecordAsync(new AuditEntry(id is null ? "BankAccount.Created" : "BankAccount.Updated", "BankAccount", b.Id.ToString(), old,
            new { b.Code, b.Name, b.BankName, b.AccountNumber, b.GlAccountId, b.IsActive }), ct);
        return await MapBankAsync(b, ct);
    }

    public async Task<IReadOnlyList<CashAccountDto>> ListCashAccountsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        await support.EnsureScopeAsync(null, ct);
        var list = await db.CashAccounts.AsNoTracking().Where(c => !activeOnly || c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);
        var result = new List<CashAccountDto>();
        foreach (var c in list) result.Add(await MapCashAsync(c, ct));
        return result;
    }

    private async Task<CashAccountDto> MapCashAsync(CashAccount c, CancellationToken ct)
    {
        var gl = await db.ChartOfAccounts.AsNoTracking().FirstAsync(a => a.Id == c.GlAccountId, ct);
        var project = c.ProjectId is { } p ? await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.ProjectCode).FirstOrDefaultAsync(ct) : null;
        var balance = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.AccountId == c.GlAccountId).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        return new CashAccountDto(c.Id, c.Code, c.Name, c.Custodian, c.GlAccountId, gl.Code, gl.Name, c.ProjectId, project, c.IsActive, balance, c.RowVersion.ToToken());
    }

    public async Task<CashAccountDto> SaveCashAccountAsync(long? id, SaveCashAccountRequest r, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingSetup, ct);
        await support.EnsureScopeAsync(null, ct);
        await cashValidator.ValidateOrThrowAsync(r, ct);
        if (r.ProjectId is { } pid && !await db.Projects.AnyAsync(p => p.Id == pid, ct)) throw AccountingSupport.Invalid("ProjectId", "The project was not found.");
        await CheckLedgerAccountAsync(r.GlAccountId, AccountClassification.Cash, ct);
        CashAccount c; object? old = null;
        if (id is { } cid)
        {
            c = await db.CashAccounts.FirstOrDefaultAsync(x => x.Id == cid, ct) ?? throw new NotFoundException("Cash account not found.");
            db.UseRowVersion(c, r.RowVersion);
            old = new { c.Code, c.Name, c.GlAccountId, c.IsActive };
            if (c.GlAccountId != r.GlAccountId && await db.GeneralLedgerEntries.AnyAsync(e => e.AccountId == c.GlAccountId, ct))
                throw new DomainException("The ledger account of a cash account with postings cannot change.");
        }
        else { c = new CashAccount(); db.CashAccounts.Add(c); }
        c.Code = r.Code.Trim().ToUpperInvariant(); c.Name = r.Name.Trim(); c.Custodian = AccountingSupport.TrimOrNull(r.Custodian, 150); c.GlAccountId = r.GlAccountId; c.ProjectId = r.ProjectId; c.IsActive = r.IsActive;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw AccountingSupport.Invalid("Code", "The code or the ledger account is already used by another cash account."); }
        await audit.RecordAsync(new AuditEntry(id is null ? "CashAccount.Created" : "CashAccount.Updated", "CashAccount", c.Id.ToString(), old, new { c.Code, c.Name, c.GlAccountId, c.IsActive }), ct);
        return await MapCashAsync(c, ct);
    }

    private async Task CheckLedgerAccountAsync(long glAccountId, AccountClassification required, CancellationToken ct)
    {
        var a = await db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == glAccountId, ct) ?? throw AccountingSupport.Invalid("GlAccountId", "The ledger account was not found.");
        var c = await db.AccountGroups.AsNoTracking().Where(g => g.Id == a.AccountGroupId).Select(g => g.Classification).FirstAsync(ct);
        if (c != required) throw AccountingSupport.Invalid("GlAccountId", $"Choose a ledger account in a {required} group.");
        if (!a.IsActive) throw AccountingSupport.Invalid("GlAccountId", "The ledger account is inactive.");
        if (await db.ChartOfAccounts.AnyAsync(x => x.ParentAccountId == glAccountId, ct)) throw AccountingSupport.Invalid("GlAccountId", "The ledger account has sub-accounts.");
        if (await db.BankAccounts.AnyAsync(b => b.GlAccountId == glAccountId, ct) && required == AccountClassification.Cash) throw AccountingSupport.Invalid("GlAccountId", "The ledger account belongs to a bank account.");
        if (await db.CashAccounts.AnyAsync(b => b.GlAccountId == glAccountId, ct) && required == AccountClassification.Bank) throw AccountingSupport.Invalid("GlAccountId", "The ledger account belongs to a cash account.");
    }

    // =====================================================================================================
    // statement import and matching
    // =====================================================================================================
    /// <summary>
    /// Parses a CSV statement: header row, then Date (yyyy-MM-dd or dd/MM/yyyy), Description, Reference, Deposit, Withdrawal and optionally Balance.
    /// Quoted fields with commas are supported.
    /// </summary>
    public static IReadOnlyList<StatementLineInput> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<StatementLineInput>();
        var header = reader.ReadLine() ?? throw AccountingSupport.Invalid("File", "The statement file is empty.");
        if (!header.Contains("date", StringComparison.OrdinalIgnoreCase)) throw AccountingSupport.Invalid("File", "The first row must be the header: Date,Description,Reference,Deposit,Withdrawal[,Balance].");
        var row = 1;
        while (reader.ReadLine() is { } line)
        {
            row++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            if (f.Count < 5) throw AccountingSupport.Invalid("File", $"Row {row}: expected at least 5 columns.");
            if (!DateOnly.TryParseExact(f[0].Trim(), ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw AccountingSupport.Invalid("File", $"Row {row}: invalid date '{f[0]}'.");
            decimal Amount(string s, string name) => string.IsNullOrWhiteSpace(s) ? 0m
                : decimal.TryParse(s.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : throw AccountingSupport.Invalid("File", $"Row {row}: invalid {name} '{s}'.");
            decimal? balance = f.Count > 5 && !string.IsNullOrWhiteSpace(f[5]) ? Amount(f[5], "balance") : null;
            lines.Add(new StatementLineInput(date, f[1].Trim(), string.IsNullOrWhiteSpace(f[2]) ? null : f[2].Trim(), Amount(f[3], "deposit"), Amount(f[4], "withdrawal"), balance));
        }
        return lines;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>Imports statement lines; identical lines already imported are skipped as duplicates; new lines are auto-matched to book entries.</summary>
    public async Task<ImportStatementResultDto> ImportAsync(long bankAccountId, ImportStatementRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        await importValidator.ValidateOrThrowAsync(r, ct);
        var bank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account not found.");
        if (!bank.IsActive) throw new DomainException("The bank account is inactive.");
        var batch = "IMP-" + support.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var seen = new Dictionary<string, int>();
        var candidates = new List<BankTransaction>();
        foreach (var l in r.Lines)
        {
            var key = string.Join("|", bankAccountId, l.TransactionDate.ToString("yyyy-MM-dd"), l.Description.Trim(), l.Reference?.Trim(), l.Deposit.ToString("0.00", CultureInfo.InvariantCulture),
                l.Withdrawal.ToString("0.00", CultureInfo.InvariantCulture), l.RunningBalance?.ToString("0.00", CultureInfo.InvariantCulture));
            var n = seen[key] = seen.GetValueOrDefault(key) + 1;
            candidates.Add(new BankTransaction
            {
                BankAccountId = bankAccountId, ImportBatch = batch, TransactionDate = l.TransactionDate, Description = AccountingSupport.Trim(l.Description, 500),
                Reference = AccountingSupport.TrimOrNull(l.Reference, 100), Deposit = l.Deposit, Withdrawal = l.Withdrawal, RunningBalance = l.RunningBalance,
                LineHash = PaymentSupport.Sha256(key + "#" + n), ImportedBy = support.Actor, ImportedAtUtc = support.Now,
            });
        }
        var hashes = candidates.Select(c => c.LineHash).ToList();
        var existing = (await db.BankTransactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId && hashes.Contains(t.LineHash)).Select(t => t.LineHash).ToListAsync(ct)).ToHashSet();
        var fresh = candidates.Where(c => !existing.Contains(c.LineHash)).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<BankAccount>(bankAccountId, ct);
        db.BankTransactions.AddRange(fresh);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        var matched = await AutoMatchCoreAsync(bank, fresh.Select(f => f.Id).ToList(), ct);
        await audit.RecordAsync(new AuditEntry("BankStatement.Imported", "BankAccount", bankAccountId.ToString(), NewValues: new { batch, Imported = fresh.Count, Duplicates = candidates.Count - fresh.Count, AutoMatched = matched }), ct);
        await tx.CommitAsync(ct);
        return new ImportStatementResultDto(batch, fresh.Count, candidates.Count - fresh.Count, matched);
    }

    /// <summary>Runs automatic matching for all unmatched lines of the bank account.</summary>
    public async Task<int> AutoMatchAsync(long bankAccountId, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        var bank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<BankAccount>(bankAccountId, ct);
        var ids = await db.BankTransactions.Where(t => t.BankAccountId == bankAccountId && t.Status == BankTransactionStatus.Unmatched).Select(t => t.Id).ToListAsync(ct);
        var n = await AutoMatchCoreAsync(bank, ids, ct);
        await audit.RecordAsync(new AuditEntry("BankStatement.AutoMatched", "BankAccount", bankAccountId.ToString(), NewValues: new { Matched = n }), ct);
        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>One-to-one matching on the same amount and direction within ±7 days, preferring an equal reference, then the nearest date.</summary>
    private async Task<int> AutoMatchCoreAsync(BankAccount bank, IReadOnlyList<long> transactionIds, CancellationToken ct)
    {
        if (transactionIds.Count == 0) return 0;
        var lines = await db.BankTransactions.Where(t => transactionIds.Contains(t.Id) && t.Status == BankTransactionStatus.Unmatched).OrderBy(t => t.TransactionDate).ThenBy(t => t.Id).ToListAsync(ct);
        if (lines.Count == 0) return 0;
        var from = lines.Min(l => l.TransactionDate).AddDays(-MatchWindowDays);
        var to = lines.Max(l => l.TransactionDate).AddDays(MatchWindowDays);
        var matchedIds = db.BankTransactions.Where(t => t.MatchedLedgerEntryId != null).Select(t => t.MatchedLedgerEntryId!.Value);
        var book = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.AccountId == bank.GlAccountId && e.EntryDate >= from && e.EntryDate <= to && !matchedIds.Contains(e.Id)).ToListAsync(ct);
        var used = new HashSet<long>();
        var count = 0;
        foreach (var l in lines)
        {
            var best = book.Where(e => !used.Contains(e.Id) && (l.Deposit > 0 ? e.Debit == l.Deposit : e.Credit == l.Withdrawal) && Math.Abs(e.EntryDate.DayNumber - l.TransactionDate.DayNumber) <= MatchWindowDays)
                .OrderByDescending(e => l.Reference is not null && e.Reference is not null && string.Equals(e.Reference, l.Reference, StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => Math.Abs(e.EntryDate.DayNumber - l.TransactionDate.DayNumber)).ThenBy(e => e.Id).FirstOrDefault();
            if (best is null) continue;
            used.Add(best.Id);
            l.Status = BankTransactionStatus.Matched; l.MatchedLedgerEntryId = best.Id; l.MatchedBy = support.Actor + " (auto)"; l.MatchedAtUtc = support.Now;
            count++;
        }
        await db.SaveWithConcurrencyAsync(ct);
        return count;
    }

    public async Task<BankTransactionDto> MatchAsync(long bankAccountId, MatchRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        var bank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<BankAccount>(bankAccountId, ct);
        var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == r.BankTransactionId && x.BankAccountId == bankAccountId, ct) ?? throw new NotFoundException("Statement line not found.");
        if (t.Status != BankTransactionStatus.Unmatched) throw new DomainException("The statement line is already matched or ignored.");
        var e = await db.GeneralLedgerEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LedgerEntryId, ct) ?? throw new NotFoundException("Book entry not found.");
        if (e.AccountId != bank.GlAccountId) throw new DomainException("The book entry is not on this bank account's ledger account.");
        if (t.Deposit > 0 ? e.Debit != t.Deposit : e.Credit != t.Withdrawal) throw new DomainException("The amounts or directions differ (a deposit matches a debit, a withdrawal a credit).");
        if (await db.BankTransactions.AnyAsync(x => x.MatchedLedgerEntryId == e.Id, ct)) throw new DomainException("The book entry is already matched to another statement line.");
        t.Status = BankTransactionStatus.Matched; t.MatchedLedgerEntryId = e.Id; t.MatchedBy = support.Actor; t.MatchedAtUtc = support.Now;
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (AccountingSupport.IsDuplicate(ex)) { throw new ConcurrencyException(); }
        await audit.RecordAsync(new AuditEntry("BankStatement.Matched", "BankTransaction", t.Id.ToString(), NewValues: new { LedgerEntryId = e.Id, e.VoucherNumber }), ct);
        await tx.CommitAsync(ct);
        return await TransactionAsync(t.Id, ct);
    }

    public async Task<BankTransactionDto> UnmatchAsync(long bankAccountId, long transactionId, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == transactionId && x.BankAccountId == bankAccountId, ct) ?? throw new NotFoundException("Statement line not found.");
        if (t.Status == BankTransactionStatus.Unmatched) throw new DomainException("The statement line is not matched.");
        if (t.ReconciliationId is { } rid && await db.BankReconciliations.AnyAsync(x => x.Id == rid && x.Status == BankReconciliationStatus.Completed, ct))
            throw new DomainException("The line belongs to a completed reconciliation and cannot change.");
        var old = new { Status = t.Status.ToString(), t.MatchedLedgerEntryId, t.IgnoreReason };
        t.Status = BankTransactionStatus.Unmatched; t.MatchedLedgerEntryId = null; t.MatchedBy = null; t.MatchedAtUtc = null; t.IgnoreReason = null; t.ReconciliationId = null;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("BankStatement.Unmatched", "BankTransaction", t.Id.ToString(), old, new { Status = "Unmatched" }), ct);
        return await TransactionAsync(t.Id, ct);
    }

    public async Task<BankTransactionDto> IgnoreAsync(long bankAccountId, long transactionId, IgnoreTransactionRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw AccountingSupport.Invalid("Reason", "Give a reason of at least 5 characters.");
        var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == transactionId && x.BankAccountId == bankAccountId, ct) ?? throw new NotFoundException("Statement line not found.");
        db.UseRowVersion(t, r.RowVersion);
        if (t.Status != BankTransactionStatus.Unmatched) throw new DomainException("Only unmatched statement lines can be ignored.");
        t.Status = BankTransactionStatus.Ignored; t.IgnoreReason = r.Reason.Trim(); t.MatchedBy = support.Actor; t.MatchedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("BankStatement.Ignored", "BankTransaction", t.Id.ToString(), new { Status = "Unmatched" }, new { Status = "Ignored" }, t.IgnoreReason), ct);
        return await TransactionAsync(t.Id, ct);
    }

    private async Task<BankTransactionDto> TransactionAsync(long id, CancellationToken ct) =>
        (await TransactionsAsync(db.BankTransactions.AsNoTracking().Where(t => t.Id == id), ct)).Single();

    private async Task<List<BankTransactionDto>> TransactionsAsync(IQueryable<BankTransaction> q, CancellationToken ct) =>
        await (from t in q
               join e in db.GeneralLedgerEntries.AsNoTracking() on t.MatchedLedgerEntryId equals (long?)e.Id into es
               from e in es.DefaultIfEmpty()
               select new BankTransactionDto(t.Id, t.TransactionDate, t.Description, t.Reference, t.Deposit, t.Withdrawal, t.RunningBalance, t.Status, t.MatchedLedgerEntryId,
                   e == null ? null : e.VoucherNumber, t.MatchedBy, t.IgnoreReason, t.ReconciliationId, t.ImportBatch, t.RowVersion.ToToken()))
            .ToListAsync(ct);

    // =====================================================================================================
    // reconciliation
    // =====================================================================================================
    public async Task<BankReconciliationWorkspaceDto> WorkspaceAsync(long bankAccountId, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        var account = await GetBankAccountAsync(bankAccountId, ct);
        var current = await db.BankReconciliations.AsNoTracking().Where(x => x.BankAccountId == bankAccountId && x.Status == BankReconciliationStatus.InProgress).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var unmatched = await TransactionsAsync(db.BankTransactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId && t.Status == BankTransactionStatus.Unmatched)
            .OrderBy(t => t.TransactionDate).ThenBy(t => t.Id).Take(500), ct);
        var recent = await TransactionsAsync(db.BankTransactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId && t.Status != BankTransactionStatus.Unmatched)
            .OrderByDescending(t => t.MatchedAtUtc).ThenByDescending(t => t.Id).Take(50), ct);
        var matchedIds = db.BankTransactions.Where(t => t.MatchedLedgerEntryId != null).Select(t => t.MatchedLedgerEntryId!.Value);
        var book = await db.GeneralLedgerEntries.AsNoTracking().Where(e => e.AccountId == account.GlAccountId && !matchedIds.Contains(e.Id)).OrderBy(e => e.EntryDate).ThenBy(e => e.Id).Take(500)
            .Select(e => new BookEntryDto(e.Id, e.EntryDate, e.VoucherNumber, e.Narration, e.Reference, e.Debit, e.Credit, false)).ToListAsync(ct);
        var history = await db.BankReconciliations.AsNoTracking().Where(x => x.BankAccountId == bankAccountId).OrderByDescending(x => x.StatementDate).ThenByDescending(x => x.Id).Take(50).ToListAsync(ct);
        return new BankReconciliationWorkspaceDto(account, current is null ? null : Map(current, account.Name), unmatched, book, recent, history.Select(h => Map(h, account.Name)).ToList(), await support.CurrencyAsync(ct));
    }

    public async Task<IReadOnlyList<BankReconciliationDto>> HistoryAsync(long? bankAccountId, CancellationToken ct = default)
    {
        await support.EnsureAsync(Permissions.AccountingView, ct);
        await support.EnsureScopeAsync(null, ct);
        var banks = await db.BankAccounts.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var list = await db.BankReconciliations.AsNoTracking().Where(x => bankAccountId == null || x.BankAccountId == bankAccountId).OrderByDescending(x => x.StatementDate).ThenByDescending(x => x.Id).Take(200).ToListAsync(ct);
        return list.Select(x => Map(x, banks.GetValueOrDefault(x.BankAccountId) ?? "")).ToList();
    }

    private static BankReconciliationDto Map(BankReconciliation x, string name) =>
        new(x.Id, x.BankAccountId, name, x.StatementDate, x.StatementBalance, x.BookBalance, x.UnclearedBookNet, x.UnmatchedStatementNet, x.Difference, x.Status, x.CompletedBy, x.CompletedAtUtc,
            x.Notes, x.CreatedBy, x.CreatedAtUtc, x.RowVersion.ToToken());

    /// <summary>Starts (or restarts with new figures) the in-progress reconciliation of the bank account and computes the difference.</summary>
    public async Task<BankReconciliationDto> StartAsync(long bankAccountId, StartReconciliationRequest r, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        await startValidator.ValidateOrThrowAsync(r, ct);
        var bank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<BankAccount>(bankAccountId, ct);
        var last = await db.BankReconciliations.AsNoTracking().Where(x => x.BankAccountId == bankAccountId && x.Status == BankReconciliationStatus.Completed).MaxAsync(x => (DateOnly?)x.StatementDate, ct);
        if (last is { } l && r.StatementDate <= l) throw AccountingSupport.Invalid("StatementDate", $"The statement date must be after the last completed reconciliation ({l:yyyy-MM-dd}).");
        var rec = await db.BankReconciliations.FirstOrDefaultAsync(x => x.BankAccountId == bankAccountId && x.Status == BankReconciliationStatus.InProgress, ct);
        if (rec is null) { rec = new BankReconciliation { BankAccountId = bankAccountId }; db.BankReconciliations.Add(rec); }
        rec.StatementDate = r.StatementDate; rec.StatementBalance = r.StatementBalance; rec.Notes = AccountingSupport.TrimOrNull(r.Notes, 1000);
        await ComputeAsync(rec, bank, ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("BankReconciliation.Started", "BankReconciliation", rec.Id.ToString(),
            NewValues: new { rec.StatementDate, rec.StatementBalance, rec.BookBalance, rec.UnclearedBookNet, rec.UnmatchedStatementNet, rec.Difference }), ct);
        await tx.CommitAsync(ct);
        return Map(rec, bank.Name);
    }

    /// <summary>Statement balance = book balance - book entries not yet on the statement + statement lines not yet in the books (up to the statement date).</summary>
    private async Task ComputeAsync(BankReconciliation rec, BankAccount bank, CancellationToken ct)
    {
        var date = rec.StatementDate;
        rec.BookBalance = await db.GeneralLedgerEntries.Where(e => e.AccountId == bank.GlAccountId && e.EntryDate <= date).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var matchedIds = db.BankTransactions.Where(t => t.MatchedLedgerEntryId != null && t.TransactionDate <= date).Select(t => t.MatchedLedgerEntryId!.Value);
        rec.UnclearedBookNet = await db.GeneralLedgerEntries.Where(e => e.AccountId == bank.GlAccountId && e.EntryDate <= date && !matchedIds.Contains(e.Id))
            .SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        rec.UnmatchedStatementNet = await db.BankTransactions.Where(t => t.BankAccountId == bank.Id && t.TransactionDate <= date && t.Status == BankTransactionStatus.Unmatched)
            .SumAsync(t => (decimal?)(t.Deposit - t.Withdrawal), ct) ?? 0m;
        rec.Difference = rec.StatementBalance - (rec.BookBalance - rec.UnclearedBookNet + rec.UnmatchedStatementNet);
    }

    /// <summary>Completes the reconciliation when the difference is zero; matched and ignored lines up to the statement date are attached to it.</summary>
    public async Task<BankReconciliationDto> CompleteAsync(long bankAccountId, long reconciliationId, string? rowVersion, CancellationToken ct = default)
    {
        support.RequireUser();
        await support.EnsureAsync(Permissions.AccountingBankReconcile, ct);
        await support.EnsureScopeAsync(null, ct);
        var bank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("Bank account not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<BankAccount>(bankAccountId, ct);
        var rec = await db.BankReconciliations.FirstOrDefaultAsync(x => x.Id == reconciliationId && x.BankAccountId == bankAccountId, ct) ?? throw new NotFoundException("Reconciliation not found.");
        db.UseRowVersion(rec, rowVersion);
        if (rec.Status != BankReconciliationStatus.InProgress) throw new DomainException("The reconciliation is already completed.");
        await ComputeAsync(rec, bank, ct);
        if (rec.Difference != 0m)
        {
            await db.SaveWithConcurrencyAsync(ct);
            await tx.CommitAsync(ct);
            throw new DomainException($"The reconciliation does not balance (difference {rec.Difference:N2}). Match, record or ignore the remaining items first.");
        }
        var date = rec.StatementDate;
        var lines = await db.BankTransactions.Where(t => t.BankAccountId == bankAccountId && t.TransactionDate <= date && t.Status != BankTransactionStatus.Unmatched && t.ReconciliationId == null).ToListAsync(ct);
        rec.Status = BankReconciliationStatus.Completed; rec.CompletedBy = support.Actor; rec.CompletedAtUtc = support.Now;
        await db.SaveWithConcurrencyAsync(ct);
        foreach (var t in lines) t.ReconciliationId = rec.Id;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("BankReconciliation.Completed", "BankReconciliation", rec.Id.ToString(), new { Status = "InProgress" },
            new { Status = "Completed", rec.StatementDate, rec.StatementBalance, rec.BookBalance, Lines = lines.Count }), ct);
        await tx.CommitAsync(ct);
        return Map(rec, bank.Name);
    }
}
