using RealEstate.Application.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Portal;

/// <summary>Writes in-app customer notifications in the caller's unit of work (the caller saves). Delivery channels (SMS, e-mail, push) are Phase 11.</summary>
public static class PortalNotifications
{
    public static void Add(IAppDbContext db, long customerId, NotificationKind kind, string title, string message, string? relatedType, long? relatedId, DateTime nowUtc) =>
        db.CustomerNotifications.Add(new CustomerNotification
        {
            CustomerId = customerId, Kind = kind, Title = Trim(title, 200), Message = Trim(message, 2000), RelatedType = relatedType, RelatedId = relatedId, CreatedAtUtc = nowUtc,
        });

    private static string Trim(string v, int max) => v.Length > max ? v[..max] : v;
}
