using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Auth;
using RealEstate.Application.Notifications;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Notifications;

namespace RealEstate.Infrastructure.Identity;

/// <summary>
/// Delivers the password reset code by e-mail through the notification e-mail channel (<c>Notifications:Email</c>). The code is a secret, so it is sent
/// directly and never stored in the notification tables. When the e-mail channel is not configured the request is only logged (the code itself is written
/// to the log only when <c>PasswordReset:LogToken</c> is true, in development). Failures never reach the caller, so the reset endpoint cannot reveal
/// whether an account exists.
/// </summary>
public sealed class EmailPasswordResetNotifier(SmtpEmailSender email, ILogger<EmailPasswordResetNotifier> logger, IConfiguration config) : IPasswordResetNotifier
{
    public async Task SendAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        var body = $"Dear {token.FullName},\n\nYour password reset code is:\n\n{token.Token}\n\nEnter it in the app together with your new password. If you did not ask for a reset, ignore this message.";
        SendResult result;
        try { result = await email.SendAsync(new DeliveryMessage(0, 0, NotificationChannel.Email, token.Email, "Your password reset code", body, null, token.UserId, null), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Password reset e-mail for user {UserId} could not be sent", token.UserId);
            return;
        }
        if (result.Success) { logger.LogInformation("Password reset code e-mailed to user {UserId}", token.UserId); return; }
        if (!result.Skipped) { logger.LogWarning("Password reset e-mail for user {UserId} failed: {Error}", token.UserId, result.Error); return; }

        if (config.GetValue<bool>("PasswordReset:LogToken"))
            logger.LogWarning("DEVELOPMENT ONLY: password reset code for {Email}: {Token}", token.Email, token.Token);
        else
            logger.LogInformation("Password reset requested for user {UserId}; the e-mail channel is not configured", token.UserId);
    }
}
