using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Companies;

public sealed record CompanyDto(
    string Name, string? LegalName, string? TaxNumber, string? Email, string? Phone, string? Website,
    string? Address, string? City, string? Country, string? PostalCode, bool HasLogo,
    string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPlaces, RoundingMode RoundingMode,
    int FiscalYearStartMonth, string TimeZoneId, int MaxUploadSizeMb, string AllowedFileExtensions, string RowVersion);

public sealed record UpdateCompanyRequest(
    string Name, string? LegalName, string? TaxNumber, string? Email, string? Phone, string? Website,
    string? Address, string? City, string? Country, string? PostalCode,
    string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPlaces, RoundingMode RoundingMode,
    int FiscalYearStartMonth, string TimeZoneId, int MaxUploadSizeMb, string AllowedFileExtensions, string? RowVersion);

public class UpdateCompanyRequestValidator : AbstractValidator<UpdateCompanyRequest>
{
    public UpdateCompanyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LegalName).MaximumLength(250);
        RuleFor(x => x.TaxNumber).MaximumLength(50);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Website).MaximumLength(250);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Country).MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.CurrencyCode).NotEmpty().Length(3).Matches("^[A-Za-z]{3}$").WithMessage("Currency code must be a 3-letter ISO code.");
        RuleFor(x => x.CurrencySymbol).NotEmpty().MaximumLength(8);
        RuleFor(x => x.CurrencyDecimalPlaces).InclusiveBetween(0, 4);
        RuleFor(x => x.RoundingMode).IsInEnum();
        RuleFor(x => x.FiscalYearStartMonth).InclusiveBetween(1, 12);
        RuleFor(x => x.TimeZoneId).NotEmpty().Must(BeKnownTimeZone).WithMessage("Unknown time zone.");
        RuleFor(x => x.MaxUploadSizeMb).InclusiveBetween(1, 100);
        RuleFor(x => x.AllowedFileExtensions).NotEmpty().MaximumLength(300)
            .Must(v => v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).All(e => e.TrimStart('.').All(char.IsLetterOrDigit)))
            .WithMessage("Use a comma-separated list of extensions such as pdf,jpg,png.");
    }

    private static bool BeKnownTimeZone(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; } catch (Exception) { return false; }
    }
}

/// <summary>Company profile and settings. There is exactly one company.</summary>
public class CompanyService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, IAuditService audit,
    DocumentUploader uploader, IFileStorage storage, IValidator<UpdateCompanyRequest> validator)
{
    private static readonly HashSet<string> LogoTypes = ["jpg", "jpeg", "png"];

    public async Task<CompanyDto> GetAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CompanyView, ct);
        return Map(await LoadAsync(ct));
    }

    public async Task<CompanyDto> UpdateAsync(UpdateCompanyRequest request, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CompanyManage, ct);
        await validator.ValidateOrThrowAsync(request, ct);
        var c = await LoadAsync(ct);
        var before = Snapshot(c);
        db.UseRowVersion(c, request.RowVersion);

        c.Rename(request.Name);
        c.LegalName = N(request.LegalName); c.TaxNumber = N(request.TaxNumber); c.Email = N(request.Email);
        c.Phone = N(request.Phone); c.Website = N(request.Website); c.Address = N(request.Address);
        c.City = N(request.City); c.Country = N(request.Country); c.PostalCode = N(request.PostalCode);
        c.CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant(); c.CurrencySymbol = request.CurrencySymbol.Trim();
        c.CurrencyDecimalPlaces = request.CurrencyDecimalPlaces; c.RoundingMode = request.RoundingMode;
        c.FiscalYearStartMonth = request.FiscalYearStartMonth; c.TimeZoneId = request.TimeZoneId.Trim();
        c.MaxUploadSizeMb = request.MaxUploadSizeMb;
        c.AllowedFileExtensions = string.Join(",", c.AllowedExtensionsFrom(request.AllowedFileExtensions));

        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Company.Updated", "Company", c.Id.ToString(), before, Snapshot(c)), ct);
        return Map(c);
    }

    public async Task SetLogoAsync(FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CompanyManage, ct);
        var ext = Path.GetExtension(DocumentUploader.SafeName(file.FileName)).TrimStart('.').ToLowerInvariant();
        if (!LogoTypes.Contains(ext)) throw new DomainException("The logo must be a JPG or PNG image.");
        if (file.Length > 2 * 1024 * 1024) throw new DomainException("The logo must be 2 MB or smaller.");
        var c = await LoadAsync(ct);
        var stored = await uploader.StoreAsync(file, "company", ct);
        var old = c.LogoStorageKey;
        c.LogoStorageKey = stored.File.Key;
        c.LogoContentType = stored.ContentType;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Company.LogoChanged", "Company", c.Id.ToString(), new { LogoStorageKey = old }, new { LogoStorageKey = stored.File.Key }), ct);
    }

    public async Task RemoveLogoAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.CompanyManage, ct);
        var c = await LoadAsync(ct);
        if (c.LogoStorageKey is null) return;
        var old = c.LogoStorageKey;
        c.LogoStorageKey = null; c.LogoContentType = null;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Company.LogoRemoved", "Company", c.Id.ToString(), new { LogoStorageKey = old }), ct);
    }

    /// <summary>Any signed-in user may see the logo (it is part of the application chrome).</summary>
    public async Task<DownloadedFile?> GetLogoAsync(CancellationToken ct = default)
    {
        if (!user.IsAuthenticated) throw new ForbiddenException();
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(ct);
        if (c?.LogoStorageKey is null) return null;
        return new DownloadedFile(await storage.OpenReadAsync(c.LogoStorageKey, ct), "logo", c.LogoContentType ?? "image/png");
    }

    private async Task<Company> LoadAsync(CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(ct);
        if (c is not null) return c;
        c = new Company("My Company");
        db.Companies.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static object Snapshot(Company c) => new
    {
        c.Name, c.LegalName, c.TaxNumber, c.Email, c.Phone, c.Website, c.Address, c.City, c.Country, c.PostalCode,
        c.CurrencyCode, c.CurrencySymbol, c.CurrencyDecimalPlaces, RoundingMode = c.RoundingMode.ToString(),
        c.FiscalYearStartMonth, c.TimeZoneId, c.MaxUploadSizeMb, c.AllowedFileExtensions,
    };

    private static CompanyDto Map(Company c) => new(c.Name, c.LegalName, c.TaxNumber, c.Email, c.Phone, c.Website, c.Address, c.City,
        c.Country, c.PostalCode, c.LogoStorageKey is not null, c.CurrencyCode, c.CurrencySymbol, c.CurrencyDecimalPlaces, c.RoundingMode,
        c.FiscalYearStartMonth, c.TimeZoneId, c.MaxUploadSizeMb, c.AllowedFileExtensions, c.RowVersion.ToToken());
}

internal static class CompanyExtensions
{
    public static IEnumerable<string> AllowedExtensionsFrom(this Company _, string csv) =>
        csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(e => e.TrimStart('.').ToLowerInvariant()).Distinct();
}
