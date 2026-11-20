using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Payment collection workflow: submit, verify, approve (posts, allocates, issues the receipt), allocate, reject, reverse.</summary>
[ApiController, Authorize]
[Route("api/v1/payments")]
public class PaymentsController(PaymentService payments, ReceiptService receipts) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<PagedResult<PaymentListItemDto>>> List([FromQuery] PaymentListRequest request, CancellationToken ct) => await payments.ListAsync(request, ct);

    /// <summary>
    /// Submits a payment. The idempotency key is required: repeating the request with the same key and values returns the original payment (no second payment
    /// or ledger entry). Customer-submitted proof and non-cash methods wait for verification; nothing is posted until approval.
    /// </summary>
    [HttpPost, HasPermission(Permissions.PaymentsCollect)]
    [ProducesResponseType<PaymentDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePaymentRequest request, CancellationToken ct)
    {
        var p = await payments.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<PaymentDetailDto>> Get(long id, CancellationToken ct) => await payments.GetAsync(id, ct);

    [HttpPost("{id:long}/verify"), HasPermission(Permissions.PaymentsVerify)]
    public async Task<ActionResult<PaymentDetailDto>> Verify(long id, VerifyPaymentRequest request, CancellationToken ct) => await payments.VerifyAsync(id, request, ct);

    /// <summary>Approves and posts the payment: allocation, receipt number, ledger entries and accounting outbox event in one transaction.</summary>
    [HttpPost("{id:long}/approve"), HasPermission(Permissions.PaymentsApprove)]
    public async Task<ActionResult<PaymentDetailDto>> Approve(long id, ApprovePaymentRequest request, CancellationToken ct) => await payments.ApproveAsync(id, request, ct);

    [HttpPost("{id:long}/reject"), Authorize]
    public async Task<ActionResult<PaymentDetailDto>> Reject(long id, RejectPaymentRequest request, CancellationToken ct) => await payments.RejectAsync(id, request, ct);

    /// <summary>Allocates the unallocated balance of a posted payment (oldest-due-first, manual, specific installment, charge-specific or advance).</summary>
    [HttpPost("{id:long}/allocate"), HasPermission(Permissions.PaymentsAllocate)]
    public async Task<ActionResult<PaymentDetailDto>> Allocate(long id, AllocatePaymentRequest request, CancellationToken ct) => await payments.AllocateAsync(id, request, ct);

    [HttpGet("{id:long}/allocation-options"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<AllocationOptionsDto>> AllocationOptions(long id, CancellationToken ct) => await payments.AllocationOptionsAsync(id, ct);

    [HttpPost("{id:long}/reverse"), HasPermission(Permissions.PaymentsReverse)]
    public async Task<ActionResult<PaymentDetailDto>> Reverse(long id, ReversePaymentRequest request, CancellationToken ct) => await payments.ReverseAsync(id, request, ct);

    [HttpGet("methods"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<IReadOnlyList<PaymentMethodDto>>> Methods(CancellationToken ct) => Ok(await payments.MethodsAsync(true, ct));

    [HttpGet("collection-context/{agreementId:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<CollectionContextDto>> CollectionContext(long agreementId, CancellationToken ct) => await payments.CollectionContextAsync(agreementId, ct);

    [HttpGet("receipts"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<PagedResult<ReceiptDto>>> Receipts([FromQuery] ReceiptListRequest request, CancellationToken ct) => await receipts.ListAsync(request, ct);

    [HttpGet("receipts/{receiptId:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<ReceiptDto>> Receipt(long receiptId, CancellationToken ct) => await receipts.GetAsync(receiptId, ct);

    /// <summary>Receipt PDF. The first copy is the original; later copies are marked as numbered duplicates.</summary>
    [HttpGet("receipts/{receiptId:long}/pdf"), HasPermission(Permissions.PaymentsReceipts)]
    public async Task<IActionResult> ReceiptPdf(long receiptId, CancellationToken ct)
    {
        var f = await receipts.PdfAsync(receiptId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }
}

/// <summary>Customer and agreement ledgers and property statements.</summary>
[ApiController, Authorize]
[Route("api/v1")]
public class LedgersController(LedgerService ledgers) : ControllerBase
{
    [HttpGet("customers/{id:long}/ledger"), HasPermission(Permissions.LedgersView)]
    public async Task<ActionResult<CustomerLedgerDto>> CustomerLedger(long id, [FromQuery] LedgerPageRequest page, CancellationToken ct) => await ledgers.GetCustomerLedgerAsync(id, page, ct);

    [HttpGet("agreements/{id:long}/ledger"), HasPermission(Permissions.LedgersView)]
    public async Task<ActionResult<AgreementLedgerDto>> AgreementLedger(long id, [FromQuery] LedgerPageRequest page, CancellationToken ct) => await ledgers.GetAgreementLedgerAsync(id, page, ct);

    [HttpGet("properties/{id:long}/statement"), HasPermission(Permissions.LedgersView)]
    public async Task<ActionResult<PropertyStatementDto>> PropertyStatement(long id, CancellationToken ct) => await ledgers.GetPropertyStatementAsync(id, ct);
}

/// <summary>Refund requests, maker-checker approval and payout.</summary>
[ApiController, Authorize]
[Route("api/v1/refunds")]
public class RefundsController(RefundService refunds) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<PagedResult<RefundListItemDto>>> List([FromQuery] RefundListRequest request, CancellationToken ct) => await refunds.ListAsync(request, ct);

    [HttpPost, HasPermission(Permissions.RefundsRequest)]
    [ProducesResponseType<RefundDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateRefundRequest request, CancellationToken ct)
    {
        var r = await refunds.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = r.Id }, r);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<RefundDetailDto>> Get(long id, CancellationToken ct) => await refunds.GetAsync(id, ct);

    [HttpGet("refundable/{agreementId:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<RefundableDto>> Refundable(long agreementId, CancellationToken ct) => await refunds.RefundableAsync(agreementId, ct);

    [HttpPost("{id:long}/approve"), HasPermission(Permissions.RefundsApprove)]
    public async Task<ActionResult<RefundDetailDto>> Approve(long id, ApproveRefundRequest request, CancellationToken ct) => await refunds.ApproveAsync(id, request, ct);

    [HttpPost("{id:long}/reject"), HasPermission(Permissions.RefundsApprove)]
    public async Task<ActionResult<RefundDetailDto>> Reject(long id, RejectRefundRequest request, CancellationToken ct) => await refunds.RejectAsync(id, request, ct);

    [HttpPost("{id:long}/cancel"), HasPermission(Permissions.RefundsRequest)]
    public async Task<ActionResult<RefundDetailDto>> Cancel(long id, CancelRefundRequest request, CancellationToken ct) => await refunds.CancelAsync(id, request, ct);

    [HttpPost("{id:long}/pay"), HasPermission(Permissions.RefundsPay)]
    public async Task<ActionResult<RefundDetailDto>> Pay(long id, PayRefundRequest request, CancellationToken ct) => await refunds.PayAsync(id, request, ct);
}
