using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Gateway;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Office-side management of the customer application login of a customer.</summary>
[ApiController, Authorize]
[Route("api/v1/customers/{customerId:long}/portal-account")]
public class CustomerPortalAccountsController(CustomerAccountService accounts) : ControllerBase
{
    /// <summary>The customer's application login, or 404 when the customer has none.</summary>
    [HttpGet, HasPermission(Permissions.PortalAccountsManage)]
    public async Task<ActionResult<CustomerAccountDto>> Get(long customerId, CancellationToken ct) =>
        await accounts.GetAsync(customerId, ct) is { } a ? a : NotFound();

    [HttpPost, HasPermission(Permissions.PortalAccountsManage)]
    [ProducesResponseType<CustomerAccountDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(long customerId, CreateCustomerAccountRequest request, CancellationToken ct) =>
        CreatedAtAction(nameof(Get), new { customerId }, await accounts.CreateAsync(customerId, request, ct));

    [HttpPost("disable"), HasPermission(Permissions.PortalAccountsManage)]
    public async Task<ActionResult<CustomerAccountDto>> Disable(long customerId, DisableCustomerAccountRequest request, CancellationToken ct) => await accounts.DisableAsync(customerId, request, ct);

    [HttpPost("enable"), HasPermission(Permissions.PortalAccountsManage)]
    public async Task<ActionResult<CustomerAccountDto>> Enable(long customerId, [FromQuery] string? rowVersion, CancellationToken ct) => await accounts.EnableAsync(customerId, rowVersion, ct);

    [HttpPost("reset-password"), HasPermission(Permissions.PortalAccountsManage)]
    public async Task<IActionResult> ResetPassword(long customerId, ResetCustomerPasswordRequest request, CancellationToken ct)
    {
        await accounts.ResetPasswordAsync(customerId, request, ct);
        return NoContent();
    }
}

/// <summary>Support desk for requests raised in the customer application.</summary>
[ApiController, Authorize]
[Route("api/v1/support-requests")]
public class SupportRequestsController(SupportDeskService desk) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.SupportView)]
    public async Task<ActionResult<PagedResult<SupportListItemDto>>> List([FromQuery] SupportListRequest request, CancellationToken ct) => await desk.ListAsync(request, ct);

    [HttpGet("{id:long}"), HasPermission(Permissions.SupportView)]
    public async Task<ActionResult<SupportDetailDto>> Get(long id, CancellationToken ct) => await desk.GetAsync(id, ct);

    [HttpPost("{id:long}/replies"), HasPermission(Permissions.SupportManage)]
    public async Task<ActionResult<SupportDetailDto>> Reply(long id, SupportStaffReplyRequest request, CancellationToken ct) => await desk.ReplyAsync(id, request, ct);

    [HttpPost("{id:long}/status"), HasPermission(Permissions.SupportManage)]
    public async Task<ActionResult<SupportDetailDto>> SetStatus(long id, SupportStatusRequest request, CancellationToken ct) => await desk.SetStatusAsync(id, request, ct);
}

/// <summary>Announcements, office notifications to customers and the payment instructions note.</summary>
[ApiController, Authorize]
[Route("api/v1/portal-admin")]
public class PortalContentController(PortalContentService content) : ControllerBase
{
    [HttpGet("announcements"), HasPermission(Permissions.PortalContentManage)]
    public async Task<ActionResult<IReadOnlyList<AnnouncementDto>>> Announcements(CancellationToken ct) => Ok(await content.ListAnnouncementsAsync(ct));

    [HttpGet("announcements/{id:long}"), HasPermission(Permissions.PortalContentManage)]
    public async Task<ActionResult<AnnouncementDto>> Announcement(long id, CancellationToken ct) => await content.GetAnnouncementAsync(id, ct);

