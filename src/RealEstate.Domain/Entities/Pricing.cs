using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>
/// A configurable pricing rule (base rate, premium or additional charge). Pricing-defining fields are immutable after creation so historic
/// calculations stay reproducible: a rule is never edited, only retired (its effective window is closed) and replaced by a new rule.
/// </summary>
public class PricingRule : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    /// <summary>Rules sharing a code compete: the most specific applicable rule overrides the more general ones (base rates all compete as one group).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public PricingRuleKind Kind { get; set; }
    public PricingFactor Factor { get; set; }
    public PricingScope Scope { get; set; }
    public long? BlockId { get; set; }
    public long? StreetId { get; set; }
    public long? CategoryId { get; set; }
    public long? PropertyId { get; set; }
    /// <summary>Customer-negotiated pricing. No FK until Phase 4 introduces customers.</summary>
    public long? CustomerId { get; set; }
    public string? CustomerReference { get; set; }
    public PricingMethod Method { get; set; }
    /// <summary>Per-unit rate (DECIMAL 18,4), flat amount or percentage depending on <see cref="Method"/>.</summary>
    public decimal Value { get; set; }
    /// <summary>Unit of a per-unit rate and of any size conditions.</summary>
    public MeasurementUnit? Unit { get; set; }
    public int Priority { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public PricingRuleStatus Status { get; set; } = PricingRuleStatus.Active;
    public string? StatusReason { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<PricingRuleCondition> Conditions { get; set; } = [];

    /// <summary>Retired rules stay effective inside their (closed) window so past calculations never change.</summary>
    public bool IsEffectiveOn(DateOnly date) =>
        Status is PricingRuleStatus.Active or PricingRuleStatus.Retired && EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);

    public void Activate(DateTime nowUtc)
    {
        if (Status != PricingRuleStatus.PendingApproval) throw new DomainException("Only a rule awaiting approval can be activated.");
        Status = PricingRuleStatus.Active; StatusChangedAtUtc = nowUtc;
    }

    public void Reject(string reason, DateTime nowUtc)
    {
        if (Status != PricingRuleStatus.PendingApproval) throw new DomainException("Only a rule awaiting approval can be rejected.");
        Status = PricingRuleStatus.Rejected; StatusReason = reason; StatusChangedAtUtc = nowUtc;
    }

    /// <summary>Closes the effective window the day before <paramref name="today"/>.</summary>
    public void Retire(string reason, DateOnly today, DateTime nowUtc)
    {
        if (Status != PricingRuleStatus.Active) throw new DomainException("Only an active rule can be retired.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to retire a rule.");
        var end = today.AddDays(-1);
        if (EffectiveTo is null || EffectiveTo > end) EffectiveTo = end;
        Status = PricingRuleStatus.Retired; StatusReason = reason.Trim(); StatusChangedAtUtc = nowUtc;
    }
}

public class PricingRuleCondition : Entity<long>
{
    public long PricingRuleId { get; set; }
    public PricingConditionField Field { get; set; }
    /// <summary>Invariant-culture text: decimal, id, enum name or true/false depending on the field.</summary>
    public string Value { get; set; } = default!;
}

/// <summary>Automatic discount configuration. Customer-specific discounts are not configured here: they require a PricingApproval.</summary>
public class DiscountRule : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>Project, Block, Street, PropertyCategory or Property (never Customer).</summary>
    public PricingScope Scope { get; set; }
    public long? BlockId { get; set; }
    public long? StreetId { get; set; }
    public long? CategoryId { get; set; }
    public long? PropertyId { get; set; }
    /// <summary>Percentage or FixedAmount.</summary>
    public PricingMethod Method { get; set; }
    public decimal Value { get; set; }
    /// <summary>Stackable discounts all apply; non-stackable ones compete and only the most specific applies.</summary>
    public bool IsStackable { get; set; }
    public int Priority { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public PricingRuleStatus Status { get; set; } = PricingRuleStatus.Active;
    public string? StatusReason { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public bool IsEffectiveOn(DateOnly date) =>
        Status is PricingRuleStatus.Active or PricingRuleStatus.Retired && EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);

    public void Retire(string reason, DateOnly today, DateTime nowUtc)
    {
        if (Status != PricingRuleStatus.Active) throw new DomainException("Only an active discount can be retired.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to retire a discount.");
        var end = today.AddDays(-1);
        if (EffectiveTo is null || EffectiveTo > end) EffectiveTo = end;
        Status = PricingRuleStatus.Retired; StatusReason = reason.Trim(); StatusChangedAtUtc = nowUtc;
    }
}

