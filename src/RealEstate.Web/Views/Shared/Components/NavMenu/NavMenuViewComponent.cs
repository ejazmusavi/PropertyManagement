using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;

namespace RealEstate.Web.Views.Shared.Components.NavMenu;

public class NavMenuViewComponent(IPermissionService permissions, ICurrentUser user) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(user.UserId is { } id ? await permissions.GetPermissionsAsync(id) : []);
}
