using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Recovery;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Recovery: upcoming and overdue dues, defaulters, officer performance, assignments and follow-ups.</summary>
[ApiController, Authorize]
[Route("api/v1/recovery")]
public class RecoveryController(RecoveryService recovery) : ControllerBase
{
    /// <summary>Overdue installments (default: at least one day past due), with aging buckets. Recovery officers without recovery.view see their assigned agreements only.</summary>
    [HttpGet("overdue")]
    public async Task<ActionResult<DueListDto>> Overdue([FromQuery] DueListRequest request, CancellationToken ct) => await recovery.OverdueAsync(request, ct);

    [HttpGet("upcoming")]
    public async Task<ActionResult<DueListDto>> Upcoming([FromQuery] DueListRequest request, CancellationToken ct) => await recovery.UpcomingAsync(request, ct);

    [HttpGet("defaulters")]
    public async Task<ActionResult<PagedResult<DefaulterDto>>> Defaulters([FromQuery] DefaulterListRequest request, CancellationToken ct) => await recovery.DefaultersAsync(request, ct);

    [HttpGet("dashboard"), HasPermission(Permissions.RecoveryView)]
    public async Task<ActionResult<RecoveryDashboardDto>> Dashboard([FromQuery] long? projectId, CancellationToken ct) => await recovery.DashboardAsync(projectId, ct);

    [HttpGet("officer-performance"), HasPermission(Permissions.RecoveryView)]
    public async Task<ActionResult<IReadOnlyList<OfficerPerformanceDto>>> OfficerPerformance([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] long? projectId, CancellationToken ct) =>
        Ok(await recovery.OfficerPerformanceAsync(new OfficerPerformanceRequest(from, to, projectId), ct));

    [HttpGet("assignments")]
    public async Task<ActionResult<PagedResult<AssignmentDto>>> Assignments([FromQuery] AssignmentListRequest request, CancellationToken ct) => await recovery.ListAssignmentsAsync(request, ct);

    [HttpPost("assignments"), HasPermission(Permissions.RecoveryAssign)]
    public async Task<ActionResult<AssignmentDto>> Assign(CreateAssignmentRequest request, CancellationToken ct) => await recovery.AssignAsync(request, ct);

    [HttpPost("assignments/{id:long}/close"), HasPermission(Permissions.RecoveryAssign)]
    public async Task<ActionResult<AssignmentDto>> Close(long id, CloseAssignmentRequest request, CancellationToken ct) => await recovery.CloseAssignmentAsync(id, request, ct);

    [HttpGet("officers/{agreementId:long}"), HasPermission(Permissions.RecoveryAssign)]
    public async Task<ActionResult<IReadOnlyList<OfficerOptionDto>>> Officers(long agreementId, CancellationToken ct) => Ok(await recovery.OfficersAsync(agreementId, ct));

    [HttpGet("follow-ups")]
    public async Task<ActionResult<PagedResult<FollowUpDto>>> FollowUps([FromQuery] FollowUpListRequest request, CancellationToken ct) => await recovery.ListFollowUpsAsync(request, ct);

    [HttpPost("follow-ups"), HasPermission(Permissions.RecoveryFollowUp)]
    public async Task<ActionResult<FollowUpDto>> CreateFollowUp(CreateFollowUpRequest request, CancellationToken ct) => await recovery.CreateFollowUpAsync(request, ct);
}
