using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/projects")]
public class ProjectsController(ProjectService projects, ProjectAccessAdminService projectAccess) : ControllerBase
{
    /// <summary>Lists the projects the caller may access (server-side paging, search, filter and sort).</summary>
    [HttpGet, HasPermission(Permissions.ProjectsView)]
    public async Task<ActionResult<PagedResult<ProjectListItemDto>>> List([FromQuery] ProjectListRequest request, CancellationToken ct) =>
        await projects.ListAsync(request, ct);

    [HttpPost, HasPermission(Permissions.ProjectsCreate)]
    [ProducesResponseType<ProjectDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken ct)
    {
        var created = await projects.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.ProjectsView)]
    public async Task<ActionResult<ProjectDetailDto>> Get(long id, CancellationToken ct) => await projects.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.ProjectsEdit)]
    public async Task<ActionResult<ProjectDetailDto>> Update(long id, UpdateProjectRequest request, CancellationToken ct) => await projects.UpdateAsync(id, request, ct);

    [HttpPost("{id:long}/status"), HasPermission(Permissions.ProjectsStatus)]
    public async Task<ActionResult<ProjectDetailDto>> ChangeStatus(long id, ChangeProjectStatusRequest request, CancellationToken ct) => await projects.ChangeStatusAsync(id, request, ct);

    // ----- documents -----
    [HttpGet("{id:long}/documents"), HasPermission(Permissions.ProjectsView)]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> Documents(long id, CancellationToken ct) => Ok(await projects.ListDocumentsAsync(id, ct));

    [HttpPost("{id:long}/documents"), HasPermission(Permissions.ProjectsDocuments), RequestSizeLimit(110_000_000)]
    public async Task<ActionResult<DocumentDto>> Upload(long id, IFormFile file, [FromForm] ProjectDocumentType type = ProjectDocumentType.Other, [FromForm] string? title = null, [FromForm] string? description = null, CancellationToken ct = default) =>
        await projects.UploadDocumentAsync(id, type, title, description, file.ToUpload(), ct);

    [HttpGet("{id:long}/documents/{documentId:long}/download"), HasPermission(Permissions.ProjectsView)]
    public async Task<IActionResult> Download(long id, long documentId, CancellationToken ct)
    {
        var f = await projects.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpDelete("{id:long}/documents/{documentId:long}"), HasPermission(Permissions.ProjectsDocuments)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, [FromQuery] string reason, CancellationToken ct)
    {
        await projects.DeleteDocumentAsync(id, documentId, reason, ct);
        return NoContent();
    }

    // ----- project access assignment -----
    [HttpGet("{id:long}/access"), HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<ActionResult<IReadOnlyList<ProjectAccessUserDto>>> Access(long id, CancellationToken ct) => Ok(await projectAccess.ListAsync(id, ct));

    [HttpPost("{id:long}/access"), HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<IActionResult> Assign(long id, AssignProjectAccessRequest request, CancellationToken ct)
    {
        await projectAccess.AssignAsync(id, request.UserId, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}/access/{userId:guid}"), HasPermission(Permissions.ProjectsAccessAssign)]
    public async Task<IActionResult> Revoke(long id, Guid userId, [FromQuery] string reason, CancellationToken ct)
    {
        await projectAccess.RevokeAsync(id, userId, reason, ct);
        return NoContent();
    }
}
