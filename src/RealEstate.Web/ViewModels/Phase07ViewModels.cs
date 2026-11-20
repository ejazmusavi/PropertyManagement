using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;
using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

// ---------- expenses ----------
public class ExpenseDashboardViewModel
{
    public ExpenseDashboardDto Dashboard { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public long? ProjectId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public class ExpenseListViewModel
{
    public ExpenseListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<ExpenseListItemDto> Expenses { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<ExpenseCategoryDto> Categories { get; set; } = [];
    public string Title { get; set; } = "Expenses";
    /// <summary>The approval queue: only expenses pending approval are listed.</summary>
    public bool ApprovalQueue { get; set; }
    public Guid? CurrentUserId { get; set; }
}

public class ExpenseFormViewModel
{
    public long? Id { get; set; }
    [Display(Name = "Project"), Range(1, long.MaxValue, ErrorMessage = "Select the project.")] public long ProjectId { get; set; }
    [Display(Name = "Category"), Range(1, long.MaxValue, ErrorMessage = "Select the category.")] public long CategoryId { get; set; }
    [Display(Name = "Vendor (optional)")] public long? VendorId { get; set; }
    [Required, StringLength(500, MinimumLength = 3)] public string Description { get; set; } = "";
    [Display(Name = "Expense date")] public DateOnly ExpenseDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [Display(Name = "Due date")] public DateOnly? DueDate { get; set; }
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")] public decimal Amount { get; set; }
    [StringLength(100)] public string? Reference { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public string? RowVersion { get; set; }
    public bool AmountLocked { get; set; }
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<ExpenseCategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<VendorDto> Vendors { get; set; } = [];
}

public class ExpenseDetailsViewModel
{
    public ExpenseDetailDto Expense { get; set; } = default!;
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];
    public bool IsSubmitter { get; set; }
}

public class ExpenseCategoriesViewModel
{
    public IReadOnlyList<ExpenseCategoryDto> Categories { get; set; } = [];
    public ExpenseCategoryFormViewModel Form { get; set; } = new();
}

public class ExpenseCategoryFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(40), RegularExpression("^[A-Za-z0-9_-]+$", ErrorMessage = "Use letters, digits, '-' and '_' only.")] public string Code { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
}

// ---------- vendors ----------
public class VendorListViewModel
{
    public VendorListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<VendorListItemDto> Vendors { get; set; } = default!;
}

public class VendorFormViewModel
{
    public long? Id { get; set; }
    public string? Code { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Display(Name = "Contact person"), StringLength(150)] public string? ContactPerson { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(200)] public string? Email { get; set; }
    [StringLength(500)] public string? Address { get; set; }
    [Display(Name = "Tax number (NTN)"), StringLength(50)] public string? TaxNumber { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

public class VendorDetailsViewModel
{
    public VendorDetailDto Vendor { get; set; } = default!;
}

public class VendorInvoiceListViewModel
{
    public VendorInvoiceListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<VendorInvoiceListItemDto> Invoices { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<VendorDto> Vendors { get; set; } = [];
}

public class VendorInvoiceFormViewModel
{
    [Display(Name = "Project"), Range(1, long.MaxValue, ErrorMessage = "Select the project.")] public long ProjectId { get; set; }
    [Display(Name = "Vendor"), Range(1, long.MaxValue, ErrorMessage = "Select the vendor.")] public long VendorId { get; set; }
    [Display(Name = "Invoice number"), Required, StringLength(60)] public string InvoiceNumber { get; set; } = "";
    [Display(Name = "Category"), Range(1, long.MaxValue, ErrorMessage = "Select the category.")] public long CategoryId { get; set; }
    [Display(Name = "Invoice date")] public DateOnly InvoiceDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [Display(Name = "Due date")] public DateOnly DueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")] public decimal Amount { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<ExpenseCategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<VendorDto> Vendors { get; set; } = [];
}

public class VendorPaymentListViewModel
{
    public VendorPaymentListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<VendorPaymentDto> Payments { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
}

public class VendorPaymentFormViewModel
{
    public long ExpenseId { get; set; }
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")] public decimal Amount { get; set; }
    [Display(Name = "Payment method"), Range(1, long.MaxValue, ErrorMessage = "Select the payment method.")] public long PaymentMethodId { get; set; }
    [Display(Name = "Payment date")] public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [StringLength(100)] public string? Reference { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public string? RowVersion { get; set; }
    public ExpenseDetailDto? Expense { get; set; }
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];
}

/// <summary>Payable expenses to choose from when recording a vendor payment.</summary>
public class VendorPaymentPickerViewModel
{
    public string? Search { get; set; }
    public Application.Common.PagedResult<ExpenseListItemDto> Payables { get; set; } = default!;
}

// ---------- government fees ----------
public class GovFeeTypeListViewModel
{
    public IReadOnlyList<GovernmentFeeTypeDto> Types { get; set; } = [];
}

public class GovFeeTypeFormViewModel
{
    public long? Id { get; set; }
    [Display(Name = "Project (blank = company-wide)")] public long? ProjectId { get; set; }
    [Required, StringLength(40), RegularExpression("^[A-Za-z0-9_-]+$", ErrorMessage = "Use letters, digits, '-' and '_' only.")] public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public GovernmentFeeKind Kind { get; set; } = GovernmentFeeKind.Other;
    [Display(Name = "Calculation")] public FeeCalculationMethod Method { get; set; } = FeeCalculationMethod.Manual;
    [Display(Name = "Fixed amount")] public decimal? FixedAmount { get; set; }
    [Display(Name = "Rate (%)")] public decimal? Rate { get; set; }
    [Display(Name = "Default bearer")] public FeeBearer DefaultBearer { get; set; } = FeeBearer.Customer;
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public string? RowVersion { get; set; }
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
}

public class GovFeeListViewModel
{
    public GovernmentFeeListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<GovernmentFeeListItemDto> Fees { get; set; } = default!;
    public GovernmentFeeSummaryDto Summary { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
    public IReadOnlyList<GovernmentFeeTypeDto> Types { get; set; } = [];
}

public class GovFeeAssessFormViewModel
{
    public long AgreementId { get; set; }
    public AgreementLookupDto? Agreement { get; set; }
    [Display(Name = "Fee type"), Range(1, long.MaxValue, ErrorMessage = "Select the fee type.")] public long FeeTypeId { get; set; }
    [Display(Name = "Amount the percentage applies to")] public decimal? BasisAmount { get; set; }
    [Display(Name = "Fee amount")] public decimal? Amount { get; set; }
    [Display(Name = "Company-borne part")] public decimal? CompanyBorneAmount { get; set; }
    [Display(Name = "Due date")] public DateOnly? DueDate { get; set; }
    [Display(Name = "Challan / reference"), StringLength(100)] public string? Reference { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<GovernmentFeeTypeDto> Types { get; set; } = [];
}

public class GovFeeDetailsViewModel
{
    public GovernmentFeeDetailDto Fee { get; set; } = default!;
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];
}

public class GovPaymentListViewModel
{
    public GovernmentPaymentListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<GovernmentFeePaymentDto> Payments { get; set; } = default!;
    public IReadOnlyList<ProjectOptionDto> Projects { get; set; } = [];
}

public class GovPaymentFormViewModel
{
    public long AssessmentId { get; set; }
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")] public decimal Amount { get; set; }
    [Display(Name = "Payment method"), Range(1, long.MaxValue, ErrorMessage = "Select the payment method.")] public long PaymentMethodId { get; set; }
    [Display(Name = "Payment date")] public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [Display(Name = "Challan / receipt number"), StringLength(100)] public string? Reference { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public GovernmentFeeDetailDto? Fee { get; set; }
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];
}

/// <summary>Assessments with an unpaid government payable, to choose from when recording a government payment.</summary>
public class GovPaymentPickerViewModel
{
    public string? Search { get; set; }
    public Application.Common.PagedResult<GovernmentFeeListItemDto> Payable { get; set; } = default!;
}
