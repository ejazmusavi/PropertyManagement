using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Expenses;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Accounting;

public sealed record OptionDto(long Id, string Code, string Name);

// ---------- account groups and chart of accounts ----------
public sealed record AccountGroupDto(long Id, string Code, string Name, AccountNature Nature, AccountClassification Classification, long? ParentGroupId, string? ParentName,
    CashFlowSection CashFlowSection, int SortOrder, bool IsSystem, bool IsActive, string? Description, int Depth, int AccountCount, string RowVersion);
public sealed record SaveAccountGroupRequest(string Code, string Name, long? ParentGroupId, AccountClassification Classification, CashFlowSection CashFlowSection, int SortOrder,
    string? Description, bool IsActive = true, string? RowVersion = null);

public class AccountListRequest : PagedRequest
{
    public long? GroupId { get; set; }
    public AccountNature? Nature { get; set; }
    public bool? Active { get; set; }
    public bool PostableOnly { get; set; }
}
public sealed record AccountDto(long Id, string Code, string Name, long AccountGroupId, string GroupName, AccountClassification Classification, AccountNature Nature,
    long? ParentAccountId, string? ParentCode, bool IsPostable, bool IsActive, bool AllowManualEntries, string? Description, int Depth, string RowVersion);
public sealed record SaveAccountRequest(string Code, string Name, long AccountGroupId, long? ParentAccountId, bool AllowManualEntries = true, string? Description = null,
    bool IsActive = true, string? RowVersion = null);
public sealed record AccountDetailDto(AccountDto Account, decimal Debit, decimal Credit, decimal Balance, int PostingCount, string CurrencySymbol);

public sealed record CostCenterDto(long Id, string Code, string Name, long? ProjectId, string? ProjectCode, bool IsActive, string? Description, string RowVersion);
public sealed record SaveCostCenterRequest(string Code, string Name, long? ProjectId, string? Description, bool IsActive = true, string? RowVersion = null);

// ---------- fiscal years and periods ----------
public sealed record AccountingPeriodDto(long Id, long FiscalYearId, int PeriodNo, string Name, DateOnly StartDate, DateOnly EndDate, AccountingPeriodStatus Status, int PostedJournals, string RowVersion);
public sealed record FiscalYearDto(long Id, string Name, DateOnly StartDate, DateOnly EndDate, FiscalYearStatus Status, string? ClosedBy, DateTime? ClosedAtUtc,
    IReadOnlyList<AccountingPeriodDto> Periods, string RowVersion);
public sealed record CreateFiscalYearRequest(string Name, DateOnly StartDate, DateOnly EndDate);
public sealed record PeriodActionRequest(PeriodLockAction Action, string Reason, string? RowVersion = null);
public sealed record CloseFiscalYearRequest(string Reason, string? RowVersion = null);
public sealed record PeriodLockDto(long Id, long PeriodId, string PeriodName, PeriodLockAction Action, AccountingPeriodStatus FromStatus, AccountingPeriodStatus ToStatus, string Reason, string By, DateTime AtUtc);

// ---------- journals ----------
public sealed record JournalLineInput(long AccountId, decimal Debit, decimal Credit, long? ProjectId = null, long? CostCenterId = null, string? Description = null, string? Reference = null);
public sealed record CreateJournalRequest(DateOnly EntryDate, string Narration, string? Reference, IReadOnlyList<JournalLineInput> Lines, bool Submit = false, string? IdempotencyKey = null);
public sealed record UpdateJournalRequest(DateOnly EntryDate, string Narration, string? Reference, IReadOnlyList<JournalLineInput> Lines, string? RowVersion);
public sealed record SubmitJournalRequest(string? RowVersion);
public sealed record ApproveJournalRequest(string? Comments, string? RowVersion, bool PostToClosedPeriod = false, string? ClosedPeriodReason = null);
public sealed record RejectJournalRequest(string Comments, string? RowVersion);
public sealed record CancelJournalRequest(string Reason, string? RowVersion);
public sealed record ReverseJournalRequest(DateOnly? ReversalDate, string Reason, bool PostToClosedPeriod = false, string? ClosedPeriodReason = null);

