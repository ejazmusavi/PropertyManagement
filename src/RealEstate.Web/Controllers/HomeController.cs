using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Users;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class HomeController(UserService users) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var summary = await users.GetDashboardAsync(ct);
        var profile = await users.GetProfileAsync(ct);
        return View(new DashboardViewModel { Summary = summary, FullName = profile.FullName });
    }

    [AllowAnonymous]
    public IActionResult HttpStatus(int code) => View("HttpStatus", code);

    [AllowAnonymous, ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}

