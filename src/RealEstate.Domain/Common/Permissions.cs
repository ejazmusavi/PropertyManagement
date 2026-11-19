namespace RealEstate.Domain.Common;

public sealed record PermissionDefinition(string Code, string Name, string Module);

/// <summary>Permission catalog. Later phases append their permissions here.</summary>
public static class Permissions
{
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesView = "roles.view";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SettingsView = "settings.view";
    public const string SettingsManage = "settings.manage";

    // Phase 1
    public const string CompanyView = "company.view";
    public const string CompanyManage = "company.manage";
    public const string ProjectsView = "projects.view";
    public const string ProjectsCreate = "projects.create";
    public const string ProjectsEdit = "projects.edit";
    public const string ProjectsStatus = "projects.status";
    public const string ProjectsDocuments = "projects.documents";
    public const string ProjectsAccessAll = "projects.access.all";
    public const string ProjectsAccessAssign = "projects.access.assign";
    public const string LandView = "land.view";
    public const string LandManage = "land.manage";
    public const string LandAdjust = "land.adjust";
    public const string LandApprove = "land.approve";

    // Phase 2
    public const string InventoryView = "inventory.view";
    public const string InventoryManage = "inventory.manage";
    public const string InventorySetup = "inventory.setup";
    public const string InventoryDocuments = "inventory.documents";
    public const string InventoryReserve = "inventory.reserve";
    public const string InventoryStatus = "inventory.status";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(UsersView, "View users", "Administration"),
        new(UsersManage, "Manage users", "Administration"),
        new(RolesView, "View roles and permissions", "Administration"),
        new(RolesManage, "Manage roles and permissions", "Administration"),
        new(AuditView, "View audit log", "Administration"),
        new(SettingsView, "View application settings", "Administration"),
        new(SettingsManage, "Manage application settings", "Administration"),
        new(CompanyView, "View company profile", "Company"),
        new(CompanyManage, "Manage company profile and settings", "Company"),
        new(ProjectsView, "View assigned projects", "Projects"),
        new(ProjectsCreate, "Create projects", "Projects"),
        new(ProjectsEdit, "Edit projects and assign managers", "Projects"),
        new(ProjectsStatus, "Change project status", "Projects"),
        new(ProjectsDocuments, "Upload and remove project documents", "Projects"),
        new(ProjectsAccessAll, "Access all projects (bypass project assignment)", "Projects"),
        new(ProjectsAccessAssign, "Assign users to projects", "Projects"),
        new(LandView, "View land and distribution", "Land"),
        new(LandManage, "Manage land parcels, acquisitions, distribution and documents", "Land"),
        new(LandAdjust, "Record land area adjustments", "Land"),
        new(LandApprove, "Approve land area adjustments", "Land"),
        new(InventoryView, "View property inventory, history and reports", "Inventory"),
        new(InventoryManage, "Create and edit properties, dimensions and features", "Inventory"),
        new(InventorySetup, "Manage property types, categories, blocks, sectors and streets", "Inventory"),
        new(InventoryDocuments, "Upload and remove property documents", "Inventory"),
        new(InventoryReserve, "Reserve properties and release reservations", "Inventory"),
        new(InventoryStatus, "Change property availability status", "Inventory"),
    ];
}

public static class RoleNames
{
    /// <summary>The Super Admin role: always holds every permission and cannot be edited.</summary>
    public const string Administrator = "Administrator";
    public const string Director = "Director";
    public const string ProjectManager = "Project Manager";
    public const string SalesManager = "Sales Manager";
    public const string Accountant = "Accountant";
    public const string Cashier = "Cashier";
    public const string RecoveryOfficer = "Recovery Officer";
    public const string InventoryManager = "Inventory Manager";
    public const string CustomerSupport = "Customer Support";
}

public sealed record DefaultRoleDefinition(string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>System roles seeded on first run. Permissions are granted only when the role is first created, so later edits are kept.</summary>
public static class DefaultRoles
{
    public static readonly IReadOnlyList<DefaultRoleDefinition> All =
    [
        new(RoleNames.Director, "Company director with access to all projects.",
            [Permissions.UsersView, Permissions.RolesView, Permissions.AuditView, Permissions.SettingsView, Permissions.CompanyView,
             Permissions.ProjectsView, Permissions.ProjectsCreate, Permissions.ProjectsEdit, Permissions.ProjectsStatus, Permissions.ProjectsDocuments,
             Permissions.ProjectsAccessAll, Permissions.LandView, Permissions.LandManage, Permissions.LandAdjust, Permissions.LandApprove,
             Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments, Permissions.InventoryReserve, Permissions.InventoryStatus]),
        new(RoleNames.ProjectManager, "Manages assigned projects and their land.",
            [Permissions.ProjectsView, Permissions.ProjectsEdit, Permissions.ProjectsDocuments, Permissions.LandView, Permissions.LandManage, Permissions.LandAdjust,
             Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments]),
        new(RoleNames.SalesManager, "Oversees sales for assigned projects.", [Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView, Permissions.InventoryReserve]),
        new(RoleNames.Accountant, "Maintains company finances.", [Permissions.CompanyView, Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView]),
        new(RoleNames.Cashier, "Collects payments at the office.", [Permissions.ProjectsView]),
        new(RoleNames.RecoveryOfficer, "Follows up overdue installments.", [Permissions.ProjectsView]),
        new(RoleNames.InventoryManager, "Maintains property inventory.",
            [Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments,
             Permissions.InventoryReserve, Permissions.InventoryStatus]),
        new(RoleNames.CustomerSupport, "Assists customers.", [Permissions.ProjectsView, Permissions.InventoryView]),
    ];
}