public class JournalListRequest : PagedRequest
{
    public JournalStatus? Status { get; set; }
    public JournalSource? Source { get; set; }
    public long? ProjectId { get; set; }
    public long? AccountId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}
public sealed record JournalListItemDto(long Id, string EntryNumber, string? VoucherNumber, DateOnly EntryDate, JournalSource Source, JournalStatus Status, string Narration, string? Reference,
    decimal TotalDebit, decimal TotalCredit, string? CreatedBy, string? SubmittedBy, string? SourceType, long? SourceId, bool IsReversed);
public sealed record JournalLineDto(long Id, int LineNo, long AccountId, string AccountCode, string AccountName, long? ProjectId, string? ProjectCode, long? CostCenterId, string? CostCenterCode,
    decimal Debit, decimal Credit, string? Description, string? Reference);
public sealed record JournalDetailDto(long Id, string EntryNumber, string? VoucherNumber, DateOnly EntryDate, JournalSource Source, JournalStatus Status, string Narration, string? Reference,
    string? SourceType, long? SourceId, long? PostingRequestId, long? PostingRuleId, string? PostingRuleName, long? ReversalOfJournalId, string? ReversalOfNumber,
    long? ReversedByJournalId, string? ReversedByNumber, string? ReversalReason, decimal TotalDebit, decimal TotalCredit, string? PeriodName, string? CreatedBy, DateTime CreatedAtUtc,
    string? SubmittedBy, DateTime? SubmittedAtUtc, string? ApprovedBy, DateTime? ApprovedAtUtc, string? ApprovalComment, DateTime? PostedAtUtc, string? RejectedBy, string? RejectionComment,
    string? CancelledBy, string? CancelReason, string? ClosedPeriodReason, IReadOnlyList<JournalLineDto> Lines, string CurrencySymbol, string RowVersion);

public sealed record CreateTransferRequest(long FromAccountId, long ToAccountId, decimal Amount, DateOnly TransferDate, string Narration, string? Reference, long? ProjectId, bool Submit = true,
    string? IdempotencyKey = null);

public sealed record CostAllocationLineInput(long ProjectId, decimal Percentage, long? CostCenterId = null);
public sealed record CreateCostAllocationRequest(DateOnly AllocationDate, long AccountId, long? SourceProjectId, long? SourceCostCenterId, decimal Amount, string Basis,
    IReadOnlyList<CostAllocationLineInput> Lines);
public sealed record CostAllocationLineDto(long ProjectId, string ProjectCode, long? CostCenterId, decimal Percentage, decimal Amount);
public sealed record CostAllocationDto(long Id, string AllocationNumber, DateOnly AllocationDate, long AccountId, string AccountName, long? SourceProjectId, string? SourceProjectCode,
    decimal Amount, string Basis, CostAllocationStatus Status, long JournalEntryId, string JournalNumber, string? CreatedBy, IReadOnlyList<CostAllocationLineDto> Lines);

// ---------- posting rules ----------
public sealed record PostingRuleLineInput(EntrySide Side, string AmountKey, long? AccountId, string? AccountKey, long? CostCenterId = null, string? Description = null);
public sealed record SavePostingRuleRequest(string Name, AccountingEventType EventType, long? ProjectId, string? ConditionField, string? ConditionValue, int Priority, DateOnly EffectiveFrom,
    string? Description, IReadOnlyList<PostingRuleLineInput> Lines, string? RowVersion = null);
