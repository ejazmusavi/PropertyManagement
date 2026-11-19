using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>Top-level grouping of property types (Residential, Commercial, or any custom category).</summary>
public class PropertyCategory : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A kind of property (Residential Plot, House, Apartment, Shop...). Form drives which physical fields apply.</summary>
public class PropertyType : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public long CategoryId { get; set; }
    public PropertyCategory? Category { get; set; }
    public PropertyForm Form { get; set; }
    public bool RequiresFloor { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Block, sector and street share one shape: a named, coded subdivision of a project.</summary>
public abstract class ProjectZone : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

public class ProjectBlock : ProjectZone { }
public class ProjectSector : ProjectZone { }
public class ProjectStreet : ProjectZone { }

public class Property : AuditableEntity<long>
{
    public string PropertyCode { get; private set; } = default!;
    public long ProjectId { get; private set; }
    public long? BlockId { get; set; }
    public long? SectorId { get; set; }
    public long? StreetId { get; set; }
    public string PropertyNumber { get; set; } = default!;
    public long PropertyTypeId { get; set; }
    public PropertyType? PropertyType { get; set; }
    public decimal Size { get; set; }
    public MeasurementUnit MeasurementUnit { get; set; }
    public string? LocationDescription { get; set; }
    public int? FloorNumber { get; set; }
    public bool IsCorner { get; set; }
    public bool FacesMainRoad { get; set; }
    public bool FacesPark { get; set; }
    public DevelopmentStatus DevelopmentStatus { get; set; }
    public PropertyStatus Status { get; private set; } = PropertyStatus.Available;
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<PropertyDimension> Dimensions { get; set; } = [];
    public List<PropertyFeature> Features { get; set; } = [];

    private Property() { }

    public static Property Create(long projectId, string code, string number, long typeId, decimal size, MeasurementUnit unit)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("Property code is required.");
        if (string.IsNullOrWhiteSpace(number)) throw new DomainException("Property number is required.");
        ValidateSize(size);
        return new Property
        {
            ProjectId = projectId, PropertyCode = code.Trim().ToUpperInvariant(), PropertyNumber = number.Trim(),
            PropertyTypeId = typeId, Size = size, MeasurementUnit = unit, Status = PropertyStatus.Available,
        };
    }

    public static void ValidateSize(decimal size)
    {
        if (size <= 0) throw new DomainException("Size must be greater than zero.");
        if (size > 999_999_999m) throw new DomainException("Size is too large.");
        if (decimal.Round(size, 4) != size) throw new DomainException("Size supports at most 4 decimal places.");
    }

    /// <summary>Controlled inventory state machine. Reserved is entered only through a reservation; Reserved leaves via release/expiry/booking/cancellation.</summary>
    public static bool CanTransition(PropertyStatus from, PropertyStatus to) => (from, to) switch
    {
        (PropertyStatus.Available, PropertyStatus.Reserved) => true,
        (PropertyStatus.Available, PropertyStatus.Booked) => true,
        (PropertyStatus.Available, PropertyStatus.Cancelled) => true,
        (PropertyStatus.Reserved, PropertyStatus.Available) => true,
        (PropertyStatus.Reserved, PropertyStatus.Booked) => true,
        (PropertyStatus.Reserved, PropertyStatus.Cancelled) => true,
        (PropertyStatus.Booked, PropertyStatus.Sold) => true,
        (PropertyStatus.Booked, PropertyStatus.Cancelled) => true,
        (PropertyStatus.Sold, PropertyStatus.Allocated) => true,
        (PropertyStatus.Sold, PropertyStatus.PossessionReady) => true,
        (PropertyStatus.Sold, PropertyStatus.Transferred) => true,
        (PropertyStatus.Sold, PropertyStatus.Cancelled) => true,
        (PropertyStatus.Allocated, PropertyStatus.PossessionReady) => true,
        (PropertyStatus.Allocated, PropertyStatus.Transferred) => true,
        (PropertyStatus.Allocated, PropertyStatus.Cancelled) => true,
        (PropertyStatus.PossessionReady, PropertyStatus.PossessionDelivered) => true,
        (PropertyStatus.PossessionReady, PropertyStatus.Cancelled) => true,
        (PropertyStatus.PossessionDelivered, PropertyStatus.Transferred) => true,
        (PropertyStatus.Cancelled, PropertyStatus.Available) => true,
        _ => false,
    };

    /// <summary>True while the property can still be edited (not sold or beyond).</summary>
    public bool IsStructureEditable => Status is PropertyStatus.Available or PropertyStatus.Reserved or PropertyStatus.Cancelled;

    public PropertyStatusHistory ChangeStatus(PropertyStatus to, string reason, DateTime nowUtc, Guid? userId, string? userName, long? reservationId = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to change the property status.");
        if (!CanTransition(Status, to)) throw new DomainException($"A property cannot move from {Status} to {to}.");
        var h = new PropertyStatusHistory
        {
            PropertyId = Id, Property = this, FromStatus = Status, ToStatus = to, Reason = reason.Trim(),
            ChangedAtUtc = nowUtc, ChangedByUserId = userId, ChangedBy = userName, ReservationId = reservationId,
        };
        Status = to;
        return h;
    }

    public static PropertyStatusHistory InitialHistory(Property p, DateTime nowUtc, Guid? userId, string? userName) => new()
    {
        Property = p, FromStatus = null, ToStatus = PropertyStatus.Available, Reason = "Property created",
        ChangedAtUtc = nowUtc, ChangedByUserId = userId, ChangedBy = userName,
    };
}

