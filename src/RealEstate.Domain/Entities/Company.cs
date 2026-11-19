using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

/// <summary>The single company that owns all projects (no multi-tenancy).</summary>
public class Company : AuditableEntity<int>
{
    public string Name { get; private set; } = default!;
    public string? LegalName { get; set; }
    public string? TaxNumber { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public byte[] RowVersion { get; set; } = [];

    private Company() { }

    public Company(string name) => Rename(name);

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Company name is required.");
        Name = name.Trim();
    }
}
