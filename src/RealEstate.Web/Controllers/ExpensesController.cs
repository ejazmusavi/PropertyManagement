using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Expenses;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Expense dashboard, registration, approval queue, payment and supporting documents.</summary>
public class ExpensesController(ExpenseService expenses, VendorService vendors, ICurrentUser current) : InstallmentControllerBase
{
    [HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> Dashboard(long? projectId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        View(new ExpenseDashboardViewModel { Dashboard = await expenses.DashboardAsync(projectId, from, to, ct), Projects = await expenses.ProjectOptionsAsync(false, ct), ProjectId = projectId, From = from, To = to });

    [HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> Index(ExpenseListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new ExpenseListViewModel
        {
            Filter = filter, Expenses = await expenses.ListAsync(filter, ct), Projects = await expenses.ProjectOptionsAsync(false, ct), Categories = await vendors.ListCategoriesAsync(false, ct),
        });
    }

    /// <summary>The approval queue: expenses pending approval (the approver must differ from the submitter).</summary>
    [HasPermission(Permissions.ExpensesApprove)]
    public async Task<IActionResult> Approvals(ExpenseListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20; filter.Status = ExpenseStatus.PendingApproval;
        return View("Index", new ExpenseListViewModel
        {
            Filter = filter, Expenses = await expenses.ListAsync(filter, ct), Projects = await expenses.ProjectOptionsAsync(false, ct), Categories = await vendors.ListCategoriesAsync(false, ct),
            Title = "Expense approvals", ApprovalQueue = true, CurrentUserId = current.UserId,
        });
    }

    [HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var e = await expenses.GetAsync(id, ct);
        return View(new ExpenseDetailsViewModel
        {
            Expense = e, Methods = await OrAsync(async () => await expenses.MethodsAsync(ct), []),
            IsSubmitter = e.SubmittedBy is not null && string.Equals(e.SubmittedBy, current.UserName, StringComparison.OrdinalIgnoreCase),
        });
    }

    // ----- create / edit -----
    [HttpGet, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> Create(long? projectId, CancellationToken ct) =>
        View("Form", await LoadListsAsync(new ExpenseFormViewModel { ProjectId = projectId ?? 0 }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> Create(ExpenseFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var e = await expenses.CreateAsync(new CreateExpenseRequest(m.ProjectId, m.CategoryId, m.VendorId, m.Description, m.ExpenseDate, m.Amount, m.DueDate, m.Reference, m.Remarks, m.IdempotencyKey), ct);
                TempData["Success"] = $"Expense {e.ExpenseNumber} registered. Upload the supporting document, then submit it for approval.";
                return RedirectToAction(nameof(Details), new { id = e.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", await LoadListsAsync(m, ct));
    }

    [HttpGet, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var e = await expenses.GetAsync(id, ct);
        if (!Expense.CanEdit(e.Status)) { TempData["Error"] = $"A {e.Status} expense cannot be edited."; return RedirectToAction(nameof(Details), new { id }); }
        return View("Form", await LoadListsAsync(new ExpenseFormViewModel
        {
            Id = e.Id, ProjectId = e.ProjectId, CategoryId = e.CategoryId, VendorId = e.VendorId, Description = e.Description, ExpenseDate = e.ExpenseDate, DueDate = e.DueDate, Amount = e.Amount,
            Reference = e.Reference, Remarks = e.Remarks, RowVersion = e.RowVersion, AmountLocked = e.Invoice is not null,
        }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> Edit(long id, ExpenseFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await expenses.UpdateAsync(id, new UpdateExpenseRequest(m.CategoryId, m.VendorId, m.Description, m.ExpenseDate, m.Amount, m.DueDate, m.Reference, m.Remarks, m.RowVersion), ct);
                TempData["Success"] = "Expense updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The expense was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("Form", await LoadListsAsync(m, ct));
    }

    // ----- workflow -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> Submit(long id, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => expenses.SubmitAsync(id, new SubmitExpenseRequest(rowVersion), ct), "Expense submitted for approval.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesApprove)]
    public async Task<IActionResult> Approve(long id, string? comments, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => expenses.ApproveAsync(id, new ApproveExpenseRequest(comments, rowVersion), ct), "Expense approved; it is now payable and queued for accounting.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesApprove)]
    public async Task<IActionResult> Reject(long id, string comments, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => expenses.RejectAsync(id, new RejectExpenseRequest(comments ?? "", rowVersion), ct), "Expense rejected. The submitter can edit and resubmit it.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> Cancel(long id, string reason, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => expenses.CancelAsync(id, new CancelExpenseRequest(reason ?? "", rowVersion), ct), "Expense cancelled.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesPay)]
    public async Task<IActionResult> Pay(long id, VendorPaymentFormViewModel m, CancellationToken ct) =>
        await Act(id, () => expenses.PayAsync(id, new PayExpenseRequest(m.Amount, m.PaymentMethodId, m.PaymentDate, m.Reference, m.Remarks, m.IdempotencyKey, m.RowVersion), ct), "Payment recorded and posted for accounting.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesReverse)]
    public async Task<IActionResult> ReversePayment(long id, long paymentId, string reason, CancellationToken ct) =>
        await Act(id, () => expenses.ReversePaymentAsync(paymentId, new ReverseVendorPaymentRequest(reason ?? ""), ct), "Payment reversed.");

    // ----- documents -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> UploadDocument(long id, IFormFile? file, ExpenseDocumentType type, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Details), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await expenses.UploadAttachmentAsync(id, type, title, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> DownloadDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await expenses.DownloadAttachmentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await expenses.DeleteAttachmentAsync(id, documentId, reason ?? "", ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> Act(long id, Func<Task<ExpenseDetailDto>> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<ExpenseFormViewModel> LoadListsAsync(ExpenseFormViewModel m, CancellationToken ct)
    {
        m.Projects = await expenses.ProjectOptionsAsync(true, ct);
        m.Categories = await vendors.ListCategoriesAsync(true, ct);
        m.Vendors = await OrAsync(async () => await vendors.LookupAsync(ct), []);
        return m;
    }
}

/// <summary>Expense categories (list with inline create / edit).</summary>
[HasPermission(Permissions.ExpensesView)]
public class ExpenseCategoriesController(VendorService vendors) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(long? edit, CancellationToken ct)
    {
        var list = await vendors.ListCategoriesAsync(false, ct);
        var vm = new ExpenseCategoriesViewModel { Categories = list };
        if (edit is { } id && list.FirstOrDefault(c => c.Id == id) is { } c)
            vm.Form = new ExpenseCategoryFormViewModel { Id = c.Id, Code = c.Code, Name = c.Name, Description = c.Description, IsActive = c.IsActive, IsSystem = c.IsSystem };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesSetup)]
    public async Task<IActionResult> Save([Bind(Prefix = "Form")] ExpenseCategoryFormViewModel m, CancellationToken ct)
    {
        if (m.IsSystem) m.IsActive = true;
        if (ModelState.IsValid)
        {
            try
            {
                var req = new SaveExpenseCategoryRequest(m.Code, m.Name, m.Description, m.IsActive);
                if (m.Id is { } id) await vendors.UpdateCategoryAsync(id, req, ct); else await vendors.CreateCategoryAsync(req, ct);
                TempData["Success"] = "Expense category saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Index", new ExpenseCategoriesViewModel { Categories = await vendors.ListCategoriesAsync(false, ct), Form = m });
    }
}

/// <summary>Vendors.</summary>
[HasPermission(Permissions.VendorsView)]
public class VendorsController(VendorService vendors) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(VendorListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new VendorListViewModel { Filter = filter, Vendors = await vendors.ListAsync(filter, ct) });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(new VendorDetailsViewModel { Vendor = await vendors.GetAsync(id, ct) });

    [HttpGet, HasPermission(Permissions.VendorsManage)]
    public IActionResult Create() => View("Form", new VendorFormViewModel());

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.VendorsManage)]
    public async Task<IActionResult> Create(VendorFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var v = await vendors.CreateAsync(new SaveVendorRequest(m.Name, m.ContactPerson, m.Phone, m.Email, m.Address, m.TaxNumber, m.Notes, m.IsActive), ct);
                TempData["Success"] = $"Vendor {v.Code} created.";
                return RedirectToAction(nameof(Details), new { id = v.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.VendorsManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var v = (await vendors.GetAsync(id, ct)).Vendor;
        return View("Form", new VendorFormViewModel
        {
            Id = v.Id, Code = v.Code, Name = v.Name, ContactPerson = v.ContactPerson, Phone = v.Phone, Email = v.Email, Address = v.Address, TaxNumber = v.TaxNumber, Notes = v.Notes,
            IsActive = v.IsActive, RowVersion = v.RowVersion,
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.VendorsManage)]
    public async Task<IActionResult> Edit(long id, VendorFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await vendors.UpdateAsync(id, new SaveVendorRequest(m.Name, m.ContactPerson, m.Phone, m.Email, m.Address, m.TaxNumber, m.Notes, m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "Vendor updated.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The vendor was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        return View("Form", m);
    }
}

/// <summary>Vendor invoices: each registers the vendor's bill and its payable expense.</summary>
[HasPermission(Permissions.VendorsView)]
public class VendorInvoicesController(VendorInvoiceService invoices, ExpenseService expenses, VendorService vendors) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(VendorInvoiceListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new VendorInvoiceListViewModel
        {
            Filter = filter, Invoices = await invoices.ListAsync(filter, ct), Projects = await expenses.ProjectOptionsAsync(false, ct), Vendors = await vendors.LookupAsync(ct),
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var i = await invoices.GetAsync(id, ct);
        return RedirectToAction("Details", "Expenses", new { id = i.ExpenseId });
    }

    [HttpGet, HasPermission(Permissions.VendorsManage)]
    public async Task<IActionResult> Create(long? vendorId, CancellationToken ct) => View(await LoadAsync(new VendorInvoiceFormViewModel { VendorId = vendorId ?? 0 }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.VendorsManage)]
    public async Task<IActionResult> Create(VendorInvoiceFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var i = await invoices.CreateAsync(new CreateVendorInvoiceRequest(m.ProjectId, m.VendorId, m.InvoiceNumber, m.CategoryId, m.InvoiceDate, m.DueDate, m.Amount, m.Description, m.Notes, m.IdempotencyKey), ct);
                TempData["Success"] = $"Invoice {i.InvoiceNumber} registered as expense {i.ExpenseNumber}. Upload the invoice document, then submit it for approval.";
                return RedirectToAction("Details", "Expenses", new { id = i.ExpenseId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await LoadAsync(m, ct));
    }

    private async Task<VendorInvoiceFormViewModel> LoadAsync(VendorInvoiceFormViewModel m, CancellationToken ct)
    {
        m.Projects = await expenses.ProjectOptionsAsync(true, ct);
        m.Categories = await vendors.ListCategoriesAsync(true, ct);
        m.Vendors = await vendors.LookupAsync(ct);
        return m;
    }
}

/// <summary>Vendor payments: list and record a payment against an approved expense.</summary>
[HasPermission(Permissions.ExpensesView)]
public class VendorPaymentsController(ExpenseService expenses) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(VendorPaymentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new VendorPaymentListViewModel { Filter = filter, Payments = await expenses.ListPaymentsAsync(filter, ct), Projects = await expenses.ProjectOptionsAsync(false, ct) });
    }

    [HttpGet, HasPermission(Permissions.ExpensesPay)]
    public async Task<IActionResult> Create(long? expenseId, string? search, CancellationToken ct)
    {
        if (expenseId is not { } id)
        {
            var filter = new ExpenseListRequest { PageSize = 20, PayableOnly = true, Search = search };
            return View("Pick", new VendorPaymentPickerViewModel { Search = search, Payables = await expenses.ListAsync(filter, ct) });
        }
        var e = await expenses.GetAsync(id, ct);
        return View(new VendorPaymentFormViewModel { ExpenseId = id, Amount = e.Outstanding, RowVersion = e.RowVersion, Expense = e, Methods = await expenses.MethodsAsync(ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.ExpensesPay)]
    public async Task<IActionResult> Create(VendorPaymentFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await expenses.PayAsync(m.ExpenseId, new PayExpenseRequest(m.Amount, m.PaymentMethodId, m.PaymentDate, m.Reference, m.Remarks, m.IdempotencyKey, m.RowVersion), ct);
                TempData["Success"] = "Payment recorded and posted for accounting.";
                return RedirectToAction("Details", "Expenses", new { id = m.ExpenseId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Expense = await expenses.GetAsync(m.ExpenseId, ct);
        m.Methods = await expenses.MethodsAsync(ct);
        return View(m);
    }
}
