using RealEstate.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Infrastructure.Persistence;

/// <summary>Applies migrations and synchronises system data (permission catalog, Administrator role). Contains no credentials.</summary>
public class DatabaseInitializer(AppDbContext db, ILogger<DatabaseInitializer> logger)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync(ct);
        await SyncSystemDataAsync(ct);
    }

    public async Task SyncSystemDataAsync(CancellationToken ct = default)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        foreach (var def in Permissions.All)
        {
            if (existing.TryGetValue(def.Code, out var p))
            {
                p.Name = def.Name;
                p.Module = def.Module;
            }
            else
            {
                var created = new Permission { Code = def.Code, Name = def.Name, Module = def.Module };
                db.Permissions.Add(created);
                existing[def.Code] = created;
            }
        }
        await db.SaveChangesAsync(ct);

        var admin = await db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.Administrator.ToUpperInvariant(), ct);
        if (admin is null)
        {
            admin = new ApplicationRole(RoleNames.Administrator) { NormalizedName = RoleNames.Administrator.ToUpperInvariant(), Description = "Super Admin: full access to every function.", IsSystem = true };
            db.Roles.Add(admin);
            await db.SaveChangesAsync(ct);
        }


        if (!admin.IsSystem) { admin.IsSystem = true; await db.SaveChangesAsync(ct); }

        // Seed the standard system roles once; permissions are granted only on creation so later edits are kept.
        foreach (var def in DefaultRoles.All)
        {
            if (await db.Roles.AnyAsync(r => r.NormalizedName == def.Name.ToUpperInvariant(), ct)) continue;
            var role = new ApplicationRole(def.Name) { NormalizedName = def.Name.ToUpperInvariant(), Description = def.Description, IsSystem = true };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            foreach (var code in def.Permissions.Where(existing.ContainsKey))
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = existing[code].Id });
            await db.SaveChangesAsync(ct);
        }
        // The Administrator role always holds the complete permission catalog.
        var granted = (await db.RolePermissions.Where(rp => rp.RoleId == admin.Id).Select(rp => rp.PermissionId).ToListAsync(ct)).ToHashSet();
        foreach (var p in existing.Values.Where(p => !granted.Contains(p.Id)))
            db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = p.Id });
        await db.SaveChangesAsync(ct);

        await SeedPropertyCatalogAsync(ct);
        await SeedChargeTypesAsync(ct);
        await SeedPaymentMethodsAsync(ct);
        await SeedExpenseCategoriesAsync(ct);
        await SeedGovernmentFeeTypesAsync(ct);
        await SeedAccountGroupsAsync(ct);
        await SeedNotificationTemplatesAsync(ct);
        await SeedSpatialReferenceSystemsAsync(ct);
    }

    /// <summary>
    /// Seeds the account group hierarchy of spec 8.2 once (five natures and their main groups, matched by code). Accounts and posting rules are NOT seeded:
    /// they are the accountant's policy (spec-issues #7).
    /// </summary>
    private async Task SeedAccountGroupsAsync(CancellationToken ct)
    {
        var existing = await db.AccountGroups.ToDictionaryAsync(g => g.Code, ct);
        (string Code, string Name, AccountNature Nature)[] roots =
            [("ASSETS", "Assets", AccountNature.Asset), ("LIABILITIES", "Liabilities", AccountNature.Liability), ("EQUITY", "Equity", AccountNature.Equity),
             ("REVENUE", "Revenue", AccountNature.Revenue), ("EXPENSES", "Expenses", AccountNature.Expense)];
        var order = 0;
        foreach (var r in roots.Where(r => !existing.ContainsKey(r.Code)))
            db.AccountGroups.Add(new AccountGroup { Code = r.Code, Name = r.Name, Nature = r.Nature, Classification = AccountClassification.None, IsSystem = true, SortOrder = ++order * 100 });
        await db.SaveChangesAsync(ct);
        existing = await db.AccountGroups.ToDictionaryAsync(g => g.Code, ct);
        (string Parent, string Code, string Name, AccountClassification C, CashFlowSection F)[] groups =
        [
            ("ASSETS", "CASH", "Cash", AccountClassification.Cash, CashFlowSection.Operating),
            ("ASSETS", "BANK", "Bank", AccountClassification.Bank, CashFlowSection.Operating),
            ("ASSETS", "CUSTOMER_RECEIVABLES", "Customer receivables", AccountClassification.CustomerReceivables, CashFlowSection.Operating),
            ("ASSETS", "LAND_INVENTORY", "Land inventory", AccountClassification.LandInventory, CashFlowSection.Operating),
            ("ASSETS", "PROPERTY_INVENTORY", "Property inventory", AccountClassification.PropertyInventory, CashFlowSection.Operating),
            ("ASSETS", "FIXED_ASSETS", "Fixed assets", AccountClassification.FixedAssets, CashFlowSection.Investing),
            ("ASSETS", "OTHER_ASSETS", "Other assets", AccountClassification.OtherAssets, CashFlowSection.Operating),
            ("LIABILITIES", "CUSTOMER_ADVANCES", "Customer advances", AccountClassification.CustomerAdvances, CashFlowSection.Operating),
            ("LIABILITIES", "GOVERNMENT_PAYABLES", "Government payables", AccountClassification.GovernmentPayables, CashFlowSection.Operating),
            ("LIABILITIES", "VENDOR_PAYABLES", "Vendor payables", AccountClassification.VendorPayables, CashFlowSection.Operating),
            ("LIABILITIES", "TAX_LIABILITIES", "Tax liabilities", AccountClassification.TaxLiabilities, CashFlowSection.Operating),
            ("LIABILITIES", "OTHER_LIABILITIES", "Other liabilities", AccountClassification.OtherLiabilities, CashFlowSection.Operating),
            ("EQUITY", "CAPITAL", "Capital", AccountClassification.Capital, CashFlowSection.Financing),
            ("EQUITY", "RETAINED_EARNINGS", "Retained earnings", AccountClassification.RetainedEarnings, CashFlowSection.Financing),
            ("EQUITY", "RESERVES", "Reserves", AccountClassification.Reserves, CashFlowSection.Financing),
            ("REVENUE", "PROPERTY_SALES_REVENUE", "Property sales revenue", AccountClassification.PropertySalesRevenue, CashFlowSection.Operating),
            ("REVENUE", "DEVELOPMENT_REVENUE", "Development-related revenue", AccountClassification.DevelopmentRevenue, CashFlowSection.Operating),
            ("REVENUE", "OTHER_INCOME", "Other income", AccountClassification.OtherIncome, CashFlowSection.Operating),
            ("EXPENSES", "OPERATING_EXPENSES", "Operating expenses", AccountClassification.OperatingExpenses, CashFlowSection.Operating),
            ("EXPENSES", "CONSTRUCTION_EXPENSES", "Construction expenses", AccountClassification.ConstructionExpenses, CashFlowSection.Operating),
            ("EXPENSES", "MARKETING", "Marketing", AccountClassification.Marketing, CashFlowSection.Operating),
            ("EXPENSES", "SALARIES", "Salaries", AccountClassification.Salaries, CashFlowSection.Operating),
            ("EXPENSES", "ADMINISTRATIVE_EXPENSES", "Administrative expenses", AccountClassification.AdministrativeExpenses, CashFlowSection.Operating),
            ("EXPENSES", "OTHER_EXPENSES", "Other expenses", AccountClassification.OtherExpenses, CashFlowSection.Operating),
        ];
        var i = 0;
        foreach (var g in groups)
        {
            i++;
            if (existing.ContainsKey(g.Code)) continue;
            var parent = existing[g.Parent];
            db.AccountGroups.Add(new AccountGroup
            {
                Code = g.Code, Name = g.Name, Nature = parent.Nature, Classification = g.C, CashFlowSection = g.F, ParentGroupId = parent.Id, IsSystem = true, SortOrder = parent.SortOrder + i,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the standard property categories and types once (matched by code, so edits and custom entries are kept).</summary>
    private async Task SeedPropertyCatalogAsync(CancellationToken ct)
    {
        var categories = await db.PropertyCategories.ToDictionaryAsync(c => c.Code, ct);
        foreach (var (code, name) in new[] { ("RESIDENTIAL", "Residential"), ("COMMERCIAL", "Commercial") })
        {
            if (categories.ContainsKey(code)) continue;
            var c = new PropertyCategory { Code = code, Name = name, IsSystem = true };
            db.PropertyCategories.Add(c);
            categories[code] = c;
        }
        await db.SaveChangesAsync(ct);

        var types = await db.PropertyTypes.Select(t => t.Code).ToListAsync(ct);
        (string Code, string Name, string Cat, PropertyForm Form, bool Floor)[] defaults =
        [
            ("RES-PLOT", "Residential Plot", "RESIDENTIAL", PropertyForm.Plot, false),
            ("COM-PLOT", "Commercial Plot", "COMMERCIAL", PropertyForm.Plot, false),
            ("HOUSE", "House", "RESIDENTIAL", PropertyForm.House, false),
            ("APARTMENT", "Apartment", "RESIDENTIAL", PropertyForm.Apartment, true),
            ("SHOP", "Shop", "COMMERCIAL", PropertyForm.Commercial, true),
            ("OFFICE", "Office", "COMMERCIAL", PropertyForm.Commercial, true),
        ];
        foreach (var d in defaults.Where(d => !types.Contains(d.Code)))
            db.PropertyTypes.Add(new PropertyType { Code = d.Code, Name = d.Name, CategoryId = categories[d.Cat].Id, Form = d.Form, RequiresFloor = d.Floor, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the eleven standard charge components of spec 5.3 once (matched by code, so renames are kept).</summary>

    /// <summary>Seeds the standard notification wording once per (type, channel, language); later edits are kept.</summary>
    private async Task SeedNotificationTemplatesAsync(CancellationToken ct)
    {
        var existing = (await db.NotificationTemplates.Select(t => new { t.Type, t.Channel, t.Language }).ToListAsync(ct)).Select(t => (t.Type, t.Channel, t.Language)).ToHashSet();
        foreach (var d in NotificationTemplateDefaults.All.Where(d => !existing.Contains((d.Type, d.Channel, "en"))))
            db.NotificationTemplates.Add(new NotificationTemplate { Type = d.Type, Channel = d.Channel, Language = "en", Subject = d.Subject, Body = d.Body, IsActive = true });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the coordinate systems of the GIS foundation (WGS 84, Web Mercator and the UTM zones covering Pakistan) once, matched by code.</summary>
    private async Task SeedSpatialReferenceSystemsAsync(CancellationToken ct)
    {
        var codes = await db.SpatialReferenceSystems.Select(s => s.Code).ToListAsync(ct);
        (string Code, int Srid, string Name, SpatialCoordinateKind Kind, string Unit, string Description)[] defaults =
        [
            ("EPSG:4326", 4326, "WGS 84", SpatialCoordinateKind.Geographic, "degree", "Longitude/latitude used by GPS, GeoJSON and OpenStreetMap."),
            ("EPSG:3857", 3857, "WGS 84 / Pseudo-Mercator", SpatialCoordinateKind.Projected, "metre", "Web map tiles (OpenStreetMap, MapLibre)."),
            ("EPSG:32642", 32642, "WGS 84 / UTM zone 42N", SpatialCoordinateKind.Projected, "metre", "Metric survey coordinates for western Pakistan."),
            ("EPSG:32643", 32643, "WGS 84 / UTM zone 43N", SpatialCoordinateKind.Projected, "metre", "Metric survey coordinates for eastern Pakistan."),
        ];
        foreach (var d in defaults.Where(d => !codes.Contains(d.Code)))
            db.SpatialReferenceSystems.Add(new SpatialReferenceSystem { Code = d.Code, Srid = d.Srid, Name = d.Name, Kind = d.Kind, Unit = d.Unit, Description = d.Description, IsSystem = true, IsActive = true });
        await db.SaveChangesAsync(ct);
    }
    private async Task SeedChargeTypesAsync(CancellationToken ct)
    {
        var codes = await db.ChargeTypes.Select(t => t.Code).ToListAsync(ct);
        (string Code, string Name, ChargeComponentKind Kind, ChargeCategory Category, ScheduleFrequency Frequency)[] defaults =
        [
            ("DOWN_PAYMENT", "Down payment", ChargeComponentKind.DownPayment, ChargeCategory.SaleConsideration, ScheduleFrequency.OneTime),
            ("CONFIRMATION", "Confirmation payment", ChargeComponentKind.ConfirmationPayment, ChargeCategory.SaleConsideration, ScheduleFrequency.OneTime),
            ("MONTHLY", "Monthly installment", ChargeComponentKind.MonthlyInstallment, ChargeCategory.SaleConsideration, ScheduleFrequency.Monthly),
            ("QUARTERLY", "Quarterly installment", ChargeComponentKind.QuarterlyInstallment, ChargeCategory.SaleConsideration, ScheduleFrequency.Quarterly),
            ("HALF_YEARLY", "Half-yearly installment", ChargeComponentKind.HalfYearlyInstallment, ChargeCategory.SaleConsideration, ScheduleFrequency.HalfYearly),
            ("ANNUAL", "Annual installment", ChargeComponentKind.AnnualInstallment, ChargeCategory.SaleConsideration, ScheduleFrequency.Annual),
            ("POSSESSION", "Possession payment", ChargeComponentKind.PossessionPayment, ChargeCategory.SaleConsideration, ScheduleFrequency.OneTime),
            ("DEVELOPMENT", "Development charges", ChargeComponentKind.DevelopmentCharges, ChargeCategory.DevelopmentCharges, ScheduleFrequency.OneTime),
            ("UTILITY", "Utility charges", ChargeComponentKind.UtilityCharges, ChargeCategory.UtilityCharges, ScheduleFrequency.OneTime),
            ("GOVT_FEE", "Government fees", ChargeComponentKind.GovernmentFees, ChargeCategory.GovernmentFees, ScheduleFrequency.OneTime),
            ("CUSTOM", "Custom charge", ChargeComponentKind.CustomCharge, ChargeCategory.OtherCharges, ScheduleFrequency.OneTime),
        ];
        foreach (var d in defaults.Where(d => !codes.Contains(d.Code)))
            db.ChargeTypes.Add(new ChargeType { Code = d.Code, Name = d.Name, Kind = d.Kind, DefaultCategory = d.Category, DefaultFrequency = d.Frequency, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the six payment methods of spec 6.2 once (matched by code, so later edits are kept). Only cash skips verification.</summary>
    private async Task SeedPaymentMethodsAsync(CancellationToken ct)
    {
        var codes = await db.PaymentMethods.Select(t => t.Code).ToListAsync(ct);
        (string Code, string Name, PaymentMethodKind Kind, bool Verify, bool Reference)[] defaults =
        [
            ("CASH", "Cash", PaymentMethodKind.Cash, false, false),
            ("BANK_TRANSFER", "Bank transfer", PaymentMethodKind.BankTransfer, true, true),
            ("CHEQUE", "Cheque", PaymentMethodKind.Cheque, true, true),
            ("ONLINE_TRANSFER", "Online transfer", PaymentMethodKind.OnlineTransfer, true, true),
            ("CARD", "Card", PaymentMethodKind.Card, true, true),
            ("GATEWAY", "Payment gateway", PaymentMethodKind.PaymentGateway, true, true),
        ];
        foreach (var d in defaults.Where(d => !codes.Contains(d.Code)))
            db.PaymentMethods.Add(new PaymentMethod { Code = d.Code, Name = d.Name, Kind = d.Kind, RequiresVerification = d.Verify, RequiresReference = d.Reference, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the eleven expense categories of spec 7.2 once (matched by code, so edits and custom categories are kept).</summary>
    private async Task SeedExpenseCategoriesAsync(CancellationToken ct)
    {
        var codes = await db.ExpenseCategories.Select(t => t.Code).ToListAsync(ct);
        (string Code, string Name)[] defaults =
        [
            ("SALARIES", "Salaries"), ("OFFICE_RENT", "Office rent"), ("UTILITIES", "Utilities"), ("MARKETING", "Marketing"), ("CONSTRUCTION", "Construction"),
            ("LAND_ACQUISITION", "Land acquisition"), ("LEGAL", "Legal expenses"), ("TRANSPORTATION", "Transportation"), ("MAINTENANCE", "Maintenance"),
            ("AGENT_COMMISSION", "Agent commission"), ("MISCELLANEOUS", "Miscellaneous"),
        ];
        foreach (var d in defaults.Where(d => !codes.Contains(d.Code))) db.ExpenseCategories.Add(new ExpenseCategory { Code = d.Code, Name = d.Name, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Seeds the standard government fee types of spec 7.4 once (amount entered per assessment until a method/rate is configured).</summary>
    private async Task SeedGovernmentFeeTypesAsync(CancellationToken ct)
    {
        var codes = await db.GovernmentFeeTypes.Select(t => t.Code).ToListAsync(ct);
        (string Code, string Name, GovernmentFeeKind Kind)[] defaults =
        [
            ("STAMP_DUTY", "Stamp duty", GovernmentFeeKind.StampDuty), ("REGISTRATION", "Registration fee", GovernmentFeeKind.Registration),
            ("TRANSFER_FEE", "Transfer fee", GovernmentFeeKind.TransferFee), ("TAX", "Applicable tax", GovernmentFeeKind.Tax),
            ("NOC_FEE", "NOC fee", GovernmentFeeKind.NocFee), ("DEVELOPMENT_CHARGES", "Development charges", GovernmentFeeKind.DevelopmentCharge),
            ("OTHER_FEE", "Other government fee", GovernmentFeeKind.Other),
        ];
        foreach (var d in defaults.Where(d => !codes.Contains(d.Code)))
            db.GovernmentFeeTypes.Add(new GovernmentFeeType { Code = d.Code, Name = d.Name, Kind = d.Kind, Method = FeeCalculationMethod.Manual, DefaultBearer = FeeBearer.Customer, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }
}
