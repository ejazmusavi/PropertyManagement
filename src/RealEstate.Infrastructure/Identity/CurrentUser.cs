using System.Security.Claims;
using RealEstate.Application.Common;

namespace RealEstate.Infrastructure.Identity;

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
