using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>The single company that owns all projects (no multi-tenancy).</summary>
public class Company : AuditableEntity<int>
{
    public string Name { get; private set; } = default!;
    public string? LegalName { get; set; }
    public string? TaxNumber { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }

    public string? LogoStorageKey { get; set; }
    public string? LogoContentType { get; set; }

    // Currency and financial settings
    public string CurrencyCode { get; set; } = "PKR";
    public string CurrencySymbol { get; set; } = "Rs";
    public int CurrencyDecimalPlaces { get; set; } = 2;
    public RoundingMode RoundingMode { get; set; } = RoundingMode.Nearest;
    /// <summary>1-12; the fiscal year starts on the first day of this month.</summary>
    public int FiscalYearStartMonth { get; set; } = 7;
    public string TimeZoneId { get; set; } = "Asia/Karachi";

    // Document settings
    public int MaxUploadSizeMb { get; set; } = 10;
    public string AllowedFileExtensions { get; set; } = "pdf,jpg,jpeg,png,doc,docx,xls,xlsx";

    public byte[] RowVersion { get; set; } = [];

    private Company() { }

    public Company(string name) => Rename(name);

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Company name is required.");
        Name = name.Trim();
    }

    public IReadOnlySet<string> AllowedExtensions() =>
        AllowedFileExtensions.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();
}
