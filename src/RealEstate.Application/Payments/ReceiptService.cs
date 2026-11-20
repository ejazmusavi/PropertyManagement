using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Payments;

/// <summary>
/// Receipts (spec 6.6): a unique receipt number is issued when a payment is posted. The first PDF produced is the original; every later one is marked
/// as a numbered duplicate copy. Receipts show payment references, verification information and reversal history.
/// </summary>
public class ReceiptService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support, PaymentService payments, IAuditService audit,
    IReceiptPdfRenderer renderer)
{
    public async Task<PagedResult<ReceiptDto>> ListAsync(ReceiptListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from rc in db.PaymentReceipts.AsNoTracking()
                join pay in db.Payments.AsNoTracking() on rc.PaymentId equals pay.Id
                join p in projects on pay.ProjectId equals p.Id
                join a in db.CustomerAgreements.AsNoTracking() on pay.AgreementId equals a.Id
                join c in db.Customers.AsNoTracking() on pay.CustomerId equals c.Id
                join m in db.PaymentMethods.AsNoTracking() on pay.PaymentMethodId equals m.Id
                select new { rc, pay, p.ProjectCode, a.AgreementNumber, c.FullName, c.CustomerCode, MethodName = m.Name };
        if (r.ProjectId is { } pid) q = q.Where(x => x.pay.ProjectId == pid);
        if (r.CustomerId is { } cid) q = q.Where(x => x.pay.CustomerId == cid);
        if (r.From is { } from) q = q.Where(x => x.pay.PaymentDate >= from);
        if (r.To is { } to) q = q.Where(x => x.pay.PaymentDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(x => x.rc.ReceiptNumber.Contains(t) || x.pay.PaymentNumber.Contains(t) || x.AgreementNumber.Contains(t) || x.FullName.Contains(t) || x.CustomerCode.Contains(t)
                || (x.pay.Reference != null && x.pay.Reference.Contains(t)));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.rc.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(x => new ReceiptDto(x.rc.Id, x.rc.ReceiptNumber, x.pay.Id, x.pay.PaymentNumber, x.rc.Amount, x.rc.IssuedAtUtc, x.rc.IssuedBy, x.rc.CopiesPrinted, x.rc.LastPrintedAtUtc,
                x.rc.LastPrintedBy, x.pay.CustomerId, x.FullName, x.AgreementNumber, x.ProjectCode, x.pay.Status, x.pay.PaymentDate, x.MethodName)).ToListAsync(ct);
        return new PagedResult<ReceiptDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<ReceiptDto> GetAsync(long receiptId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsView, ct);
        var paymentId = await PaymentIdAsync(receiptId, ct);
        return (await payments.GetAsync(paymentId, ct)).Receipt!;
    }

    /// <summary>Produces the receipt PDF and counts the copy (first = original, later = duplicate copy n). Audited.</summary>
    public async Task<DownloadedFile> PdfAsync(long receiptId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PaymentsReceipts, ct);
        var paymentId = await PaymentIdAsync(receiptId, ct);
        var detail = await payments.GetAsync(paymentId, ct); // project/data access
        var now = support.Now;
        int copy;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Atomic increment: concurrent prints get consecutive copy numbers (the row stays locked until commit).
            await db.PaymentReceipts.Where(x => x.Id == receiptId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CopiesPrinted, x => x.CopiesPrinted + 1)
                .SetProperty(x => x.LastPrintedAtUtc, now)
                .SetProperty(x => x.LastPrintedBy, support.Actor), ct);
            copy = await db.PaymentReceipts.AsNoTracking().Where(x => x.Id == receiptId).Select(x => x.CopiesPrinted).FirstAsync(ct);
            await audit.RecordAsync(new AuditEntry(copy == 1 ? "Receipt.Printed" : "Receipt.DuplicatePrinted", "PaymentReceipt", receiptId.ToString(),
                NewValues: new { detail.Receipt!.ReceiptNumber, Copy = copy }, UserId: user.UserId), ct);
            await tx.CommitAsync(ct);
        }
        var company = await db.Companies.AsNoTracking().Select(c => new { c.Name, c.Address, c.City, c.Phone }).FirstOrDefaultAsync(ct);
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == detail.ProjectId).Select(p => p.ProjectName).FirstAsync(ct);
        var allocations = detail.Allocations.Where(a => a.Status == AllocationStatus.Active || detail.Status == PaymentStatus.Reversed)
            .Where(a => a.Kind != AllocationKind.Refund || a.Status == AllocationStatus.Active)
            .Select(a => new ReceiptLine(a.Kind switch
            {
                AllocationKind.Installment => $"Line {a.LineNo}: {a.Description} (due {a.DueDate:dd-MMM-yyyy})",
                AllocationKind.Advance => "Advance balance",
                _ => $"Refunded ({a.RefundNumber})",
            } + (a.Status == AllocationStatus.Active ? "" : $" [{a.Status}]"), $"{detail.CurrencySymbol} {a.Amount:N2}")).ToList();
        if (detail.Status == PaymentStatus.Posted && detail.Unallocated > 0m) allocations.Add(new ReceiptLine("Unallocated balance", $"{detail.CurrencySymbol} {detail.Unallocated:N2}"));
        var verification = detail.Verifications.Select(v => new ReceiptLine($"{v.Result} by {v.VerifiedBy} on {v.VerifiedAtUtc:dd-MMM-yyyy}",
            string.Join(", ", new[] { v.BankReference is null ? null : $"bank ref {v.BankReference}", v.ClearedOn is null ? null : $"cleared {v.ClearedOn:dd-MMM-yyyy}", v.Notes }
                .Where(s => s is not null)))).ToList();
        if (verification.Count == 0) verification.Add(new ReceiptLine("Verification", detail.VerificationRequired ? "Pending" : "Not required (cash received at office)"));
        verification.Add(new ReceiptLine("Approved", $"{detail.ApprovedBy} on {detail.ApprovedAtUtc:dd-MMM-yyyy HH:mm} UTC"));
        var reversals = detail.Reversal is { } rv
            ? new List<ReceiptLine> { new($"REVERSED on {rv.ReversedAtUtc:dd-MMM-yyyy HH:mm} UTC by {rv.ReversedBy}", rv.Reason) }
            : [];
        var doc = new ReceiptDocument(company?.Name ?? "Company", string.Join(", ", new[] { company?.Address, company?.City }.Where(s => !string.IsNullOrWhiteSpace(s))), company?.Phone,
            detail.Receipt!.ReceiptNumber, copy == 1 ? "ORIGINAL" : $"DUPLICATE COPY {copy - 1}", copy, detail.PaymentNumber, detail.PaymentDate, detail.Receipt.IssuedAtUtc, detail.CustomerName,
            detail.CustomerCode, detail.AgreementNumber, detail.BookingNumber, project, detail.MethodName, detail.Reference, detail.BankName, detail.InstrumentDate, detail.CurrencySymbol,
            detail.Amount, AmountInWords.Convert(detail.Amount, detail.CurrencyCode), detail.Status, allocations, verification, reversals, support.Actor, now);
        var bytes = renderer.Render(doc);
        return new DownloadedFile(new MemoryStream(bytes), $"{detail.Receipt.ReceiptNumber}{(copy == 1 ? "" : $"-copy{copy - 1}")}.pdf", "application/pdf");
    }

    private async Task<long> PaymentIdAsync(long receiptId, CancellationToken ct)
    {
        var paymentId = await db.PaymentReceipts.AsNoTracking().Where(x => x.Id == receiptId).Select(x => (long?)x.PaymentId).FirstOrDefaultAsync(ct);
        if (paymentId is null)
        {
            if (!await permissions.HasPermissionAsync(user.UserId ?? Guid.Empty, Permissions.ProjectsAccessAll, ct)) throw new ForbiddenException("You do not have access to this receipt.");
            throw new NotFoundException("Receipt not found.");
        }
        return paymentId.Value;
    }
}