public sealed record RuleDecisionRequest(string? Comments, string? RowVersion);
public class PostingRuleListRequest : PagedRequest
{
    public AccountingEventType? EventType { get; set; }
    public PostingRuleStatus? Status { get; set; }
    public long? ProjectId { get; set; }
}
public sealed record PostingRuleLineDto(int LineNo, EntrySide Side, string AmountKey, long? AccountId, string? AccountCode, string? AccountName, string? AccountKey, long? CostCenterId, string? Description);
public sealed record PostingRuleDto(long Id, string Name, AccountingEventType EventType, string EventLabel, long? ProjectId, string? ProjectCode, string? ConditionField, string? ConditionValue,
    int Priority, DateOnly EffectiveFrom, PostingRuleStatus Status, string? Description, string? CreatedBy, string? SubmittedBy, DateTime? SubmittedAtUtc, string? ApprovedBy, DateTime? ApprovedAtUtc,
    string? DecisionComment, string? RetiredBy, int JournalCount, IReadOnlyList<PostingRuleLineDto> Lines, string RowVersion);
public sealed record EventCatalogDto(AccountingEventType EventType, string Label, string Source, IReadOnlyList<string> AmountKeys, IReadOnlyList<string> ConditionFields,
    IReadOnlyList<string> AccountKeys, bool IsReversal, string SpecItems);

// ---------- automatic posting queue ----------
public class PostingQueueRequest : PagedRequest
{
    public AccountingPostingStatus? Status { get; set; }
    public AccountingEventType? EventType { get; set; }
    public long? ProjectId { get; set; }
}
public sealed record PostingRequestDto(long Id, AccountingEventType EventType, string EventLabel, string SourceType, long SourceId, long ProjectId, string ProjectCode, long? AgreementId,
    decimal Amount, DateOnly EventDate, AccountingPostingStatus Status, int Attempts, string? LastError, long? JournalEntryId, string? JournalNumber, string? SkipReason,
    DateTime CreatedAtUtc, DateTime? ProcessedAtUtc, string PayloadJson);
public sealed record PostingRunResultDto(int Fed, int Processed, int Posted, int Failed, int Skipped);
public sealed record SkipPostingRequest(string Reason);
public sealed record PostingQueueSummaryDto(int Pending, int Failed, int Posted, int Skipped);

// ---------- banking ----------
public sealed record BankAccountDto(long Id, string Code, string Name, string BankName, string? Branch, string AccountTitle, string AccountNumber, string? Iban, string CurrencyCode,
    long GlAccountId, string GlAccountCode, string GlAccountName, long? ProjectId, string? ProjectCode, bool IsActive, decimal BookBalance, int UnmatchedCount, string RowVersion);
public sealed record SaveBankAccountRequest(string Code, string Name, string BankName, string? Branch, string AccountTitle, string AccountNumber, string? Iban, string CurrencyCode,
    long GlAccountId, long? ProjectId, bool IsActive = true, string? RowVersion = null);
public sealed record CashAccountDto(long Id, string Code, string Name, string? Custodian, long GlAccountId, string GlAccountCode, string GlAccountName, long? ProjectId, string? ProjectCode,
    bool IsActive, decimal BookBalance, string RowVersion);
public sealed record SaveCashAccountRequest(string Code, string Name, string? Custodian, long GlAccountId, long? ProjectId, bool IsActive = true, string? RowVersion = null);

public sealed record StatementLineInput(DateOnly TransactionDate, string Description, string? Reference, decimal Deposit, decimal Withdrawal, decimal? RunningBalance = null);
public sealed record ImportStatementRequest(IReadOnlyList<StatementLineInput> Lines);
public sealed record ImportStatementResultDto(string Batch, int Imported, int Duplicates, int AutoMatched);
public sealed record BankTransactionDto(long Id, DateOnly TransactionDate, string Description, string? Reference, decimal Deposit, decimal Withdrawal, decimal? RunningBalance,
    BankTransactionStatus Status, long? MatchedLedgerEntryId, string? MatchedVoucher, string? MatchedBy, string? IgnoreReason, long? ReconciliationId, string ImportBatch, string RowVersion);
