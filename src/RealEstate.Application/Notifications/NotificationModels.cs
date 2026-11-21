using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

// ----- channels -----
/// <summary>What a channel adapter is given to deliver. The text is already rendered.</summary>
public sealed record DeliveryMessage(long DeliveryId, long NotificationId, NotificationChannel Channel, string Recipient, string Subject, string Body, long? CustomerId, Guid? UserId, long? CustomerNotificationId);

/// <summary>Outcome of one send attempt. <see cref="Skipped"/> = channel off or not configured (not an error); <see cref="Permanent"/> = do not retry (e.g. invalid address).</summary>
public sealed record SendResult(bool Success, bool Skipped = false, bool Permanent = false, string? ProviderReference = null, string? Error = null)
{
    public static SendResult Sent(string? reference = null) => new(true, ProviderReference: reference);
    public static SendResult Skip(string reason) => new(false, Skipped: true, Error: reason);
    public static SendResult Transient(string error) => new(false, Error: error);
    public static SendResult Permanently(string error) => new(false, Permanent: true, Error: error);
}

/// <summary>One adapter per channel. In-app is built in; e-mail uses SMTP when configured; SMS, WhatsApp and push providers are added as adapters without touching the module.</summary>
public interface INotificationChannelSender
{
    NotificationChannel Channel { get; }
    Task<SendResult> SendAsync(DeliveryMessage message, CancellationToken ct = default);
}

// ----- DTOs -----
public sealed record NotificationListItemDto(long Id, NotificationType Type, string Title, long? CustomerId, string? CustomerName, Guid? RecipientUserId, long? ProjectId, NotificationStatus Status,
    DateTime CreatedAtUtc, int DeliveryCount, int SentCount, int FailedCount);

public sealed record DeliveryDto(long Id, NotificationChannel Channel, string? Recipient, string Subject, string Body, DeliveryStatus Status, int Attempts, int MaxAttempts,
    DateTime NextAttemptAtUtc, DateTime? LastAttemptAtUtc, DateTime? SentAtUtc, string? ProviderReference, string? LastError);

public sealed record NotificationDetailDto(long Id, NotificationType Type, string Title, string Body, long? CustomerId, string? CustomerName, Guid? RecipientUserId, long? ProjectId, long? AgreementId,
    string? RelatedType, long? RelatedId, string DedupeKey, NotificationStatus Status, DateTime ScheduledForUtc, DateTime CreatedAtUtc, string? CreatedBy, string? CancelReason,
    IReadOnlyList<DeliveryDto> Deliveries, string RowVersion);

public sealed class NotificationListRequest : PagedRequest
{
    public NotificationType? Type { get; set; }
    public NotificationStatus? Status { get; set; }
    public NotificationChannel? Channel { get; set; }
    public DeliveryStatus? DeliveryStatus { get; set; }
    public long? CustomerId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record NotificationSummaryDto(int Pending, int Completed, int PartiallyFailed, int Failed, int RetryingDeliveries, int FailedDeliveries, int SentLast24Hours);

public sealed record TemplateDto(long Id, NotificationType Type, NotificationChannel Channel, string Language, string Subject, string Body, bool IsActive, string RowVersion);

public sealed record SaveTemplateRequest(NotificationType Type, NotificationChannel Channel, string? Language, string Subject, string Body, bool IsActive, string? RowVersion);

public sealed record SendNotificationRequest(NotificationType Type, long? CustomerId, long? ProjectId, long? PropertyId, string Title, string Message, string? IdempotencyKey);

public sealed record SendNotificationResultDto(int Recipients, int Queued, int Duplicates);

public sealed record CancelNotificationRequest(string Reason, string? RowVersion);

public sealed record PreferenceDto(NotificationChannel Channel, NotificationType? Type, bool IsEnabled, bool Locked);

public sealed record SavePreferenceRequest(NotificationChannel Channel, NotificationType? Type, bool IsEnabled);

public sealed record SavePreferencesRequest(IReadOnlyList<SavePreferenceRequest> Preferences);

public sealed record ReminderSettingsDto(int UpcomingDaysBefore, int OverdueRepeatDays, int EventLookbackDays);

public sealed record StaffInboxItemDto(long Id, NotificationType Type, string Title, string Body, DateTime CreatedAtUtc, DateTime? ReadAtUtc);

public sealed record DispatchResultDto(int Processed, int Sent, int Retrying, int Failed, int Skipped);

/// <summary>What a producer asks the queue for. <see cref="Data"/> feeds the template placeholders.</summary>
public sealed record NotificationDraft(NotificationType Type, string DedupeKey, long? CustomerId, Guid? UserId, long? ProjectId, long? AgreementId, string Title, string Message,
    IReadOnlyDictionary<string, string> Data, string? RelatedType = null, long? RelatedId = null, DateTime? ScheduledForUtc = null,
    IReadOnlyCollection<NotificationChannel>? OnlyChannels = null);

// ----- validators -----
public class SaveTemplateRequestValidator : AbstractValidator<SaveTemplateRequest>
{
    public SaveTemplateRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Channel).IsInEnum();
        RuleFor(x => x.Language).Must(l => string.IsNullOrWhiteSpace(l) || l.Trim().Length is >= 2 and <= 10).WithMessage("The language code must have 2-10 characters.");
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body).NotEmpty().MaximumLength(2000);
        RuleFor(x => x).Custom((r, ctx) =>
        {
            foreach (var u in NotificationRules.UnknownPlaceholders(r.Subject ?? "").Concat(NotificationRules.UnknownPlaceholders(r.Body ?? "")).Distinct())
                ctx.AddFailure("Body", $"Unknown placeholder {{{{{u}}}}}. Allowed: {string.Join(", ", NotificationRules.Placeholders)}.");
        });
    }
}

public class SendNotificationRequestValidator : AbstractValidator<SendNotificationRequest>
{
    public SendNotificationRequestValidator()
    {
        RuleFor(x => x.Type).Must(t => t is NotificationType.CompanyAnnouncement or NotificationType.PossessionNotice or NotificationType.AgreementAmendment or NotificationType.BookingConfirmation)
            .WithMessage("Only announcements, possession, amendment and booking messages can be sent by hand.");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
        RuleFor(x => x).Must(x => x.CustomerId is not null || x.ProjectId is not null || x.PropertyId is not null || x.Type == NotificationType.CompanyAnnouncement)
            .WithMessage("Choose a customer, a project or a property to send to.");
    }
}

public class CancelNotificationRequestValidator : AbstractValidator<CancelNotificationRequest>
{
    public CancelNotificationRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500);
}

public class SavePreferencesRequestValidator : AbstractValidator<SavePreferencesRequest>
{
    public SavePreferencesRequestValidator()
    {
        RuleFor(x => x.Preferences).NotNull().Must(p => p is { Count: <= 100 }).WithMessage("Too many preferences.");
        RuleForEach(x => x.Preferences).ChildRules(p =>
        {
            p.RuleFor(x => x.Channel).IsInEnum();
            p.RuleFor(x => x.Type).Must(t => t is null || Enum.IsDefined(t.Value)).WithMessage("Invalid notification type.");
        });
    }
}
