namespace RealEstate.Domain.Common;

/// <summary>A file held in secure storage (outside the web root). Documents are soft-deleted only, with a reason.</summary>
public abstract class StoredDocument : AuditableEntity<long>
{
    public string Title { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string StorageKey { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = default!;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public string? DeletedBy { get; private set; }
    public string? DeleteReason { get; private set; }

    public void MarkDeleted(DateTime nowUtc, string by, string reason)
    {
        if (IsDeleted) throw new DomainException("The document has already been removed.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to remove a document.");
        IsDeleted = true;
        DeletedAtUtc = nowUtc;
        DeletedBy = by;
        DeleteReason = reason.Trim();
    }
}
