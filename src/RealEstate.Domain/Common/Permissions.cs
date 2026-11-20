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

    // Phase 3
    public const string PricingView = "pricing.view";
    public const string PricingManage = "pricing.manage";
    public const string PricingRequest = "pricing.request";
    public const string PricingApprove = "pricing.approve";

    // Phase 4
    public const string CustomersView = "customers.view";
    public const string CustomersManage = "customers.manage";
    public const string CustomersDocuments = "customers.documents";
    public const string BookingsView = "bookings.view";
    public const string BookingsCreate = "bookings.create";
    public const string BookingsApprove = "bookings.approve";
    public const string BookingsCancel = "bookings.cancel";
    public const string BookingsTransfer = "bookings.transfer";
    public const string BookingsDiscount = "bookings.discount";
    public const string BookingsAuthorizeBalance = "bookings.balance.authorize";
    public const string BookingsRefund = "bookings.refund";
    public const string AgreementsDocuments = "agreements.documents";

    // Phase 5
    public const string InstallmentsView = "installments.view";
    public const string InstallmentsManage = "installments.manage";
    public const string InstallmentsApprove = "installments.approve";
    public const string InstallmentsSchedule = "installments.schedule";
    public const string AgreementsAmend = "agreements.amend";
    public const string AgreementsAmendApprove = "agreements.amend.approve";

    // Phase 6
    public const string PaymentsView = "payments.view";
    public const string PaymentsCollect = "payments.collect";
    public const string PaymentsVerify = "payments.verify";
    public const string PaymentsApprove = "payments.approve";
    public const string PaymentsAllocate = "payments.allocate";
    public const string PaymentsReverse = "payments.reverse";
    public const string PaymentsReceipts = "payments.receipts";
    public const string RefundsRequest = "refunds.request";
    public const string RefundsApprove = "refunds.approve";
    public const string RefundsPay = "refunds.pay";
    public const string LedgersView = "ledgers.view";
    public const string RecoveryView = "recovery.view";
    public const string RecoveryAssign = "recovery.assign";
    public const string RecoveryFollowUp = "recovery.followup";

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
        new(PricingView, "View pricing rules, prices, history and use the price calculator", "Pricing"),
        new(PricingManage, "Manage pricing rules and discounts and publish property prices", "Pricing"),
        new(PricingRequest, "Request price overrides, customer discounts, special pricing and exceptions", "Pricing"),
        new(PricingApprove, "Approve or reject pricing requests and revoke adjustments", "Pricing"),
        new(CustomersView, "View customers", "Customers"),
        new(CustomersManage, "Register and edit customers, nominees and relationships", "Customers"),
        new(CustomersDocuments, "Upload and remove customer documents", "Customers"),
        new(BookingsView, "View bookings, agreements and approvals", "Sales"),
        new(BookingsCreate, "Create bookings and submit them for approval", "Sales"),
        new(BookingsApprove, "Approve or reject bookings and refund requests", "Sales"),
        new(BookingsCancel, "Cancel bookings and agreements", "Sales"),
        new(BookingsTransfer, "Transfer bookings between customers and change ownership shares", "Sales"),
        new(BookingsDiscount, "Apply additional booking discounts", "Sales"),
        new(BookingsAuthorizeBalance, "Authorize a cash sale with an outstanding balance", "Sales"),
        new(BookingsRefund, "Request refunds when cancelling a booking", "Sales"),
        new(AgreementsDocuments, "Upload and remove agreement documents", "Sales"),
        new(InstallmentsView, "View installment plans, late fee rules, agreement schedules and schedule history", "Installments"),
        new(InstallmentsManage, "Manage installment plan templates, components, charge types and late fee rules", "Installments"),
        new(InstallmentsApprove, "Activate customer-specific installment plans (maker-checker)", "Installments"),
        new(InstallmentsSchedule, "Build agreement charges from plans and generate payment schedules", "Installments"),
        new(AgreementsAmend, "Request agreement amendments and waivers", "Installments"),
        new(AgreementsAmendApprove, "Approve or reject agreement amendments and waivers", "Installments"),
        new(PaymentsView, "View payments, allocations and receipts", "Payments"),
        new(PaymentsCollect, "Record payments (office collection and customer-submitted proof)", "Payments"),
        new(PaymentsVerify, "Verify or reject submitted payments", "Payments"),
        new(PaymentsApprove, "Approve and post payments (maker-checker)", "Payments"),
        new(PaymentsAllocate, "Allocate posted payments to installments, advance or unallocated balance", "Payments"),
        new(PaymentsReverse, "Reverse posted payments", "Payments"),
        new(PaymentsReceipts, "Print receipts and duplicate copies", "Payments"),
        new(RefundsRequest, "Request customer refunds", "Payments"),
        new(RefundsApprove, "Approve or reject refunds (maker-checker)", "Payments"),
        new(RefundsPay, "Record refund payouts", "Payments"),
        new(LedgersView, "View customer and agreement ledgers and property statements", "Payments"),
        new(RecoveryView, "View upcoming and overdue dues, defaulters and recovery reports", "Recovery"),
        new(RecoveryAssign, "Assign and close recovery assignments", "Recovery"),
        new(RecoveryFollowUp, "Record customer follow-ups", "Recovery"),
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
             Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments, Permissions.InventoryReserve, Permissions.InventoryStatus,
             Permissions.PricingView, Permissions.PricingManage, Permissions.PricingRequest, Permissions.PricingApprove,
             Permissions.CustomersView, Permissions.CustomersManage, Permissions.CustomersDocuments, Permissions.BookingsView, Permissions.BookingsCreate, Permissions.BookingsApprove,
             Permissions.BookingsCancel, Permissions.BookingsTransfer, Permissions.BookingsDiscount, Permissions.BookingsAuthorizeBalance, Permissions.BookingsRefund, Permissions.AgreementsDocuments,
             Permissions.InstallmentsView, Permissions.InstallmentsManage, Permissions.InstallmentsApprove, Permissions.InstallmentsSchedule, Permissions.AgreementsAmend, Permissions.AgreementsAmendApprove,
             Permissions.PaymentsView, Permissions.PaymentsCollect, Permissions.PaymentsVerify, Permissions.PaymentsApprove, Permissions.PaymentsAllocate, Permissions.PaymentsReverse,
             Permissions.PaymentsReceipts, Permissions.RefundsRequest, Permissions.RefundsApprove, Permissions.RefundsPay, Permissions.LedgersView,
             Permissions.RecoveryView, Permissions.RecoveryAssign, Permissions.RecoveryFollowUp]),
        new(RoleNames.ProjectManager, "Manages assigned projects and their land.",
            [Permissions.ProjectsView, Permissions.ProjectsEdit, Permissions.ProjectsDocuments, Permissions.LandView, Permissions.LandManage, Permissions.LandAdjust,
             Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments,
             Permissions.PricingView, Permissions.PricingManage, Permissions.PricingRequest, Permissions.CustomersView, Permissions.BookingsView,
             Permissions.InstallmentsView, Permissions.InstallmentsManage, Permissions.PaymentsView, Permissions.LedgersView, Permissions.RecoveryView]),
        new(RoleNames.SalesManager, "Oversees sales for assigned projects.", [Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView, Permissions.InventoryReserve, Permissions.PricingView, Permissions.PricingRequest,
             Permissions.CustomersView, Permissions.CustomersManage, Permissions.CustomersDocuments, Permissions.BookingsView, Permissions.BookingsCreate, Permissions.BookingsCancel, Permissions.AgreementsDocuments,
             Permissions.InstallmentsView, Permissions.InstallmentsSchedule, Permissions.AgreementsAmend,
             Permissions.PaymentsView, Permissions.PaymentsCollect, Permissions.PaymentsReceipts, Permissions.RefundsRequest, Permissions.LedgersView, Permissions.RecoveryView, Permissions.RecoveryAssign]),
        new(RoleNames.Accountant, "Maintains company finances.", [Permissions.CompanyView, Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView, Permissions.PricingView,
             Permissions.CustomersView, Permissions.BookingsView, Permissions.InstallmentsView, Permissions.AgreementsAmendApprove,
             Permissions.PaymentsView, Permissions.PaymentsVerify, Permissions.PaymentsApprove, Permissions.PaymentsAllocate, Permissions.PaymentsReverse, Permissions.PaymentsReceipts,
             Permissions.RefundsApprove, Permissions.RefundsPay, Permissions.LedgersView, Permissions.RecoveryView]),
        new(RoleNames.Cashier, "Collects payments at the office.", [Permissions.ProjectsView, Permissions.CustomersView, Permissions.BookingsView, Permissions.InstallmentsView,
             Permissions.PaymentsView, Permissions.PaymentsCollect, Permissions.PaymentsAllocate, Permissions.PaymentsReceipts, Permissions.LedgersView]),
        new(RoleNames.RecoveryOfficer, "Follows up overdue installments.", [Permissions.ProjectsView, Permissions.CustomersView, Permissions.BookingsView, Permissions.InstallmentsView, Permissions.AgreementsAmend,
             Permissions.PaymentsView, Permissions.LedgersView, Permissions.RecoveryFollowUp]),
        new(RoleNames.InventoryManager, "Maintains property inventory.",
            [Permissions.ProjectsView, Permissions.LandView, Permissions.InventoryView, Permissions.InventoryManage, Permissions.InventorySetup, Permissions.InventoryDocuments,
             Permissions.InventoryReserve, Permissions.InventoryStatus, Permissions.PricingView]),
        new(RoleNames.CustomerSupport, "Assists customers.", [Permissions.ProjectsView, Permissions.InventoryView, Permissions.PricingView,
             Permissions.CustomersView, Permissions.CustomersManage, Permissions.CustomersDocuments, Permissions.BookingsView, Permissions.InstallmentsView,
             Permissions.PaymentsView, Permissions.LedgersView]),
    ];
}
