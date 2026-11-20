using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Installments;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Expenses;

/// <summary>
/// Shared core for expenses, vendors and government fees: caller identity, company clock, project access, payment-method checks, the accounting outbox and
/// expense creation. Money operations (payments, collections, reversals) run in one transaction that starts with a row lock on the owning record
/// (<see cref="IAppDbContext.LockForUpdateAsync{T}"/>), so concurrent operations on the same expense/assessment serialize and cannot overpay it.
/// </summary>
public class ExpenseSupport(IAppDbContext db, ICurrentUser user, InstallmentSupport installments, ProjectAccessService access, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DateTime Now => clock.GetUtcNow().UtcDateTime;
    public string Actor => user.UserName ?? "system";
    public Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => installments.TodayAsync(ct);
    public Task<string> CurrencySymbolAsync(CancellationToken ct = default) => installments.CurrencyAsync(ct);
    public Task<MoneyPolicyDto> PolicyAsync(CancellationToken ct = default) => PolicyCoreAsync(ct);
    public sealed record MoneyPolicyDto(int Decimals, RoundingMode Mode);
    private async Task<MoneyPolicyDto> PolicyCoreAsync(CancellationToken ct) { var p = await installments.PolicyAsync(ct); return new MoneyPolicyDto(p.Decimals, p.Mode); }

    public static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    public static string Trim(string? v, int max) { var t = (v ?? "").Trim(); return t.Length > max ? t[..max] : t; }
    public static string? TrimOrNull(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : Trim(v, max);
    public static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    public static string Sha256(string value) => PaymentSupport.Sha256(value);
    public static bool IsDuplicate(DbUpdateException ex, string? contains = null) => PaymentSupport.IsDuplicate(ex, contains);

    // ---------- project access ----------
    public Task<IQueryable<Project>> ProjectsAsync(CancellationToken ct = default) => access.ScopeAsync(db.Projects.AsNoTracking(), ct);
    public Task EnsureProjectAccessAsync(long projectId, CancellationToken ct = default) => access.EnsureAccessAsync(projectId, ct);
    public Task<bool> HasAllAccessAsync(CancellationToken ct = default) => access.HasAllAccessAsync(ct);

    /// <summary>Project access plus a status check: archived projects take no new records.</summary>
    public async Task<Project> OpenProjectAsync(long projectId, CancellationToken ct = default)
    {
        await access.EnsureAccessAsync(projectId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        if (project.Status == ProjectStatus.Archived) throw new DomainException("The project is archived and takes no new financial records.");
        return project;
    }

    // ---------- lookups ----------
    public async Task<ExpenseCategory> ActiveCategoryAsync(long id, CancellationToken ct = default)
    {
        var c = await db.ExpenseCategories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw Invalid("CategoryId", "The expense category was not found.");
        if (!c.IsActive) throw Invalid("CategoryId", "The expense category is inactive.");
        return c;
    }

    public async Task<Vendor> ActiveVendorAsync(long id, CancellationToken ct = default)
    {
        var v = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw Invalid("VendorId", "The vendor was not found.");
        if (!v.IsActive) throw Invalid("VendorId", "The vendor is inactive.");
        return v;
    }

    /// <summary>A payment method for paying out or collecting: active, with a reference where the method requires one.</summary>
    public async Task<PaymentMethod> MethodAsync(long methodId, string? reference, CancellationToken ct = default)
    {
        var m = await db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(x => x.Id == methodId && x.IsActive, ct) ?? throw Invalid("PaymentMethodId", "The payment method was not found or is inactive.");
        if (m.RequiresReference && string.IsNullOrWhiteSpace(reference)) throw Invalid("Reference", $"A reference is required for {m.Name}.");
        return m;
    }

    public async Task<IReadOnlyList<PaymentMethod>> MethodsAsync(CancellationToken ct = default) =>
        await db.PaymentMethods.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.Id).ToListAsync(ct);

    // ---------- accounting outbox (resolves spec-issues #1 for Phase 7: Phase 8 consumes these rows) ----------
    public void Outbox(AccountingEventType type, string sourceType, long sourceId, long projectId, long? agreementId, decimal amount, DateOnly date, object payload) =>
        db.AccountingPostingRequests.Add(new AccountingPostingRequest
        {
            EventType = type, SourceType = sourceType, SourceId = sourceId, ProjectId = projectId, AgreementId = agreementId, Amount = amount, EventDate = date,
            PayloadJson = JsonSerializer.Serialize(payload, Json), Status = AccountingPostingStatus.Pending, CreatedAtUtc = Now,
        });

    // ---------- expense creation (used by expenses and vendor invoices) ----------
    public async Task<Expense> CreateExpenseAsync(long projectId, long categoryId, long? vendorId, string description, DateOnly expenseDate, DateOnly? dueDate, decimal amount,
        string? reference, string? remarks, string? idempotencyKey, CancellationToken ct = default)
    {
        var userId = RequireUser();
        await OpenProjectAsync(projectId, ct);
        await ActiveCategoryAsync(categoryId, ct);
        if (vendorId is { } v) await ActiveVendorAsync(v, ct);
        var expense = new Expense
        {
            ExpenseNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = NullIfBlank(idempotencyKey), ProjectId = projectId, CategoryId = categoryId, VendorId = vendorId,
            Description = Trim(description, 500), ExpenseDate = expenseDate, DueDate = dueDate, Amount = amount, Reference = TrimOrNull(reference, 100), Remarks = TrimOrNull(remarks, 1000),
            Status = ExpenseStatus.Draft,
        };
        db.Expenses.Add(expense);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw Invalid("IdempotencyKey", "This expense was already submitted."); }
        expense.ExpenseNumber = $"EXP-{expense.Id:D6}";
        await db.SaveChangesAsync(ct);
        _ = userId;
        return expense;
    }
}
