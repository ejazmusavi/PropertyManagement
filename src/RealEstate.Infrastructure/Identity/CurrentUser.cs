using System.Security.Claims;
using RealEstate.Application.Common;
using RealEstate.Infrastructure.Jobs;

namespace RealEstate.Infrastructure.Identity;

/// <summary>The caller: the authenticated HTTP user, or (inside a background job) the configured service user set on <see cref="BackgroundUserContext"/>.</summary>
public class CurrentUser(IHttpContextAccessor accessor, BackgroundUserContext? background = null) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : background?.UserId;
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name) ?? background?.UserName;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true || background?.UserId is not null;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
