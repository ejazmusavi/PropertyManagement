using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Payments;

/// <summary>
/// Customer consolidated ledger, agreement-specific ledger and property-specific financial statement (spec 6.5). Balances are read from the ledger
/// tables; outstanding/overdue come from installments that have fallen due (posted receivables), never from the schedule as a whole.
/// Every summary carries a reconciliation check: ledger balance = outstanding receivables - advance - unallocated money.
/// </summary>
public class LedgerService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support, CustomerService customers, PricingSupport pricing,
    InstallmentSupport installments)
{
    // =====================================================================================================
    // agreement ledger
    // =====================================================================================================
    public async Task<AgreementLedgerDto> GetAgreementLedgerAsync(long agreementId, LedgerPageRequest? page = null, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.LedgersView, ct);
        await support.EnsureAgreementAccessAsync(agreementId, ct);
        await support.EnsureLedgerCurrentAsync(agreementId, ct);
        return await BuildAgreementLedgerAsync(agreementId, page ?? new LedgerPageRequest(), ct);
    }

    internal async Task<AgreementLedgerDto> BuildAgreementLedgerAsync(long agreementId, LedgerPageRequest page, CancellationToken ct)
    {
        var ag = await support.InfoAsync(agreementId, ct);
        var holder = await db.Customers.AsNoTracking().Where(c => c.Id == ag.HolderCustomerId).Select(c => new { c.CustomerCode, c.FullName }).FirstAsync(ct);
        var properties = await (from bp in db.BookingProperties.AsNoTracking()
                                join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                                where bp.BookingId == ag.BookingId
                                orderby p.PropertyCode
                                select p.PropertyCode).ToListAsync(ct);
        var summary = await SummaryAsync(ag, ct);
        var q = db.AgreementLedgerEntries.AsNoTracking().Where(e => e.AgreementId == agreementId);
        var entries = await PageAsync(q.Select(e => new Row { Id = e.Id, EntryDate = e.EntryDate, PostedAtUtc = e.PostedAtUtc, EntryType = e.EntryType, Description = e.Description, Debit = e.Debit, Credit = e.Credit, AgreementId = e.AgreementId, PaymentId = e.PaymentId, RefundId = e.RefundId }), page, ct);
        return new AgreementLedgerDto(ag.AgreementId, ag.AgreementNumber, ag.BookingNumber, ag.ProjectId, ag.ProjectCode, ag.Status, ag.HolderCustomerId, holder.CustomerCode, holder.FullName,
            await support.CurrencySymbolAsync(ct), properties, summary, entries);
    }

    /// <summary>Figures of one agreement (the ledger must be current - see <see cref="PaymentSupport.EnsureLedgerCurrentAsync"/>).</summary>
    internal async Task<AgreementLedgerSummaryDto> SummaryAsync(PaymentSupport.AgreementInfo ag, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().Where(b => b.Id == ag.BookingId).Select(b => new { b.TotalPrice, b.TotalExtraDiscount, b.TotalCharges, b.TotalPayable }).FirstAsync(ct);
        var charges = await db.AgreementCharges.AsNoTracking().Where(c => c.AgreementId == ag.AgreementId).ToListAsync(ct);
        var extraCharges = charges.Where(c => c.Status == AgreementChargeStatus.Active && c.Category != ChargeCategory.SaleConsideration).Sum(c => c.Amount);
        var items = await support.ActiveItemsAsync(ag.AgreementId, tracked: false, ct);
        var today = await support.TodayAsync(ct);
        var sums = await db.AgreementLedgerEntries.AsNoTracking().Where(e => e.AgreementId == ag.AgreementId).GroupBy(e => e.EntryType)
            .Select(g => new { g.Key, Debit = g.Sum(e => e.Debit), Credit = g.Sum(e => e.Credit) }).ToListAsync(ct);
        decimal Net(params LedgerEntryType[] types) => sums.Where(s => types.Contains(s.Key)).Sum(s => s.Debit - s.Credit);
        var dueToDate = Net(LedgerEntryType.InstallmentDue, LedgerEntryType.DueAdjustment);
        var received = -Net(LedgerEntryType.PaymentReceived);
        var reversed = Net(LedgerEntryType.PaymentReversed);
        var waivers = -Net(LedgerEntryType.Waiver, LedgerEntryType.WaiverAdjustment);
        var refunds = Net(LedgerEntryType.RefundPaid);
        var balance = sums.Sum(s => s.Debit - s.Credit);

        var posted = await db.Payments.AsNoTracking().Where(p => p.AgreementId == ag.AgreementId && p.Status == PaymentStatus.Posted).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var allocated = await (from a in db.PaymentAllocations.AsNoTracking()
                               join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                               where a.AgreementId == ag.AgreementId && a.Status == AllocationStatus.Active && p.Status == PaymentStatus.Posted
                               group a by a.Kind into g
                               select new { g.Key, Sum = g.Sum(a => a.Amount) }).ToListAsync(ct);
        var advanceHeld = allocated.Where(a => a.Key == AllocationKind.Advance).Sum(a => a.Sum);
        var unallocated = posted - allocated.Sum(a => a.Sum);

        var outstanding = items.Where(i => i.DueDate <= today).Sum(i => i.Outstanding);
        var overdue = items.Where(i => i.DueDate < today).Sum(i => i.Outstanding);
        var notYetDue = items.Where(i => i.DueDate > today).Sum(i => i.Outstanding);
        var futurePaid = items.Where(i => i.DueDate > today).Sum(i => i.AmountPaid);
        var advance = futurePaid + advanceHeld;

        var penalties = 0m;
        if (items.Count > 0)
        {
            var chargeMap = charges.ToDictionary(c => c.Id);
            var ruleIds = charges.Where(c => c.LateFeeRuleId != null).Select(c => c.LateFeeRuleId!.Value).Distinct().ToList();
            var rules = await db.LateFeeRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            var policy = await installments.PolicyAsync(ct);
            penalties = items.Sum(i => InstallmentSupport.Line(i, chargeMap, rules, today, policy).LateFeeDue);
        }
        var reconciled = ag.Status == AgreementStatus.Active
            ? balance == outstanding - advance - unallocated
            : dueToDate == 0m && waivers == 0m && balance == -(received - reversed - refunds);
        return new AgreementLedgerSummaryDto(booking.TotalPrice, booking.TotalExtraDiscount, booking.TotalCharges + extraCharges, booking.TotalPayable + extraCharges,
            items.Sum(i => i.Amount), dueToDate, received, reversed, waivers, penalties, refunds, ag.Status == AgreementStatus.Active ? outstanding : 0m,
            ag.Status == AgreementStatus.Active ? overdue : 0m, ag.Status == AgreementStatus.Active ? notYetDue : 0m, advance, unallocated, balance, reconciled);
    }

    // =====================================================================================================
    // customer consolidated ledger
    // =====================================================================================================
    public async Task<CustomerLedgerDto> GetCustomerLedgerAsync(long customerId, LedgerPageRequest? page = null, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.LedgersView, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.CustomerCode, c.FullName }).FirstAsync(ct);
        var projects = await support.ProjectsAsync(ct);
        var agreementIds = await (from a in db.CustomerAgreements.AsNoTracking()
                                  join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                                  join p in projects on b.ProjectId equals p.Id
                                  where db.BookingCustomers.Any(bc => bc.BookingId == b.Id && bc.CustomerId == customerId)
                                        || db.CustomerLedgerEntries.Any(e => e.AgreementId == a.Id && e.CustomerId == customerId)
                                  orderby a.Id
                                  select a.Id).ToListAsync(ct);
        foreach (var id in agreementIds) await support.EnsureLedgerCurrentAsync(id, ct);

        var balances = await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId && agreementIds.Contains(e.AgreementId))
            .GroupBy(e => e.AgreementId).Select(g => new { AgreementId = g.Key, Balance = g.Sum(e => e.Debit - e.Credit) }).ToDictionaryAsync(x => x.AgreementId, x => x.Balance, ct);
        var rows = new List<CustomerAgreementBalanceDto>();
        var reconciled = true;
        foreach (var id in agreementIds)
        {
            var ag = await support.InfoAsync(id, ct);
            var mine = balances.GetValueOrDefault(id);
            if (ag.HolderCustomerId == customerId)
            {
                var s = await SummaryAsync(ag, ct);
                reconciled &= s.Reconciled && mine == s.LedgerBalance;
                rows.Add(new CustomerAgreementBalanceDto(id, ag.AgreementNumber, ag.ProjectCode, ag.Status, true, mine, s.Outstanding, s.Overdue, s.AdvanceBalance + s.UnallocatedBalance));
            }
            else
            {
                reconciled &= mine == 0m; // the balance moved to the current holder
                rows.Add(new CustomerAgreementBalanceDto(id, ag.AgreementNumber, ag.ProjectCode, ag.Status, false, mine, 0m, 0m, 0m));
            }
        }
        var q = db.CustomerLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId && agreementIds.Contains(e.AgreementId));
        var totals = await q.GroupBy(e => 1).Select(g => new { Debit = g.Sum(e => e.Debit), Credit = g.Sum(e => e.Credit) }).FirstOrDefaultAsync(ct);
        var entries = await PageAsync(q.Select(e => new Row { Id = e.Id, EntryDate = e.EntryDate, PostedAtUtc = e.PostedAtUtc, EntryType = e.EntryType, Description = e.Description, Debit = e.Debit, Credit = e.Credit, AgreementId = e.AgreementId, PaymentId = e.PaymentId, RefundId = e.RefundId }),
            page ?? new LedgerPageRequest(), ct);
        return new CustomerLedgerDto(customerId, customer.CustomerCode, customer.FullName, await support.CurrencySymbolAsync(ct), totals?.Debit ?? 0m, totals?.Credit ?? 0m,
            (totals?.Debit ?? 0m) - (totals?.Credit ?? 0m), rows.Sum(r => r.Outstanding), rows.Sum(r => r.Overdue), rows.Sum(r => r.AdvanceBalance), reconciled, rows, entries);
    }

    // =====================================================================================================
    // property statement
    // =====================================================================================================
    /// <summary>Financial statement of a property: every booking that included it with its agreement ledger (amounts are agreement-level; other properties on the same agreement are listed).</summary>
    public async Task<PropertyStatementDto> GetPropertyStatementAsync(long propertyId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.LedgersView, ct);
        await pricing.EnsurePropertyAccessAsync(propertyId, ct);
        var prop = await (from p in db.Properties.AsNoTracking()
                          join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                          where p.Id == propertyId
                          select new { p.PropertyCode, pr.ProjectCode, p.Status }).FirstAsync(ct);
        var bookings = await (from bp in db.BookingProperties.AsNoTracking()
                              join b in db.Bookings.AsNoTracking() on bp.BookingId equals b.Id
                              where bp.PropertyId == propertyId
                              orderby b.Id descending
                              select new { b.Id, b.BookingNumber, b.Status, b.BookingDate, bp.LinePrice }).ToListAsync(ct);
        var result = new List<PropertyStatementBookingDto>();
        foreach (var b in bookings)
        {
            var others = await (from bp in db.BookingProperties.AsNoTracking()
                                join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                                where bp.BookingId == b.Id && bp.PropertyId != propertyId
                                select p.PropertyCode).ToListAsync(ct);
            var agreementId = await db.CustomerAgreements.AsNoTracking().Where(a => a.BookingId == b.Id).Select(a => (long?)a.Id).FirstOrDefaultAsync(ct);
            AgreementLedgerDto? ledger = null;
            if (agreementId is { } aid)
            {
                await support.EnsureLedgerCurrentAsync(aid, ct);
                ledger = await BuildAgreementLedgerAsync(aid, new LedgerPageRequest { PageSize = PagedRequest.MaxPageSize }, ct);
            }
            result.Add(new PropertyStatementBookingDto(b.Id, b.BookingNumber, b.Status, b.BookingDate, b.LinePrice, others, ledger));
        }
        return new PropertyStatementDto(propertyId, prop.PropertyCode, prop.ProjectCode, prop.Status, await support.CurrencySymbolAsync(ct), result);
    }

    // =====================================================================================================
    // paging with running balance
    // =====================================================================================================
    private sealed class Row
    {
        public long Id { get; init; }
        public DateOnly EntryDate { get; init; }
        public DateTime PostedAtUtc { get; init; }
        public LedgerEntryType EntryType { get; init; }
        public string Description { get; init; } = default!;
        public decimal Debit { get; init; }
        public decimal Credit { get; init; }
        public long AgreementId { get; init; }
        public long? PaymentId { get; init; }
        public long? RefundId { get; init; }
    }

    private async Task<PagedResult<LedgerEntryDto>> PageAsync(IQueryable<Row> q, LedgerPageRequest page, CancellationToken ct)
    {
        var ordered = q.OrderBy(e => e.EntryDate).ThenBy(e => e.Id);
        var total = await q.CountAsync(ct);
        var skip = (page.Page - 1) * page.PageSize;
        var opening = skip == 0 ? 0m : await ordered.Take(skip).SumAsync(e => e.Debit - e.Credit, ct);
        var rows = await ordered.Skip(skip).Take(page.PageSize).ToListAsync(ct);
        var agreementIds = rows.Select(r => r.AgreementId).Distinct().ToList();
        var paymentIds = rows.Where(r => r.PaymentId != null).Select(r => r.PaymentId!.Value).Distinct().ToList();
        var refundIds = rows.Where(r => r.RefundId != null).Select(r => r.RefundId!.Value).Distinct().ToList();
        var agreements = await db.CustomerAgreements.AsNoTracking().Where(a => agreementIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.AgreementNumber, ct);
        var payments = await db.Payments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PaymentNumber, ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => refundIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RefundNumber, ct);
        var running = opening;
        var list = rows.Select(r =>
        {
            running += r.Debit - r.Credit;
            return new LedgerEntryDto(r.Id, r.EntryDate, r.PostedAtUtc, r.EntryType, r.Description, r.Debit, r.Credit, running, r.AgreementId, agreements.GetValueOrDefault(r.AgreementId),
                r.PaymentId, r.PaymentId is { } pid ? payments.GetValueOrDefault(pid) : null, r.RefundId, r.RefundId is { } rid ? refunds.GetValueOrDefault(rid) : null);
        }).ToList();
        return new PagedResult<LedgerEntryDto>(list, page.Page, page.PageSize, total);
    }
}
