using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Expenses;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Expenses: registration, supporting documents, approval (maker-checker) and payment.</summary>
[ApiController, Authorize]
[Route("api/v1/expenses")]
public class ExpensesController(ExpenseService expenses) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<PagedResult<ExpenseListItemDto>>> List([FromQuery] ExpenseListRequest request, CancellationToken ct) => await expenses.ListAsync(request, ct);

    [HttpPost, HasPermission(Permissions.ExpensesManage)]
    [ProducesResponseType<ExpenseDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateExpenseRequest request, CancellationToken ct)
    {
        var e = await expenses.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = e.Id }, e);
    }

    [HttpGet("dashboard"), HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<ExpenseDashboardDto>> Dashboard([FromQuery] long? projectId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        await expenses.DashboardAsync(projectId, from, to, ct);

    [HttpGet("{id:long}"), HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<ExpenseDetailDto>> Get(long id, CancellationToken ct) => await expenses.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.ExpensesManage)]
    public async Task<ActionResult<ExpenseDetailDto>> Update(long id, UpdateExpenseRequest request, CancellationToken ct) => await expenses.UpdateAsync(id, request, ct);

    [HttpPost("{id:long}/submit"), HasPermission(Permissions.ExpensesManage)]
    public async Task<ActionResult<ExpenseDetailDto>> Submit(long id, SubmitExpenseRequest request, CancellationToken ct) => await expenses.SubmitAsync(id, request, ct);

    [HttpPost("{id:long}/approve"), HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseDetailDto>> Approve(long id, ApproveExpenseRequest request, CancellationToken ct) => await expenses.ApproveAsync(id, request, ct);

    [HttpPost("{id:long}/reject"), HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseDetailDto>> Reject(long id, RejectExpenseRequest request, CancellationToken ct) => await expenses.RejectAsync(id, request, ct);

    [HttpPost("{id:long}/cancel"), HasPermission(Permissions.ExpensesView)] // the service decides: makers cancel drafts, approvers cancel approved expenses
    public async Task<ActionResult<ExpenseDetailDto>> Cancel(long id, CancelExpenseRequest request, CancellationToken ct) => await expenses.CancelAsync(id, request, ct);

    [HttpPost("{id:long}/pay"), HasPermission(Permissions.ExpensesPay)]
    public async Task<ActionResult<ExpenseDetailDto>> Pay(long id, PayExpenseRequest request, CancellationToken ct) => await expenses.PayAsync(id, request, ct);

    [HttpGet("payments"), HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<PagedResult<VendorPaymentDto>>> Payments([FromQuery] VendorPaymentListRequest request, CancellationToken ct) => await expenses.ListPaymentsAsync(request, ct);

    [HttpGet("payment-methods"), HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<IReadOnlyList<PaymentMethodDto>>> Methods(CancellationToken ct) => Ok(await expenses.MethodsAsync(ct));

    [HttpPost("payments/{paymentId:long}/reverse"), HasPermission(Permissions.ExpensesReverse)]
    public async Task<ActionResult<ExpenseDetailDto>> ReversePayment(long paymentId, ReverseVendorPaymentRequest request, CancellationToken ct) => await expenses.ReversePaymentAsync(paymentId, request, ct);

    // ----- attachments -----
    [HttpGet("{id:long}/attachments"), HasPermission(Permissions.ExpensesView)]
    public async Task<ActionResult<IReadOnlyList<ExpenseAttachmentDto>>> Attachments(long id, CancellationToken ct) => Ok(await expenses.ListAttachmentsAsync(id, ct));

    [HttpPost("{id:long}/attachments"), HasPermission(Permissions.ExpensesManage), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<ExpenseAttachmentDto>> Upload(long id, IFormFile file, [FromForm] ExpenseDocumentType type = ExpenseDocumentType.Invoice, [FromForm] string? title = null, CancellationToken ct = default) =>
        await expenses.UploadAttachmentAsync(id, type, title, file.ToUpload(), ct);

    [HttpGet("{id:long}/attachments/{documentId:long}/download"), HasPermission(Permissions.ExpensesView)]
    public async Task<IActionResult> Download(long id, long documentId, CancellationToken ct)
    {
        var f = await expenses.DownloadAttachmentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/attachments/{documentId:long}"), HasPermission(Permissions.ExpensesManage)]
    public async Task<IActionResult> DeleteAttachment(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await expenses.DeleteAttachmentAsync(id, documentId, reason, ct);
        return NoContent();
    }
}

/// <summary>Expense categories.</summary>
[ApiController, Authorize]
[Route("api/v1/expense-categories")]
public class ExpenseCategoriesController(VendorService vendors) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExpenseCategoryDto>>> List([FromQuery] bool activeOnly, CancellationToken ct) => Ok(await vendors.ListCategoriesAsync(activeOnly, ct));

    [HttpPost, HasPermission(Permissions.ExpensesSetup)]
    [ProducesResponseType<ExpenseCategoryDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveExpenseCategoryRequest request, CancellationToken ct)
    {
        var c = await vendors.CreateCategoryAsync(request, ct);
        return Created($"/api/v1/expense-categories/{c.Id}", c);
    }

    [HttpPut("{id:long}"), HasPermission(Permissions.ExpensesSetup)]
    public async Task<ActionResult<ExpenseCategoryDto>> Update(long id, SaveExpenseCategoryRequest request, CancellationToken ct) => await vendors.UpdateCategoryAsync(id, request, ct);
}

/// <summary>Vendors and their payables.</summary>
[ApiController, Authorize]
[Route("api/v1/vendors")]
public class VendorsController(VendorService vendors) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.VendorsView)]
    public async Task<ActionResult<PagedResult<VendorListItemDto>>> List([FromQuery] VendorListRequest request, CancellationToken ct) => await vendors.ListAsync(request, ct);

    [HttpPost, HasPermission(Permissions.VendorsManage)]
    [ProducesResponseType<VendorDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveVendorRequest request, CancellationToken ct)
    {
        var v = await vendors.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = v.Id }, await vendors.GetAsync(v.Id, ct));
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.VendorsView)]
    public async Task<ActionResult<VendorDetailDto>> Get(long id, CancellationToken ct) => await vendors.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.VendorsManage)]
    public async Task<ActionResult<VendorDto>> Update(long id, SaveVendorRequest request, CancellationToken ct) => await vendors.UpdateAsync(id, request, ct);
}

/// <summary>Vendor invoices (each registers a payable expense).</summary>
[ApiController, Authorize]
[Route("api/v1/vendor-invoices")]
public class VendorInvoicesController(VendorInvoiceService invoices) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.VendorsView)]
    public async Task<ActionResult<PagedResult<VendorInvoiceListItemDto>>> List([FromQuery] VendorInvoiceListRequest request, CancellationToken ct) => await invoices.ListAsync(request, ct);

    [HttpPost, HasPermission(Permissions.VendorsManage)]
    [ProducesResponseType<VendorInvoiceListItemDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateVendorInvoiceRequest request, CancellationToken ct)
    {
        var i = await invoices.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = i.Id }, i);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.VendorsView)]
    public async Task<ActionResult<VendorInvoiceListItemDto>> Get(long id, CancellationToken ct) => await invoices.GetAsync(id, ct);
}
