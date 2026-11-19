namespace RealEstate.Domain.Common;

public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; set; } = default!;
}

/// <summary>Creator/modifier tracking, filled automatically by the DbContext on save.</summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTime? ModifiedAtUtc { get; set; }
    string? ModifiedBy { get; set; }
}

public abstract class AuditableEntity<TId> : Entity<TId>, IAuditable where TId : notnull
{
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
}
