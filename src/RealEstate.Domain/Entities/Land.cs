using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class LandParcel : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string ParcelCode { get; set; } = default!;
    public string? Description { get; set; }
    public LandParcelType ParcelType { get; set; }
    public decimal Area { get; set; }
    public string? TitleReference { get; set; }
    public string? Location { get; set; }
}

/// <summary>Immutable record of land acquired for the project; it increases the project area.</summary>
public class LandAcquisition : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public LandParcel? Parcel { get; set; }
    public DateOnly AcquisitionDate { get; set; }
    public string SellerName { get; set; } = default!;
    public decimal Area { get; set; }
    public decimal? Cost { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Immutable record of an approved area change: date, previous area, adjustment, new area, reason, support and approver.</summary>
public class LandAdjustment : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public DateOnly AdjustmentDate { get; set; }
    public LandAdjustmentType Type { get; set; }
    public decimal PreviousArea { get; set; }
    /// <summary>Signed: positive for additions, negative for reductions.</summary>
    public decimal AdjustmentArea { get; set; }
    public decimal NewArea { get; set; }
    public string Reason { get; set; } = default!;
    public string SupportingReference { get; set; } = default!;
    public Guid ApprovedByUserId { get; set; }
    public string ApprovedByName { get; set; } = default!;
    public DateTime ApprovedAtUtc { get; set; }
}

/// <summary>Current area per category. One row per project and category; Unclassified is the explicit remainder.</summary>
public class LandDistribution : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public LandCategory Category { get; set; }
    public decimal Area { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class LandDistributionHistory : Entity<long>
{
    public long ProjectId { get; set; }
    public LandCategory Category { get; set; }
    public decimal PreviousArea { get; set; }
    public decimal NewArea { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime ChangedAtUtc { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
}

public class LandDocument : StoredDocument
{
    public long ProjectId { get; set; }
    public LandDocumentType DocumentType { get; set; }
    public long? LandParcelId { get; set; }
    public long? LandAcquisitionId { get; set; }
    public long? LandAdjustmentId { get; set; }
    public LandParcel? Parcel { get; set; }
    public LandAcquisition? Acquisition { get; set; }
    public LandAdjustment? Adjustment { get; set; }
}

public static class LandRules
{
    public static readonly IReadOnlyList<LandCategory> ClassifiedCategories =
        [LandCategory.Residential, LandCategory.Commercial, LandCategory.Roads, LandCategory.Parks, LandCategory.OpenSpaces, LandCategory.Reserved, LandCategory.OtherFacilities];

    /// <summary>Validates a classified distribution against the project area and returns the explicit unclassified remainder.</summary>
    public static decimal ComputeUnclassified(decimal totalArea, IEnumerable<decimal> classified)
    {
        var list = classified.ToList();
        if (list.Any(a => a < 0)) throw new DomainException("Distribution areas cannot be negative.");
        if (list.Any(a => decimal.Round(a, 4) != a)) throw new DomainException("Areas support at most 4 decimal places.");
        var sum = list.Sum();
        if (sum > totalArea) throw new DomainException($"The distribution ({sum:0.####}) exceeds the project area ({totalArea:0.####}).");
        return totalArea - sum;
    }
}
