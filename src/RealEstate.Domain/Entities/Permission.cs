using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

/// <summary>A named capability, e.g. "users.view". The catalog is defined in code and synchronised to the database.</summary>
public class Permission : Entity<int>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Module { get; set; } = default!;
}
