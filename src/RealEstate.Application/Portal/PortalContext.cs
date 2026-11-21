using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Portal;

public sealed record PortalCustomer(long CustomerId, long AccountId, Guid UserId, string CustomerCode, string FullName, string? LoginEmail);

/// <summary>
/// Resolves the customer of the authenticated login. This is the only place where a customer identity enters the customer application: it comes from the
/// CustomerAccount link of the authenticated user, never from a request value. Records outside the customer's own agreements look like they do not exist.
/// </summary>
public class PortalContext(IAppDbContext db, ICurrentUser user, IPermissionService permissions)
{
    private PortalCustomer? cached;

    public async Task<PortalCustomer> RequireAsync(CancellationToken ct = default)
    {
        if (cached is not null) return cached;
        var userId = user.UserId ?? throw new ForbiddenException("Authentication required.");
        if (!await permissions.HasPermissionAsync(userId, Permissions.PortalAccess, ct)) throw new ForbiddenException("This account cannot use the customer application.");
        var x = await (from a in db.CustomerAccounts.AsNoTracking()
                       join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                       where a.UserId == userId && a.IsActive
                       select new { a.Id, CustomerId = c.Id, c.CustomerCode, c.FullName, c.Status }).FirstOrDefaultAsync(ct)
                ?? throw new ForbiddenException("No active customer account is linked to this login.");
        if (x.Status == CustomerStatus.Blocked) throw new ForbiddenException("Your account is blocked. Please contact the office.");
        return cached = new PortalCustomer(x.CustomerId, x.Id, userId, x.CustomerCode, x.FullName, user.UserName);
    }

    /// <summary>Agreements where the customer is a current party of the booking.</summary>
    public IQueryable<long> OwnAgreementIds(long customerId) =>
        from a in db.CustomerAgreements.AsNoTracking()
        where db.BookingCustomers.Any(bc => bc.BookingId == a.BookingId && bc.CustomerId == customerId && bc.IsActive)
        select a.Id;

    public async Task RequireAgreementAsync(long customerId, long agreementId, CancellationToken ct = default)
    {
        if (!await OwnAgreementIds(customerId).AnyAsync(id => id == agreementId, ct)) throw new NotFoundException("Agreement not found.");
    }
}
