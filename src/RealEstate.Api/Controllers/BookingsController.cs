using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/bookings")]
public class BookingsController(BookingService bookings) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.BookingsView)]
    public async Task<ActionResult<PagedResult<BookingListItemDto>>> List([FromQuery] BookingListRequest request, CancellationToken ct) => await bookings.ListAsync(request, ct);

    /// <summary>Creates a booking (Draft, or PendingApproval when submitForApproval is true). Prices come from the pricing engine and are stored as an immutable snapshot.</summary>
    [HttpPost, HasPermission(Permissions.BookingsCreate)]
    [ProducesResponseType<BookingDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateBookingRequest request, CancellationToken ct)
    {
        var created = await bookings.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.BookingsView)]
    public async Task<ActionResult<BookingDetailDto>> Get(long id, CancellationToken ct) => await bookings.GetAsync(id, ct);

    [HttpGet("quote"), HasPermission(Permissions.BookingsCreate)]
    public async Task<ActionResult<PriceCalculationDto>> Quote([FromQuery] long propertyId, [FromQuery] long? customerId, CancellationToken ct) => await bookings.QuoteAsync(propertyId, customerId, ct);

    [HttpPost("{id:long}/submit"), HasPermission(Permissions.BookingsCreate)]
    public async Task<ActionResult<BookingDetailDto>> Submit(long id, SubmitBookingRequest request, CancellationToken ct) => await bookings.SubmitAsync(id, request, ct);

    [HttpPost("{id:long}/approve"), HasPermission(Permissions.BookingsApprove)]
    public async Task<ActionResult<BookingDetailDto>> Approve(long id, ApproveBookingRequest request, CancellationToken ct) => await bookings.ApproveAsync(id, request, ct);

    [HttpPost("{id:long}/reject"), HasPermission(Permissions.BookingsApprove)]
    public async Task<ActionResult<BookingDetailDto>> Reject(long id, RejectBookingRequest request, CancellationToken ct) => await bookings.RejectAsync(id, request, ct);

    [HttpPost("{id:long}/cancel"), HasPermission(Permissions.BookingsCancel)]
    public async Task<ActionResult<BookingDetailDto>> Cancel(long id, CancelBookingRequest request, CancellationToken ct) => await bookings.CancelAsync(id, request, ct);

    [HttpPost("{id:long}/transfer"), HasPermission(Permissions.BookingsTransfer)]
    public async Task<ActionResult<BookingDetailDto>> Transfer(long id, TransferBookingRequest request, CancellationToken ct) => await bookings.TransferAsync(id, request, ct);

    [HttpPost("{id:long}/refunds/{refundId:long}/decision"), HasPermission(Permissions.BookingsApprove)]
    public async Task<ActionResult<BookingDetailDto>> DecideRefund(long id, long refundId, DecideRefundRequest request, CancellationToken ct) => await bookings.DecideRefundAsync(id, refundId, request, ct);

    // ----- agreement -----
    [HttpGet("{id:long}/agreement"), HasPermission(Permissions.BookingsView)]
    public async Task<ActionResult<AgreementDto>> Agreement(long id, [FromQuery] int? version, CancellationToken ct) => await bookings.GetAgreementAsync(id, version, ct);

    [HttpPost("{id:long}/agreement/documents"), HasPermission(Permissions.AgreementsDocuments), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<AgreementDocumentDto>> UploadAgreementDocument(long id, IFormFile file, [FromForm] AgreementDocumentType type = AgreementDocumentType.Signed, [FromForm] string? title = null, CancellationToken ct = default) =>
        await bookings.UploadAgreementDocumentAsync(id, type, title, file.ToUpload(), ct);

    [HttpGet("{id:long}/agreement/documents/{documentId:long}/download"), HasPermission(Permissions.BookingsView)]
    public async Task<IActionResult> DownloadAgreementDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await bookings.DownloadAgreementDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/agreement/documents/{documentId:long}"), HasPermission(Permissions.AgreementsDocuments)]
    public async Task<IActionResult> DeleteAgreementDocument(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await bookings.DeleteAgreementDocumentAsync(id, documentId, reason, ct);
        return NoContent();
    }
}