public sealed record BookEntryDto(long Id, DateOnly EntryDate, string VoucherNumber, string Narration, string? Reference, decimal Debit, decimal Credit, bool Matched);
public sealed record MatchRequest(long BankTransactionId, long LedgerEntryId);
public sealed record IgnoreTransactionRequest(string Reason, string? RowVersion = null);
public sealed record StartReconciliationRequest(DateOnly StatementDate, decimal StatementBalance, string? Notes);
public sealed record BankReconciliationDto(long Id, long BankAccountId, string BankAccountName, DateOnly StatementDate, decimal StatementBalance, decimal BookBalance,
    decimal UnclearedBookNet, decimal UnmatchedStatementNet, decimal Difference, BankReconciliationStatus Status, string? CompletedBy, DateTime? CompletedAtUtc, string? Notes,
    string? CreatedBy, DateTime CreatedAtUtc, string RowVersion);
public sealed record BankReconciliationWorkspaceDto(BankAccountDto Account, BankReconciliationDto? Current, IReadOnlyList<BankTransactionDto> UnmatchedStatement,
    IReadOnlyList<BookEntryDto> UnmatchedBook, IReadOnlyList<BankTransactionDto> RecentlyMatched, IReadOnlyList<BankReconciliationDto> History, string CurrencySymbol);

// ---------- reports ----------
public sealed record ReportFilter(DateOnly? From, DateOnly? To, long? ProjectId, long? CostCenterId);
public sealed record TrialBalanceRowDto(long AccountId, string Code, string Name, string GroupName, AccountNature Nature, decimal OpeningDebit, decimal OpeningCredit,
    decimal PeriodDebit, decimal PeriodCredit, decimal ClosingDebit, decimal ClosingCredit);
public sealed record TrialBalanceDto(DateOnly? From, DateOnly To, long? ProjectId, IReadOnlyList<TrialBalanceRowDto> Rows, decimal TotalOpeningDebit, decimal TotalOpeningCredit,
    decimal TotalPeriodDebit, decimal TotalPeriodCredit, decimal TotalClosingDebit, decimal TotalClosingCredit, bool IsBalanced, string CurrencySymbol);

public sealed record LedgerLineDto(long Id, DateOnly EntryDate, string VoucherNumber, long JournalEntryId, string Narration, string? Reference, string? ProjectCode, string? CostCenterCode,
    decimal Debit, decimal Credit, decimal Balance, JournalSource Source);
public sealed record GeneralLedgerDto(long AccountId, string AccountCode, string AccountName, AccountNature Nature, DateOnly? From, DateOnly To, long? ProjectId, decimal OpeningBalance,
    decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance, PagedResult<LedgerLineDto> Lines, string CurrencySymbol);
public sealed record GeneralLedgerRequest(long AccountId, DateOnly? From, DateOnly? To, long? ProjectId, long? CostCenterId, int Page = 1, int PageSize = 50);

public sealed record StatementLineDto(long? AccountId, string Code, string Name, decimal Amount, int Level, bool IsTotal);
public sealed record StatementSectionDto(string Title, IReadOnlyList<StatementLineDto> Lines, decimal Total);
public sealed record BalanceSheetDto(DateOnly AsOf, long? ProjectId, StatementSectionDto Assets, StatementSectionDto Liabilities, StatementSectionDto Equity,
    decimal RetainedEarningsComputed, decimal CurrentYearProfit, decimal TotalLiabilitiesAndEquity, bool IsBalanced, string CurrencySymbol);
public sealed record IncomeStatementDto(DateOnly From, DateOnly To, long? ProjectId, StatementSectionDto Revenue, StatementSectionDto Expenses, decimal NetProfit, string CurrencySymbol);
public sealed record CashFlowDto(DateOnly From, DateOnly To, long? ProjectId, decimal OpeningCash, decimal Operating, decimal Investing, decimal Financing, decimal NetChange,
    decimal ClosingCash, decimal ClosingCashPerLedger, bool Reconciles, IReadOnlyList<StatementLineDto> Details, string CurrencySymbol);

public sealed record JournalRegisterDto(DateOnly? From, DateOnly? To, PagedResult<JournalDetailDto> Journals, decimal TotalDebit, decimal TotalCredit);

