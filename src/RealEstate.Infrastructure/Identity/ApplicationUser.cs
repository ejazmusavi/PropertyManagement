using Microsoft.AspNetCore.Identity;

namespace RealEstate.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string name) : base(name) { }
    public string? Description { get; set; }
    /// <summary>System roles cannot be renamed or deleted.</summary>
    public bool IsSystem { get; set; }
}
