using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Pricing;

/// <summary>Central rounding policy (company-configurable) and area unit conversion. Decimal arithmetic only; never floating point.</summary>
public static class MoneyRounding
{
    public static decimal Round(decimal value, int decimals, RoundingMode mode)
    {
        if (decimals is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(decimals));
        var factor = (decimal)Math.Pow(10, decimals);
        return mode switch
        {
            RoundingMode.Bankers => Math.Round(value, decimals, MidpointRounding.ToEven),
            RoundingMode.Up => Math.Ceiling(value * factor) / factor,
            RoundingMode.Down => Math.Floor(value * factor) / factor,
            _ => Math.Round(value, decimals, MidpointRounding.AwayFromZero),
        };
    }
}

public static class AreaConversion
{
    /// <summary>Square feet per unit. Marla = 272.25 sq ft and Kanal = 20 marla (standard Punjab measures); square metre factor is a 10-digit constant.</summary>
    public static decimal SquareFeetPer(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.SquareFeet => 1m,
        MeasurementUnit.SquareYard => 9m,
        MeasurementUnit.SquareMeter => 10.7639104167m,
        MeasurementUnit.Marla => 272.25m,
        MeasurementUnit.Kanal => 5445m,
        MeasurementUnit.Acre => 43560m,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    public static decimal Convert(decimal size, MeasurementUnit from, MeasurementUnit to) =>
        from == to ? size : size * SquareFeetPer(from) / SquareFeetPer(to);
}
