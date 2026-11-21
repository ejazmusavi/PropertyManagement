using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Reporting;

/// <summary>The rows of one report before they are wrapped into a <see cref="ReportTableDto"/>.</summary>
public sealed record ReportBuild(IReadOnlyList<ReportColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals, int TotalRows, IReadOnlyList<string> Notes,
    bool Truncated = false, string? Subtitle = null);

/// <summary>
/// Customer, sales and collection reports (spec 9.3-9.5). Every figure comes from the business tables that Phases 4-7 maintain (bookings, payments, installment schedules, ledgers,
/// expenses), restricted to the caller's projects and, for customer reports, to customers the caller may see. Detail reports page in the database; grouped reports are small
/// aggregates and are paged in memory. Totals always cover the whole result.
/// </summary>
public class OperationalReportService(IAppDbContext db, ReportingSupport s)
{
    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private static ReportColumnDto C(string key, string header, ReportColumnKind kind = ReportColumnKind.Text) => new(key, header, kind);
    private const ReportColumnKind Money = ReportColumnKind.Money, Num = ReportColumnKind.Number, Dt = ReportColumnKind.Date, Pct = ReportColumnKind.Percent;

    private static (int Page, int Size) PageOf(ReportFilters f) => (Math.Max(1, f.Page), Math.Clamp(f.PageSize <= 0 ? 50 : f.PageSize, 1, ReportLimits.MaxPageSize));

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(IEnumerable<object?[]> rows) => rows.Select(r => (IReadOnlyList<object?>)r).ToList();

    /// <summary>Pages an in-memory aggregate (or returns everything for an export).</summary>
    private static ReportBuild Grouped(ReportColumnDto[] cols, List<object?[]> rows, object?[]? totals, ReportFilters f, bool export, params string[] notes)
    {
        var (page, size) = PageOf(f);
        var shown = export ? rows.Take(ReportLimits.MaxExportRows).ToList() : rows.Skip((page - 1) * size).Take(size).ToList();
        return new ReportBuild(cols, Rows(shown), totals, rows.Count, notes, export && rows.Count > ReportLimits.MaxExportRows);
    }

    /// <summary>Pages a detail query in the database (or returns up to the export limit).</summary>
    private static async Task<(List<T> Items, int Total, bool Truncated)> PageQueryAsync<T>(IQueryable<T> ordered, ReportFilters f, bool export, CancellationToken ct)
    {
        var total = await ordered.CountAsync(ct);
        var (page, size) = PageOf(f);
        var items = export ? await ordered.Take(ReportLimits.MaxExportRows).ToListAsync(ct) : await ordered.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return (items, total, export && total > ReportLimits.MaxExportRows);
    }

    private static object?[] R(object? first, object?[] rest) => [first, .. rest];

    private static string Pad(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v;

    private async Task<Dictionary<long, string>> ProjectNamesAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ProjectCode, ct);

    private async Task<Dictionary<long, string>> PrimaryCustomersAsync(IEnumerable<long> bookingIds, CancellationToken ct)
    {
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await (from bc in db.BookingCustomers.AsNoTracking() join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                          where ids.Contains(bc.BookingId) && bc.IsActive && bc.IsPrimary select new { bc.BookingId, c.FullName }).ToListAsync(ct);
        return rows.GroupBy(r => r.BookingId).ToDictionary(g => g.Key, g => g.First().FullName);
    }

