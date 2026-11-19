using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.LandManagement;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/projects/{projectId:long}/land")]
public class ProjectLandController(LandService land) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.LandView)]
    public async Task<ActionResult<LandOverviewDto>> Overview(long projectId, CancellationToken ct) => await land.GetOverviewAsync(projectId, ct);

    [HttpPost("parcels"), HasPermission(Permissions.LandManage)]
    public async Task<ActionResult<LandParcelDto>> AddParcel(long projectId, AddLandParcelRequest request, CancellationToken ct) => await land.AddParcelAsync(projectId, request, ct);

    [HttpPost("acquisitions"), HasPermission(Permissions.LandManage)]
    public async Task<ActionResult<LandAcquisitionDto>> Acquire(long projectId, AcquireLandRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await land.AcquireAsync(projectId, request, null, ct));

    [HttpPost("adjustments"), HasPermission(Permissions.LandAdjust)]
    public async Task<ActionResult<LandAdjustmentDto>> Adjust(long projectId, AdjustLandRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await land.AdjustAsync(projectId, request, null, ct));

    [HttpPut("distribution"), HasPermission(Permissions.LandManage)]
    public async Task<ActionResult<LandOverviewDto>> SetDistribution(long projectId, SetLandDistributionRequest request, CancellationToken ct) => await land.SetDistributionAsync(projectId, request, ct);

    [HttpGet("history"), HasPermission(Permissions.LandView)]
    public async Task<ActionResult<PagedResult<AreaHistoryDto>>> History(long projectId, [FromQuery] PagedRequest request, CancellationToken ct) => await land.GetAreaHistoryAsync(projectId, request, ct);

    [HttpGet("distribution/history"), HasPermission(Permissions.LandView)]
    public async Task<ActionResult<PagedResult<DistributionHistoryDto>>> DistributionHistory(long projectId, [FromQuery] PagedRequest request, CancellationToken ct) => await land.GetDistributionHistoryAsync(projectId, request, ct);

    [HttpGet("documents"), HasPermission(Permissions.LandView)]
    public async Task<ActionResult<IReadOnlyList<LandDocumentDto>>> Documents(long projectId, CancellationToken ct) => Ok(await land.ListDocumentsAsync(projectId, ct));

    [HttpPost("documents"), HasPermission(Permissions.LandManage), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<LandDocumentDto>> Upload(long projectId, IFormFile file, [FromForm] LandDocumentType type = LandDocumentType.Other, [FromForm] string? title = null,
        [FromForm] long? parcelId = null, [FromForm] long? acquisitionId = null, [FromForm] long? adjustmentId = null, CancellationToken ct = default) =>
        await land.UploadDocumentAsync(projectId, new LandDocumentUpload(file.ToUpload(), type, title), parcelId, acquisitionId, adjustmentId, ct);

    [HttpGet("documents/{documentId:long}/download"), HasPermission(Permissions.LandView)]
    public async Task<IActionResult> Download(long projectId, long documentId, CancellationToken ct)
    {
        var f = await land.DownloadDocumentAsync(projectId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("documents/{documentId:long}"), HasPermission(Permissions.LandManage)]
    public async Task<IActionResult> DeleteDocument(long projectId, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await land.DeleteDocumentAsync(projectId, documentId, reason, ct);
        return NoContent();
    }
}
