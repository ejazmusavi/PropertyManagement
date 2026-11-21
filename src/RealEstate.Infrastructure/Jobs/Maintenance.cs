using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Jobs;
using RealEstate.Infrastructure.Persistence;

namespace RealEstate.Infrastructure.Jobs;

public sealed class MaintenanceStore(AppDbContext db) : IMaintenanceStore
{
    /// <summary>Removes login tokens that expired or were revoked before the cut-off (they can no longer be used; the audit log is untouched).</summary>
    public async Task<int> PurgeExpiredRefreshTokensAsync(DateTime olderThanUtc, CancellationToken ct = default) =>
        await db.RefreshTokens.Where(t => t.ExpiresAtUtc < olderThanUtc || (t.RevokedAtUtc != null && t.RevokedAtUtc < olderThanUtc)).ExecuteDeleteAsync(ct);
}

/// <summary>Set by the job runner for the duration of one job: the configured service user the permission-checked services run as.</summary>
public sealed class BackgroundUserContext
{
    public Guid? UserId { get; private set; }
    public string? UserName { get; private set; }
    public void Set(Guid id, string name) { UserId = id; UserName = name; }
}
