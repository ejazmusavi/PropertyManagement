using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/properties")]
public class PropertiesController(PropertyService properties) : ControllerBase
{
    /// <summary>Lists properties in the caller's projects (server-side paging, search, filters, sort).</summary>
    [HttpGet, HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<PagedResult<PropertyListItemDto>>> List([FromQuery] PropertyListRequest request, CancellationToken ct) => await properties.ListAsync(request, ct);

    /// <summary>Lists available properties only; expired reservations are returned to stock first.</summary>
    [HttpGet("available"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<PagedResult<PropertyListItemDto>>> Available([FromQuery] PropertyListRequest request, CancellationToken ct) => await properties.ListAvailableAsync(request, ct);

    [HttpPost, HasPermission(Permissions.InventoryManage)]
    [ProducesResponseType<PropertyDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePropertyRequest request, CancellationToken ct)
    {
        var created = await properties.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<PropertyDetailDto>> Get(long id, CancellationToken ct) => await properties.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.InventoryManage)]
    public async Task<ActionResult<PropertyDetailDto>> Update(long id, UpdatePropertyRequest request, CancellationToken ct) => await properties.UpdateAsync(id, request, ct);

    [HttpPost("{id:long}/reserve"), HasPermission(Permissions.InventoryReserve)]
    public async Task<ActionResult<PropertyDetailDto>> Reserve(long id, ReservePropertyRequest request, CancellationToken ct) => await properties.ReserveAsync(id, request, ct);

    [HttpPost("{id:long}/release-reservation"), HasPermission(Permissions.InventoryReserve)]
    public async Task<ActionResult<PropertyDetailDto>> Release(long id, ReleaseReservationRequest request, CancellationToken ct) => await properties.ReleaseReservationAsync(id, request, ct);

    [HttpPost("{id:long}/status"), HasPermission(Permissions.InventoryStatus)]
    public async Task<ActionResult<PropertyDetailDto>> ChangeStatus(long id, ChangePropertyStatusRequest request, CancellationToken ct) => await properties.ChangeStatusAsync(id, request, ct);

    [HttpGet("{id:long}/reservations"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<ReservationDto>>> Reservations(long id, CancellationToken ct) => Ok(await properties.ListReservationsAsync(id, ct));

    [HttpGet("{id:long}/history"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<PagedResult<PropertyStatusHistoryDto>>> History(long id, [FromQuery] InventoryHistoryRequest request, CancellationToken ct)
    {
        request.PropertyId = id;
        return await properties.ListHistoryAsync(request, ct);
    }

    // ----- documents -----
    [HttpGet("{id:long}/documents"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<PropertyDocumentDto>>> Documents(long id, CancellationToken ct) => Ok(await properties.ListDocumentsAsync(id, ct));

    [HttpPost("{id:long}/documents"), HasPermission(Permissions.InventoryDocuments), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<PropertyDocumentDto>> Upload(long id, IFormFile file, [FromForm] PropertyDocumentType type = PropertyDocumentType.Other, [FromForm] string? title = null, CancellationToken ct = default) =>
        await properties.UploadDocumentAsync(id, type, title, file.ToUpload(), ct);

    [HttpGet("{id:long}/documents/{documentId:long}/download"), HasPermission(Permissions.InventoryView)]
    public async Task<IActionResult> Download(long id, long documentId, CancellationToken ct)
    {
        var f = await properties.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/documents/{documentId:long}"), HasPermission(Permissions.InventoryDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await properties.DeleteDocumentAsync(id, documentId, reason, ct);
        return NoContent();
    }
}
