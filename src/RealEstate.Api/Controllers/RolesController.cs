using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1")]
public class RolesController(RoleService roles) : ControllerBase
{
    [HttpGet("roles"), HasPermission(RealEstate.Domain.Common.Permissions.RolesView)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken ct) => Ok(await roles.ListAsync(ct));

    [HttpGet("roles/{id:guid}"), HasPermission(RealEstate.Domain.Common.Permissions.RolesView)]
    public async Task<ActionResult<RoleDetailDto>> Get(Guid id, CancellationToken ct) => await roles.GetAsync(id, ct);

    [HttpGet("permissions"), HasPermission(RealEstate.Domain.Common.Permissions.RolesView)]
    public async Task<ActionResult<IReadOnlyList<PermissionDto>>> ListPermissions(CancellationToken ct) => Ok(await roles.ListPermissionsAsync(ct));
}
