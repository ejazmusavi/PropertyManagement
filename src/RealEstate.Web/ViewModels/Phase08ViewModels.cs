using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Accounting;
using RealEstate.Application.Common;
using RealEstate.Application.Expenses;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class AccountingDashboardViewModel
{
    public AccountingDashboardDto Dashboard { get; set; } = default!;
}

// ---------- groups and accounts ----------
public class AccountGroupFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Parent group")] public long? ParentGroupId { get; set; }
    public AccountClassification Classification { get; set; }
    [Display(Name = "Cash flow section")] public CashFlowSection CashFlowSection { get; set; } = CashFlowSection.Operating;
    [Display(Name = "Sort order"), Range(0, 100_000)] public int SortOrder { get; set; } = 500;
    [StringLength(500)] public string? Description { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public string? RowVersion { get; set; }
}

public class AccountGroupsViewModel
{
    public IReadOnlyList<AccountGroupDto> Groups { get; set; } = [];
    public AccountGroupFormViewModel Form { get; set; } = new();
}

public class AccountsViewModel
{
    public IReadOnlyList<AccountDto> Accounts { get; set; } = [];
    public IReadOnlyList<AccountGroupDto> Groups { get; set; } = [];
    public string? Search { get; set; }
    public long? GroupId { get; set; }
    public AccountNature? Nature { get; set; }
    public bool? Active { get; set; }
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
}

public class AccountFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Account group"), Range(1, long.MaxValue, ErrorMessage = "Choose an account group.")] public long AccountGroupId { get; set; }
    [Display(Name = "Parent account")] public long? ParentAccountId { get; set; }
    [Display(Name = "Allow manual journals")] public bool AllowManualEntries { get; set; } = true;
    [StringLength(500)] public string? Description { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
    public IReadOnlyList<AccountGroupDto> Groups { get; set; } = [];
    public IReadOnlyList<AccountDto> Parents { get; set; } = [];
}

public class CostCenterFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