    [HttpPost("announcements"), HasPermission(Permissions.PortalContentManage)]
    [ProducesResponseType<AnnouncementDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAnnouncement(SaveAnnouncementRequest request, CancellationToken ct)
    {
        var a = await content.SaveAnnouncementAsync(null, request, ct);
        return CreatedAtAction(nameof(Announcement), new { id = a.Id }, a);
    }

    [HttpPut("announcements/{id:long}"), HasPermission(Permissions.PortalContentManage)]
    public async Task<ActionResult<AnnouncementDto>> UpdateAnnouncement(long id, SaveAnnouncementRequest request, CancellationToken ct) => await content.SaveAnnouncementAsync(id, request, ct);

    [HttpPost("notifications"), HasPermission(Permissions.PortalContentManage)]
    public async Task<IActionResult> SendNotification(CreateNotificationRequest request, CancellationToken ct)
    {
        await content.SendNotificationAsync(request, ct);
        return NoContent();
    }

    [HttpGet("payment-instructions"), HasPermission(Permissions.PortalContentManage)]
    public async Task<ActionResult<PaymentInstructionsNoteDto>> Instructions(CancellationToken ct) => await content.GetInstructionsAsync(ct);

    [HttpPut("payment-instructions"), HasPermission(Permissions.PortalContentManage)]
    public async Task<ActionResult<PaymentInstructionsNoteDto>> SaveInstructions(PaymentInstructionsNoteDto request, CancellationToken ct) => await content.SaveInstructionsAsync(request, ct);
}

/// <summary>Proof files that customers attached to a payment, for the office to verify.</summary>
[ApiController, Authorize]
[Route("api/v1/payments/{paymentId:long}/proofs")]
public class PaymentProofsController(PaymentProofAdminService proofs) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.PaymentsView)]
    public async Task<ActionResult<IReadOnlyList<PaymentProofAdminDto>>> List(long paymentId, CancellationToken ct) => Ok(await proofs.ListAsync(paymentId, ct));

    [HttpGet("{documentId:long}"), HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Download(long paymentId, long documentId, CancellationToken ct)
    {
        var f = await proofs.DownloadAsync(paymentId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }
}

/// <summary>Online payment transactions: monitoring, reconciliation and refunds.</summary>
[ApiController, Authorize]
[Route("api/v1/gateway")]
public class GatewayController(GatewayQueryService query, GatewayReconciliationService reconciliation, GatewayRefundService refunds) : ControllerBase
{
    [HttpGet("transactions"), HasPermission(Permissions.GatewayView)]
    public async Task<ActionResult<PagedResult<GatewayTransactionDto>>> List([FromQuery] GatewayListRequest request, CancellationToken ct) => await query.ListAsync(request, ct);

    [HttpGet("transactions/{id:long}"), HasPermission(Permissions.GatewayView)]
    public async Task<ActionResult<GatewayTransactionDto>> Get(long id, CancellationToken ct) => await query.GetAsync(id, ct);

    [HttpGet("webhook-logs"), HasPermission(Permissions.GatewayView)]
    public async Task<ActionResult<PagedResult<WebhookLogDto>>> Webhooks([FromQuery] WebhookLogListRequest request, CancellationToken ct) => await query.WebhooksAsync(request, ct);

    /// <summary>Confirms every open transaction with the provider and creates the payments of confirmed successes.</summary>
    [HttpPost("reconcile"), HasPermission(Permissions.GatewayManage)]
    public async Task<ActionResult<ReconciliationResultDto>> Reconcile(CancellationToken ct) => await reconciliation.ReconcileAsync(ct);

    [HttpPost("transactions/{id:long}/reconcile"), HasPermission(Permissions.GatewayManage)]
    public async Task<ActionResult<ReconciliationItemDto>> ReconcileOne(long id, CancellationToken ct) => await reconciliation.ReconcileOneAsync(id, ct);

    [HttpPost("transactions/{id:long}/refund"), HasPermission(Permissions.GatewayManage)]
    public async Task<ActionResult<GatewayTransactionDto>> Refund(long id, GatewayRefundRequestDto request, CancellationToken ct) => await refunds.RefundAsync(id, request, ct);
}

/// <summary>
/// Provider callback endpoint (anonymous by necessity). Nothing in the body is trusted: the adapter verifies the signature, every delivery is logged, repeated
/// events are ignored and the transaction state is confirmed with the provider before anything is recorded.
/// </summary>
[ApiController, AllowAnonymous]
[Route("api/v1/payment-webhooks")]
public class PaymentWebhooksController(PaymentCallbackHandler handler) : ControllerBase
{
    [HttpPost("{provider}")]
    [Consumes("application/json", "text/plain", "application/x-www-form-urlencoded")]
    public async Task<IActionResult> Receive(string provider, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await handler.HandleAsync(provider, body, headers, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return result.Outcome switch
        {
            CallbackOutcome.InvalidSignature => Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid signature" }),
            CallbackOutcome.UnknownProvider => NotFound(),
            _ => Ok(new { status = result.Outcome.ToString() }),
        };
    }
}
