using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class Customer : AuditableEntity<long>
{
    public string CustomerCode { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public IdentityDocumentType IdentityType { get; set; }
    /// <summary>Normalized: CNIC digits only (13), passport upper-case letters and digits.</summary>
    public string IdentityNumber { get; set; } = default!;
    public string Mobile { get; set; } = default!;
    public string? Email { get; set; }
    /// <summary>Primary postal address as one line; structured addresses live in <see cref="CustomerAddress"/>.</summary>
    public string? Address { get; set; }
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<CustomerAddress> Addresses { get; set; } = [];
    public List<CustomerContact> Contacts { get; set; } = [];
    public List<CustomerNominee> Nominees { get; set; } = [];
}

public class CustomerAddress : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public AddressType AddressType { get; set; }
    public string Line1 { get; set; } = default!;
    public string? City { get; set; }
    public string? Country { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>An additional contact person or number for a customer.</summary>
public class CustomerContact : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public string Name { get; set; } = default!;
    public string? Relation { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public class CustomerNominee : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public string FullName { get; set; } = default!;
    public string Relationship { get; set; } = default!;
    public string? IdentityNumber { get; set; }
    public string? Mobile { get; set; }
    /// <summary>Share of the nominee in percent; the nominees of one customer may total at most 100.</summary>
    public decimal SharePercent { get; set; }
}

public class CustomerRelationship : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public long RelatedCustomerId { get; set; }
    public CustomerRelationshipType RelationshipType { get; set; }
    public string? Notes { get; set; }
}

public class CustomerDocument : StoredDocument
{
    public long CustomerId { get; set; }
    public CustomerDocumentType DocumentType { get; set; }
}

/// <summary>
/// A customer's share of a property from a confirmed agreement. Rows are never deleted: a transfer or cancellation closes the row
/// (IsActive false, end date and reason) so the original ownership history is preserved. For one property the active shares total at most 100%.
/// </summary>
public class CustomerOwnership : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public long PropertyId { get; set; }
    public Property? Property { get; set; }
    public long BookingId { get; set; }
    public long AgreementId { get; set; }
    public decimal SharePercent { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>"propertyId:customerId" while active (unique), null once closed: a customer cannot hold two active rows for one property.</summary>
    public string? ActiveKey { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }

    public static string KeyFor(long propertyId, long customerId) => $"{propertyId}:{customerId}";

    public void Close(DateTime nowUtc, string reason)
    {
        if (!IsActive) throw new DomainException("The ownership is already closed.");
        IsActive = false; ActiveKey = null; EndedAtUtc = nowUtc; EndReason = reason;
    }
}

public static class CustomerRules
{
    public static string NormalizeIdentity(IdentityDocumentType type, string? number)
    {
        var raw = (number ?? "").Trim();
        return type == IdentityDocumentType.Cnic
            ? new string(raw.Where(char.IsDigit).ToArray())
            : new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    public static string? IdentityError(IdentityDocumentType type, string normalized) => type switch
    {
        IdentityDocumentType.Cnic when normalized.Length != 13 => "A CNIC must have exactly 13 digits.",
        IdentityDocumentType.Passport when normalized.Length is < 5 or > 20 => "A passport number must have 5 to 20 letters or digits.",
        _ => null,
    };

    /// <summary>Allowed customer status changes: Active ⇄ Inactive, Active/Inactive → Blocked, Blocked → Inactive (re-activation takes two steps).</summary>
    public static bool CanChangeStatus(CustomerStatus from, CustomerStatus to) =>
        from == to || (from, to) is (CustomerStatus.Active, CustomerStatus.Inactive) or (CustomerStatus.Inactive, CustomerStatus.Active)
            or (CustomerStatus.Active, CustomerStatus.Blocked) or (CustomerStatus.Inactive, CustomerStatus.Blocked) or (CustomerStatus.Blocked, CustomerStatus.Inactive);
}
