using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RealEstate.Application.Auth;

namespace RealEstate.Api.Controllers;

/// <summary>Forgot / reset password for every login (staff and customers).</summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthPasswordController(PasswordResetService reset) : ControllerBase
{
    /// <summary>Requests a reset code. Always answers 204: the response never tells whether the e-mail has an account.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Forgot(ForgotPasswordRequest request, CancellationToken ct)
    {
        await reset.RequestAsync(request, ct);
        return NoContent();
    }

    /// <summary>Sets a new password with the reset code; all sessions of the user end.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Reset(ResetPasswordRequest request, CancellationToken ct)
    {
        await reset.ResetAsync(request, ct);
        return NoContent();
    }
}
