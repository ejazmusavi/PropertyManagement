using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RealEstate.Application.Auth;
using RealEstate.Application.Notifications;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Infrastructure.Notifications;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Channel adapters and the password-reset delivery (no database needed).</summary>
public class Phase11ChannelTests
{
    private static IConfiguration Config(params (string, string?)[] kv) => new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();

    private static DeliveryMessage Message(NotificationChannel channel, string recipient) => new(1, 1, channel, recipient, "Subject", "Body", 5, null, null);

    [Fact]
    public async Task Email_IsSkipped_WhenNotConfigured()
    {
        var sender = new SmtpEmailSender(Config(), NullLogger<SmtpEmailSender>.Instance);
        var r = await sender.SendAsync(Message(NotificationChannel.Email, "a@example.com"));
        Assert.True(r.Skipped);
        Assert.False(r.Success);
        Assert.Contains("not configured", r.Error);
        // enabled but without a host is still "not configured"
        Assert.True((await new SmtpEmailSender(Config(("Notifications:Email:Enabled", "true")), NullLogger<SmtpEmailSender>.Instance).SendAsync(Message(NotificationChannel.Email, "a@example.com"))).Skipped);
    }

    [Fact]
    public async Task Email_RejectsBadAddresses_Permanently_AndNeedsAFromAddress()
    {
        var on = Config(("Notifications:Email:Enabled", "true"), ("Notifications:Email:Host", "127.0.0.1"), ("Notifications:Email:FromAddress", "office@example.com"));
        var bad = await new SmtpEmailSender(on, NullLogger<SmtpEmailSender>.Instance).SendAsync(Message(NotificationChannel.Email, "not an address"));
        Assert.True(bad.Permanent);
        Assert.False(bad.Success);
        var noFrom = Config(("Notifications:Email:Enabled", "true"), ("Notifications:Email:Host", "127.0.0.1"));
        Assert.True((await new SmtpEmailSender(noFrom, NullLogger<SmtpEmailSender>.Instance).SendAsync(Message(NotificationChannel.Email, "a@example.com"))).Skipped);
    }

    [Fact]
    public async Task Email_ServerOutage_IsATransientFailure()
    {
        var cfg = Config(("Notifications:Email:Enabled", "true"), ("Notifications:Email:Host", "127.0.0.1"), ("Notifications:Email:Port", "1"), ("Notifications:Email:UseSsl", "false"), ("Notifications:Email:FromAddress", "office@example.com"));
        var r = await new SmtpEmailSender(cfg, NullLogger<SmtpEmailSender>.Instance).SendAsync(Message(NotificationChannel.Email, "a@example.com"));
        Assert.False(r.Success);
        Assert.False(r.Permanent);
        Assert.False(r.Skipped);          // retried later by the dispatcher
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Theory]
    [InlineData(NotificationChannel.Sms)]
    [InlineData(NotificationChannel.WhatsApp)]
    [InlineData(NotificationChannel.Push)]
    public async Task ProviderChannels_AreSkippedByDefault_AndLogOnlyInDevelopmentMode(NotificationChannel channel)
    {
        var off = await new ConfigurableChannelSender(channel, Config(), NullLogger<ConfigurableChannelSender>.Instance).SendAsync(Message(channel, "0300-1"));
        Assert.True(off.Skipped);
        Assert.Contains("no provider", off.Error);
        var explicitOff = await new ConfigurableChannelSender(channel, Config(($"Notifications:Channels:{channel}:Mode", "Off")), NullLogger<ConfigurableChannelSender>.Instance).SendAsync(Message(channel, "0300-1"));
        Assert.True(explicitOff.Skipped);
        var log = await new ConfigurableChannelSender(channel, Config(($"Notifications:Channels:{channel}:Mode", "Log")), NullLogger<ConfigurableChannelSender>.Instance).SendAsync(Message(channel, "0300-1"));
        Assert.True(log.Success);
        Assert.Equal("log", log.ProviderReference);
        Assert.Equal(channel, new ConfigurableChannelSender(channel, Config(), NullLogger<ConfigurableChannelSender>.Instance).Channel);
    }

    [Fact]
    public async Task PasswordReset_FallsBackToTheLog_AndNeverThrows()
    {
        var token = new PasswordResetToken(Guid.NewGuid(), "ali@example.com", "Ali Khan", "SECRET-CODE-123");
        var logger = new CapturingLogger<EmailPasswordResetNotifier>();

        // e-mail not configured: logged, the code is not
        var cfg = Config();
        await new EmailPasswordResetNotifier(new SmtpEmailSender(cfg, NullLogger<SmtpEmailSender>.Instance), logger, cfg).SendAsync(token);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("SECRET-CODE-123"));
        Assert.Contains(logger.Lines, l => l.Contains("e-mail channel is not configured"));

        // development switch: the code may be logged
        var dev = Config(("PasswordReset:LogToken", "true"));
        var devLogger = new CapturingLogger<EmailPasswordResetNotifier>();
        await new EmailPasswordResetNotifier(new SmtpEmailSender(dev, NullLogger<SmtpEmailSender>.Instance), devLogger, dev).SendAsync(token);
        Assert.Contains(devLogger.Lines, l => l.Contains("SECRET-CODE-123"));

        // a broken mail server must not break (or reveal anything about) the reset request, and the code is never logged
        var broken = Config(("Notifications:Email:Enabled", "true"), ("Notifications:Email:Host", "127.0.0.1"), ("Notifications:Email:Port", "1"), ("Notifications:Email:UseSsl", "false"), ("Notifications:Email:FromAddress", "office@example.com"));
        var brokenLogger = new CapturingLogger<EmailPasswordResetNotifier>();
        await new EmailPasswordResetNotifier(new SmtpEmailSender(broken, NullLogger<SmtpEmailSender>.Instance), brokenLogger, broken).SendAsync(token);
        Assert.DoesNotContain(brokenLogger.Lines, l => l.Contains("SECRET-CODE-123"));
        Assert.Contains(brokenLogger.Lines, l => l.Contains("failed"));
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
