using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Users;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class ProfileController(UserService users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await LoadAsync(null, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ProfileViewModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await users.UpdateProfileAsync(new UpdateProfileRequest(model.FullName, model.PhoneNumber), ct);
                TempData["Success"] = "Profile updated.";
                return RedirectToAction(nameof(Index));
            }
            catch (RequestValidationException ex) { ModelState.AddErrors(ex); }
        }
        return View(await LoadAsync(model, ct));
    }

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await users.ChangePasswordAsync(new ChangePasswordRequest(model.CurrentPassword, model.NewPassword, model.ConfirmNewPassword), ct);
                TempData["Success"] = "Password changed.";
                return RedirectToAction(nameof(Index));
            }
            catch (RequestValidationException ex) { ModelState.AddErrors(ex); }
        }
        return View(new ChangePasswordViewModel());
    }

    private async Task<ProfileViewModel> LoadAsync(ProfileViewModel? posted, CancellationToken ct)
    {
        var p = await users.GetProfileAsync(ct);
        return new ProfileViewModel
        {
            Email = p.Email, Roles = p.Roles, LastLoginAtUtc = p.LastLoginAtUtc,
            FullName = posted?.FullName ?? p.FullName, PhoneNumber = posted is null ? p.PhoneNumber : posted.PhoneNumber,
        };
    }
}
