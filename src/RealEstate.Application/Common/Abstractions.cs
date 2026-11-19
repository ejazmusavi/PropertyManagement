namespace RealEstate.Application.Common;

/// <summary>The authenticated caller, resolved from the current HTTP context.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? UserName { get; }
    bool IsAuthenticated { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
}

/// <summary>Permission lookups against the database (role-based, evaluated on every call so revocation is immediate).</summary>
public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
}

public static class PermissionServiceExtensions
{
    /// <summary>Throws <see cref="ForbiddenException"/> unless the current user holds the permission.</summary>
    public static async Task EnsureAsync(this IPermissionService permissions, ICurrentUser user, string permissionCode, CancellationToken ct = default)
    {
        if (user.UserId is not { } id || !await permissions.HasPermissionAsync(id, permissionCode, ct))
            throw new ForbiddenException();
    }
}

public static class ConcurrencyExtensions
{
    /// <summary>Applies the client-supplied row version (base64) so stale edits fail instead of overwriting.</summary>
    public static void UseRowVersion<T>(this IAppDbContext db, T entity, string? rowVersion) where T : class
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) return;
        try { db.Entry(entity).Property("RowVersion").OriginalValue = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new RequestValidationException(new Dictionary<string, string[]> { ["RowVersion"] = ["Invalid row version."] }); }
    }

    public static string ToToken(this byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static async Task<int> SaveWithConcurrencyAsync(this IAppDbContext db, CancellationToken ct = default)
    {
        try { return await db.SaveChangesAsync(ct); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { throw new ConcurrencyException(); }
        // Two writers racing on the same rows can be resolved by MySQL as a deadlock (the loser is rolled back): same outcome as a stale edit.
        catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException && IsDeadlock(ex)) { throw new ConcurrencyException(); }
    }

    private static bool IsDeadlock(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("Deadlock found", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
