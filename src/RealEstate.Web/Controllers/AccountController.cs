using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Domain.Common;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;


public class AccountController(AuthService auth, IIdentityService identity, IAuditService audit, ICurrentUser currentUser) : Controller
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null) =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, ValidateAntiForgeryToken, AllowAnonymous]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        Result<UserIdentityInfo> result;
        try { result = await auth.VerifyCredentialsAsync(new LoginRequest(model.Email, model.Password), ct); }
        catch (RequestValidationException ex) { ModelState.AddErrors(ex); return View(model); }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!.Message);
            model.Password = "";
            return View(model);
        }

        var user = result.Value!;
        var roles = await identity.GetRolesAsync(user.Id, ct);
        if (roles.Contains(RoleNames.Customer))
        {
            // Customer logins belong to the mobile app; they have no permission for any office screen.
            ModelState.AddModelError(string.Empty, "This login is for the customer app. Please sign in there.");
            model.Password = "";
            return View(model);
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Email, user.Email),
            new("fullname", user.FullName),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme, ClaimTypes.Name, ClaimTypes.Role));
        await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, principal,
            new AuthenticationProperties { IsPersistent = model.RememberMe });

        return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl!) : RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry("Logout", "User", currentUser.UserId?.ToString()), ct);
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();
}
