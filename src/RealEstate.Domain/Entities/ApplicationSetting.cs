using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

public class ApplicationSetting : AuditableEntity<int>
{
    public string Key { get; set; } = default!;
    public string? Value { get; set; }
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
