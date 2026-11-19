using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RealEstate.Application.Auth;

namespace RealEstate.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Exchanges email and password for an access token and a refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct) =>
        ToActionResult(await auth.LoginAsync(request, ct));

    /// <summary>Rotates a refresh token and returns a new token pair.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct) =>
        ToActionResult(await auth.RefreshAsync(request, ct));

    /// <summary>Revokes the supplied refresh token (and every token issued from the same login).</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request, ct);
        return NoContent();
    }

    /// <summary>Returns the authenticated user with roles and permissions.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct) => await auth.GetCurrentUserAsync(ct);

    private ObjectResult ToActionResult(RealEstate.Application.Common.Result<TokenResponse> result)
    {
        if (result.Succeeded) return Ok(result.Value);
        var status = result.Error!.Code == AuthErrors.LockedOut ? StatusCodes.Status423Locked : StatusCodes.Status401Unauthorized;
        return StatusCode(status, new ProblemDetails { Status = status, Title = "Authentication failed", Detail = result.Error.Message });
    }
}
