using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;

namespace RealEstate.Domain.Sales;

/// <summary>Pure booking rules: ownership structure, totals and the booking state machine.</summary>
public static class BookingRules
{
    public const decimal FullOwnership = 100m;

    /// <summary>Joint ownership: every share is positive with at most 4 decimals and the shares total exactly 100%.</summary>
    public static void ValidateOwnership(IReadOnlyCollection<decimal> shares)
    {
        if (shares.Count == 0) throw new DomainException("At least one owner is required.");
        foreach (var s in shares)
        {
            if (s <= 0) throw new DomainException("Each ownership share must be greater than zero.");
            if (s > FullOwnership) throw new DomainException("An ownership share cannot exceed 100%.");
            if (decimal.Round(s, 4) != s) throw new DomainException("Ownership shares support at most 4 decimal places.");
        }
        var total = shares.Sum();
        if (total != FullOwnership) throw new DomainException($"The ownership shares total {total:0.####}%; they must total exactly 100%.");
    }

    /// <summary>Allocation across all active owners of one property may never exceed 100%.</summary>
    public static void EnsureAllocationWithinLimit(decimal existingActiveTotal, decimal newShares)
    {
        if (existingActiveTotal + newShares > FullOwnership)
            throw new DomainException($"Ownership would total {existingActiveTotal + newShares:0.####}% for the property; the total allocation cannot exceed 100%.");
    }

    public static bool CanTransition(BookingStatus from, BookingStatus to) => (from, to) switch
    {
        (BookingStatus.Draft, BookingStatus.PendingApproval) => true,
        (BookingStatus.Draft, BookingStatus.Cancelled) => true,
        (BookingStatus.PendingApproval, BookingStatus.Confirmed) => true,
        (BookingStatus.PendingApproval, BookingStatus.Rejected) => true,
        (BookingStatus.PendingApproval, BookingStatus.Cancelled) => true,
        (BookingStatus.Confirmed, BookingStatus.Cancelled) => true,
        _ => false,
    };

    /// <summary>A booking that still holds its properties. Rejected and cancelled bookings have released them.</summary>
    public static bool HoldsProperties(BookingStatus s) => s is BookingStatus.Draft or BookingStatus.PendingApproval or BookingStatus.Confirmed;

    public sealed record LineTotals(decimal FinalPrice, decimal ExtraDiscount, decimal LinePrice);
    public sealed record Totals(decimal TotalPrice, decimal TotalExtraDiscount, decimal TotalCharges, decimal TotalPayable);

    /// <summary>Line price = snapshot final price minus the additional booking discount (never below zero, never more than the price).</summary>
    public static LineTotals Line(decimal finalPrice, decimal extraDiscount, int decimals, RoundingMode mode)
    {
        if (extraDiscount < 0) throw new DomainException("A discount cannot be negative.");
        var d = MoneyRounding.Round(extraDiscount, decimals, mode);
        if (d > finalPrice) throw new DomainException("The discount cannot exceed the property price.");
        return new LineTotals(finalPrice, d, finalPrice - d);
    }

    public static Totals Sum(IEnumerable<LineTotals> lines, IEnumerable<decimal> charges)
    {
        var l = lines.ToList();
        var price = l.Sum(x => x.FinalPrice); var disc = l.Sum(x => x.ExtraDiscount); var ch = charges.Sum();
        return new Totals(price, disc, ch, price - disc + ch);
    }

    /// <summary>Cash sale: the amount due at booking may be below the total only when the balance was authorized.</summary>
    public static void ValidateBookingAmount(PaymentMode mode, decimal bookingAmount, decimal totalPayable, bool balanceAuthorized)
    {
        if (bookingAmount < 0) throw new DomainException("The booking amount cannot be negative.");
        if (bookingAmount > totalPayable) throw new DomainException("The booking amount cannot exceed the total payable.");
        if (mode == PaymentMode.Cash && bookingAmount < totalPayable && !balanceAuthorized)
            throw new DomainException("A cash sale must be paid in full at booking unless an authorized user approves an outstanding balance.");
        if (mode == PaymentMode.Installment && bookingAmount == totalPayable) { /* paid up front is allowed */ }
    }
}
