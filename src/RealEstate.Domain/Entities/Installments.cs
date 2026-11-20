using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>Catalog of charge components (spec 5.3). The eleven standard types are system rows; custom types can be added.</summary>
public class ChargeType : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public ChargeComponentKind Kind { get; set; }
    public ChargeCategory DefaultCategory { get; set; }
    public ScheduleFrequency DefaultFrequency { get; set; }
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Late fee configuration. Calculation terms cannot change once the rule is used by a plan or an agreement (penalty changes go through amendments).</summary>
public class LateFeeRule : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Null = available to every project.</summary>
    public long? ProjectId { get; set; }
    public LateFeeMethod Method { get; set; }
    public decimal Value { get; set; }
    public decimal? MaxAmount { get; set; }
    public decimal? MaxPercentOfInstallment { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>An installment plan template (spec 5.2). Editable while Draft; Active plans are applied to agreements; Retired plans stay for history.</summary>
public class InstallmentPlan : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public InstallmentPlanType PlanType { get; set; }
    public long? PropertyCategoryId { get; set; }
    public long? PropertyId { get; set; }
    public long? CustomerId { get; set; }
    public InstallmentPlanStatus Status { get; set; } = InstallmentPlanStatus.Draft;
    public Guid? ActivatedByUserId { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public string? RetireReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<InstallmentPlanComponent> Components { get; set; } = [];
}

/// <summary>One configured charge of a plan template (spec 5.4).</summary>
public class InstallmentPlanComponent : AuditableEntity<long>
{
    public long PlanId { get; set; }
    public int SortOrder { get; set; }
    public long ChargeTypeId { get; set; }
    public ChargeType? ChargeType { get; set; }
    public string Name { get; set; } = default!;
    public ChargeCategory Category { get; set; }
    public AmountBasis AmountBasis { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Percent { get; set; }
    public ScheduleFrequency Frequency { get; set; }
    public int? NumberOfInstallments { get; set; }
    public decimal? InstallmentAmount { get; set; }
    public int FirstDueOffsetMonths { get; set; }
    public int FirstDueOffsetDays { get; set; }
    public int? DueDayOfMonth { get; set; }
    /// <summary>JSON list of {OffsetMonths, SharePercent?} for custom schedules.</summary>
    public string? CustomScheduleJson { get; set; }
    public int GracePeriodDays { get; set; }
    public long? LateFeeRuleId { get; set; }
    public bool WaiverAllowed { get; set; }
    public decimal MaxWaiverPercent { get; set; }
    public TaxTreatment TaxTreatment { get; set; } = TaxTreatment.NotApplicable;
    public decimal TaxRatePercent { get; set; }
    public decimal? RoundingUnit { get; set; }
    public string? Remarks { get; set; }
}

/// <summary>
/// A charge of a customer agreement with absolute amounts and dates. Freely editable until the first schedule is generated;
/// afterwards it changes only through approved amendments (removed charges are Cancelled, never deleted).
/// </summary>
public class AgreementCharge : AuditableEntity<long>
{
    public long AgreementId { get; set; }
    public int SortOrder { get; set; }
    public long ChargeTypeId { get; set; }
    public ChargeType? ChargeType { get; set; }
    public string Description { get; set; } = default!;
    public ChargeCategory Category { get; set; }
    public decimal Amount { get; set; }
    public ScheduleFrequency Frequency { get; set; }
    public int? NumberOfInstallments { get; set; }
    public decimal? InstallmentAmount { get; set; }
    public DateOnly FirstDueDate { get; set; }
    public int? DueDayOfMonth { get; set; }
    /// <summary>JSON list of {DueDate, Amount?} for custom/irregular schedules.</summary>
    public string? CustomScheduleJson { get; set; }
    public int GracePeriodDays { get; set; }
    public long? LateFeeRuleId { get; set; }
    public bool WaiverAllowed { get; set; }
    public decimal MaxWaiverPercent { get; set; }
    public TaxTreatment TaxTreatment { get; set; } = TaxTreatment.NotApplicable;
    public decimal TaxRatePercent { get; set; }
    public decimal? RoundingUnit { get; set; }
    public string? Remarks { get; set; }
    public AgreementChargeStatus Status { get; set; } = AgreementChargeStatus.Active;
    public long? SourcePlanComponentId { get; set; }
    public long? AddedByAmendmentId { get; set; }
}

/// <summary>A version of the payment schedule. Exactly one version is Active per agreement; older versions are kept (Superseded) and never changed.</summary>
public class PaymentSchedule : AuditableEntity<long>
{
    public long AgreementId { get; set; }
    public int VersionNo { get; set; }
    public PaymentScheduleStatus Status { get; set; } = PaymentScheduleStatus.Active;
    /// <summary>Equals AgreementId while Active, null otherwise. Unique: one active schedule per agreement.</summary>
    public long? ActiveAgreementId { get; set; }
    public string Reason { get; set; } = default!;
    public long? SourceAmendmentId { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalTax { get; set; }
    public int ItemCount { get; set; }
    public string ContentHash { get; set; } = default!;
    public DateTime GeneratedAtUtc { get; set; }
    public string GeneratedBy { get; set; } = default!;
    public DateTime? SupersededAtUtc { get; set; }
    public List<PaymentScheduleItem> Items { get; set; } = [];

    public void Supersede(DateTime nowUtc, PaymentScheduleStatus to = PaymentScheduleStatus.Superseded)
    {
        if (Status != PaymentScheduleStatus.Active) throw new DomainException("Only the active schedule can be superseded.");
        Status = to; ActiveAgreementId = null; SupersededAtUtc = nowUtc;
    }
}

/// <summary>
/// One due installment. Payment progress (paid, waived) is carried into each new schedule version. Recording payments is Phase 6;
/// the settlement methods below define how partial and advance payments are represented.
/// </summary>
public class PaymentScheduleItem : Entity<long>
{
    public long ScheduleId { get; set; }
    public long AgreementChargeId { get; set; }
    public int LineNo { get; set; }
    public int InstallmentNo { get; set; }
    public int InstallmentCount { get; set; }
    public string Description { get; set; } = default!;
    public DateOnly DueDate { get; set; }
    public DateOnly GraceEndDate { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public bool IsPartial { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountWaived { get; set; }
    public decimal LateFeeWaived { get; set; }
    public DateOnly? LastPaymentDate { get; set; }
    public ScheduleItemStatus Status { get; set; } = ScheduleItemStatus.Pending;
    /// <summary>The item of the previous schedule version this line continues (null for new lines).</summary>
    public long? PreviousItemId { get; set; }

    public decimal Outstanding => Amount - AmountPaid - AmountWaived;
    public bool IsSettled => Outstanding == 0m;
    public bool HasSettlement => AmountPaid > 0m || AmountWaived > 0m;
    /// <summary>Some money was received before the due date (advance payment).</summary>
    public bool IsPaidInAdvance => AmountPaid > 0m && LastPaymentDate is { } d && d < DueDate;

    public void RecordPayment(decimal amount, DateOnly paidOn)
    {
        if (amount <= 0m) throw new DomainException("A payment amount must be greater than zero.");
        if (amount > Outstanding) throw new DomainException("The payment exceeds the outstanding amount of the installment.");
        AmountPaid += amount;
        LastPaymentDate = LastPaymentDate is { } d && d > paidOn ? d : paidOn;
        RefreshStatus();
    }

    public void ApplyWaiver(decimal amount)
    {
        if (amount <= 0m) throw new DomainException("A waiver amount must be greater than zero.");
        if (amount > Outstanding) throw new DomainException("The waiver exceeds the outstanding amount of the installment.");
        AmountWaived += amount;
        RefreshStatus();
    }

    public void RefreshStatus() => Status = Outstanding == 0m
        ? (AmountPaid == 0m ? ScheduleItemStatus.Waived : ScheduleItemStatus.Paid)
        : HasSettlement ? ScheduleItemStatus.PartiallyPaid : ScheduleItemStatus.Pending;
}

/// <summary>One schedule line changed by an approved amendment (what changed, old and new values). Together with the superseded versions this is the schedule history.</summary>
public class ScheduleAmendment : Entity<long>
{
    public long AmendmentId { get; set; }
    public long FromScheduleId { get; set; }
    public long ToScheduleId { get; set; }
    public ScheduleChangeOperation Operation { get; set; }
    public long? AgreementChargeId { get; set; }
    public long? FromItemId { get; set; }
    public long? ToItemId { get; set; }
    public DateOnly? OldDueDate { get; set; }
    public DateOnly? NewDueDate { get; set; }
    public decimal? OldAmount { get; set; }
    public decimal? NewAmount { get; set; }
    public string? Note { get; set; }
}

/// <summary>A waiver of installment principal or late fee. Requested with a Waiver amendment and approved with it (maker-checker).</summary>
public class Waiver : AuditableEntity<long>
{
    public long AgreementId { get; set; }
    public long AmendmentId { get; set; }
    public long ScheduleItemId { get; set; }
    public long AgreementChargeId { get; set; }
    public WaiverKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = default!;
    public WaiverStatus Status { get; set; } = WaiverStatus.Pending;
    public string RequestedBy { get; set; } = default!;
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComments { get; set; }
}
