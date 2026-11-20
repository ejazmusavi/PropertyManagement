using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Expenses;

/// <summary>
/// Vendor invoices: a vendor's bill is registered together with the payable <see cref="Expense"/> it creates (Draft, then the normal approval workflow).
/// Duplicate invoice numbers per vendor are rejected while the expense is live. Payments are made against the expense (see <see cref="ExpenseService.PayAsync"/>).
/// </summary>
public class VendorInvoiceService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ExpenseSupport support, IAuditService audit, IValidator<CreateVendorInvoiceRequest> validator)
{
    public async Task<PagedResult<VendorInvoiceListItemDto>> ListAsync(VendorInvoiceListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var today = await support.TodayAsync(ct);
        var q = from i in db.VendorInvoices.AsNoTracking()
                join e in db.Expenses.AsNoTracking() on i.ExpenseId equals e.Id
                join p in projects on i.ProjectId equals p.Id
                join v in db.Vendors.AsNoTracking() on i.VendorId equals v.Id
                select new { i, e, p.ProjectCode, VendorName = v.Name };
        if (r.ProjectId is { } pid) q = q.Where(x => x.i.ProjectId == pid);
        if (r.VendorId is { } vid) q = q.Where(x => x.i.VendorId == vid);
        if (r.PaymentStatus is { } ps)
            q = ps switch
            {
                ExpensePaymentStatus.Unpaid => q.Where(x => x.e.AmountPaid <= 0m),
                ExpensePaymentStatus.PartiallyPaid => q.Where(x => x.e.AmountPaid > 0m && x.e.AmountPaid < x.e.Amount),
                _ => q.Where(x => x.e.AmountPaid >= x.e.Amount),
            };
        if (r.OverdueOnly) q = q.Where(x => x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid < x.e.Amount && x.i.DueDate < today);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(x => x.i.InvoiceNumber.Contains(t) || x.VendorName.Contains(t) || x.e.ExpenseNumber.Contains(t)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.i.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new VendorInvoiceListItemDto(x.i.Id, x.i.InvoiceNumber, x.i.VendorId, x.VendorName,
            x.i.ProjectId, x.ProjectCode, x.e.Id, x.e.ExpenseNumber, x.i.InvoiceDate, x.i.DueDate, x.i.Amount, x.e.AmountPaid, x.e.Status == ExpenseStatus.Approved ? x.e.Amount - x.e.AmountPaid : 0m,
            x.e.Status, x.e.AmountPaid <= 0m ? ExpensePaymentStatus.Unpaid : x.e.AmountPaid >= x.e.Amount ? ExpensePaymentStatus.Paid : ExpensePaymentStatus.PartiallyPaid,
            x.e.Status == ExpenseStatus.Approved && x.e.AmountPaid < x.e.Amount && x.i.DueDate < today)).ToListAsync(ct);
        return new PagedResult<VendorInvoiceListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<VendorInvoiceListItemDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsView, ct);
        var projectId = await db.VendorInvoices.AsNoTracking().Where(i => i.Id == id).Select(i => (long?)i.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await support.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this invoice.");
            throw new NotFoundException("Vendor invoice not found.");
        }
        await support.EnsureProjectAccessAsync(projectId.Value, ct);
        var today = await support.TodayAsync(ct);
        return await (from i in db.VendorInvoices.AsNoTracking()
                      join e in db.Expenses.AsNoTracking() on i.ExpenseId equals e.Id
                      join p in db.Projects.AsNoTracking() on i.ProjectId equals p.Id
                      join v in db.Vendors.AsNoTracking() on i.VendorId equals v.Id
                      where i.Id == id
                      select new VendorInvoiceListItemDto(i.Id, i.InvoiceNumber, i.VendorId, v.Name, i.ProjectId, p.ProjectCode, e.Id, e.ExpenseNumber, i.InvoiceDate, i.DueDate, i.Amount, e.AmountPaid,
                          e.Status == ExpenseStatus.Approved ? e.Amount - e.AmountPaid : 0m, e.Status,
                          e.AmountPaid <= 0m ? ExpensePaymentStatus.Unpaid : e.AmountPaid >= e.Amount ? ExpensePaymentStatus.Paid : ExpensePaymentStatus.PartiallyPaid,
                          e.Status == ExpenseStatus.Approved && e.AmountPaid < e.Amount && i.DueDate < today)).FirstAsync(ct);
    }

    public async Task<VendorInvoiceListItemDto> CreateAsync(CreateVendorInvoiceRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsManage, ct);
        var userId = support.RequireUser();
        await validator.ValidateOrThrowAsync(r, ct);
        var number = r.InvoiceNumber.Trim();
        var activeKey = $"{r.VendorId}:{number.ToUpperInvariant()}";
        var key = ExpenseSupport.NullIfBlank(r.IdempotencyKey);
        if (key is not null && await (from e in db.Expenses.AsNoTracking() join i in db.VendorInvoices.AsNoTracking() on e.Id equals i.ExpenseId where e.IdempotencyKey == key
                                      select new { i.Id, i.VendorId, i.ProjectId, i.InvoiceNumber, i.Amount, i.InvoiceDate, i.DueDate, e.CategoryId }).FirstOrDefaultAsync(ct) is { } existing)
        {
            if (existing.VendorId != r.VendorId || existing.ProjectId != r.ProjectId || existing.CategoryId != r.CategoryId || existing.InvoiceDate != r.InvoiceDate || existing.DueDate != r.DueDate
                || !string.Equals(existing.InvoiceNumber, number, StringComparison.OrdinalIgnoreCase) || existing.Amount != r.Amount)
                throw ExpenseSupport.Invalid("IdempotencyKey", "This idempotency key was already used for a different invoice.");
            return await GetAsync(existing.Id, ct);
        }
        await support.OpenProjectAsync(r.ProjectId, ct);
        var vendor = await support.ActiveVendorAsync(r.VendorId, ct);
        if (await db.VendorInvoices.AnyAsync(i => i.ActiveKey == activeKey, ct)) throw ExpenseSupport.Invalid("InvoiceNumber", $"Invoice {number} is already registered for {vendor.Name}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var description = string.IsNullOrWhiteSpace(r.Description) ? $"Invoice {number} - {vendor.Name}" : r.Description.Trim();
        var expense = await support.CreateExpenseAsync(r.ProjectId, r.CategoryId, r.VendorId, description, r.InvoiceDate, r.DueDate, r.Amount, number, r.Notes, key, ct);
        var invoice = new VendorInvoice
        {
            InvoiceNumber = ExpenseSupport.Trim(number, 60), VendorId = r.VendorId, ProjectId = r.ProjectId, ExpenseId = expense.Id, InvoiceDate = r.InvoiceDate, DueDate = r.DueDate, Amount = r.Amount,
            Notes = ExpenseSupport.TrimOrNull(r.Notes, 1000), ActiveKey = activeKey,
        };
        db.VendorInvoices.Add(invoice);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw ExpenseSupport.Invalid("InvoiceNumber", $"Invoice {number} is already registered for {vendor.Name}."); }
        await audit.RecordAsync(new AuditEntry("VendorInvoice.Registered", "VendorInvoice", invoice.Id.ToString(),
            NewValues: new { invoice.InvoiceNumber, vendor.Code, r.ProjectId, invoice.Amount, invoice.DueDate, expense.ExpenseNumber }, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(invoice.Id, ct);
    }
}
