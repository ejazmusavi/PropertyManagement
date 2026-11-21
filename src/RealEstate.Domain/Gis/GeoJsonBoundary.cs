using System.Text.Json;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Gis;

public sealed record GeoJsonSummary(string GeometryType, int VertexCount, decimal MinX, decimal MinY, decimal MaxX, decimal MaxY);

/// <summary>
/// Validates a boundary geometry given as GeoJSON (a Polygon or MultiPolygon, either bare or wrapped in a Feature) and summarizes it. No mapping or spatial
/// computation is done here: this only guarantees that stored outlines are well-formed (closed rings of at least four positions, numeric coordinates,
/// WGS 84 coordinates inside the valid longitude/latitude range) so the later map viewer and MySQL spatial import can rely on them.
/// </summary>
public static class GeoJsonBoundary
{
    public const int MaxLength = 4_000_000;

    public static GeoJsonSummary Parse(string? json, SpatialCoordinateKind kind)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new DomainException("The boundary geometry (GeoJSON) is required.");
        if (json.Length > MaxLength) throw new DomainException("The GeoJSON is too large (4 MB at most).");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new DomainException("The geometry is not valid JSON."); }
        using (doc)
        {
            var geometry = doc.RootElement;
            if (geometry.ValueKind == JsonValueKind.Object && Type(geometry) == "Feature")
            {
                if (!geometry.TryGetProperty("geometry", out geometry) || geometry.ValueKind != JsonValueKind.Object) throw new DomainException("The GeoJSON feature has no geometry.");
            }
            var type = geometry.ValueKind == JsonValueKind.Object ? Type(geometry) : null;
            if (type is not ("Polygon" or "MultiPolygon")) throw new DomainException("The boundary must be a GeoJSON Polygon or MultiPolygon.");
            if (!geometry.TryGetProperty("coordinates", out var coords) || coords.ValueKind != JsonValueKind.Array) throw new DomainException("The geometry has no coordinates.");

            var acc = new Accumulator(kind);
            if (type == "Polygon") Polygon(coords, acc);
            else
            {
                if (coords.GetArrayLength() == 0) throw new DomainException("A MultiPolygon needs at least one polygon.");
                foreach (var p in coords.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array) throw new DomainException("Invalid MultiPolygon coordinates.");
                    Polygon(p, acc);
                }
            }
            return new GeoJsonSummary(type, acc.Count, acc.MinX, acc.MinY, acc.MaxX, acc.MaxY);
        }
    }

    private static string? Type(JsonElement e) => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static void Polygon(JsonElement rings, Accumulator acc)
    {
        if (rings.GetArrayLength() == 0) throw new DomainException("A polygon needs at least an outer ring.");
        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4) throw new DomainException("Each ring needs at least four positions (first equal to last).");
            (decimal X, decimal Y)? first = null, last = null;
            foreach (var pos in ring.EnumerateArray())
            {
                if (pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() < 2) throw new DomainException("A position needs at least an x and a y coordinate.");
                var items = pos.EnumerateArray().ToList();
                if (items[0].ValueKind != JsonValueKind.Number || items[1].ValueKind != JsonValueKind.Number) throw new DomainException("Coordinates must be numbers.");
                var x = Convert.ToDecimal(items[0].GetDouble()); var y = Convert.ToDecimal(items[1].GetDouble());
                acc.Add(x, y);
                first ??= (x, y);
                last = (x, y);
            }
            if (first != last) throw new DomainException("A ring must be closed: the first and last position must be equal.");
        }
    }

    private sealed class Accumulator(SpatialCoordinateKind kind)
    {
        public int Count { get; private set; }
        public decimal MinX { get; private set; } = decimal.MaxValue;
        public decimal MinY { get; private set; } = decimal.MaxValue;
        public decimal MaxX { get; private set; } = decimal.MinValue;
        public decimal MaxY { get; private set; } = decimal.MinValue;

        public void Add(decimal x, decimal y)
        {
            if (kind == SpatialCoordinateKind.Geographic && (x is < -180m or > 180m || y is < -90m or > 90m))
                throw new DomainException("Geographic coordinates must be longitude -180..180 and latitude -90..90 (GeoJSON order: longitude, latitude).");
            if (Math.Abs(x) > 9_999_999_999m || Math.Abs(y) > 9_999_999_999m) throw new DomainException("A coordinate is out of range.");
            Count++;
            MinX = Math.Min(MinX, x); MinY = Math.Min(MinY, y); MaxX = Math.Max(MaxX, x); MaxY = Math.Max(MaxY, y);
        }
    }
}
