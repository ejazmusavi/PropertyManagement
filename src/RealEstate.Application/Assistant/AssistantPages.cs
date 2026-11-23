using RealEstate.Application.Common;
using RealEstate.Domain.Common;

namespace RealEstate.Application.Assistant;

/// <summary>A page the assistant may open. <see cref="DetailPath"/> (with {0} = record id) makes record pages reachable by id.</summary>
public sealed record AssistantPage(string Key, string Title, string Path, string? Permission, string? DetailPath = null);

/// <summary>
/// The only navigation targets the assistant can produce. A closed, server-side list: the model picks a key, the server resolves the URL, and the user's permission is
/// checked here (the target controller still enforces it again). Nothing the model or the database returns can send the browser elsewhere.
/// </summary>
public static class AssistantPageCatalog
{
    public static readonly IReadOnlyList<AssistantPage> All =
    [
        new("dashboard", "Dashboard", "/", null),
        new("company-dashboard", "Company dashboard", "/Dashboards/Company", Permissions.ReportsView),
        new("my-notifications", "My notifications", "/MyNotifications", null),
        new("profile", "My profile", "/Profile", null),
        new("projects", "Projects", "/Projects", Permissions.ProjectsView, "/Projects/Details/{0}"),
        new("inventory", "Property dashboard", "/Inventory", Permissions.InventoryView),
        new("properties", "Properties", "/Properties", Permissions.InventoryView, "/Properties/Details/{0}"),
        new("pricing", "Pricing engine", "/Pricing/Rules", Permissions.PricingView),
        new("customers", "Customers", "/Customers", Permissions.CustomersView, "/Customers/Details/{0}"),
        new("bookings", "Bookings", "/Bookings", Permissions.BookingsView, "/Bookings/Details/{0}"),
        new("booking-approvals", "Booking approvals", "/Bookings/Approvals", Permissions.BookingsApprove),
        new("installment-agreements", "Agreement schedules", "/InstallmentAgreements", Permissions.InstallmentsView, "/InstallmentAgreements/Details/{0}"),
        new("installment-plans", "Installment plans", "/InstallmentPlans", Permissions.InstallmentsView, "/InstallmentPlans/Details/{0}"),
        new("payments", "Payments", "/Payments", Permissions.PaymentsView, "/Payments/Details/{0}"),
        new("overdue", "Overdue installments", "/Recovery/Overdue", Permissions.RecoveryView),
        new("upcoming", "Upcoming installments", "/Recovery/Upcoming", Permissions.RecoveryView),
        new("defaulters", "Defaulters", "/Recovery/Defaulters", Permissions.RecoveryView),
        new("expenses", "Expenses", "/Expenses", Permissions.ExpensesView, "/Expenses/Details/{0}"),
        new("vendors", "Vendors", "/Vendors", Permissions.VendorsView, "/Vendors/Details/{0}"),
        new("accounting", "Accounting", "/Accounting", Permissions.AccountingView),
        new("journals", "Journal entries", "/Journals", Permissions.AccountingView, "/Journals/Details/{0}"),
        new("reports", "Reports", "/Reports", Permissions.ReportsView),
        new("notifications", "Notification history", "/Notifications", Permissions.NotificationsView),
        new("jobs", "Background jobs", "/Jobs", Permissions.JobsView),
        new("gis", "GIS and boundaries", "/Gis", Permissions.GisView),
        new("users", "Users", "/Users", Permissions.UsersView),
        new("roles", "Roles and permissions", "/Roles", Permissions.RolesView),
        new("assistant", "AI assistant (full page)", "/Assistant", null),
    ];

    public static AssistantPage? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    public static async Task<IReadOnlyList<AssistantPage>> AllowedAsync(IPermissionService permissions, Guid userId, CancellationToken ct = default)
    {
        var granted = await permissions.GetPermissionsAsync(userId, ct);
        return All.Where(p => p.Permission is null || granted.Contains(p.Permission)).ToList();
    }

    public static string Resolve(AssistantPage page, long? id) =>
        id is { } v && page.DetailPath is { } d ? string.Format(System.Globalization.CultureInfo.InvariantCulture, d, v) : page.Path;
}
