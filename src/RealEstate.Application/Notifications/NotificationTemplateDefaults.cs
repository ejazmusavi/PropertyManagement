using RealEstate.Domain.Enums;

namespace RealEstate.Application.Notifications;

/// <summary>Standard wording seeded once per (type, channel); later edits are kept. Text is short so it also fits SMS/WhatsApp/push.</summary>
public static class NotificationTemplateDefaults
{
    public sealed record Entry(NotificationType Type, NotificationChannel Channel, string Subject, string Body);

    private static readonly (NotificationType Type, string Subject, string Body)[] Customer =
    [
        (NotificationType.InstallmentUpcoming, "Installment due on {{DueDate}}", "Dear {{CustomerName}}, your installment of {{Amount}} for agreement {{AgreementNumber}} is due on {{DueDate}}. Thank you."),
        (NotificationType.InstallmentOverdue, "Installment overdue - {{AgreementNumber}}", "Dear {{CustomerName}}, your installment of {{Amount}} for agreement {{AgreementNumber}} was due on {{DueDate}} and is {{DaysOverdue}} day(s) overdue. Please pay as soon as possible."),
        (NotificationType.PaymentConfirmation, "Payment received", "Dear {{CustomerName}}, we received {{Amount}} for agreement {{AgreementNumber}}. Receipt: {{ReceiptNumber}}. Thank you."),
        (NotificationType.BookingConfirmation, "Booking confirmed", "Dear {{CustomerName}}, your booking of {{PropertyCode}} in {{ProjectName}} is confirmed (agreement {{AgreementNumber}})."),
        (NotificationType.AgreementAmendment, "Agreement amended", "Dear {{CustomerName}}, agreement {{AgreementNumber}} was amended. {{Message}}"),
        (NotificationType.PossessionNotice, "Possession notice", "Dear {{CustomerName}}, {{Message}}"),
        (NotificationType.CompanyAnnouncement, "{{Title}}", "Dear {{CustomerName}}, {{Message}}"),
    ];

    public static IReadOnlyList<Entry> All { get; } = Build();

    private static List<Entry> Build()
    {
        var list = new List<Entry>();
        foreach (var (type, subject, body) in Customer)
            foreach (var channel in new[] { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Push })
                list.Add(new Entry(type, channel, subject, body));
        foreach (var channel in new[] { NotificationChannel.InApp, NotificationChannel.Email })
            list.Add(new Entry(NotificationType.StaffDigest, channel, "{{Title}}", "{{Message}}"));
        return list;
    }
}
