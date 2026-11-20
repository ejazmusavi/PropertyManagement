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
}
