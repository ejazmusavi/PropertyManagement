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
