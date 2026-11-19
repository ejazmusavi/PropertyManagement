using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

/// <summary>Assignment of a user to a project (project-level access). ProjectId references Project, added in Phase 1.</summary>
public class UserProject : AuditableEntity<long>
{
    public Guid UserId { get; set; }
    public long ProjectId { get; set; }
    public bool IsActive { get; set; } = true;
}
