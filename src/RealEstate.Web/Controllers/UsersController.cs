using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.UsersView)]
public class UsersController(UserService users) : Controller
{
    public async Task<IActionResult> Index(string? search, string? sortBy, bool desc = false, int page = 1, CancellationToken ct = default)
    {
        var result = await users.ListAsync(new PagedRequest { Page = page, PageSize = 20, Search = search, SortBy = sortBy, Descending = desc }, ct);
        return View(new UserListViewModel { Users = result, Search = search, SortBy = sortBy, Descending = desc });
    }
}
