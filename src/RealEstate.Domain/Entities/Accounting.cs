using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>
/// Hierarchical account group (spec 8.2). The five natures are root groups; the main groups of the specification are seeded below them as system groups.
/// The accountant may add further groups; the classification decides where balances appear in the statements.
/// </summary>
public class AccountGroup : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public AccountNature Nature { get; set; }
    public AccountClassification Classification { get; set; }
    public long? ParentGroupId { get; set; }
    /// <summary>Cash flow statement section for movements against accounts of this group (ignored for cash and bank groups).</summary>
    public CashFlowSection CashFlowSection { get; set; } = CashFlowSection.Operating;
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A ledger account. Accounts may have sub-accounts (ParentAccountId); only accounts without sub-accounts take postings. Nature is copied from the group
/// (a group change must keep the nature). Accounts with postings are never deleted; they can be deactivated.
/// </summary>
public class ChartOfAccount : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public long AccountGroupId { get; set; }
    public long? ParentAccountId { get; set; }
    public AccountNature Nature { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>False for control accounts that only automatic postings should touch (manual journals then need accounting.journals.control).</summary>
    public bool AllowManualEntries { get; set; } = true;
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Debit-normal natures: assets and expenses.</summary>
    public static bool IsDebitNormal(AccountNature n) => n is AccountNature.Asset or AccountNature.Expense;
}