public sealed record ReceivableRowDto(long AgreementId, string AgreementNumber, long ProjectId, string ProjectCode, long CustomerId, string CustomerName, decimal Debit, decimal Credit, decimal Balance);
public sealed record AccountsReceivableDto(DateOnly AsOf, long? ProjectId, IReadOnlyList<ReceivableRowDto> Rows, decimal TotalReceivable, decimal TotalAdvance, decimal NetBalance,
    decimal ControlBalance, decimal Difference, string CurrencySymbol);
public sealed record PayableRowDto(long? VendorId, string VendorName, int OpenExpenses, decimal Approved, decimal Paid, decimal Outstanding, decimal Overdue);
public sealed record AccountsPayableDto(DateOnly AsOf, long? ProjectId, IReadOnlyList<PayableRowDto> Rows, decimal TotalOutstanding, decimal ControlBalance, decimal Difference,
    decimal GovernmentPayableOutstanding, decimal GovernmentControlBalance, string CurrencySymbol);
public sealed record ProjectProfitRowDto(long? ProjectId, string ProjectCode, string ProjectName, decimal Revenue, decimal Expenses, decimal NetProfit, decimal? MarginPercent);
public sealed record ProjectProfitabilityDto(DateOnly From, DateOnly To, IReadOnlyList<ProjectProfitRowDto> Rows, decimal TotalRevenue, decimal TotalExpenses, decimal TotalNet, string CurrencySymbol);
public sealed record ProjectExpenseStatementDto(long ProjectId, string ProjectCode, string ProjectName, DateOnly From, DateOnly To, StatementSectionDto ByAccount,
    IReadOnlyList<StatementLineDto> ByCostCenter, decimal Total, decimal ApprovedExpensesInModule, string CurrencySymbol);
public sealed record ControlReconciliationRowDto(long ProjectId, string ProjectCode, decimal LedgerBalance, decimal ControlBalance, decimal Difference, int UnprocessedEvents, bool Reconciled);
public sealed record ControlReconciliationDto(DateOnly AsOf, IReadOnlyList<ControlReconciliationRowDto> Rows, decimal TotalLedger, decimal TotalControl, decimal TotalDifference,
    bool AllReconciled, string CurrencySymbol);
public sealed record ProjectReconciliationRowDto(long? ProjectId, string ProjectCode, decimal Debit, decimal Credit, decimal CustomerLedger, decimal CustomerControl,
    decimal VendorOutstanding, decimal VendorControl, decimal ModuleExpenses, decimal LedgerExpenses, bool Reconciled);
public sealed record ProjectReconciliationDto(DateOnly AsOf, IReadOnlyList<ProjectReconciliationRowDto> Rows, decimal SumProjectDebit, decimal SumProjectCredit,
    decimal CompanyDebit, decimal CompanyCredit, bool TotalsAgree, bool AllReconciled, int UnprocessedEvents, string CurrencySymbol);

public sealed record AccountingDashboardDto(int DraftJournals, int PendingApprovals, int PendingRules, PostingQueueSummaryDto Queue, int OpenPeriods, string? CurrentPeriod,
    AccountingPeriodStatus? CurrentPeriodStatus, decimal CashBalance, decimal BankBalance, int UnmatchedBankLines, string CurrencySymbol);

// ================================================================================================================ validators
public class SaveAccountGroupRequestValidator : AbstractValidator<SaveAccountGroupRequest>
{
    public SaveAccountGroupRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Code may contain letters, digits, '.', '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.ParentGroupId).NotNull().WithMessage("Choose the parent group (new groups sit below a nature or another group).");
        RuleFor(x => x.Classification).IsInEnum();
        RuleFor(x => x.CashFlowSection).IsInEnum();
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100_000);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class SaveAccountRequestValidator : AbstractValidator<SaveAccountRequest>
{
    public SaveAccountRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Code may contain letters, digits, '.', '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.AccountGroupId).GreaterThan(0).WithMessage("Choose an account group.");
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class SaveCostCenterRequestValidator : AbstractValidator<SaveCostCenterRequest>
{
    public SaveCostCenterRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Code may contain letters, digits, '.', '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class CreateFiscalYearRequestValidator : AbstractValidator<CreateFiscalYearRequest>
{
    public CreateFiscalYearRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(50);
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithMessage("The end date must be after the start date.");
    }
}

public class PeriodActionRequestValidator : AbstractValidator<PeriodActionRequest>
{
    public PeriodActionRequestValidator()
    {
        RuleFor(x => x.Action).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500).WithMessage("Give a reason of at least 5 characters.");
    }
}

