using RealEstate.Application.Common;

namespace RealEstate.Web.Services;

/// <summary>Per-request permission set for showing/hiding UI. Enforcement still happens in the backend services.</summary>
public class UiPermissions(IPermissionService permissions, ICurrentUser user)
{
    private HashSet<string>? loaded;

    public async Task<UiPermissions> LoadAsync()
    {
        loaded ??= user.UserId is { } id ? [.. await permissions.GetPermissionsAsync(id)] : [];
        return this;
    }

    public bool Has(string code) => loaded?.Contains(code) ?? false;
}
