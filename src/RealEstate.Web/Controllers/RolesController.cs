using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

[HasPermission(Permissions.RolesView)]
public class RolesController(RoleService roles) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await roles.ListAsync(ct));

    [HttpGet, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", new RoleFormViewModel { AllPermissions = await roles.ListPermissionsAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Create(RoleFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await roles.CreateAsync(new CreateRoleRequest(m.Name, m.Description, m.PermissionCodes), ct);
                TempData["Success"] = "Role created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.AllPermissions = await roles.ListPermissionsAsync(ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var r = await roles.GetAsync(id, ct);
        return View("Form", new RoleFormViewModel
        {
            Id = r.Id, Name = r.Name, Description = r.Description, IsSystem = r.IsSystem, IsAdministrator = r.Name == RoleNames.Administrator,
            PermissionCodes = [.. r.PermissionCodes], AllPermissions = await roles.ListPermissionsAsync(ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Edit(Guid id, RoleFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await roles.UpdateAsync(id, new UpdateRoleRequest(m.Name, m.Description, m.PermissionCodes), ct);
                TempData["Success"] = "Role saved.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        var r = await roles.GetAsync(id, ct);
        m.Id = id; m.IsSystem = r.IsSystem; m.IsAdministrator = r.Name == RoleNames.Administrator;
        m.AllPermissions = await roles.ListPermissionsAsync(ct);
        return View("Form", m);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await roles.DeleteAsync(id, ct); TempData["Success"] = "Role deleted."; }
        catch (Exception ex) when (ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}

[HasPermission(Permissions.RolesView)]
public class PermissionsController(RoleService roles) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await roles.GetMatrixAsync(ct));

    /// <summary>Posted grants arrive as "grant" values of the form roleId|permissionCode.</summary>
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RolesManage)]
    public async Task<IActionResult> Index(List<string>? grant, CancellationToken ct)
    {
        var matrix = await roles.GetMatrixAsync(ct);
        var posted = (grant ?? []).Select(g => g.Split('|', 2)).Where(p => p.Length == 2 && Guid.TryParse(p[0], out _))
            .GroupBy(p => Guid.Parse(p[0])).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p[1]).ToList());
        var grants = matrix.Roles.ToDictionary(r => r.Id, r => posted.GetValueOrDefault(r.Id) ?? []);
        try { await roles.SaveMatrixAsync(new SavePermissionMatrixRequest(grants), ct); TempData["Success"] = "Permissions saved."; }
        catch (Exception ex) when (ex is DomainException && ex is not ForbiddenException && ex is not NotFoundException) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}
