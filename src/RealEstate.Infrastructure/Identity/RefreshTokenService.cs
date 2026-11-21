using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RealEstate.Application.Auth;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Persistence;

namespace RealEstate.Infrastructure.Identity;

public class RefreshTokenService(AppDbContext db, IOptions<JwtOptions> options, TimeProvider clock) : IRefreshTokenService
{
    public async Task<RefreshTokenIssue> IssueAsync(Guid userId, string? ip, CancellationToken ct = default)
    {
        var (entity, raw) = Create(userId, Guid.NewGuid(), ip);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return new RefreshTokenIssue(raw, entity.ExpiresAtUtc);
    }

    public async Task<RefreshOutcome> RotateAsync(string token, string? ip, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(token);
        var existing = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null) return new RefreshOutcome(false, Guid.Empty, null);

        if (existing.RevokedAtUtc is not null)
        {
            // A rotated/revoked token was presented again: assume theft and kill the whole family.
            await RevokeFamilyAsync(existing.FamilyId, now, ct);
            return new RefreshOutcome(false, existing.UserId, null);
        }
        if (existing.ExpiresAtUtc <= now) return new RefreshOutcome(false, existing.UserId, null);

        var (next, raw) = Create(existing.UserId, existing.FamilyId, ip);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Atomic compare-and-set: only one concurrent caller can revoke the token.
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == existing.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now).SetProperty(t => t.ReplacedByTokenHash, next.TokenHash), ct);
        if (claimed != 1)
        {
            await tx.RollbackAsync(ct);
            await RevokeFamilyAsync(existing.FamilyId, now, ct);
            return new RefreshOutcome(false, existing.UserId, null);
        }
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new RefreshOutcome(true, existing.UserId, new RefreshTokenIssue(raw, next.ExpiresAtUtc));
    }

    public async Task RevokeAsync(string token, Guid? expectedUserId, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null || (expectedUserId is { } uid && existing.UserId != uid)) return;
        await RevokeFamilyAsync(existing.FamilyId, now, ct);
    }

    public Task RevokeAllAsync(Guid userId, CancellationToken ct = default) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, clock.GetUtcNow().UtcDateTime), ct);

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);

    private (RefreshToken Entity, string Raw) Create(Guid userId, Guid familyId, string? ip)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = clock.GetUtcNow().UtcDateTime;
        return (new RefreshToken
        {
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(options.Value.RefreshTokenDays),
            CreatedByIp = ip,
        }, raw);
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
