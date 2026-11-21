using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>
/// Customer application API. The customer is always derived from the authenticated login (customer account link); no customer id is ever accepted from the
/// client, and records of other customers answer 404. Only logins holding <c>portal.access</c> (the Customer role) can call it.
/// </summary>
[ApiController, Authorize, HasPermission(Permissions.PortalAccess)]
[Route("api/v1/portal")]
public class PortalController(CustomerPortalService portal) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<PortalDashboardDto>> Dashboard(CancellationToken ct) => await portal.DashboardAsync(ct);

    [HttpGet("properties")]
    public async Task<ActionResult<IReadOnlyList<PortalPropertyDto>>> Properties(CancellationToken ct) => Ok(await portal.PropertiesAsync(ct));

    [HttpGet("agreements/{agreementId:long}")]
    public async Task<ActionResult<PortalAgreementDto>> Agreement(long agreementId, CancellationToken ct) => await portal.AgreementAsync(agreementId, ct);

    [HttpGet("agreements/{agreementId:long}/schedule")]
    public async Task<ActionResult<PortalScheduleDto>> Schedule(long agreementId, CancellationToken ct) => await portal.ScheduleAsync(agreementId, ct);

    [HttpGet("agreements/{agreementId:long}/ledger")]
    public async Task<ActionResult<AgreementLedgerDto>> AgreementLedger(long agreementId, [FromQuery] LedgerPageRequest page, CancellationToken ct) => await portal.AgreementLedgerAsync(agreementId, page, ct);

    [HttpGet("ledger")]
    public async Task<ActionResult<CustomerLedgerDto>> Ledger([FromQuery] LedgerPageRequest page, CancellationToken ct) => await portal.LedgerAsync(page, ct);

    // ---------- payments ----------
    [HttpGet("payments")]
    public async Task<ActionResult<PagedResult<PortalPaymentDto>>> Payments([FromQuery] PortalPaymentListRequest request, CancellationToken ct) => await portal.PaymentsAsync(request, ct);

    [HttpGet("payments/{id:long}")]
    public async Task<ActionResult<PortalPaymentDetailDto>> Payment(long id, CancellationToken ct) => await portal.PaymentAsync(id, ct);

    /// <summary>
    /// Payment proof submission (multipart/form-data: the fields of the request plus the file in <c>proof</c>). The idempotency key makes retries safe. The payment is
    /// verified by the office before it can be approved; nothing is credited by this call.
    /// </summary>
    [HttpPost("payments")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(26_214_400)]
    [ProducesResponseType<PortalPaymentDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> SubmitProof([FromForm] SubmitPaymentProofRequest request, IFormFile? proof, CancellationToken ct)
    {
        await using var _ = proof?.OpenReadStream();
        var result = await portal.SubmitProofAsync(request, proof?.ToUpload(), ct);
        return CreatedAtAction(nameof(Payment), new { id = result.Payment.Id }, result);
    }

    [HttpGet("payments/{paymentId:long}/proofs/{documentId:long}")]
    public async Task<IActionResult> Proof(long paymentId, long documentId, CancellationToken ct)
    {
        var f = await portal.ProofAsync(paymentId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    /// <summary>Receipt PDF of one of the customer's own posted payments.</summary>
    [HttpGet("receipts/{receiptId:long}/pdf")]
    public async Task<IActionResult> ReceiptPdf(long receiptId, CancellationToken ct)
    {
        var f = await portal.ReceiptPdfAsync(receiptId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpGet("payment-instructions")]
    public async Task<ActionResult<PortalPaymentInstructionsDto>> Instructions(CancellationToken ct) => await portal.InstructionsAsync(ct);

    // ---------- online payment ----------
    /// <summary>Starts an online payment when the company has enabled a provider; otherwise 422. Retrying with the same idempotency key returns the same transaction.</summary>
    [HttpPost("online-payments")]
    [ProducesResponseType<PortalOnlinePaymentDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> InitiateOnline(InitiateOnlinePaymentRequest request, CancellationToken ct)
    {
        var t = await portal.InitiateOnlinePaymentAsync(request, ct);
        return CreatedAtAction(nameof(OnlinePayment), new { id = t.Id }, t);
    }

    [HttpGet("online-payments/{id:long}")]
    public async Task<ActionResult<PortalOnlinePaymentDto>> OnlinePayment(long id, CancellationToken ct) => await portal.OnlinePaymentAsync(id, ct);

    // ---------- notifications, announcements ----------
    [HttpGet("notifications")]
    public async Task<ActionResult<PortalNotificationListDto>> Notifications([FromQuery] PagedRequest request, CancellationToken ct) => await portal.NotificationsAsync(request, ct);

    [HttpPost("notifications/{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken ct)
    {
        await portal.MarkNotificationReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await portal.MarkAllNotificationsReadAsync(ct);
        return NoContent();
    }

    [HttpGet("announcements")]
    public async Task<ActionResult<IReadOnlyList<PortalAnnouncementDto>>> Announcements(CancellationToken ct) => Ok(await portal.AnnouncementsAsync(ct));

    // ---------- support ----------
    [HttpGet("support-requests")]
    public async Task<ActionResult<IReadOnlyList<PortalSupportRequestDto>>> SupportRequests(CancellationToken ct) => Ok(await portal.SupportRequestsAsync(ct));

    [HttpPost("support-requests")]
    [ProducesResponseType<PortalSupportRequestDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateSupport(CreateSupportRequestRequest request, CancellationToken ct)
    {
        var s = await portal.CreateSupportRequestAsync(request, ct);
        return CreatedAtAction(nameof(Support), new { id = s.Request.Id }, s);
    }

    [HttpGet("support-requests/{id:long}")]
    public async Task<ActionResult<PortalSupportRequestDetailDto>> Support(long id, CancellationToken ct) => await portal.SupportDetailAsync(id, ct);

    [HttpPost("support-requests/{id:long}/messages")]
    public async Task<ActionResult<PortalSupportRequestDetailDto>> ReplySupport(long id, SupportReplyRequest request, CancellationToken ct) => await portal.ReplySupportAsync(id, request, ct);

    // ---------- profile ----------
    [HttpGet("profile")]
    public async Task<ActionResult<PortalProfileDto>> Profile(CancellationToken ct) => await portal.ProfileAsync(ct);

    [HttpPut("profile")]
    public async Task<ActionResult<PortalProfileDto>> UpdateProfile(UpdatePortalProfileRequest request, CancellationToken ct) => await portal.UpdateProfileAsync(request, ct);

    [HttpPost("profile/change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequestDto request, CancellationToken ct)
    {
        await portal.ChangePasswordAsync(request, ct);
        return NoContent();
    }
}