public class CostCentersViewModel
{
    public IReadOnlyList<CostCenterDto> Items { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public CostCenterFormViewModel Form { get; set; } = new();
}

// ---------- journals ----------
public class JournalLineForm
{
    public long? AccountId { get; set; }
    public long? ProjectId { get; set; }
    public long? CostCenterId { get; set; }
    [Range(0, 9_999_999_999_999_999)] public decimal Debit { get; set; }
    [Range(0, 9_999_999_999_999_999)] public decimal Credit { get; set; }
    [StringLength(500)] public string? Description { get; set; }
}

public class JournalLookups
{
    public IReadOnlyList<OptionDto> Accounts { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<OptionDto> CostCenters { get; set; } = [];
    public bool CompanyLevelAllowed { get; set; }
}

public class JournalFormViewModel
{
    public long? Id { get; set; }
    [Display(Name = "Entry date")] public DateOnly EntryDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Required, StringLength(500)] public string Narration { get; set; } = "";
    [StringLength(100)] public string? Reference { get; set; }
    public List<JournalLineForm> Lines { get; set; } = [new(), new()];
    public string? RowVersion { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public bool Submit { get; set; }
    public JournalLookups Lookups { get; set; } = new();
}

public class JournalListViewModel
{
    public JournalListRequest Filter { get; set; } = new();
    public PagedResult<JournalListItemDto> Journals { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public string Title { get; set; } = "Journal entries";
    public bool ApprovalQueue { get; set; }
    public string? CurrentUser { get; set; }
}

public class JournalDetailsViewModel
{
    public JournalDetailDto Journal { get; set; } = default!;
    public bool IsMaker { get; set; }
    public DateOnly Today { get; set; }
}

public class TransferFormViewModel
{
    [Display(Name = "From account"), Range(1, long.MaxValue, ErrorMessage = "Choose the source account.")] public long FromAccountId { get; set; }
    [Display(Name = "To account"), Range(1, long.MaxValue, ErrorMessage = "Choose the destination account.")] public long ToAccountId { get; set; }
    [Range(0.01, 9_999_999_999_999_999)] public decimal Amount { get; set; }
    [Display(Name = "Transfer date")] public DateOnly TransferDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Required, StringLength(500)] public string Narration { get; set; } = "";
    [StringLength(100)] public string? Reference { get; set; }
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<OptionDto> MoneyAccounts { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public bool CompanyLevelAllowed { get; set; }
}

public class CostAllocationLineForm
{
    public long? ProjectId { get; set; }
    [Range(0, 100)] public decimal Percentage { get; set; }
}

public class CostAllocationFormViewModel
{
    [Display(Name = "Allocation date")] public DateOnly AllocationDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [Display(Name = "Expense account"), Range(1, long.MaxValue, ErrorMessage = "Choose the expense account.")] public long AccountId { get; set; }
    [Display(Name = "Source project")] public long? SourceProjectId { get; set; }
    [Range(0.01, 9_999_999_999_999_999)] public decimal Amount { get; set; }
    [Required, StringLength(500, MinimumLength = 5)] public string Basis { get; set; } = "";
    public List<CostAllocationLineForm> Lines { get; set; } = [new(), new()];
    public IReadOnlyList<OptionDto> ExpenseAccounts { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public bool CompanyLevelAllowed { get; set; }
}

public class AllocationsViewModel
{
    public IReadOnlyList<CostAllocationDto> Items { get; set; } = [];
}

// ---------- posting rules and queue ----------
public class RuleLineForm
{
    public EntrySide Side { get; set; } = EntrySide.Debit;
    public string? AmountKey { get; set; } = "Amount";
    public long? AccountId { get; set; }
    public string? AccountKey { get; set; }
    public long? CostCenterId { get; set; }
    [StringLength(200)] public string? Description { get; set; }
}

public class PostingRuleFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Accounting event")] public AccountingEventType EventType { get; set; } = AccountingEventType.PaymentPosted;
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [Display(Name = "Condition field")] public string? ConditionField { get; set; }
    [Display(Name = "Condition value"), StringLength(100)] public string? ConditionValue { get; set; }
    [Range(1, 10_000)] public int Priority { get; set; } = 100;
    [Display(Name = "Effective from")] public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [StringLength(1000)] public string? Description { get; set; }
    public List<RuleLineForm> Lines { get; set; } = [new() { Side = EntrySide.Debit }, new() { Side = EntrySide.Credit }];
    public string? RowVersion { get; set; }
    public IReadOnlyList<EventCatalogDto> Catalog { get; set; } = [];
    public JournalLookups Lookups { get; set; } = new();
}

public class PostingRulesViewModel
{
    public PostingRuleListRequest Filter { get; set; } = new();
    public PagedResult<PostingRuleDto> Rules { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<EventCatalogDto> Catalog { get; set; } = [];
}

public class PostingRuleDetailsViewModel
{
    public PostingRuleDto Rule { get; set; } = default!;
    public EventCatalogDto Event { get; set; } = default!;
    public bool IsMaker { get; set; }
}

public class PostingQueueViewModel
{
    public PostingQueueRequest Filter { get; set; } = new();
    public PagedResult<PostingRequestDto> Items { get; set; } = new([], 1, 20, 0);
    public PostingQueueSummaryDto Summary { get; set; } = new(0, 0, 0, 0);
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public bool CanProcess { get; set; }
}

// ---------- fiscal years ----------
public class FiscalYearFormViewModel
{
    [Required, StringLength(50)] public string Name { get; set; } = "";
    [Display(Name = "Start date")] public DateOnly StartDate { get; set; }
    [Display(Name = "End date")] public DateOnly EndDate { get; set; }
}

public class FiscalYearsViewModel
{
    public IReadOnlyList<FiscalYearDto> Years { get; set; } = [];
    public FiscalYearFormViewModel Form { get; set; } = new();
}

public class FiscalYearDetailsViewModel
{
    public FiscalYearDto Year { get; set; } = default!;
    public PagedResult<PeriodLockDto> History { get; set; } = new([], 1, 50, 0);
}

// ---------- reports ----------
public class ReportFilterViewModel
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public DateOnly? AsOf { get; set; }
    public long? ProjectId { get; set; }
    public long? CostCenterId { get; set; }
    public long? AccountId { get; set; }
    public long? BankAccountId { get; set; }
    public long? CashAccountId { get; set; }
    public JournalSource? Source { get; set; }
    public int Page { get; set; } = 1;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<OptionDto> Accounts { get; set; } = [];
    public IReadOnlyList<OptionDto> CostCenters { get; set; } = [];
    public IReadOnlyList<OptionDto> BankAccounts { get; set; } = [];
    public IReadOnlyList<OptionDto> CashAccounts { get; set; } = [];
    public bool CompanyWideAllowed { get; set; }
}

public class ReportPageViewModel<T> where T : class
{
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public T? Report { get; set; }
    public ReportFilterViewModel Filter { get; set; } = new();
    public string? Message { get; set; }
}

// ---------- banking ----------
public class BankAccountFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Display(Name = "Bank"), Required, StringLength(150)] public string BankName { get; set; } = "";
    [StringLength(150)] public string? Branch { get; set; }
    [Display(Name = "Account title"), Required, StringLength(150)] public string AccountTitle { get; set; } = "";
    [Display(Name = "Account number"), Required, StringLength(50)] public string AccountNumber { get; set; } = "";
    [Display(Name = "IBAN"), StringLength(50)] public string? Iban { get; set; }
    [Display(Name = "Currency"), Required, StringLength(3, MinimumLength = 3)] public string CurrencyCode { get; set; } = "PKR";
    [Display(Name = "Ledger account"), Range(1, long.MaxValue, ErrorMessage = "Choose the ledger account.")] public long GlAccountId { get; set; }
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
    public IReadOnlyList<OptionDto> LedgerAccounts { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
}

public class CashAccountFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [StringLength(150)] public string? Custodian { get; set; }
    [Display(Name = "Ledger account"), Range(1, long.MaxValue, ErrorMessage = "Choose the ledger account.")] public long GlAccountId { get; set; }
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
    public IReadOnlyList<OptionDto> LedgerAccounts { get; set; } = [];
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
}

public class MoneyAccountsViewModel
{
    public IReadOnlyList<BankAccountDto> Banks { get; set; } = [];
    public IReadOnlyList<CashAccountDto> Cash { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class ReconcileViewModel
{
    public BankReconciliationWorkspaceDto Workspace { get; set; } = default!;
    public DateOnly Today { get; set; }
}
