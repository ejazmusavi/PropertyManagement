using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Inventory;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Inventory setup (categories, types, blocks, sectors, streets), summaries and reports.</summary>
[ApiController, Authorize]
[Route("api/v1")]
public class InventoryController(PropertySetupService setup, InventoryReportService reports, PropertyService properties) : ControllerBase
{
    [HttpGet("projects/{id:long}/inventory-summary"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<InventorySummaryDto>> Summary(long id, CancellationToken ct) => await reports.GetProjectSummaryAsync(id, ct);

    [HttpGet("inventory/reports"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<InventoryReportDto>> Report([FromQuery] long? projectId, CancellationToken ct) => await reports.GetReportAsync(projectId, ct);

    [HttpGet("inventory/history"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<PagedResult<PropertyStatusHistoryDto>>> History([FromQuery] InventoryHistoryRequest request, CancellationToken ct) => await properties.ListHistoryAsync(request, ct);

    [HttpGet("inventory/approvers"), HasPermission(Permissions.InventoryReserve)]
    public async Task<ActionResult<IReadOnlyList<UserLookupDto>>> Approvers(CancellationToken ct) => Ok(await properties.ListApproversAsync(ct));

    [HttpPost("inventory/expire-reservations"), HasPermission(Permissions.InventoryReserve)]
    public async Task<IActionResult> Expire(CancellationToken ct) => Ok(new { expired = await properties.ExpireOverdueAsync(ct) });

    // ----- categories -----
    [HttpGet("property-categories"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken ct) => Ok(await setup.ListCategoriesAsync(ct));

    [HttpPost("property-categories"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<CategoryDto>> CreateCategory(SaveCategoryRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await setup.CreateCategoryAsync(request, ct));

    [HttpPut("property-categories/{id:long}"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<CategoryDto>> UpdateCategory(long id, SaveCategoryRequest request, CancellationToken ct) => await setup.UpdateCategoryAsync(id, request, ct);

    // ----- types -----
    [HttpGet("property-types"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<PropertyTypeDto>>> Types([FromQuery] bool activeOnly, CancellationToken ct) => Ok(await setup.ListTypesAsync(activeOnly, ct));

    [HttpPost("property-types"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<PropertyTypeDto>> CreateType(SavePropertyTypeRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await setup.CreateTypeAsync(request, ct));

    [HttpPut("property-types/{id:long}"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<PropertyTypeDto>> UpdateType(long id, SavePropertyTypeRequest request, CancellationToken ct) => await setup.UpdateTypeAsync(id, request, ct);

    // ----- blocks / sectors / streets -----
    [HttpGet("projects/{projectId:long}/blocks"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<ZoneDto>>> Blocks(long projectId, CancellationToken ct) => Ok(await setup.ListZonesAsync(ZoneKind.Block, projectId, false, ct));
    [HttpPost("projects/{projectId:long}/blocks"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> CreateBlock(long projectId, SaveZoneRequest r, CancellationToken ct) => StatusCode(StatusCodes.Status201Created, await setup.CreateZoneAsync(ZoneKind.Block, projectId, r, ct));
    [HttpPut("projects/{projectId:long}/blocks/{id:long}"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> UpdateBlock(long projectId, long id, SaveZoneRequest r, CancellationToken ct) => await setup.UpdateZoneAsync(ZoneKind.Block, projectId, id, r, ct);

    [HttpGet("projects/{projectId:long}/sectors"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<ZoneDto>>> Sectors(long projectId, CancellationToken ct) => Ok(await setup.ListZonesAsync(ZoneKind.Sector, projectId, false, ct));
    [HttpPost("projects/{projectId:long}/sectors"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> CreateSector(long projectId, SaveZoneRequest r, CancellationToken ct) => StatusCode(StatusCodes.Status201Created, await setup.CreateZoneAsync(ZoneKind.Sector, projectId, r, ct));
    [HttpPut("projects/{projectId:long}/sectors/{id:long}"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> UpdateSector(long projectId, long id, SaveZoneRequest r, CancellationToken ct) => await setup.UpdateZoneAsync(ZoneKind.Sector, projectId, id, r, ct);

    [HttpGet("projects/{projectId:long}/streets"), HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<ZoneDto>>> Streets(long projectId, CancellationToken ct) => Ok(await setup.ListZonesAsync(ZoneKind.Street, projectId, false, ct));
    [HttpPost("projects/{projectId:long}/streets"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> CreateStreet(long projectId, SaveZoneRequest r, CancellationToken ct) => StatusCode(StatusCodes.Status201Created, await setup.CreateZoneAsync(ZoneKind.Street, projectId, r, ct));
    [HttpPut("projects/{projectId:long}/streets/{id:long}"), HasPermission(Permissions.InventorySetup)]
    public async Task<ActionResult<ZoneDto>> UpdateStreet(long projectId, long id, SaveZoneRequest r, CancellationToken ct) => await setup.UpdateZoneAsync(ZoneKind.Street, projectId, id, r, ct);
}
