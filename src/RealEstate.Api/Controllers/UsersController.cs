using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/users")]
public class UsersController(UserService users, UserAdministrationService admin) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<PagedResult<UserListItemDto>>> List([FromQuery] PagedRequest request, CancellationToken ct) => await users.ListAsync(request, ct);

    [HttpGet("{id:guid}"), HasPermission(Permissions.UsersView)]
    public async Task<ActionResult<UserDetailDto>> Get(Guid id, CancellationToken ct) => await admin.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct)
    {
        var created = await admin.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), HasPermission(Permissions.UsersManage)]
    public async Task<ActionResult<UserDetailDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct) => await admin.UpdateAsync(id, request, ct);
}
