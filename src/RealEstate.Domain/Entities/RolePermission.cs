namespace RealEstate.Domain.Entities;

public class RolePermission
{
    public Guid RoleId { get; set; }
    public int PermissionId { get; set; }
    public Permission Permission { get; set; } = default!;
}
