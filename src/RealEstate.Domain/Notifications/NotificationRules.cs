using System.Text.RegularExpressions;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>Pure rules of the notification module: template rendering, retry back-off, channel resolution, aggregate status.</summary>
public static partial class NotificationRules
{
    public const int MaxAttempts = 5;

    /// <summary>Placeholders every template may use (values come from the notification's data; a missing value renders as an empty string).</summary>
    public static readonly IReadOnlyList<string> Placeholders =
        ["CustomerName", "ProjectName", "PropertyCode", "AgreementNumber", "Amount", "DueDate", "DaysOverdue", "ReceiptNumber", "Title", "Message"];

    /// <summary>Types whose business event is already shown to the customer as an in-app notice by an earlier phase (so the in-app channel is not duplicated).</summary>
    public static readonly IReadOnlySet<NotificationType> InAppWrittenElsewhere = new HashSet<NotificationType> { NotificationType.PaymentConfirmation };

    /// <summary>Back-off after a failed attempt: 1 minute, 5 minutes, 15 minutes, 1 hour, 6 hours.</summary>
    public static TimeSpan Backoff(int attemptsMade) => attemptsMade switch
    {
        <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5), 3 => TimeSpan.FromMinutes(15), 4 => TimeSpan.FromHours(1), _ => TimeSpan.FromHours(6),
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Placeholder names used in a text that are not in the allowed list.</summary>
    public static IReadOnlyList<string> UnknownPlaceholders(string text) =>
        [.. PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).Where(n => !Placeholders.Contains(n, StringComparer.Ordinal)).Distinct()];

    public static string Render(string template, IReadOnlyDictionary<string, string> data) =>
        PlaceholderRegex().Replace(template, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>
    /// Channels a recipient receives a notification on: the in-app channel always (unless an earlier phase already wrote it), every other channel unless the
    /// recipient switched it off, either for all types (type key 0) or for this type (the specific choice wins).
    /// </summary>
    public static IReadOnlyList<NotificationChannel> ResolveChannels(NotificationType type, IEnumerable<NotificationChannel> candidates,
        IEnumerable<(NotificationChannel Channel, int TypeKey, bool Enabled)> preferences)
    {
        var prefs = preferences.ToList();
        var result = new List<NotificationChannel>();
        foreach (var channel in candidates.Distinct())
        {
            if (channel == NotificationChannel.InApp)
            {
                if (!InAppWrittenElsewhere.Contains(type)) result.Add(channel);
                continue;
            }
            var specific = prefs.FirstOrDefault(p => p.Channel == channel && p.TypeKey == (int)type);
            var general = prefs.FirstOrDefault(p => p.Channel == channel && p.TypeKey == 0);
            var enabled = specific != default ? specific.Enabled : general == default || general.Enabled;
            if (enabled) result.Add(channel);
        }
        return result;
    }

    /// <summary>The state of a notification after its deliveries changed.</summary>
    public static NotificationStatus Aggregate(IReadOnlyCollection<DeliveryStatus> deliveries)
    {
        if (deliveries.Count == 0) return NotificationStatus.Completed;
        if (deliveries.Any(d => d is DeliveryStatus.Pending or DeliveryStatus.Retrying)) return NotificationStatus.Pending;
        var failed = deliveries.Count(d => d == DeliveryStatus.Failed);
        if (failed == 0) return deliveries.All(d => d == DeliveryStatus.Cancelled) ? NotificationStatus.Cancelled : NotificationStatus.Completed;
        return failed == deliveries.Count(d => d != DeliveryStatus.Skipped && d != DeliveryStatus.Cancelled) ? NotificationStatus.Failed : NotificationStatus.PartiallyFailed;
    }

    public static string OwnerKey(long? customerId, Guid? userId) =>
        customerId is { } c ? $"C:{c}" : userId is { } u ? $"U:{u:N}" : throw new DomainException("A preference belongs to a customer or a user.");
}