public class JournalLineInputValidator : AbstractValidator<JournalLineInput>
{
    public JournalLineInputValidator()
    {
        RuleFor(x => x.AccountId).GreaterThan(0).WithMessage("Choose an account.");
        RuleFor(x => x.Debit).GreaterThanOrEqualTo(0).LessThanOrEqualTo(9_999_999_999_999_999m);
        RuleFor(x => x.Credit).GreaterThanOrEqualTo(0).LessThanOrEqualTo(9_999_999_999_999_999m);
        RuleFor(x => x).Must(x => (x.Debit > 0) != (x.Credit > 0)).WithName("Amount").WithMessage("Enter either a debit or a credit amount on each line.");
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

public class CreateJournalRequestValidator : AbstractValidator<CreateJournalRequest>
{
    public CreateJournalRequestValidator()
    {
        RuleFor(x => x.Narration).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 2 }).WithMessage("A journal needs at least two lines.");
        RuleFor(x => x.Lines).Must(l => l is null || l.Count <= 200).WithMessage("A journal can have at most 200 lines.");
        RuleForEach(x => x.Lines).SetValidator(new JournalLineInputValidator());
        RuleFor(x => x.Lines).Must(l => l is null || l.Sum(x => x.Debit) == l.Sum(x => x.Credit)).WithMessage("Total debit must equal total credit.");
    }
}

public class UpdateJournalRequestValidator : AbstractValidator<UpdateJournalRequest>
{
    public UpdateJournalRequestValidator()
    {
        RuleFor(x => x.Narration).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 2 }).WithMessage("A journal needs at least two lines.");
        RuleFor(x => x.Lines).Must(l => l is null || l.Count <= 200).WithMessage("A journal can have at most 200 lines.");
        RuleForEach(x => x.Lines).SetValidator(new JournalLineInputValidator());
        RuleFor(x => x.Lines).Must(l => l is null || l.Sum(x => x.Debit) == l.Sum(x => x.Credit)).WithMessage("Total debit must equal total credit.");
    }
}

public class RejectJournalRequestValidator : AbstractValidator<RejectJournalRequest>
{
    public RejectJournalRequestValidator() => RuleFor(x => x.Comments).NotEmpty().MinimumLength(5).MaximumLength(500).WithMessage("Give a rejection comment of at least 5 characters.");
}

public class ReverseJournalRequestValidator : AbstractValidator<ReverseJournalRequest>
{
    public ReverseJournalRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500).WithMessage("Give a reversal reason of at least 5 characters.");
}

public class CreateTransferRequestValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId).GreaterThan(0);
        RuleFor(x => x.ToAccountId).GreaterThan(0).NotEqual(x => x.FromAccountId).WithMessage("Choose two different accounts.");
        RuleFor(x => x.Amount).GreaterThan(0).Must(a => decimal.Round(a, 2) == a).WithMessage("Amounts support at most 2 decimal places.");
        RuleFor(x => x.Narration).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