/// <summary>The currently published price of a property (one row per property) with the immutable calculation snapshot it came from.</summary>
public class PropertyPrice : AuditableEntity<long>
{
    public long PropertyId { get; set; }
    public long ProjectId { get; set; }
    public decimal? BaseRate { get; set; }
    public MeasurementUnit? RateUnit { get; set; }
    public decimal BasePrice { get; set; }
    public decimal SizeBasedPrice { get; set; }
    public decimal TotalPremiums { get; set; }
    public decimal TotalDiscounts { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal FinalPrice { get; set; }
    public bool IsOverridden { get; set; }
    public DateOnly AsOfDate { get; set; }
    public string SnapshotJson { get; set; } = default!;
    public string SnapshotHash { get; set; } = default!;
    public DateTime PublishedAtUtc { get; set; }
    public string? PublishedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only record of every change to a property's published price.</summary>
public class PropertyPriceHistory : Entity<long>
{
    public long PropertyId { get; set; }
    public long ProjectId { get; set; }
    public decimal? PreviousPrice { get; set; }
    public decimal NewPrice { get; set; }
    public PriceChangeSource Source { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime ChangedAtUtc { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
    public string SnapshotHash { get; set; } = default!;
    public string SnapshotJson { get; set; } = default!;
    public long? ApprovalId { get; set; }
    public long? AdjustmentId { get; set; }
}

/// <summary>An approved price override, discount or surcharge on one property (optionally for one customer). Created only by approving a PricingApproval.</summary>
public class PropertyPriceAdjustment : AuditableEntity<long>
{
    public long PropertyId { get; set; }
    public long ProjectId { get; set; }
    public AdjustmentKind Kind { get; set; }
    /// <summary>Override: FixedAmount (the price). Discount/Surcharge: FixedAmount or Percentage.</summary>
    public PricingMethod Method { get; set; }
    public decimal Value { get; set; }
    public long? CustomerId { get; set; }
    public string? CustomerReference { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public AdjustmentStatus Status { get; set; } = AdjustmentStatus.Active;
    public long ApprovalId { get; set; }
    public DateTime ApprovedAtUtc { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public bool IsEffectiveOn(DateOnly date) => EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);

    public void Revoke(string reason, string by, DateOnly today, DateTime nowUtc)
    {
        if (Status != AdjustmentStatus.Active) throw new DomainException("The adjustment is already revoked.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to revoke an adjustment.");
        var end = today.AddDays(-1);
        if (EffectiveTo is null || EffectiveTo > end) EffectiveTo = end;
        Status = AdjustmentStatus.Revoked; RevokedAtUtc = nowUtc; RevokedBy = by; RevokeReason = reason.Trim();
    }
}

/// <summary>Maker-checker request for a price override, customer-specific discount, special (customer-negotiated) pricing or pricing exception.</summary>
public class PricingApproval : AuditableEntity<long>
{
    public PricingApprovalType Type { get; set; }
    public long ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public long? CustomerId { get; set; }
    public string? CustomerReference { get; set; }
    /// <summary>Set when approving a customer-negotiated pricing rule instead of a property adjustment.</summary>
    public long? PricingRuleId { get; set; }
    public AdjustmentKind? AdjustmentKind { get; set; }
    public PricingMethod? Method { get; set; }
    public decimal Value { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    /// <summary>Calculated price when the request was made, and the indicative price if approved.</summary>
    public decimal? OriginalPrice { get; set; }
    public decimal? ProposedPrice { get; set; }
    public string Reason { get; set; } = default!;
    public PricingApprovalStatus Status { get; private set; } = PricingApprovalStatus.Pending;
    public Guid RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; private set; }
    public string? DecidedByName { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionComment { get; private set; }
    public long? AdjustmentId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public void Approve(Guid approverId, string? approverName, string? comment, DateTime nowUtc)
    {
        if (Status != PricingApprovalStatus.Pending) throw new DomainException("The request has already been decided.");
        if (approverId == RequestedByUserId) throw new DomainException("You cannot approve your own request.");
        Status = PricingApprovalStatus.Approved; DecidedByUserId = approverId; DecidedByName = approverName; DecidedAtUtc = nowUtc; DecisionComment = comment?.Trim();
    }

    public void Reject(Guid approverId, string? approverName, string comment, DateTime nowUtc)
    {
        if (Status != PricingApprovalStatus.Pending) throw new DomainException("The request has already been decided.");
        if (approverId == RequestedByUserId) throw new DomainException("You cannot reject your own request; cancel it instead.");
        if (string.IsNullOrWhiteSpace(comment)) throw new DomainException("A comment is required to reject a request.");
        Status = PricingApprovalStatus.Rejected; DecidedByUserId = approverId; DecidedByName = approverName; DecidedAtUtc = nowUtc; DecisionComment = comment.Trim();
    }

    public void Cancel(Guid userId, string? userName, string? comment, DateTime nowUtc)
    {
        if (Status != PricingApprovalStatus.Pending) throw new DomainException("Only a pending request can be cancelled.");
        Status = PricingApprovalStatus.Cancelled; DecidedByUserId = userId; DecidedByName = userName; DecidedAtUtc = nowUtc; DecisionComment = comment?.Trim();
    }
}
