using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Sales;

public sealed record AgreementOwner(string CustomerCode, string FullName, string IdentityNumber, decimal SharePercent, bool IsPrimary);
public sealed record AgreementLine(string PropertyCode, string PropertyNumber, decimal Size, MeasurementUnit Unit, decimal FinalPrice, decimal ExtraDiscount, decimal LinePrice, string SnapshotHash);
public sealed record AgreementChargeLine(string Description, decimal Amount);
public sealed record AgreementScheduleLine(int LineNo, string Description, DateOnly DueDate, decimal Amount, decimal TaxAmount);

public sealed record AgreementData(
    string CompanyName, string AgreementNumber, int VersionNo, string VersionReason, DateTime GeneratedAtUtc,
    string BookingNumber, DateOnly BookingDate, string ProjectCode, string ProjectName, PaymentMode PaymentMode, string CurrencySymbol,
    IReadOnlyList<AgreementOwner> Owners, IReadOnlyList<AgreementLine> Lines, IReadOnlyList<AgreementChargeLine> Charges,
    decimal TotalPrice, decimal TotalExtraDiscount, decimal TotalCharges, decimal TotalPayable, decimal BookingAmount, string? BalanceAuthorization,
    int? ScheduleVersionNo = null, IReadOnlyList<AgreementScheduleLine>? Schedule = null, IReadOnlyList<AgreementChargeLine>? AdditionalCharges = null);

/// <summary>Deterministic agreement and booking-receipt text. The same data always yields the same text, so versions can be verified by hash.</summary>
public static class AgreementText
{
    private static string M(string symbol, decimal v) => $"{symbol} {v.ToString("N2", CultureInfo.InvariantCulture)}";

    public static string BuildAgreement(AgreementData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{d.CompanyName}");
        sb.AppendLine("PROPERTY SALE AGREEMENT");
        sb.AppendLine($"Agreement No: {d.AgreementNumber}   Version: {d.VersionNo}");
        sb.AppendLine($"Booking No: {d.BookingNumber}   Booking date: {d.BookingDate:yyyy-MM-dd}");
        sb.AppendLine($"Generated (UTC): {d.GeneratedAtUtc:yyyy-MM-dd HH:mm}   Reason: {d.VersionReason}");
        sb.AppendLine();
        sb.AppendLine($"PROJECT: {d.ProjectCode} - {d.ProjectName}");
        sb.AppendLine();
        sb.AppendLine("PURCHASER(S) AND OWNERSHIP");
        foreach (var o in d.Owners)
            sb.AppendLine($"  {o.CustomerCode}  {o.FullName}  (ID {o.IdentityNumber})  share {o.SharePercent.ToString("0.####", CultureInfo.InvariantCulture)}%{(o.IsPrimary ? "  [primary]" : "")}");
        sb.AppendLine();
        sb.AppendLine("PROPERTY");
        foreach (var l in d.Lines)
        {
            sb.AppendLine($"  {l.PropertyCode} (no. {l.PropertyNumber})  {l.Size.ToString("0.####", CultureInfo.InvariantCulture)} {l.Unit}  price {M(d.CurrencySymbol, l.FinalPrice)}" +
                          (l.ExtraDiscount > 0 ? $"  discount {M(d.CurrencySymbol, l.ExtraDiscount)}" : "") + $"  net {M(d.CurrencySymbol, l.LinePrice)}");
            sb.AppendLine($"    pricing snapshot {l.SnapshotHash}");
        }
        sb.AppendLine();
        sb.AppendLine("PRICE AND PAYMENT");
        sb.AppendLine($"  Price of properties:  {M(d.CurrencySymbol, d.TotalPrice)}");
        if (d.TotalExtraDiscount > 0) sb.AppendLine($"  Additional discount: -{M(d.CurrencySymbol, d.TotalExtraDiscount)}");
        foreach (var c in d.Charges) sb.AppendLine($"  Charge - {c.Description}: {M(d.CurrencySymbol, c.Amount)}");
        sb.AppendLine($"  TOTAL PAYABLE:        {M(d.CurrencySymbol, d.TotalPayable)}");
        sb.AppendLine($"  Payment mode: {d.PaymentMode}   Due at booking: {M(d.CurrencySymbol, d.BookingAmount)}   Balance: {M(d.CurrencySymbol, d.TotalPayable - d.BookingAmount)}");
        if (d.BalanceAuthorization is not null) sb.AppendLine($"  Outstanding balance authorized: {d.BalanceAuthorization}");
        if (d.Schedule is { Count: > 0 } schedule)
        {
            if (d.AdditionalCharges is { Count: > 0 } extra)
                foreach (var c in extra) sb.AppendLine($"  Additional agreement charge - {c.Description}: {M(d.CurrencySymbol, c.Amount)}");
            sb.AppendLine();
            sb.AppendLine($"PAYMENT SCHEDULE (version {d.ScheduleVersionNo})");
            foreach (var s in schedule)
                sb.AppendLine($"  {s.LineNo,3}. {s.DueDate:yyyy-MM-dd}  {s.Description}  {M(d.CurrencySymbol, s.Amount)}" + (s.TaxAmount != 0 ? $"  tax {M(d.CurrencySymbol, s.TaxAmount)}" : ""));
            sb.AppendLine($"  SCHEDULE TOTAL:       {M(d.CurrencySymbol, schedule.Sum(s => s.Amount))}");
        }
        else
            sb.AppendLine(d.PaymentMode == PaymentMode.Installment ? "  The installment schedule is attached to this agreement when it is generated." : "  The balance is payable as authorized above.");
        sb.AppendLine();
        sb.AppendLine("This agreement is generated from the confirmed booking. Amendments create a new version; earlier versions are retained.");
        return sb.ToString();
    }

    /// <summary>Booking confirmation receipt: what was agreed and what is due. Payment receipts are issued when payments are recorded.</summary>
    public static string BuildReceipt(AgreementData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(d.CompanyName);
        sb.AppendLine("BOOKING CONFIRMATION RECEIPT");
        sb.AppendLine($"Booking No: {d.BookingNumber}   Agreement No: {d.AgreementNumber}   Date: {d.BookingDate:yyyy-MM-dd}");
        sb.AppendLine($"Project: {d.ProjectCode} - {d.ProjectName}");
        sb.AppendLine("Customer(s): " + string.Join("; ", d.Owners.Select(o => $"{o.FullName} ({o.SharePercent.ToString("0.####", CultureInfo.InvariantCulture)}%)")));
        sb.AppendLine("Property: " + string.Join(", ", d.Lines.Select(l => l.PropertyCode)));
        sb.AppendLine($"Total payable: {M(d.CurrencySymbol, d.TotalPayable)}");
        sb.AppendLine($"Amount due at booking: {M(d.CurrencySymbol, d.BookingAmount)}");
        sb.AppendLine($"Balance after booking amount: {M(d.CurrencySymbol, d.TotalPayable - d.BookingAmount)}");
        sb.AppendLine("This receipt confirms the booking only. Money received is acknowledged by a payment receipt.");
        return sb.ToString();
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