/// <summary>Flexible measurement (Length, Width, Covered area, Bedrooms...). Plots, houses and apartments carry different sets.</summary>
public class PropertyDimension : AuditableEntity<long>
{
    public long PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public decimal Value { get; set; }
    public string? Unit { get; set; }
}

public class PropertyFeature : AuditableEntity<long>
{
    public long PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public string? Value { get; set; }
}

public class PropertyDocument : StoredDocument
{
    public long PropertyId { get; set; }
    public PropertyDocumentType DocumentType { get; set; }
}

public class PropertyStatusHistory : Entity<long>
{
    public long PropertyId { get; set; }
    public Property? Property { get; set; }
    public PropertyStatus? FromStatus { get; set; }
    public PropertyStatus ToStatus { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime ChangedAtUtc { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
    public long? ReservationId { get; set; }
}

/// <summary>
/// A time-boxed hold on a property. <see cref="ActivePropertyId"/> equals PropertyId only while the reservation is Active and is
/// covered by a unique index, so two active reservations (double booking) can never coexist, even under concurrent requests.
/// Customer is held as an optional id (no FK until Phase 4) plus the name captured at reservation time.
/// </summary>
public class PropertyReservation : AuditableEntity<long>
{
    public long PropertyId { get; set; }
    public Property? Property { get; set; }
    public DateOnly ReservationDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public decimal Amount { get; set; }
    public long? CustomerId { get; set; }
    public string ReservedForName { get; set; } = default!;
    public string? ReservedForContact { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public string? ApprovedByName { get; set; }
    public ReservationStatus Status { get; private set; } = ReservationStatus.Active;
    public long? ActivePropertyId { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public string? ClosedBy { get; private set; }
    public string? CloseReason { get; private set; }

    public static PropertyReservation Create(long propertyId, DateOnly date, DateOnly expiry, decimal amount, string forName, string? contact, Guid approvedBy, string? approvedByName)
    {
        if (expiry < date) throw new DomainException("The expiry date cannot be before the reservation date.");
        if (amount < 0 || decimal.Round(amount, 2) != amount) throw new DomainException("The reservation amount must be zero or more with at most 2 decimals.");
        if (string.IsNullOrWhiteSpace(forName)) throw new DomainException("The customer name is required.");
        return new PropertyReservation
        {
            PropertyId = propertyId, ReservationDate = date, ExpiryDate = expiry, Amount = amount, ReservedForName = forName.Trim(),
            ReservedForContact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(), ApprovedByUserId = approvedBy, ApprovedByName = approvedByName,
            Status = ReservationStatus.Active, ActivePropertyId = propertyId,
        };
    }

    public void Close(ReservationStatus status, DateTime nowUtc, string by, string reason)
    {
        if (Status != ReservationStatus.Active) throw new DomainException("The reservation is no longer active.");
        if (status == ReservationStatus.Active) throw new DomainException("Invalid closing status.");
        Status = status; ActivePropertyId = null; ClosedAtUtc = nowUtc; ClosedBy = by; CloseReason = reason;
    }

    public bool IsExpiredOn(DateOnly today) => Status == ReservationStatus.Active && ExpiryDate < today;
}
