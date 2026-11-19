using RealEstate.Domain.Common;

namespace RealEstate.Domain.Entities;

/// <summary>API refresh token. Only a hash of the token is stored. Tokens rotate on every use.</summary>
public class RefreshToken : Entity<long>
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = default!;
    /// <summary>All tokens descended from one login share a family so reuse of a rotated token revokes the lot.</summary>
    public Guid FamilyId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;

    public void Revoke(DateTime utcNow, string? replacedByHash = null)
    {
        RevokedAtUtc ??= utcNow;
        ReplacedByTokenHash = replacedByHash ?? ReplacedByTokenHash;
    }
}
