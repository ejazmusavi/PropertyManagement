using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Auth;

namespace RealEstate.Infrastructure.Identity;

/// <summary>
/// Placeholder delivery until the e-mail/SMS channels of Phase 11 exist: it logs that a reset was requested. The code itself is written to the log only when
/// <c>PasswordReset:LogToken</c> is true (development), so that a developer can complete the flow; it is never logged otherwise.
/// </summary>
public sealed class LoggingPasswordResetNotifier(ILogger<LoggingPasswordResetNotifier> logger, IConfiguration config) : IPasswordResetNotifier
{
    public Task SendAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        if (config.GetValue<bool>("PasswordReset:LogToken"))
            logger.LogWarning("DEVELOPMENT ONLY: password reset code for {Email}: {Token}", token.Email, token.Token);
        else
            logger.LogInformation("Password reset requested for user {UserId}; no delivery channel is configured yet", token.UserId);
        return Task.CompletedTask;
    }
}
