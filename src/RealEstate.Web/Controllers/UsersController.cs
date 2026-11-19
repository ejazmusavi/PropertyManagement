using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.UsersView)]
public class UsersController(UserService users, UserAdministrationService admin) : Controller
{
    public async Task<IActionResult> Index(string? search, string? sortBy, bool desc = false, int page = 1, CancellationToken ct = default)
    {
        var result = await users.ListAsync(new PagedRequest { Page = page, PageSize = 20, Search = search, SortBy = sortBy, Descending = desc }, ct);
        return View(new UserListViewModel { Users = result, Search = search, SortBy = sortBy, Descending = desc });
    }

    [HttpGet, HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", new UserFormViewModel { AllRoles = await ListRolesAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Create(UserFormViewModel m, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Password)) ModelState.AddModelError(nameof(m.Password), "A password is required.");
        if (ModelState.IsValid)
        {
            try
            {
                await admin.CreateAsync(new CreateUserRequest(m.Email, m.FullName, m.PhoneNumber, m.Password!, m.IsActive, m.Roles), ct);
                TempData["Success"] = "User created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Password = null;
        m.AllRoles = await ListRolesAsync(ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var u = await admin.GetAsync(id, ct);
        return View("Form", new UserFormViewModel { Id = u.Id, Email = u.Email, FullName = u.FullName, PhoneNumber = u.PhoneNumber, IsActive = u.IsActive, Roles = [.. u.Roles], AllRoles = await ListRolesAsync(ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Edit(Guid id, UserFormViewModel m, CancellationToken ct)
    {
        ModelState.Remove(nameof(m.Password)); ModelState.Remove(nameof(m.Email));
        if (ModelState.IsValid)
        {
            try
            {
                await admin.UpdateAsync(id, new UpdateUserRequest(m.FullName, m.PhoneNumber, m.IsActive, m.Roles), ct);
                TempData["Success"] = "User saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id; m.Email = (await admin.GetAsync(id, ct)).Email; m.AllRoles = await ListRolesAsync(ct);
        return View("Form", m);
    }

    private async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct) => await admin.ListAssignableRolesAsync(ct);
}
