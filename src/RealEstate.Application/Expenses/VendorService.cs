using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Expenses;

/// <summary>Expense categories (spec 7.2) and vendors. Payables shown for a vendor cover only the projects the caller can access.</summary>
public class VendorService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ExpenseSupport support, IAuditService audit,
    IValidator<SaveVendorRequest> vendorValidator, IValidator<SaveExpenseCategoryRequest> categoryValidator)
{
    // =====================================================================================================
    // categories
    // =====================================================================================================
    public async Task<IReadOnlyList<ExpenseCategoryDto>> ListCategoriesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        if (user.UserId is not { } id || !(await permissions.HasPermissionAsync(id, Permissions.ExpensesView, ct) || await permissions.HasPermissionAsync(id, Permissions.ExpensesManage, ct)
            || await permissions.HasPermissionAsync(id, Permissions.VendorsManage, ct) || await permissions.HasPermissionAsync(id, Permissions.ExpensesSetup, ct)))
            throw new ForbiddenException();
        var q = db.ExpenseCategories.AsNoTracking();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await q.OrderBy(c => c.Name).Select(c => new ExpenseCategoryDto(c.Id, c.Code, c.Name, c.Description, c.IsSystem, c.IsActive)).ToListAsync(ct);
    }

    public async Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesSetup, ct);
        await categoryValidator.ValidateOrThrowAsync(r, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        if (await db.ExpenseCategories.AnyAsync(c => c.Code == code, ct)) throw ExpenseSupport.Invalid("Code", "This code is already used.");
        if (await db.ExpenseCategories.AnyAsync(c => c.Name == name, ct)) throw ExpenseSupport.Invalid("Name", "This name is already used.");
        var c = new ExpenseCategory { Code = code, Name = name, Description = ExpenseSupport.TrimOrNull(r.Description, 500), IsActive = r.IsActive };
        db.ExpenseCategories.Add(c);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ExpenseSupport.IsDuplicate(ex)) { throw ExpenseSupport.Invalid("Code", "This code or name is already used."); }
        await audit.RecordAsync(new AuditEntry("ExpenseCategory.Created", "ExpenseCategory", c.Id.ToString(), NewValues: new { c.Code, c.Name, c.IsActive }), ct);
        return new ExpenseCategoryDto(c.Id, c.Code, c.Name, c.Description, false, c.IsActive);
    }

    public async Task<ExpenseCategoryDto> UpdateCategoryAsync(long id, SaveExpenseCategoryRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.ExpensesSetup, ct);
        await categoryValidator.ValidateOrThrowAsync(r, ct);
        var c = await db.ExpenseCategories.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Expense category not found.");
        var name = r.Name.Trim();
        if (c.IsSystem && !r.IsActive) throw new DomainException("Standard expense categories cannot be deactivated.");
        if (c.IsSystem && !string.Equals(c.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase)) throw ExpenseSupport.Invalid("Code", "The code of a standard category cannot be changed.");
        if (await db.ExpenseCategories.AnyAsync(x => x.Name == name && x.Id != id, ct)) throw ExpenseSupport.Invalid("Name", "This name is already used.");
        var old = new { c.Name, c.Description, c.IsActive };
        c.Name = name; c.Description = ExpenseSupport.TrimOrNull(r.Description, 500); c.IsActive = r.IsActive;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("ExpenseCategory.Updated", "ExpenseCategory", id.ToString(), old, new { c.Name, c.Description, c.IsActive }), ct);
        return new ExpenseCategoryDto(c.Id, c.Code, c.Name, c.Description, c.IsSystem, c.IsActive);
    }

    // =====================================================================================================
    // vendors
    // =====================================================================================================
    public async Task<PagedResult<VendorListItemDto>> ListAsync(VendorListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = db.Vendors.AsNoTracking().AsQueryable();
        if (r.Active is { } a) q = q.Where(v => v.IsActive == a);
        if (!string.IsNullOrWhiteSpace(r.Search)) { var t = r.Search.Trim(); q = q.Where(v => v.Name.Contains(t) || v.Code.Contains(t) || (v.Phone != null && v.Phone.Contains(t)) || (v.TaxNumber != null && v.TaxNumber.Contains(t))); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(v => v.Name).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(v => new VendorListItemDto(v.Id, v.Code, v.Name, v.ContactPerson, v.Phone, v.IsActive,
            db.Expenses.Where(e => e.VendorId == v.Id && e.Status == ExpenseStatus.Approved && projects.Any(p => p.Id == e.ProjectId)).Sum(e => (decimal?)(e.Amount - e.AmountPaid)) ?? 0m,
            db.VendorPayments.Where(p => p.VendorId == v.Id && p.Status == VendorPaymentStatus.Posted && projects.Any(pr => pr.Id == p.ProjectId)).Sum(p => (decimal?)p.Amount) ?? 0m)).ToListAsync(ct);
        return new PagedResult<VendorListItemDto>(rows, r.Page, r.PageSize, total);
    }

    /// <summary>Active vendors for dropdowns (name order, up to 200).</summary>
    public async Task<IReadOnlyList<VendorDto>> LookupAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } id || !(await permissions.HasPermissionAsync(id, Permissions.VendorsView, ct) || await permissions.HasPermissionAsync(id, Permissions.ExpensesManage, ct)
            || await permissions.HasPermissionAsync(id, Permissions.ExpensesView, ct) || await permissions.HasPermissionAsync(id, Permissions.VendorsManage, ct)))
            throw new ForbiddenException();
        return await db.Vendors.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Name).Take(200)
            .Select(v => new VendorDto(v.Id, v.Code, v.Name, v.ContactPerson, v.Phone, v.Email, v.Address, v.TaxNumber, v.Notes, v.IsActive, "")).ToListAsync(ct);
    }

    public async Task<VendorDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsView, ct);
        var v = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Vendor not found.");
        var projects = await support.ProjectsAsync(ct);
        var today = await support.TodayAsync(ct);
        var open = await (from e in db.Expenses.AsNoTracking()
                          join p in projects on e.ProjectId equals p.Id
                          where e.VendorId == id && e.Status == ExpenseStatus.Approved && e.AmountPaid < e.Amount
                          orderby e.DueDate, e.Id
                          select new ExpenseListItemDto(e.Id, e.ExpenseNumber, e.ProjectId, p.ProjectCode, e.CategoryId, e.Category!.Name, e.VendorId, v.Name, e.Description, e.ExpenseDate, e.DueDate,
                              e.Amount, e.AmountPaid, e.Amount - e.AmountPaid, e.Status, e.AmountPaid <= 0m ? ExpensePaymentStatus.Unpaid : ExpensePaymentStatus.PartiallyPaid, e.CreatedBy)).Take(50).ToListAsync(ct);
        var paid = await (from p in db.VendorPayments.AsNoTracking() join pr in projects on p.ProjectId equals pr.Id where p.VendorId == id && p.Status == VendorPaymentStatus.Posted select (decimal?)p.Amount).SumAsync(ct) ?? 0m;
        var recent = await (from p in db.VendorPayments.AsNoTracking()
                            join pr in projects on p.ProjectId equals pr.Id
                            join e in db.Expenses.AsNoTracking() on p.ExpenseId equals e.Id
                            join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                            where p.VendorId == id
                            orderby p.Id descending
                            select new VendorPaymentDto(p.Id, p.PaymentNumber, p.ExpenseId, e.ExpenseNumber, p.ProjectId, pr.ProjectCode, p.VendorId, v.Name, p.Amount, p.PaymentDate, p.PaymentMethodId, m.Name,
                                p.Reference, p.Remarks, p.Status, p.CreatedBy, p.CreatedAtUtc, p.ReversedBy, p.ReversedAtUtc, p.ReversalReason)).Take(10).ToListAsync(ct);
        var payable = open.Sum(x => x.Outstanding);
        var overdue = open.Where(x => x.DueDate is { } d && d < today).Sum(x => x.Outstanding);
        return new VendorDetailDto(Map(v), payable, paid, overdue, await support.CurrencySymbolAsync(ct), open, recent);
    }

    public async Task<VendorDto> CreateAsync(SaveVendorRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsManage, ct);
        await vendorValidator.ValidateOrThrowAsync(r, ct);
        await EnsureUniqueNameAsync(r.Name, null, ct);
        var v = new Vendor { Code = "NEW-" + Guid.NewGuid().ToString("N")[..20] };
        Apply(v, r);
        db.Vendors.Add(v);
        await db.SaveChangesAsync(ct);
        v.Code = $"VEN-{v.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Vendor.Created", "Vendor", v.Id.ToString(), NewValues: new { v.Code, v.Name, v.TaxNumber, v.IsActive }), ct);
        return Map(v);
    }

    public async Task<VendorDto> UpdateAsync(long id, SaveVendorRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.VendorsManage, ct);
        await vendorValidator.ValidateOrThrowAsync(r, ct);
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Vendor not found.");
        await EnsureUniqueNameAsync(r.Name, id, ct);
        db.UseRowVersion(v, r.RowVersion);
        var old = new { v.Name, v.ContactPerson, v.Phone, v.Email, v.Address, v.TaxNumber, v.IsActive };
        Apply(v, r);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Vendor.Updated", "Vendor", id.ToString(), old, new { v.Name, v.ContactPerson, v.Phone, v.Email, v.Address, v.TaxNumber, v.IsActive }), ct);
        return Map(v);
    }

    private async Task EnsureUniqueNameAsync(string name, long? exceptId, CancellationToken ct)
    {
        var n = name.Trim();
        if (await db.Vendors.AnyAsync(x => x.Name == n && x.Id != (exceptId ?? 0), ct)) throw ExpenseSupport.Invalid("Name", "A vendor with this name already exists.");
    }

    private static void Apply(Vendor v, SaveVendorRequest r)
    {
        v.Name = ExpenseSupport.Trim(r.Name, 200); v.ContactPerson = ExpenseSupport.TrimOrNull(r.ContactPerson, 150); v.Phone = ExpenseSupport.TrimOrNull(r.Phone, 40);
        v.Email = ExpenseSupport.TrimOrNull(r.Email, 200); v.Address = ExpenseSupport.TrimOrNull(r.Address, 500); v.TaxNumber = ExpenseSupport.TrimOrNull(r.TaxNumber, 50);
        v.Notes = ExpenseSupport.TrimOrNull(r.Notes, 1000); v.IsActive = r.IsActive;
    }

    private static VendorDto Map(Vendor v) => new(v.Id, v.Code, v.Name, v.ContactPerson, v.Phone, v.Email, v.Address, v.TaxNumber, v.Notes, v.IsActive, v.RowVersion.ToToken());
}
