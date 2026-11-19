using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

/// <summary>Immutable audit record: who, when, what, old/new values and reason. Never updated or deleted.</summary>
public class AuditLog : Entity<long>
{
    public DateTime OccurredAtUtc { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
}
