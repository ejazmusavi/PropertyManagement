using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Gis;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>GIS foundation: spatial reference systems, surveyed boundaries, map layers and map documents. No map rendering; stores verified survey data.</summary>
[ApiController, Authorize]
[Route("api/v1/gis")]
public class GisController(GisService gis) : ControllerBase
{
    [HttpGet("summary"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<GisSummaryDto>>> Summary(CancellationToken ct) => Ok(await gis.SummaryAsync(ct));

    // ----- spatial reference systems -----
    [HttpGet("spatial-reference-systems"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<SrsDto>>> Srs([FromQuery] bool activeOnly, CancellationToken ct) => Ok(await gis.ListSrsAsync(activeOnly, ct));

    [HttpPost("spatial-reference-systems"), HasPermission(Permissions.GisManage)]
    public async Task<ActionResult<SrsDto>> CreateSrs(SaveSrsRequest request, CancellationToken ct) => StatusCode(StatusCodes.Status201Created, await gis.SaveSrsAsync(null, request, ct));

    [HttpPut("spatial-reference-systems/{id:long}"), HasPermission(Permissions.GisManage)]
    public async Task<ActionResult<SrsDto>> UpdateSrs(long id, SaveSrsRequest request, CancellationToken ct) => await gis.SaveSrsAsync(id, request, ct);

    // ----- project boundaries -----
    [HttpGet("projects/{projectId:long}/boundaries"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<BoundaryDto>>> ProjectBoundaries(long projectId, CancellationToken ct) => Ok(await gis.ListProjectBoundariesAsync(projectId, ct));

    [HttpGet("projects/{projectId:long}/boundaries/{id:long}"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<BoundaryDto>> ProjectBoundary(long projectId, long id, CancellationToken ct) => await gis.GetProjectBoundaryAsync(projectId, id, ct);

    [HttpPost("projects/{projectId:long}/boundaries"), HasPermission(Permissions.GisManage), RequestSizeLimit(5_000_000)]
    public async Task<ActionResult<BoundaryDto>> SubmitProjectBoundary(long projectId, SubmitBoundaryRequest request, CancellationToken ct)
    {
        var b = await gis.SubmitProjectBoundaryAsync(projectId, request, ct);
        return CreatedAtAction(nameof(ProjectBoundary), new { projectId, id = b.Id }, b);
    }

    [HttpPost("projects/{projectId:long}/boundaries/{id:long}/decision"), HasPermission(Permissions.GisVerify)]
    public async Task<ActionResult<BoundaryDto>> DecideProjectBoundary(long projectId, long id, BoundaryDecisionRequest request, CancellationToken ct) => await gis.DecideProjectBoundaryAsync(projectId, id, request, ct);

    /// <summary>GeoJSON FeatureCollection of the verified boundaries of the project and its properties.</summary>
    [HttpGet("projects/{projectId:long}/geojson"), HasPermission(Permissions.GisView)]
    public async Task<IActionResult> GeoJson(long projectId, CancellationToken ct) => Content(await gis.ExportGeoJsonAsync(projectId, ct), "application/geo+json");

    // ----- property boundaries -----
    [HttpGet("properties/{propertyId:long}/boundaries"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<BoundaryDto>>> PropertyBoundaries(long propertyId, CancellationToken ct) => Ok(await gis.ListPropertyBoundariesAsync(propertyId, ct));

    [HttpGet("properties/{propertyId:long}/boundaries/{id:long}"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<BoundaryDto>> PropertyBoundary(long propertyId, long id, CancellationToken ct) => await gis.GetPropertyBoundaryAsync(propertyId, id, ct);

    [HttpPost("properties/{propertyId:long}/boundaries"), HasPermission(Permissions.GisManage), RequestSizeLimit(5_000_000)]
    public async Task<ActionResult<BoundaryDto>> SubmitPropertyBoundary(long propertyId, SubmitBoundaryRequest request, CancellationToken ct)
    {
        var b = await gis.SubmitPropertyBoundaryAsync(propertyId, request, ct);
        return CreatedAtAction(nameof(PropertyBoundary), new { propertyId, id = b.Id }, b);
    }

    [HttpPost("properties/{propertyId:long}/boundaries/{id:long}/decision"), HasPermission(Permissions.GisVerify)]
    public async Task<ActionResult<BoundaryDto>> DecidePropertyBoundary(long propertyId, long id, BoundaryDecisionRequest request, CancellationToken ct) => await gis.DecidePropertyBoundaryAsync(propertyId, id, request, ct);

    // ----- layers -----
    [HttpGet("projects/{projectId:long}/layers"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<MapLayerDto>>> Layers(long projectId, CancellationToken ct) => Ok(await gis.ListLayersAsync(projectId, ct));

    [HttpGet("projects/{projectId:long}/layers/{id:long}"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<MapLayerDto>> Layer(long projectId, long id, CancellationToken ct) => await gis.GetLayerAsync(projectId, id, ct);

    [HttpPost("projects/{projectId:long}/layers"), HasPermission(Permissions.GisManage)]
    public async Task<ActionResult<MapLayerDto>> CreateLayer(long projectId, SaveMapLayerRequest request, CancellationToken ct)
    {
        var l = await gis.SaveLayerAsync(projectId, null, request, ct);
        return CreatedAtAction(nameof(Layer), new { projectId, id = l.Id }, l);
    }

    [HttpPut("projects/{projectId:long}/layers/{id:long}"), HasPermission(Permissions.GisManage)]
    public async Task<ActionResult<MapLayerDto>> UpdateLayer(long projectId, long id, SaveMapLayerRequest request, CancellationToken ct) => await gis.SaveLayerAsync(projectId, id, request, ct);

    // ----- map documents -----
    [HttpGet("projects/{projectId:long}/documents"), HasPermission(Permissions.GisView)]
    public async Task<ActionResult<IReadOnlyList<MapDocumentDto>>> Documents(long projectId, CancellationToken ct) => Ok(await gis.ListDocumentsAsync(projectId, ct));

    [HttpPost("projects/{projectId:long}/documents"), HasPermission(Permissions.GisManage), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<MapDocumentDto>> Upload(long projectId, IFormFile file, [FromForm] MapDocumentType type = MapDocumentType.SurveyPlan, [FromForm] string? title = null,
        [FromForm] long? srsId = null, [FromForm] string? surveyReference = null, [FromForm] DateOnly? surveyDate = null, [FromForm] string? description = null, CancellationToken ct = default) =>
        await gis.UploadDocumentAsync(projectId, type, title, srsId, surveyReference, surveyDate, description, file.ToUpload(), ct);

    [HttpGet("projects/{projectId:long}/documents/{documentId:long}/download"), HasPermission(Permissions.GisView)]
    public async Task<IActionResult> Download(long projectId, long documentId, CancellationToken ct)
    {
        var f = await gis.DownloadDocumentAsync(projectId, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("projects/{projectId:long}/documents/{documentId:long}"), HasPermission(Permissions.GisManage)]
    public async Task<IActionResult> DeleteDocument(long projectId, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await gis.DeleteDocumentAsync(projectId, documentId, reason, ct);
        return NoContent();
    }
}