public class FiscalYear : AuditableEntity<long>
{
    public string Name { get; set; } = default!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public FiscalYearStatus Status { get; set; } = FiscalYearStatus.Open;
    public string? ClosedBy { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<AccountingPeriod> Periods { get; set; } = [];
}

/// <summary>A monthly accounting period of a fiscal year. Its status decides whether journals dated inside it may be posted.</summary>
public class AccountingPeriod : Entity<long>
{
    public long FiscalYearId { get; set; }
    public int PeriodNo { get; set; }
    public string Name { get; set; } = default!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public AccountingPeriodStatus Status { get; set; } = AccountingPeriodStatus.Open;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only history of period closing, locking and reopening (who, when, why).</summary>
public class AccountingPeriodLock : Entity<long>
{
    public long PeriodId { get; set; }
    public PeriodLockAction Action { get; set; }
    public AccountingPeriodStatus FromStatus { get; set; }
    public AccountingPeriodStatus ToStatus { get; set; }
    public string Reason { get; set; } = default!;
    public Guid ByUserId { get; set; }
    public string By { get; set; } = default!;
    public DateTime AtUtc { get; set; }
}

public class CostCenter : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Null = company-wide cost center.</summary>
    public long? ProjectId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A journal (manual, automatic, reversal, fund transfer or cost allocation). Draft and rejected journals can be edited; posting is final: posted journals are
/// never edited or deleted, only reversed by a new journal.
/// </summary>
public class JournalEntry : AuditableEntity<long>
{
    public string EntryNumber { get; set; } = default!;
    public string? IdempotencyKey { get; set; }
    public JournalSource Source { get; set; }
    public JournalStatus Status { get; set; } = JournalStatus.Draft;
    public DateOnly EntryDate { get; set; }
    public long? FiscalYearId { get; set; }
    public long? PeriodId { get; set; }
    public string Narration { get; set; } = default!;
    /// <summary>Posting reference (business document number, cheque, voucher reference).</summary>
    public string? Reference { get; set; }
    /// <summary>The business record that caused the journal (Payment, Expense, ...), for automatic journals.</summary>
    public string? SourceType { get; set; }
    public long? SourceId { get; set; }
    public long? PostingRequestId { get; set; }
    public long? PostingRuleId { get; set; }
    public long? ReversalOfJournalId { get; set; }
    public long? ReversedByJournalId { get; set; }
    public string? ReversalReason { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string? VoucherNumber { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ApprovalComment { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectionComment { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Set when an authorized user posted into a closed period.</summary>
    public string? ClosedPeriodReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<JournalEntryLine> Lines { get; set; } = [];

    public static bool CanEdit(JournalStatus s) => s is JournalStatus.Draft or JournalStatus.Rejected;
}

public class JournalEntryLine : Entity<long>
{
    public long JournalEntryId { get; set; }
    public int LineNo { get; set; }
    public long AccountId { get; set; }
    public long? ProjectId { get; set; }
    public long? CostCenterId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
}

/// <summary>Immutable general ledger posting, one per posted journal line. All financial reports read from this table.</summary>
public class GeneralLedgerEntry : Entity<long>
{
    public long JournalEntryId { get; set; }
    public long JournalEntryLineId { get; set; }
    public long AccountId { get; set; }
    public long? ProjectId { get; set; }
    public long? CostCenterId { get; set; }
    public DateOnly EntryDate { get; set; }
    public long FiscalYearId { get; set; }
    public long PeriodId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string VoucherNumber { get; set; } = default!;
    public JournalSource Source { get; set; }
    public string Narration { get; set; } = default!;
    public string? Reference { get; set; }
    public string? SourceType { get; set; }
    public long? SourceId { get; set; }
    public DateTime PostedAtUtc { get; set; }
}

/// <summary>The voucher issued when a journal is posted: number per series and fiscal year (unique, never reused).</summary>
public class AccountingVoucher : Entity<long>
{
    public VoucherType VoucherType { get; set; }
    public long FiscalYearId { get; set; }
    public int SequenceNo { get; set; }
    public string VoucherNumber { get; set; } = default!;
    public long JournalEntryId { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public string IssuedBy { get; set; } = default!;
}

/// <summary>
/// Configurable posting rule (spec 8.4, spec-issues #7): which accounts an accounting event debits and credits. Rules take effect only after approval by a
/// second user (maker-checker); active rules cannot be edited, only retired and replaced, so posted journals keep a traceable rule.
/// </summary>
public class PostingRule : AuditableEntity<long>
{
    public string Name { get; set; } = default!;
    public AccountingEventType EventType { get; set; }
    /// <summary>Null = company-wide; a project rule wins over a company-wide one.</summary>
    public long? ProjectId { get; set; }
    /// <summary>Optional condition on a payload field (e.g. Method = Cash, CategoryId = 5, ChargeCategory = DevelopmentCharges).</summary>
    public string? ConditionField { get; set; }
    public string? ConditionValue { get; set; }
    public int Priority { get; set; } = 100;
    public DateOnly EffectiveFrom { get; set; }
    public PostingRuleStatus Status { get; set; } = PostingRuleStatus.Draft;
    public string? Description { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<PostingRuleLine> Lines { get; set; } = [];
}

/// <summary>One side of a posting rule: a fixed account or an account named by the event (AccountKey), and the event amount it takes (AmountKey).</summary>
public class PostingRuleLine : Entity<long>
{
    public long PostingRuleId { get; set; }
    public int LineNo { get; set; }
    public EntrySide Side { get; set; }
    public long? AccountId { get; set; }
    public string? AccountKey { get; set; }
    public string AmountKey { get; set; } = default!;
    public long? CostCenterId { get; set; }
    public string? Description { get; set; }
}

/// <summary>A company bank account, linked one-to-one to a ledger account of a Bank group.</summary>
public class BankAccount : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string BankName { get; set; } = default!;
    public string? Branch { get; set; }
    public string AccountTitle { get; set; } = default!;
    public string AccountNumber { get; set; } = default!;
    public string? Iban { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public long GlAccountId { get; set; }
    public long? ProjectId { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A cash account (office cash, petty cash), linked one-to-one to a ledger account of a Cash group.</summary>
public class CashAccount : AuditableEntity<long>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Custodian { get; set; }
    public long GlAccountId { get; set; }
    public long? ProjectId { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>An imported bank statement line. Deposits increase the bank balance, withdrawals decrease it. Duplicate imports are rejected by LineHash.</summary>
public class BankTransaction : Entity<long>
{
    public long BankAccountId { get; set; }
    public string ImportBatch { get; set; } = default!;
    public DateOnly TransactionDate { get; set; }
    public string Description { get; set; } = default!;
    public string? Reference { get; set; }
    public decimal Deposit { get; set; }
    public decimal Withdrawal { get; set; }
    public decimal? RunningBalance { get; set; }
    public BankTransactionStatus Status { get; set; } = BankTransactionStatus.Unmatched;
    /// <summary>The book entry (general ledger posting on the bank's ledger account) this line was matched to. Unique.</summary>
    public long? MatchedLedgerEntryId { get; set; }
    public string? MatchedBy { get; set; }
    public DateTime? MatchedAtUtc { get; set; }
    public string? IgnoreReason { get; set; }
    public long? ReconciliationId { get; set; }
    public string LineHash { get; set; } = default!;
    public string ImportedBy { get; set; } = default!;
    public DateTime ImportedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A bank reconciliation as at a statement date. Completed reconciliations are history and are never changed.</summary>
public class BankReconciliation : AuditableEntity<long>
{
    public long BankAccountId { get; set; }
    public DateOnly StatementDate { get; set; }
    public decimal StatementBalance { get; set; }
    public decimal BookBalance { get; set; }
    /// <summary>Book entries up to the statement date not yet matched to the statement (deposits in transit minus outstanding payments).</summary>
    public decimal UnclearedBookNet { get; set; }
    /// <summary>Statement lines up to the statement date not matched to the books (bank charges, unrecorded receipts).</summary>
    public decimal UnmatchedStatementNet { get; set; }
    public decimal Difference { get; set; }
    public BankReconciliationStatus Status { get; set; } = BankReconciliationStatus.InProgress;
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Notes { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Allocation of a cost booked on one project (or company level) to other projects by percentage; posted through a CA journal after approval.</summary>
public class ProjectCostAllocation : AuditableEntity<long>
{
    public string AllocationNumber { get; set; } = default!;
    public DateOnly AllocationDate { get; set; }
    public long AccountId { get; set; }
    public long? SourceProjectId { get; set; }
    public long? SourceCostCenterId { get; set; }
    public decimal Amount { get; set; }
    public string Basis { get; set; } = default!;
    public CostAllocationStatus Status { get; set; } = CostAllocationStatus.PendingApproval;
    public long JournalEntryId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<ProjectCostAllocationLine> Lines { get; set; } = [];
}

public class ProjectCostAllocationLine : Entity<long>
{
    public long AllocationId { get; set; }
    public long ProjectId { get; set; }
    public long? CostCenterId { get; set; }
    public decimal Percentage { get; set; }
    public decimal Amount { get; set; }
}
