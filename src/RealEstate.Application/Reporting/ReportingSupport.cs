using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Expenses;
using RealEstate.Application.Installments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Application.Reporting;

/// <summary>
/// Shared reporting core: caller, clock, permissions and the data scope every report applies. Scope = projects the caller can access (project-level), and, when a customer is
/// chosen, a customer the caller may see (customer-level). A project id supplied by the client is always checked against the caller's access.
/// </summary>
public class ReportingSupport(IAppDbContext db, ProjectAccessService access, CustomerService customers, ICurrentUser user, IPermissionService permissions,
    InstallmentSupport installments, TimeProvider clock)
{
    public DateTime Now => clock.GetUtcNow().UtcDateTime;
    public string Actor => user.UserName ?? "system";
    public Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => installments.TodayAsync(ct);
    public Task<string> CurrencyAsync(CancellationToken ct = default) => installments.CurrencyAsync(ct);
    public static RequestValidationException Invalid(string field, string message) => ExpenseSupport.Invalid(field, message);

    public Task EnsureAsync(string permission, CancellationToken ct = default) => permissions.EnsureAsync(user, permission, ct);
    public async Task<bool> HasAsync(string permission, CancellationToken ct = default) => user.UserId is { } id && await permissions.HasPermissionAsync(id, permission, ct);
    public Task<bool> HasAllAccessAsync(CancellationToken ct = default) => access.HasAllAccessAsync(ct);

    public static T? ParseEnum<T>(string? value) where T : struct, Enum => !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, true, out var v) && Enum.IsDefined(v) ? v : null;

    /// <summary>Accessible projects, narrowed to one project when requested (Forbidden/NotFound for projects the caller may not see).</summary>
    public async Task<IQueryable<long>> ProjectIdsAsync(long? projectId, CancellationToken ct = default)
    {
        if (projectId is { } p)
        {
            await access.EnsureAccessAsync(p, ct);
            return db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.Id);
        }
        return (await access.ScopeAsync(db.Projects.AsNoTracking(), ct)).Select(x => x.Id);
    }

    public async Task<IReadOnlyList<ProjectOptionDto>> ProjectOptionsAsync(CancellationToken ct = default) =>
        await (await access.ScopeAsync(db.Projects.AsNoTracking(), ct)).OrderBy(p => p.ProjectCode).Select(p => new ProjectOptionDto(p.Id, p.ProjectCode, p.ProjectName)).ToListAsync(ct);

    public async Task EnsureCustomerAsync(long customerId, CancellationToken ct = default) => await customers.EnsureAccessAsync(customerId, ct);

    /// <summary>
    /// Bookings the report may see: caller's projects, optional project and block, and (when <paramref name="applyCustomer"/>) bookings the chosen customer is currently a party to.
    /// </summary>
    public async Task<IQueryable<Booking>> BookingsAsync(ReportFilters f, bool applyCustomer, CancellationToken ct = default)
    {
        var projects = await ProjectIdsAsync(f.ProjectId, ct);
        var q = db.Bookings.AsNoTracking().Where(b => projects.Contains(b.ProjectId));
        if (f.BlockId is { } blk)
            q = q.Where(b => db.BookingProperties.Any(bp => bp.BookingId == b.Id && db.Properties.Any(p => p.Id == bp.PropertyId && p.BlockId == blk)));
        if (applyCustomer && f.CustomerId is { } c)
        {
            await EnsureCustomerAsync(c, ct);
            q = q.Where(b => db.BookingCustomers.Any(bc => bc.BookingId == b.Id && bc.CustomerId == c && bc.IsActive));
        }
        return q;
    }

    /// <summary>Ids of the agreements whose booking passes <see cref="BookingsAsync"/> (without the customer filter, which ledgers apply on the ledger holder).</summary>
    public async Task<IQueryable<long>> AgreementIdsAsync(ReportFilters f, bool applyCustomer, CancellationToken ct = default)
    {
        var bookings = await BookingsAsync(f, applyCustomer, ct);
        return from a in db.CustomerAgreements.AsNoTracking() join b in bookings on a.BookingId equals b.Id select a.Id;
    }

    /// <summary>Echo of the applied filters for the report header and exports.</summary>
    public async Task<IReadOnlyList<ReportFilterEchoDto>> EchoAsync(ReportDefinitionDto def, ReportFilters f, DateOnly? defaultTo, CancellationToken ct)
    {
        var list = new List<ReportFilterEchoDto>();
        if (def.Uses(ReportFilterNames.From) && f.From is { } from) list.Add(new("From", from.ToString("yyyy-MM-dd")));
        if (def.Uses(ReportFilterNames.To)) list.Add(new("To", (f.To ?? defaultTo)?.ToString("yyyy-MM-dd") ?? "-"));
        if (def.Uses(ReportFilterNames.Project) && f.ProjectId is { } p)
            list.Add(new("Project", await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.ProjectCode + " " + x.ProjectName).FirstOrDefaultAsync(ct) ?? $"#{p}"));
        if (def.Uses(ReportFilterNames.Block) && f.BlockId is { } b)
            list.Add(new("Block", await db.ProjectBlocks.AsNoTracking().Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? $"#{b}"));
        if (def.Uses(ReportFilterNames.Customer) && f.CustomerId is { } c)
            list.Add(new("Customer", await db.Customers.AsNoTracking().Where(x => x.Id == c).Select(x => x.CustomerCode + " " + x.FullName).FirstOrDefaultAsync(ct) ?? $"#{c}"));
        if (def.Uses(ReportFilterNames.TransactionType) && !string.IsNullOrWhiteSpace(f.TransactionType)) list.Add(new("Transaction type", f.TransactionType!));
        if (def.Uses(ReportFilterNames.PaymentMethod) && f.PaymentMethodId is { } m)
            list.Add(new("Payment method", await db.PaymentMethods.AsNoTracking().Where(x => x.Id == m).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? $"#{m}"));
        if (def.Uses(ReportFilterNames.Status) && !string.IsNullOrWhiteSpace(f.Status)) list.Add(new("Status", f.Status!));
        if (def.Uses(ReportFilterNames.Account) && f.AccountId is { } a)
            list.Add(new("Account", await db.ChartOfAccounts.AsNoTracking().Where(x => x.Id == a).Select(x => x.Code + " " + x.Name).FirstOrDefaultAsync(ct) ?? $"#{a}"));
        return list;
    }
}
