using System.Text.Json;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.Persistence;

public class AuditService(AppDbContext db, ICurrentUser currentUser, TimeProvider clock) : IAuditService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime,
            UserId = entry.UserId ?? currentUser.UserId,
            UserName = entry.UserName ?? currentUser.UserName,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            OldValues = entry.OldValues is null ? null : JsonSerializer.Serialize(entry.OldValues, Json),
            NewValues = entry.NewValues is null ? null : JsonSerializer.Serialize(entry.NewValues, Json),
            Reason = entry.Reason,
            IpAddress = currentUser.IpAddress,
            CorrelationId = currentUser.CorrelationId,
        });
        await db.SaveChangesAsync(ct);
    }
}
