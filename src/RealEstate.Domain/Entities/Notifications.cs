using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>
/// Wording of one notification type on one channel in one language. Placeholders look like <c>{{CustomerName}}</c> (see NotificationRules.Placeholders).
/// The text is rendered when a notification is queued and stored on the delivery log, so later template edits never change what was sent.
/// </summary>
public class NotificationTemplate : AuditableEntity<long>
{
    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; }
    public string Language { get; set; } = "en";
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A channel on/off choice of one customer or one staff user, for one notification type or (TypeKey 0) for all types. No row means the default (on).
/// The in-app channel cannot be switched off. Unique per (OwnerKey, Channel, TypeKey).
/// </summary>
public class NotificationPreference : AuditableEntity<long>
{
    public long? CustomerId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>"C:{customerId}" or "U:{userId}" - makes the uniqueness rule simple.</summary>
    public string OwnerKey { get; set; } = default!;
    public NotificationChannel Channel { get; set; }
    /// <summary>0 = all notification types, otherwise (int)NotificationType.</summary>
    public int TypeKey { get; set; }
    public bool IsEnabled { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One business message to one recipient (a customer, or a staff user for the digest). It is created once per <see cref="DedupeKey"/> (so re-running a job
/// never sends twice) and fans out into one <see cref="NotificationDeliveryLog"/> per channel.
/// </summary>
public class Notification : AuditableEntity<long>
{
    public NotificationType Type { get; set; }
    public long? CustomerId { get; set; }
    public Guid? RecipientUserId { get; set; }
    public long? ProjectId { get; set; }
    public long? AgreementId { get; set; }
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    /// <summary>Placeholder values (JSON object of strings) used to render the channel templates.</summary>
    public string? DataJson { get; set; }
    public string? RelatedType { get; set; }
    public long? RelatedId { get; set; }
    /// <summary>Unique business key, e.g. "installment-upcoming:15:2026-12-01". Makes queuing idempotent.</summary>
    public string DedupeKey { get; set; } = default!;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public DateTime ScheduledForUtc { get; set; }
    /// <summary>When a staff recipient opened it in the staff inbox.</summary>
    public DateTime? ReadAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<NotificationDeliveryLog> Deliveries { get; set; } = [];
}

/// <summary>One attempt series to deliver a notification on one channel; keeps the rendered text, the outcome and the retry state.</summary>
public class NotificationDeliveryLog : Entity<long>
{
    public long NotificationId { get; set; }
    public NotificationChannel Channel { get; set; }
    /// <summary>Address used (e-mail address, phone number, device token reference, or "in-app").</summary>
    public string? Recipient { get; set; }
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = NotificationRules.MaxAttempts;
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? ProviderReference { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One execution of a background job (scheduled or started by hand); the visible history behind the job screen.</summary>
public class BackgroundJobRun : Entity<long>
{
    public string JobKey { get; set; } = default!;
    public string TriggeredBy { get; set; } = "schedule";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public BackgroundJobRunStatus Status { get; set; } = BackgroundJobRunStatus.Running;
    public int ItemsProcessed { get; set; }
    public string? Message { get; set; }
}