    private async Task<Dictionary<long, string>> PropertyCodesByBookingAsync(IEnumerable<long> bookingIds, CancellationToken ct)
    {
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await (from bp in db.BookingProperties.AsNoTracking() join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                          where ids.Contains(bp.BookingId) orderby p.PropertyCode select new { bp.BookingId, p.PropertyCode }).ToListAsync(ct);
        return rows.GroupBy(r => r.BookingId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.PropertyCode)));
    }

    private sealed class ItemRow
    {
        public long ItemId { get; init; }
        public long AgreementId { get; init; }
        public string AgreementNumber { get; init; } = default!;
        public long BookingId { get; init; }
        public long ProjectId { get; init; }
        public string Description { get; init; } = default!;
        public DateOnly DueDate { get; init; }
        public decimal Amount { get; init; }
        public decimal Paid { get; init; }
        public decimal Waived { get; init; }
        public ScheduleItemStatus Status { get; init; }
        public DateOnly? LastPaymentDate { get; init; }
        public int LineNo { get; init; }
    }

    /// <summary>Lines of the active schedules of active agreements whose booking is in <paramref name="bookings"/>.</summary>
    private IQueryable<ItemRow> Items(IQueryable<Booking> bookings) =>
        from i in db.PaymentScheduleItems.AsNoTracking()
        join sc in db.PaymentSchedules.AsNoTracking() on i.ScheduleId equals sc.Id
        join a in db.CustomerAgreements.AsNoTracking() on sc.ActiveAgreementId equals a.Id
        join b in bookings on a.BookingId equals b.Id
        where a.Status == AgreementStatus.Active
        select new ItemRow
        {
            ItemId = i.Id, AgreementId = a.Id, AgreementNumber = a.AgreementNumber, BookingId = b.Id, ProjectId = b.ProjectId, Description = i.Description, DueDate = i.DueDate,
            Amount = i.Amount, Paid = i.AmountPaid, Waived = i.AmountWaived, Status = i.Status, LastPaymentDate = i.LastPaymentDate, LineNo = i.LineNo,
        };

    // =====================================================================================================
    // 9.3 customer reports
    // =====================================================================================================
    private async Task<long> RequireCustomerAsync(ReportFilters f, CancellationToken ct)
    {
        var id = f.CustomerId ?? throw ReportingSupport.Invalid(nameof(f.CustomerId), "Choose a customer.");
        await s.EnsureCustomerAsync(id, ct);
        return id;
    }

    private async Task<IQueryable<CustomerLedgerEntry>> LedgerAsync(ReportFilters f, long customerId, CancellationToken ct)
    {
        var agreements = await s.AgreementIdsAsync(f, false, ct);
        var projects = await s.ProjectIdsAsync(f.ProjectId, ct);
        var q = db.CustomerLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId && projects.Contains(e.ProjectId) && agreements.Contains(e.AgreementId));
        if (ReportingSupport.ParseEnum<LedgerEntryType>(f.TransactionType) is { } t) q = q.Where(e => e.EntryType == t);
        return q;
    }

    /// <summary>Statement per agreement for the period: opening balance, debits, credits and closing balance (customer ledger, spec 6.5).</summary>
    public async Task<ReportBuild> CustomerStatementAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var cid = await RequireCustomerAsync(f, ct);
        var q = await LedgerAsync(f, cid, ct);
        var today = await s.TodayAsync(ct);
        var to = f.To ?? today;
        var opening = f.From is { } from ? await q.Where(e => e.EntryDate < from).GroupBy(e => e.AgreementId).Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct) : [];
        var period = await q.Where(e => e.EntryDate <= to && (f.From == null || e.EntryDate >= f.From)).GroupBy(e => e.AgreementId)
            .Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var ids = opening.Select(x => x.Key).Union(period.Select(x => x.Key)).ToList();
        var info = await (from a in db.CustomerAgreements.AsNoTracking() join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                          where ids.Contains(a.Id) select new { a.Id, a.AgreementNumber, b.ProjectId, a.BookingId }).ToListAsync(ct);
        var codes = await PropertyCodesByBookingAsync(info.Select(i => i.BookingId), ct);
        var projects = await ProjectNamesAsync(ct);
        var rows = new List<object?[]>();
        decimal to0 = 0, td = 0, tc = 0, tcl = 0;
        foreach (var a in info.OrderBy(i => i.AgreementNumber))
        {
            var o = opening.FirstOrDefault(x => x.Key == a.Id); var p = period.FirstOrDefault(x => x.Key == a.Id);
            var ob = (o?.D ?? 0m) - (o?.C ?? 0m); var d = p?.D ?? 0m; var c = p?.C ?? 0m; var cl = ob + d - c;
            rows.Add([a.AgreementNumber, projects.GetValueOrDefault(a.ProjectId, "-"), codes.GetValueOrDefault(a.BookingId, "-"), ob, d, c, cl]);
            to0 += ob; td += d; tc += c; tcl += cl;
        }
        return Grouped([C("agreement", "Agreement"), C("project", "Project"), C("properties", "Properties"), C("opening", "Opening balance", Money), C("debits", "Debits", Money),
            C("credits", "Credits", Money), C("closing", "Closing balance", Money)], rows, ["Total", "", "", to0, td, tc, tcl], f, export,
            "Positive balance = owed by the customer; negative = customer credit (advance or unallocated money).");
    }

    /// <summary>Chronological customer ledger with a running balance.</summary>
    public async Task<ReportBuild> CustomerLedgerAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var cid = await RequireCustomerAsync(f, ct);
        var q = await LedgerAsync(f, cid, ct);
        var to = f.To ?? await s.TodayAsync(ct);
        var inRange = q.Where(e => e.EntryDate <= to && (f.From == null || e.EntryDate >= f.From)).OrderBy(e => e.EntryDate).ThenBy(e => e.Id);
        var openingNet = f.From is { } from ? await q.Where(e => e.EntryDate < from).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m : 0m;
        var (page, size) = PageOf(f);
        var skip = export ? 0 : (page - 1) * size;
        var before = skip == 0 ? 0m : await inRange.Take(skip).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0m;
        var (items, total, truncated) = await PageQueryAsync(inRange.Select(e => new { e.EntryDate, e.EntryType, e.AgreementId, e.Description, e.Debit, e.Credit, e.Id }), f, export, ct);
        var agIds = items.Select(i => i.AgreementId).Distinct().ToList();
        var agreementNumbers = await db.CustomerAgreements.AsNoTracking().Where(a => agIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.AgreementNumber, ct);
        var totals = await inRange.GroupBy(_ => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
        var running = openingNet + before;
        var rows = new List<object?[]>();
        foreach (var e in items)
        {
            running += e.Debit - e.Credit;
            rows.Add([e.EntryDate, EnumLabel(e.EntryType), agreementNumbers.GetValueOrDefault(e.AgreementId, "-"), e.Description, e.Debit, e.Credit, running]);
        }
        var d = totals?.D ?? 0m; var c = totals?.C ?? 0m;
        return new ReportBuild([C("date", "Date", Dt), C("type", "Type"), C("agreement", "Agreement"), C("description", "Description"), C("debit", "Debit", Money), C("credit", "Credit", Money),
            C("balance", "Balance", Money)], Rows(rows), ["Total", "", "", "", d, c, openingNet + d - c], total, [$"Opening balance: {openingNet:N2}. Positive balance = owed by the customer."], truncated);
    }

    /// <summary>Every property a customer holds a share in (booking parties), with the ownership share.</summary>
    public async Task<ReportBuild> CustomerPropertiesAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        if (f.CustomerId is { } cid) await s.EnsureCustomerAsync(cid, ct);
        var bookings = await s.BookingsAsync(f, false, ct);
        var status = ReportingSupport.ParseEnum<BookingStatus>(f.Status) ?? BookingStatus.Confirmed;
        var q = from bc in db.BookingCustomers.AsNoTracking()
                join b in bookings on bc.BookingId equals b.Id
                join bp in db.BookingProperties.AsNoTracking() on b.Id equals bp.BookingId
                join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                join t in db.PropertyTypes.AsNoTracking() on p.PropertyTypeId equals t.Id
                join c in db.Customers.AsNoTracking() on bc.CustomerId equals c.Id
                join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id
                where bc.IsActive && b.Status == status && (f.CustomerId == null || bc.CustomerId == f.CustomerId)
                      && (f.From == null || b.BookingDate >= f.From) && (f.To == null || b.BookingDate <= f.To)
                orderby c.FullName, b.BookingNumber, p.PropertyCode
                select new { c.CustomerCode, c.FullName, pr.ProjectCode, p.PropertyCode, TypeName = t.Name, p.BlockId, p.Size, p.MeasurementUnit, b.BookingNumber, b.BookingDate, bc.SharePercent, bp.LinePrice };
        var (items, total, truncated) = await PageQueryAsync(q, f, export, ct);
        var blocks = await db.ProjectBlocks.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var rows = items.Select(x => new object?[] { x.CustomerCode + " " + x.FullName, x.ProjectCode, x.BlockId is { } bid ? blocks.GetValueOrDefault(bid, "-") : "-", x.PropertyCode, x.TypeName,
            x.Size, EnumLabel(x.MeasurementUnit), x.BookingNumber, x.BookingDate, x.SharePercent, x.LinePrice }).ToList();
        var totalPrice = await q.SumAsync(x => (decimal?)x.LinePrice, ct) ?? 0m;
        return new ReportBuild([C("customer", "Customer"), C("project", "Project"), C("block", "Block"), C("property", "Property"), C("type", "Type"), C("size", "Size", Num), C("unit", "Unit"),
            C("booking", "Booking"), C("date", "Booking date", Dt), C("share", "Share %", Pct), C("price", "Property price", Money)], Rows(rows),
            ["Total", "", "", "", "", null, "", "", null, null, totalPrice], total, [$"Bookings with status {status}. Price is the full property price; the share % is the customer's part."], truncated);
    }

    /// <summary>Payments recorded for the customer's bookings (all statuses unless filtered).</summary>
    public async Task<ReportBuild> PaymentHistoryAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await PaymentsAsync(f, null, ct);
        var ordered = from p in q join pr in db.Projects.AsNoTracking() on p.ProjectId equals pr.Id
                      join c in db.Customers.AsNoTracking() on p.CustomerId equals c.Id
                      join a in db.CustomerAgreements.AsNoTracking() on p.AgreementId equals a.Id
                      join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                      orderby p.PaymentDate descending, p.Id descending
                      select new { p.PaymentNumber, p.PaymentDate, c.FullName, a.AgreementNumber, pr.ProjectCode, MethodName = m.Name, p.Reference, p.Amount, p.Status };
        var (items, total, truncated) = await PageQueryAsync(ordered, f, export, ct);
        var rows = items.Select(x => new object?[] { x.PaymentNumber, x.PaymentDate, x.FullName, x.AgreementNumber, x.ProjectCode, x.MethodName, Pad(x.Reference), x.Amount, x.Status.ToString() }).ToList();
        var posted = await q.Where(p => p.Status == PaymentStatus.Posted).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        return new ReportBuild([C("payment", "Payment"), C("date", "Date", Dt), C("customer", "Customer"), C("agreement", "Agreement"), C("project", "Project"), C("method", "Method"),
            C("reference", "Reference"), C("amount", "Amount", Money), C("status", "Status")], Rows(rows), ["Posted total", null, "", "", "", "", "", posted, ""], total,
            ["The total counts posted payments only; reversed, rejected and pending payments are listed but not summed."], truncated);
    }

    /// <summary>Installment lines of the customer's active schedules with their payment status.</summary>
    public async Task<ReportBuild> InstallmentHistoryAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var bookings = await s.BookingsAsync(f, true, ct);
        var items = Items(bookings);
        if (f.From is { } from) items = items.Where(i => i.DueDate >= from);
        if (f.To is { } to) items = items.Where(i => i.DueDate <= to);
        if (ReportingSupport.ParseEnum<ScheduleItemStatus>(f.Status) is { } st) items = items.Where(i => i.Status == st);
        var ordered = items.OrderBy(i => i.DueDate).ThenBy(i => i.AgreementId).ThenBy(i => i.LineNo);
        var (page, total, truncated) = await PageQueryAsync(ordered, f, export, ct);
        var rows = page.Select(i => new object?[] { i.AgreementNumber, i.Description, i.DueDate, i.Amount, i.Paid, i.Waived, i.Amount - i.Paid - i.Waived, EnumLabel(i.Status), i.LastPaymentDate }).ToList();
        var sums = await items.GroupBy(_ => 1).Select(g => new { A = g.Sum(x => x.Amount), P = g.Sum(x => x.Paid), W = g.Sum(x => x.Waived) }).FirstOrDefaultAsync(ct);
        var a = sums?.A ?? 0m; var p = sums?.P ?? 0m; var w = sums?.W ?? 0m;
        return new ReportBuild([C("agreement", "Agreement"), C("installment", "Installment"), C("due", "Due date", Dt), C("amount", "Amount", Money), C("paid", "Paid", Money), C("waived", "Waived", Money),
            C("outstanding", "Outstanding", Money), C("status", "Status"), C("last", "Last payment", Dt)], Rows(rows), ["Total", "", null, a, p, w, a - p - w, "", null], total,
            ["Active schedule versions of active agreements. Superseded versions are in the agreement's schedule history."], truncated);
    }

    /// <summary>Outstanding balance per agreement as of the end date: due now, overdue and not yet due.</summary>
    public async Task<ReportBuild> OutstandingBalanceAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var asOf = f.To ?? await s.TodayAsync(ct);
        var bookings = await s.BookingsAsync(f, true, ct);
        var items = Items(bookings);
        var grouped = await items.GroupBy(i => new { i.AgreementId, i.AgreementNumber, i.BookingId, i.ProjectId }).Select(g => new
        {
            g.Key.AgreementId, g.Key.AgreementNumber, g.Key.BookingId, g.Key.ProjectId,
            Total = g.Sum(x => x.Amount), Paid = g.Sum(x => x.Paid), Waived = g.Sum(x => x.Waived),
            Due = g.Sum(x => x.DueDate <= asOf ? x.Amount - x.Paid - x.Waived : 0m),
            Overdue = g.Sum(x => x.DueDate < asOf ? x.Amount - x.Paid - x.Waived : 0m),
            Open = g.Sum(x => x.Amount - x.Paid - x.Waived),
        }).ToListAsync(ct);
        var customers = await PrimaryCustomersAsync(grouped.Select(g => g.BookingId), ct);
        var projects = await ProjectNamesAsync(ct);
        var list = grouped.Where(g => g.Open > 0m).OrderByDescending(g => g.Overdue).ThenBy(g => g.AgreementNumber).ToList();
        var rows = list.Select(g => new object?[] { customers.GetValueOrDefault(g.BookingId, "-"), g.AgreementNumber, projects.GetValueOrDefault(g.ProjectId, "-"), g.Total, g.Paid, g.Waived, g.Due, g.Overdue, g.Open - g.Due, g.Open }).ToList();
        return Grouped([C("customer", "Customer"), C("agreement", "Agreement"), C("project", "Project"), C("total", "Schedule total", Money), C("paid", "Paid", Money), C("waived", "Waived", Money),
            C("due", "Due to date", Money), C("overdue", "Overdue", Money), C("notdue", "Not yet due", Money), C("outstanding", "Total outstanding", Money)], rows,
            ["Total", "", "", list.Sum(g => g.Total), list.Sum(g => g.Paid), list.Sum(g => g.Waived), list.Sum(g => g.Due), list.Sum(g => g.Overdue), list.Sum(g => g.Open - g.Due), list.Sum(g => g.Open)], f, export,
            $"As of {asOf:yyyy-MM-dd}. Due to date = installments due on or before the date and not yet paid; overdue = due before the date.");
    }

    /// <summary>Overdue installment lines (due before the end date, outstanding &gt; 0) with days overdue.</summary>
    public async Task<ReportBuild> OverdueAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var asOf = f.To ?? await s.TodayAsync(ct);
        var bookings = await s.BookingsAsync(f, true, ct);
        var items = Items(bookings).Where(i => i.DueDate < asOf && i.Amount - i.Paid - i.Waived > 0m);
        if (f.From is { } from) items = items.Where(i => i.DueDate >= from);
        var ordered = items.OrderBy(i => i.DueDate).ThenBy(i => i.AgreementId).ThenBy(i => i.LineNo);
        var (page, total, truncated) = await PageQueryAsync(ordered, f, export, ct);
        var customers = await PrimaryCustomersAsync(page.Select(i => i.BookingId), ct);
        var projects = await ProjectNamesAsync(ct);
        var rows = page.Select(i =>
        {
            var days = asOf.DayNumber - i.DueDate.DayNumber;
            return new object?[] { customers.GetValueOrDefault(i.BookingId, "-"), i.AgreementNumber, projects.GetValueOrDefault(i.ProjectId, "-"), i.Description, i.DueDate, days, Aging(days), i.Amount - i.Paid - i.Waived };
        }).ToList();
        var sum = await items.SumAsync(i => (decimal?)(i.Amount - i.Paid - i.Waived), ct) ?? 0m;
        return new ReportBuild([C("customer", "Customer"), C("agreement", "Agreement"), C("project", "Project"), C("installment", "Installment"), C("due", "Due date", Dt),
            C("days", "Days overdue", Num), C("aging", "Aging"), C("outstanding", "Outstanding", Money)], Rows(rows), ["Total", "", "", "", null, null, "", sum], total,
            [$"Lines due before {asOf:yyyy-MM-dd} with an outstanding amount. Late fees are shown on the recovery screens and are not part of this amount."], truncated);
    }

    private static string Aging(int days) => days switch { <= 30 => "1-30 days", <= 60 => "31-60 days", <= 90 => "61-90 days", _ => "Over 90 days" };

    // =====================================================================================================
    // 9.4 sales reports
    // =====================================================================================================
    /// <summary>Bookings counted as sales: confirmed unless a status is chosen; booking date in range; optional cash/installment filter.</summary>
    private async Task<IQueryable<Booking>> SalesAsync(ReportFilters f, CancellationToken ct)
    {
        var q = await s.BookingsAsync(f, true, ct);
        var status = ReportingSupport.ParseEnum<BookingStatus>(f.Status) ?? BookingStatus.Confirmed;
        q = q.Where(b => b.Status == status);
        if (f.From is { } from) q = q.Where(b => b.BookingDate >= from);
        if (f.To is { } to) q = q.Where(b => b.BookingDate <= to);
        if (ReportingSupport.ParseEnum<PaymentMode>(f.TransactionType) is { } m) q = q.Where(b => b.PaymentMode == m);
        return q;
    }

    private static readonly ReportColumnDto[] SalesTail = [C("bookings", "Bookings", Num), C("properties", "Properties", Num), C("price", "Gross price", Money), C("discount", "Extra discount", Money),
        C("charges", "Charges", Money), C("payable", "Total payable", Money)];

    private sealed record SalesAgg(int N, int Props, decimal Price, decimal Discount, decimal Charges, decimal Payable);

    private static object?[] SalesTotals(string label, int blanks, IEnumerable<SalesAgg> a)
    {
        var l = a.ToList();
        return [label, .. Enumerable.Repeat<object?>("", blanks), l.Sum(x => x.N), l.Sum(x => x.Props), l.Sum(x => x.Price), l.Sum(x => x.Discount), l.Sum(x => x.Charges), l.Sum(x => x.Payable)];
    }

    private static object?[] SalesCells(SalesAgg a) => [a.N, a.Props, a.Price, a.Discount, a.Charges, a.Payable];

    private const string SalesNote = "Sales = bookings with the chosen status (confirmed by default), by booking date. Total payable = price - extra discount + booking charges.";

    private async Task<List<(DateOnly Date, SalesAgg Agg)>> DailySalesAsync(IQueryable<Booking> q, CancellationToken ct)
    {
        var bk = await q.GroupBy(b => b.BookingDate).Select(g => new { Key = g.Key, N = g.Count(), Price = g.Sum(b => b.TotalPrice), Disc = g.Sum(b => b.TotalExtraDiscount), Ch = g.Sum(b => b.TotalCharges), Pay = g.Sum(b => b.TotalPayable) }).ToListAsync(ct);
        var pc = await (from bp in db.BookingProperties.AsNoTracking() join b in q on bp.BookingId equals b.Id group bp by b.BookingDate into g select new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return bk.OrderBy(x => x.Key).Select(x => (x.Key, new SalesAgg(x.N, pc.GetValueOrDefault(x.Key), x.Price, x.Disc, x.Ch, x.Pay))).ToList();
    }

    public async Task<ReportBuild> DailySalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var daily = await DailySalesAsync(await SalesAsync(f, ct), ct);
        var rows = daily.Select(d => R(d.Date, SalesCells(d.Agg))).ToList();
        return Grouped([C("date", "Date", Dt), .. SalesTail], rows, SalesTotals("Total", 0, daily.Select(d => d.Agg)), f, export, SalesNote);
    }

    public async Task<ReportBuild> MonthlySalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var daily = await DailySalesAsync(await SalesAsync(f, ct), ct);
        var months = daily.GroupBy(d => new { d.Date.Year, d.Date.Month }).OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => (Label: $"{g.Key.Year:D4}-{g.Key.Month:D2}", Agg: new SalesAgg(g.Sum(x => x.Agg.N), g.Sum(x => x.Agg.Props), g.Sum(x => x.Agg.Price), g.Sum(x => x.Agg.Discount), g.Sum(x => x.Agg.Charges), g.Sum(x => x.Agg.Payable)))).ToList();
        var rows = months.Select(m => R(m.Label, SalesCells(m.Agg))).ToList();
        return Grouped([C("month", "Month"), .. SalesTail], rows, SalesTotals("Total", 0, months.Select(m => m.Agg)), f, export, SalesNote);
    }

    public async Task<ReportBuild> ProjectSalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await SalesAsync(f, ct);
        var bk = await q.GroupBy(b => b.ProjectId).Select(g => new { Key = g.Key, N = g.Count(), Price = g.Sum(b => b.TotalPrice), Disc = g.Sum(b => b.TotalExtraDiscount), Ch = g.Sum(b => b.TotalCharges), Pay = g.Sum(b => b.TotalPayable) }).ToListAsync(ct);
        var pc = await (from bp in db.BookingProperties.AsNoTracking() join b in q on bp.BookingId equals b.Id group bp by b.ProjectId into g select new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var names = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ProjectCode + " " + p.ProjectName, ct);
        var list = bk.Select(x => (Name: names.GetValueOrDefault(x.Key, "-"), Agg: new SalesAgg(x.N, pc.GetValueOrDefault(x.Key), x.Price, x.Disc, x.Ch, x.Pay))).OrderBy(x => x.Name).ToList();
        return Grouped([C("project", "Project"), .. SalesTail], list.Select(x => R(x.Name, SalesCells(x.Agg))).ToList(), SalesTotals("Total", 0, list.Select(x => x.Agg)), f, export, SalesNote);
    }

    public async Task<ReportBuild> BlockSalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await SalesAsync(f, ct);
        var g = await (from bp in db.BookingProperties.AsNoTracking() join b in q on bp.BookingId equals b.Id join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                       group bp by new { p.ProjectId, p.BlockId } into x
                       select new { x.Key.ProjectId, x.Key.BlockId, N = x.Count(), Price = x.Sum(v => v.LinePrice), Disc = x.Sum(v => v.ExtraDiscount) }).ToListAsync(ct);
        var projects = await ProjectNamesAsync(ct);
        var blocks = await db.ProjectBlocks.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code + " " + b.Name, ct);
        var list = g.Select(x => (Project: projects.GetValueOrDefault(x.ProjectId, "-"), Block: x.BlockId is { } id ? blocks.GetValueOrDefault(id, "-") : "(no block)", x.N, x.Price, x.Disc))
            .OrderBy(x => x.Project).ThenBy(x => x.Block).ToList();
        return Grouped([C("project", "Project"), C("block", "Block"), C("properties", "Properties sold", Num), C("price", "Gross price", Money), C("discount", "Extra discount", Money), C("net", "Net price", Money)],
            list.Select(x => new object?[] { x.Project, x.Block, x.N, x.Price, x.Disc, x.Price - x.Disc }).ToList(),
            ["Total", "", list.Sum(x => x.N), list.Sum(x => x.Price), list.Sum(x => x.Disc), list.Sum(x => x.Price - x.Disc)], f, export,
            "Property lines of the sales bookings, grouped by the property's block. Net price = line price - extra discount; booking charges are not split by block.");
    }

    public async Task<ReportBuild> CategorySalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await SalesAsync(f, ct);
        var g = await (from bp in db.BookingProperties.AsNoTracking() join b in q on bp.BookingId equals b.Id join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                       join t in db.PropertyTypes.AsNoTracking() on p.PropertyTypeId equals t.Id join c in db.PropertyCategories.AsNoTracking() on t.CategoryId equals c.Id
                       group bp by new { Category = c.Name, Type = t.Name } into x
                       select new { x.Key.Category, x.Key.Type, N = x.Count(), Price = x.Sum(v => v.LinePrice), Disc = x.Sum(v => v.ExtraDiscount) }).ToListAsync(ct);
        var list = g.OrderBy(x => x.Category).ThenBy(x => x.Type).ToList();
        return Grouped([C("category", "Category"), C("type", "Property type"), C("properties", "Properties sold", Num), C("price", "Gross price", Money), C("discount", "Extra discount", Money), C("net", "Net price", Money)],
            list.Select(x => new object?[] { x.Category, x.Type, x.N, x.Price, x.Disc, x.Price - x.Disc }).ToList(),
            ["Total", "", list.Sum(x => x.N), list.Sum(x => x.Price), list.Sum(x => x.Disc), list.Sum(x => x.Price - x.Disc)], f, export, "Property lines of the sales bookings by property category and type.");
    }

    public async Task<ReportBuild> SalespersonSalesReportAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await SalesAsync(f, ct);
        var bk = await q.GroupBy(b => b.CreatedBy).Select(g => new { Key = g.Key, N = g.Count(), Price = g.Sum(b => b.TotalPrice), Disc = g.Sum(b => b.TotalExtraDiscount), Ch = g.Sum(b => b.TotalCharges), Pay = g.Sum(b => b.TotalPayable) }).ToListAsync(ct);
        var pc = await (from bp in db.BookingProperties.AsNoTracking() join b in q on bp.BookingId equals b.Id group bp by b.CreatedBy into g select new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key ?? "", x => x.N, ct);
        var list = bk.Select(x => (Name: x.Key ?? "(unknown)", Agg: new SalesAgg(x.N, pc.GetValueOrDefault(x.Key ?? ""), x.Price, x.Disc, x.Ch, x.Pay))).OrderByDescending(x => x.Agg.Payable).ToList();
        return Grouped([C("salesperson", "Salesperson"), .. SalesTail], list.Select(x => R(x.Name, SalesCells(x.Agg))).ToList(), SalesTotals("Total", 0, list.Select(x => x.Agg)), f, export,
            SalesNote, "The salesperson is the user who created the booking (the system has no separate salesperson entity).");
    }

    /// <summary>Agent commissions: approved/paid expenses in the Agent commission category; the vendor is the agent (spec-issues #13).</summary>
    public async Task<ReportBuild> AgentCommissionsAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var projects = await s.ProjectIdsAsync(f.ProjectId, ct);
        var status = ReportingSupport.ParseEnum<ExpenseStatus>(f.Status) ?? ExpenseStatus.Approved;
        var q = from e in db.Expenses.AsNoTracking() join cat in db.ExpenseCategories.AsNoTracking() on e.CategoryId equals cat.Id
                where cat.Code == "AGENT_COMMISSION" && projects.Contains(e.ProjectId) && e.Status == status
                      && (f.From == null || e.ExpenseDate >= f.From) && (f.To == null || e.ExpenseDate <= f.To)
                select e;
        var ordered = from e in q join pr in db.Projects.AsNoTracking() on e.ProjectId equals pr.Id join v in db.Vendors.AsNoTracking() on e.VendorId equals (long?)v.Id into vs from v in vs.DefaultIfEmpty()
                      orderby e.ExpenseDate descending, e.Id descending
                      select new { e.ExpenseNumber, e.ExpenseDate, pr.ProjectCode, Agent = v == null ? null : v.Name, e.Description, e.Amount, e.AmountPaid };
        var (items, total, truncated) = await PageQueryAsync(ordered, f, export, ct);
        var rows = items.Select(x => new object?[] { x.ExpenseNumber, x.ExpenseDate, x.ProjectCode, x.Agent ?? "(no agent)", x.Description, x.Amount, x.AmountPaid, status == ExpenseStatus.Approved ? x.Amount - x.AmountPaid : 0m }).ToList();
        var sums = await q.GroupBy(_ => 1).Select(g => new { A = g.Sum(e => e.Amount), P = g.Sum(e => e.AmountPaid) }).FirstOrDefaultAsync(ct);
        var a = sums?.A ?? 0m; var p = sums?.P ?? 0m;
        return new ReportBuild([C("expense", "Expense"), C("date", "Date", Dt), C("project", "Project"), C("agent", "Agent (vendor)"), C("description", "Description"), C("amount", "Commission", Money),
            C("paid", "Paid", Money), C("outstanding", "Outstanding", Money)], Rows(rows), ["Total", null, "", "", "", a, p, status == ExpenseStatus.Approved ? a - p : 0m], total,
            ["There is no agent or commission-rate entity: commissions are expenses in the Agent commission category and the vendor is the agent (specification issue 13)."], truncated);
    }

    public async Task<ReportBuild> CancellationsAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var bookings = await s.BookingsAsync(f, true, ct);
        var q = bookings.Where(b => b.Status == BookingStatus.Cancelled && b.CancelledAtUtc != null);
        if (f.From is { } from) { var d = from.ToDateTime(TimeOnly.MinValue); q = q.Where(b => b.CancelledAtUtc >= d); }
        if (f.To is { } to) { var d = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(b => b.CancelledAtUtc < d); }
        if (ReportingSupport.ParseEnum<PaymentMode>(f.TransactionType) is { } m) q = q.Where(b => b.PaymentMode == m);
        var ordered = from b in q join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id orderby b.CancelledAtUtc descending, b.Id descending
                      select new { b.Id, b.BookingNumber, pr.ProjectCode, b.BookingDate, b.CancelledAtUtc, b.CancelledBy, b.CancelReason, b.TotalPayable };
        var (items, total, truncated) = await PageQueryAsync(ordered, f, export, ct);
        var ids = items.Select(i => i.Id).ToList();
        var customers = await PrimaryCustomersAsync(ids, ct);
        var codes = await PropertyCodesByBookingAsync(ids, ct);
        var refunds = await (from r in db.Refunds.AsNoTracking() join a in db.CustomerAgreements.AsNoTracking() on r.AgreementId equals a.Id
                             where ids.Contains(a.BookingId) && (r.Status == PaymentRefundStatus.Paid || r.Status == PaymentRefundStatus.Approved)
                             group r by new { a.BookingId, r.Status } into g select new { g.Key.BookingId, g.Key.Status, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
        var rows = items.Select(x => new object?[] { x.BookingNumber, customers.GetValueOrDefault(x.Id, "-"), x.ProjectCode, codes.GetValueOrDefault(x.Id, "-"), x.BookingDate, DateOnly.FromDateTime(x.CancelledAtUtc!.Value),
            Pad(x.CancelledBy), Pad(x.CancelReason), x.TotalPayable, refunds.Where(r => r.BookingId == x.Id).Sum(r => r.Sum) }).ToList();
        var sum = await q.SumAsync(b => (decimal?)b.TotalPayable, ct) ?? 0m;
        return new ReportBuild([C("booking", "Booking"), C("customer", "Customer"), C("project", "Project"), C("properties", "Properties"), C("date", "Booking date", Dt), C("cancelled", "Cancelled on", Dt),
            C("by", "Cancelled by"), C("reason", "Reason"), C("payable", "Booking value", Money), C("refunds", "Refunds approved / paid", Money)], Rows(rows),
            ["Total", "", "", "", null, null, "", "", sum, null], total, ["Cancelled bookings by cancellation date. Refunds are the approved or paid refunds of the cancelled agreement."], truncated);
    }

    // =====================================================================================================
    // 9.5 collection reports
    // =====================================================================================================
    /// <summary>Payments in the caller's scope: status (Posted by default for collections), payment date range, method and source filters.</summary>
    private async Task<IQueryable<Payment>> PaymentsAsync(ReportFilters f, PaymentStatus? defaultStatus, CancellationToken ct)
    {
        var projects = await s.ProjectIdsAsync(f.ProjectId, ct);
        var bookings = await s.BookingsAsync(f, true, ct);
        var q = db.Payments.AsNoTracking().Where(p => projects.Contains(p.ProjectId) && bookings.Any(b => b.Id == p.BookingId));
        var status = ReportingSupport.ParseEnum<PaymentStatus>(f.Status) ?? defaultStatus;
        if (status is { } st) q = q.Where(p => p.Status == st);
        if (f.From is { } from) q = q.Where(p => p.PaymentDate >= from);
        if (f.To is { } to) q = q.Where(p => p.PaymentDate <= to);
        if (f.PaymentMethodId is { } m) q = q.Where(p => p.PaymentMethodId == m);
        if (ReportingSupport.ParseEnum<PaymentSource>(f.TransactionType) is { } src) q = q.Where(p => p.Source == src);
        return q;
    }

    private const string CollectionNote = "Collections = posted payments (a status can be chosen) by payment date. Reversed payments are excluded from collections.";

    private sealed record CollAgg(int N, decimal Amount, decimal Cash, decimal Other);

    private static readonly ReportColumnDto[] CollTail = [C("payments", "Payments", Num), C("amount", "Amount", Money), C("cash", "Cash", Money), C("other", "Bank / online / other", Money)];

    private static object?[] CollCells(CollAgg a) => [a.N, a.Amount, a.Cash, a.Other];

    private static object?[] CollTotals(IEnumerable<CollAgg> a)
    {
        var l = a.ToList();
        return [l.Sum(x => x.N), l.Sum(x => x.Amount), l.Sum(x => x.Cash), l.Sum(x => x.Other)];
    }

    private async Task<List<(DateOnly Date, CollAgg Agg)>> DailyCollectionsAsync(ReportFilters f, CancellationToken ct)
    {
        var q = await PaymentsAsync(f, PaymentStatus.Posted, ct);
        var g = await q.GroupBy(p => p.PaymentDate).Select(x => new { x.Key, N = x.Count(), A = x.Sum(p => p.Amount), Cash = x.Sum(p => p.MethodKind == PaymentMethodKind.Cash ? p.Amount : 0m) }).ToListAsync(ct);
        return g.OrderBy(x => x.Key).Select(x => (x.Key, new CollAgg(x.N, x.A, x.Cash, x.A - x.Cash))).ToList();
    }

    public async Task<ReportBuild> DailyCollectionAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var d = await DailyCollectionsAsync(f, ct);
        return Grouped([C("date", "Date", Dt), .. CollTail], d.Select(x => R(x.Date, CollCells(x.Agg))).ToList(), ["Total", .. CollTotals(d.Select(x => x.Agg))], f, export, CollectionNote);
    }

    public async Task<ReportBuild> MonthlyCollectionAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var d = await DailyCollectionsAsync(f, ct);
        var months = d.GroupBy(x => new { x.Date.Year, x.Date.Month }).OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => (Label: $"{g.Key.Year:D4}-{g.Key.Month:D2}", Agg: new CollAgg(g.Sum(x => x.Agg.N), g.Sum(x => x.Agg.Amount), g.Sum(x => x.Agg.Cash), g.Sum(x => x.Agg.Other)))).ToList();
        return Grouped([C("month", "Month"), .. CollTail], months.Select(x => R(x.Label, CollCells(x.Agg))).ToList(), ["Total", .. CollTotals(months.Select(x => x.Agg))], f, export, CollectionNote);
    }

    public async Task<ReportBuild> ProjectCollectionAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await PaymentsAsync(f, PaymentStatus.Posted, ct);
        var g = await q.GroupBy(p => p.ProjectId).Select(x => new { x.Key, N = x.Count(), A = x.Sum(p => p.Amount), Cash = x.Sum(p => p.MethodKind == PaymentMethodKind.Cash ? p.Amount : 0m) }).ToListAsync(ct);
        var names = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ProjectCode + " " + p.ProjectName, ct);
        var list = g.Select(x => (Name: names.GetValueOrDefault(x.Key, "-"), Agg: new CollAgg(x.N, x.A, x.Cash, x.A - x.Cash))).OrderBy(x => x.Name).ToList();
        return Grouped([C("project", "Project"), .. CollTail], list.Select(x => R(x.Name, CollCells(x.Agg))).ToList(), ["Total", .. CollTotals(list.Select(x => x.Agg))], f, export, CollectionNote);
    }

    public async Task<ReportBuild> PaymentMethodSummaryAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await PaymentsAsync(f, PaymentStatus.Posted, ct);
        var g = await q.GroupBy(p => p.PaymentMethodId).Select(x => new { x.Key, N = x.Count(), A = x.Sum(p => p.Amount) }).ToListAsync(ct);
        var methods = await db.PaymentMethods.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var all = g.Sum(x => x.A);
        var list = g.Select(x => (Name: methods.GetValueOrDefault(x.Key, "-"), x.N, x.A)).OrderByDescending(x => x.A).ToList();
        return Grouped([C("method", "Payment method"), C("payments", "Payments", Num), C("amount", "Amount", Money), C("share", "Share %", Pct)],
            list.Select(x => new object?[] { x.Name, x.N, x.A, all == 0m ? 0m : Math.Round(x.A * 100m / all, 2) }).ToList(), ["Total", list.Sum(x => x.N), all, all == 0m ? 0m : 100m], f, export, CollectionNote);
    }

    public async Task<ReportBuild> CashierCollectionAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = await PaymentsAsync(f, PaymentStatus.Posted, ct);
        var g = await q.GroupBy(p => p.SubmittedBy).Select(x => new { x.Key, N = x.Count(), A = x.Sum(p => p.Amount), Cash = x.Sum(p => p.MethodKind == PaymentMethodKind.Cash ? p.Amount : 0m) }).ToListAsync(ct);
        var list = g.Select(x => (Name: x.Key, Agg: new CollAgg(x.N, x.A, x.Cash, x.A - x.Cash))).OrderByDescending(x => x.Agg.Amount).ToList();
        return Grouped([C("cashier", "Cashier (collected by)"), .. CollTail], list.Select(x => R(x.Name, CollCells(x.Agg))).ToList(), ["Total", .. CollTotals(list.Select(x => x.Agg))], f, export,
            CollectionNote, "The cashier is the user who recorded the payment.");
    }

    public async Task<ReportBuild> BankCollectionAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var q = (await PaymentsAsync(f, PaymentStatus.Posted, ct)).Where(p => p.MethodKind != PaymentMethodKind.Cash);
        var g = await q.GroupBy(p => new { p.BankName, p.PaymentMethodId }).Select(x => new { x.Key.BankName, x.Key.PaymentMethodId, N = x.Count(), A = x.Sum(p => p.Amount) }).ToListAsync(ct);
        var methods = await db.PaymentMethods.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var list = g.Select(x => (Bank: string.IsNullOrWhiteSpace(x.BankName) ? "(not recorded)" : x.BankName!, Method: methods.GetValueOrDefault(x.PaymentMethodId, "-"), x.N, x.A)).OrderBy(x => x.Bank).ThenBy(x => x.Method).ToList();
        return Grouped([C("bank", "Bank"), C("method", "Method"), C("payments", "Payments", Num), C("amount", "Amount", Money)], list.Select(x => new object?[] { x.Bank, x.Method, x.N, x.A }).ToList(),
            ["Total", "", list.Sum(x => x.N), list.Sum(x => x.A)], f, export, CollectionNote, "Non-cash methods only, grouped by the bank recorded on the payment.");
    }

    /// <summary>Money held in advance per agreement: installments paid before their due date plus advance allocations of unallocated money.</summary>
    public async Task<ReportBuild> AdvancePaymentsAsync(ReportFilters f, bool export, CancellationToken ct)
    {
        var asOf = f.To ?? await s.TodayAsync(ct);
        var bookings = await s.BookingsAsync(f, true, ct);
        var ahead = await Items(bookings).Where(i => i.DueDate > asOf && i.Paid > 0m).GroupBy(i => new { i.AgreementId, i.AgreementNumber, i.BookingId, i.ProjectId })
            .Select(g => new { g.Key.AgreementId, g.Key.AgreementNumber, g.Key.BookingId, g.Key.ProjectId, Sum = g.Sum(x => x.Paid), Last = g.Max(x => x.LastPaymentDate) }).ToListAsync(ct);
        var held = await (from a in db.PaymentAllocations.AsNoTracking() join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
                          join ag in db.CustomerAgreements.AsNoTracking() on a.AgreementId equals ag.Id join b in bookings on ag.BookingId equals b.Id
                          where a.Kind == AllocationKind.Advance && a.Status == AllocationStatus.Active && p.Status == PaymentStatus.Posted && ag.Status == AgreementStatus.Active
                                && (f.From == null || p.PaymentDate >= f.From) && p.PaymentDate <= asOf
                          group a by new { a.AgreementId, ag.AgreementNumber, ag.BookingId, b.ProjectId } into g
                          select new { g.Key.AgreementId, g.Key.AgreementNumber, g.Key.BookingId, g.Key.ProjectId, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
        var info = ahead.Select(a => (a.AgreementId, a.AgreementNumber, a.BookingId, a.ProjectId)).Union(held.Select(h => (h.AgreementId, h.AgreementNumber, h.BookingId, h.ProjectId))).DistinctBy(x => x.AgreementId).ToList();
        var customers = await PrimaryCustomersAsync(info.Select(i => i.BookingId), ct);
        var projects = await ProjectNamesAsync(ct);
        var list = info.Select(i =>
        {
            var a = ahead.FirstOrDefault(x => x.AgreementId == i.AgreementId); var h = held.FirstOrDefault(x => x.AgreementId == i.AgreementId);
            return (i, Paid: a?.Sum ?? 0m, Held: h?.Sum ?? 0m, Last: a?.Last);
        }).Where(x => x.Paid + x.Held > 0m).OrderByDescending(x => x.Paid + x.Held).ToList();
        var rows = list.Select(x => new object?[] { customers.GetValueOrDefault(x.i.BookingId, "-"), x.i.AgreementNumber, projects.GetValueOrDefault(x.i.ProjectId, "-"), x.Paid, x.Held, x.Paid + x.Held, x.Last }).ToList();
        return Grouped([C("customer", "Customer"), C("agreement", "Agreement"), C("project", "Project"), C("paid", "Paid before due date", Money), C("held", "Held as advance", Money),
            C("total", "Total advance", Money), C("last", "Last payment", Dt)], rows, ["Total", "", "", list.Sum(x => x.Paid), list.Sum(x => x.Held), list.Sum(x => x.Paid + x.Held), null], f, export,
            $"As of {asOf:yyyy-MM-dd}. Paid before due date = money applied to installments not yet due; held as advance = payments kept as advance on the agreement.");
    }

    private static string EnumLabel<T>(T value) where T : struct, Enum
    {
        var name = value.ToString() ?? "";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
