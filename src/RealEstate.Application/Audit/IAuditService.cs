namespace RealEstate.Application.Audit;

public sealed record AuditEntry(
    string Action,
    string EntityType,
    string? EntityId = null,
    object? OldValues = null,
    object? NewValues = null,
    string? Reason = null,
    Guid? UserId = null,
    string? UserName = null);

public interface IAuditService
{
    /// <summary>Appends an audit record. Caller identity defaults to the current user unless overridden (e.g. failed login).</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);
}