/// <summary>English words for receipt amounts (e.g. "Pakistani Rupees One Hundred Twenty Thousand and Fifty Paisa Only").</summary>
public static class AmountInWords
{
    private static readonly string[] Ones = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
    private static readonly (decimal Value, string Name)[] Scales = [(1_000_000_000_000m, "Trillion"), (1_000_000_000m, "Billion"), (1_000_000m, "Million"), (1_000m, "Thousand")];

    public static string Convert(decimal amount, string currencyCode)
    {
        amount = decimal.Round(Math.Abs(amount), 2);
        var whole = decimal.Truncate(amount);
        var cents = (int)((amount - whole) * 100m);
        var (major, minor) = currencyCode.ToUpperInvariant() switch
        {
            "PKR" => ("Pakistani Rupees", "Paisa"),
            "USD" => ("US Dollars", "Cents"),
            "AED" => ("UAE Dirhams", "Fils"),
            "GBP" => ("Pounds Sterling", "Pence"),
            "EUR" => ("Euros", "Cents"),
            var c => (c, "Cents"),
        };
        var text = $"{major} {Words(whole)}";
        if (cents > 0) text += $" and {Words(cents)} {minor}";
        return text + " Only";
    }

    private static string Words(decimal n)
    {
        if (n < 20) return Ones[(int)n];
        if (n < 100) return Tens[(int)(n / 10)] + (n % 10 > 0 ? " " + Ones[(int)(n % 10)] : "");
        if (n < 1000) return Ones[(int)(n / 100)] + " Hundred" + (n % 100 > 0 ? " " + Words(n % 100) : "");
        foreach (var (value, name) in Scales)
            if (n >= value) return Words(decimal.Truncate(n / value)) + " " + name + (n % value > 0 ? " " + Words(n % value) : "");
        return n.ToString("N0");
    }
}