public class CreateCostAllocationRequestValidator : AbstractValidator<CreateCostAllocationRequest>
{
    public CreateCostAllocationRequestValidator()
    {
        RuleFor(x => x.AccountId).GreaterThan(0).WithMessage("Choose the expense account.");
        RuleFor(x => x.Amount).GreaterThan(0).Must(a => decimal.Round(a, 2) == a).WithMessage("Amounts support at most 2 decimal places.");
        RuleFor(x => x.Basis).NotEmpty().MinimumLength(5).MaximumLength(500).WithMessage("Describe the allocation basis (at least 5 characters).");
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= 50 }).WithMessage("Add between 1 and 50 target projects.");
        RuleFor(x => x.Lines).Must(l => l is null || l.Sum(x => x.Percentage) == 100m).WithMessage("Percentages must total 100.");
        RuleFor(x => x.Lines).Must(l => l is null || l.Select(x => x.ProjectId).Distinct().Count() == l.Count).WithMessage("Each target project may appear once.");
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Percentage).GreaterThan(0).LessThanOrEqualTo(100));
    }
}

public class SavePostingRuleRequestValidator : AbstractValidator<SavePostingRuleRequest>
{
    public SavePostingRuleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.EventType).IsInEnum();
        RuleFor(x => x.ConditionField).MaximumLength(40);
        RuleFor(x => x.ConditionValue).MaximumLength(100);
        RuleFor(x => x.ConditionValue).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.ConditionField)).WithMessage("Enter the condition value.");
        RuleFor(x => x.Priority).InclusiveBetween(1, 10_000);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 2 and <= 20 }).WithMessage("A rule needs between 2 and 20 lines.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Side).IsInEnum();
            l.RuleFor(x => x.AmountKey).NotEmpty().MaximumLength(40);
            l.RuleFor(x => x.AccountKey).MaximumLength(40);
            l.RuleFor(x => x.Description).MaximumLength(200);
        });
    }
}

public class SkipPostingRequestValidator : AbstractValidator<SkipPostingRequest>
{
    public SkipPostingRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500).WithMessage("Give a reason of at least 5 characters.");
}

public class SaveBankAccountRequestValidator : AbstractValidator<SaveBankAccountRequest>
{
    public SaveBankAccountRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Code may contain letters, digits, '.', '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.BankName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Branch).MaximumLength(150);
        RuleFor(x => x.AccountTitle).NotEmpty().MaximumLength(150);
        RuleFor(x => x.AccountNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Iban).MaximumLength(50);
        RuleFor(x => x.CurrencyCode).NotEmpty().Length(3);
        RuleFor(x => x.GlAccountId).GreaterThan(0).WithMessage("Choose the ledger account.");
    }
}

public class SaveCashAccountRequestValidator : AbstractValidator<SaveCashAccountRequest>
{
    public SaveCashAccountRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_.-]+$").WithMessage("Code may contain letters, digits, '.', '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Custodian).MaximumLength(150);
        RuleFor(x => x.GlAccountId).GreaterThan(0).WithMessage("Choose the ledger account.");
    }
}

public class ImportStatementRequestValidator : AbstractValidator<ImportStatementRequest>
{
    public ImportStatementRequestValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= 5000 }).WithMessage("A statement import needs between 1 and 5000 lines.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Description).NotEmpty().MaximumLength(500);
            l.RuleFor(x => x.Reference).MaximumLength(100);
            l.RuleFor(x => x.Deposit).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.Withdrawal).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x).Must(x => (x.Deposit > 0) != (x.Withdrawal > 0)).WithName("Amount").WithMessage("Each statement line needs either a deposit or a withdrawal.");
            l.RuleFor(x => x).Must(x => decimal.Round(x.Deposit, 2) == x.Deposit && decimal.Round(x.Withdrawal, 2) == x.Withdrawal).WithName("Amount").WithMessage("Amounts support at most 2 decimal places.");
        });
    }
}

public class StartReconciliationRequestValidator : AbstractValidator<StartReconciliationRequest>
{
    public StartReconciliationRequestValidator()
    {
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.StatementBalance).Must(a => decimal.Round(a, 2) == a).WithMessage("Amounts support at most 2 decimal places.");
    }
}

/// <summary>Shared helper type re-exported so Web/API can list projects for accounting filters.</summary>
public sealed record AccountingLookupsDto(IReadOnlyList<ProjectOptionDto> Projects, IReadOnlyList<OptionDto> Accounts, IReadOnlyList<OptionDto> CostCenters);
