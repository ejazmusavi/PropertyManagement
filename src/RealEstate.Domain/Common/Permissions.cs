namespace RealEstate.Domain.Common;

public sealed record PermissionDefinition(string Code, string Name, string Module);

/// <summary>Permission catalog. Later phases append their permissions here; Phase 1 defines roles that use them.</summary>
public static class Permissions
{
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesView = "roles.view";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SettingsView = "settings.view";
    public const string SettingsManage = "settings.manage";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(UsersView, "View users", "Administration"),
        new(UsersManage, "Manage users", "Administration"),
        new(RolesView, "View roles and permissions", "Administration"),
        new(RolesManage, "Manage roles and permissions", "Administration"),
        new(AuditView, "View audit log", "Administration"),
        new(SettingsView, "View application settings", "Administration"),
        new(SettingsManage, "Manage application settings", "Administration"),
    ];
}

public static class RoleNames
{
    public const string Administrator = "Administrator";
}
