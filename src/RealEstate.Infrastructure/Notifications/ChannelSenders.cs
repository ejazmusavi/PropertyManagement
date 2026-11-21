using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RealEstate.Application.Notifications;
using RealEstate.Domain.Enums;

namespace RealEstate.Infrastructure.Notifications;

/// <summary>
/// E-mail channel over SMTP. Switched on with <c>Notifications:Email:Enabled=true</c> and a host; credentials come from environment variables or user secrets
/// (<c>Notifications:Email:UserName/Password</c>), never from project files. When it is off the delivery ends as Skipped, not as an error.
/// </summary>
public sealed class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : INotificationChannelSender
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<SendResult> SendAsync(DeliveryMessage m, CancellationToken ct = default)
    {
        var s = config.GetSection("Notifications:Email");
        if (!s.GetValue<bool>("Enabled") || string.IsNullOrWhiteSpace(s["Host"])) return SendResult.Skip("The e-mail channel is not configured (Notifications:Email).");
        if (!MailAddress.TryCreate(m.Recipient, out var to)) return SendResult.Permanently("The e-mail address is not valid.");
        var fromAddress = s["FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress) || !MailAddress.TryCreate(fromAddress, out var from)) return SendResult.Skip("Notifications:Email:FromAddress is not configured.");

        using var client = new SmtpClient(s["Host"], s.GetValue("Port", 587)) { EnableSsl = s.GetValue("UseSsl", true), Timeout = 30_000 };
        if (!string.IsNullOrWhiteSpace(s["UserName"])) client.Credentials = new NetworkCredential(s["UserName"], s["Password"]);
        using var mail = new MailMessage(new MailAddress(from.Address, s["FromName"] ?? from.DisplayName), to) { Subject = m.Subject, Body = m.Body, IsBodyHtml = false };
        try
        {
            await client.SendMailAsync(mail, ct);
            return SendResult.Sent();
        }
        catch (SmtpFailedRecipientException ex) { return SendResult.Permanently(ex.Message); }
        catch (SmtpException ex)
        {
            logger.LogWarning(ex, "SMTP failure for delivery {DeliveryId}", m.DeliveryId);
            return SendResult.Transient(ex.Message);
        }
    }
}

/// <summary>
/// Adapter slot for SMS, WhatsApp and mobile push. No provider is bundled (provider contracts are a company decision): <c>Notifications:Channels:{Channel}:Mode</c>
/// is <c>Off</c> (default: deliveries end as Skipped) or <c>Log</c> (development only: the message is written to the log and marked as sent).
/// A real provider is added by registering another <see cref="INotificationChannelSender"/> for the channel; nothing else changes.
/// </summary>
public sealed class ConfigurableChannelSender(NotificationChannel channel, IConfiguration config, ILogger<ConfigurableChannelSender> logger) : INotificationChannelSender
{
    public NotificationChannel Channel => channel;

    public Task<SendResult> SendAsync(DeliveryMessage m, CancellationToken ct = default)
    {
        var mode = config[$"Notifications:Channels:{channel}:Mode"];
        if (!string.Equals(mode, "Log", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(SendResult.Skip($"The {channel} channel has no provider configured."));
        logger.LogInformation("DEVELOPMENT {Channel} to {Recipient}: {Subject} | {Body}", channel, m.Recipient, m.Subject, m.Body);
        return Task.FromResult(SendResult.Sent("log"));
    }
}
