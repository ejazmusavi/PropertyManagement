using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.GovernmentFees;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Government fees: types, customer assessments, customer collections, payments to government and fee documents.</summary>
[ApiController, Authorize]
[Route("api/v1/government-fees")]
public class GovernmentFeesController(GovernmentFeeService fees) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<PagedResult<GovernmentFeeListItemDto>>> List([FromQuery] GovernmentFeeListRequest request, CancellationToken ct) => await fees.ListAsync(request, ct);

    [HttpGet("summary"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<GovernmentFeeSummaryDto>> Summary([FromQuery] long? projectId, CancellationToken ct) => await fees.SummaryAsync(projectId, ct);

    [HttpGet("{id:long}"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> Get(long id, CancellationToken ct) => await fees.GetAsync(id, ct);

    // ----- fee types -----
    [HttpGet("types"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<IReadOnlyList<GovernmentFeeTypeDto>>> Types([FromQuery] bool activeOnly, [FromQuery] long? projectId, CancellationToken ct) => Ok(await fees.ListTypesAsync(activeOnly, projectId, ct));

    [HttpPost("types"), HasPermission(Permissions.GovFeesSetup)]
    [ProducesResponseType<GovernmentFeeTypeDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateType(SaveGovernmentFeeTypeRequest request, CancellationToken ct)
    {
        var t = await fees.CreateTypeAsync(request, ct);
        return Created($"/api/v1/government-fees/types/{t.Id}", t);
    }

    [HttpGet("types/{id:long}"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<GovernmentFeeTypeDto>> GetType(long id, CancellationToken ct) => await fees.GetTypeAsync(id, ct);

    [HttpPut("types/{id:long}"), HasPermission(Permissions.GovFeesSetup)]
    public async Task<ActionResult<GovernmentFeeTypeDto>> UpdateType(long id, SaveGovernmentFeeTypeRequest request, CancellationToken ct) => await fees.UpdateTypeAsync(id, request, ct);

    // ----- assessments -----
    [HttpPost("assessments"), HasPermission(Permissions.GovFeesAssess)]
    [ProducesResponseType<GovernmentFeeDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Assess(CreateGovernmentFeeAssessmentRequest request, CancellationToken ct)
    {
        var a = await fees.AssessAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = a.Id }, a);
    }

    [HttpPost("{id:long}/cancel"), HasPermission(Permissions.GovFeesAssess)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> Cancel(long id, CancelGovernmentFeeAssessmentRequest request, CancellationToken ct) => await fees.CancelAsync(id, request, ct);

    // ----- customer collections -----
    [HttpPost("{id:long}/collections"), HasPermission(Permissions.GovFeesAssess)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> Collect(long id, FeeCollectionRequest request, CancellationToken ct) => await fees.CollectAsync(id, request, ct);

    [HttpPost("collections/{collectionId:long}/reverse"), HasPermission(Permissions.GovFeesPay)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> ReverseCollection(long collectionId, ReverseFeeEntryRequest request, CancellationToken ct) => await fees.ReverseCollectionAsync(collectionId, request, ct);

    // ----- payments to government -----
    [HttpGet("payments"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<PagedResult<GovernmentFeePaymentDto>>> Payments([FromQuery] GovernmentPaymentListRequest request, CancellationToken ct) => await fees.ListPaymentsAsync(request, ct);

    [HttpPost("{id:long}/payments"), HasPermission(Permissions.GovFeesPay)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> Pay(long id, GovernmentPaymentRequest request, CancellationToken ct) => await fees.PayAsync(id, request, ct);

    [HttpPost("payments/{paymentId:long}/reverse"), HasPermission(Permissions.GovFeesPay)]
    public async Task<ActionResult<GovernmentFeeDetailDto>> ReversePayment(long paymentId, ReverseFeeEntryRequest request, CancellationToken ct) => await fees.ReversePaymentAsync(paymentId, request, ct);

    // ----- documents -----
    [HttpGet("{id:long}/documents"), HasPermission(Permissions.GovFeesView)]
    public async Task<ActionResult<IReadOnlyList<GovernmentFeeDocumentDto>>> Documents(long id, CancellationToken ct) => Ok(await fees.ListDocumentsAsync(id, ct));

    [HttpPost("{id:long}/documents"), HasPermission(Permissions.GovFeesAssess), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<GovernmentFeeDocumentDto>> Upload(long id, IFormFile file, [FromForm] GovernmentFeeDocumentType type = GovernmentFeeDocumentType.GovernmentReceipt, [FromForm] string? title = null, CancellationToken ct = default) =>
        await fees.UploadDocumentAsync(id, type, title, file.ToUpload(), ct);

    [HttpGet("{id:long}/documents/{documentId:long}/download"), HasPermission(Permissions.GovFeesView)]
    public async Task<IActionResult> Download(long id, long documentId, CancellationToken ct)
    {
        var f = await fees.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/documents/{documentId:long}"), HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await fees.DeleteDocumentAsync(id, documentId, reason, ct);
        return NoContent();
    }
}
