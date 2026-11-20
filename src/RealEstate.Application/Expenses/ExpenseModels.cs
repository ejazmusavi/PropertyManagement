using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Expenses;

public sealed record ProjectOptionDto(long Id, string ProjectCode, string ProjectName);

// ---------- categories ----------
public sealed record ExpenseCategoryDto(long Id, string Code, string Name, string? Description, bool IsSystem, bool IsActive);
public sealed record SaveExpenseCategoryRequest(string Code, string Name, string? Description, bool IsActive = true);

// ---------- vendors ----------
public sealed record VendorDto(long Id, string Code, string Name, string? ContactPerson, string? Phone, string? Email, string? Address, string? TaxNumber, string? Notes, bool IsActive, string RowVersion);
public sealed record SaveVendorRequest(string Name, string? ContactPerson, string? Phone, string? Email, string? Address, string? TaxNumber, string? Notes, bool IsActive = true, string? RowVersion = null);
public class VendorListRequest : PagedRequest { public bool? Active { get; set; } }
public sealed record VendorListItemDto(long Id, string Code, string Name, string? ContactPerson, string? Phone, bool IsActive, decimal Payable, decimal Paid);
public sealed record VendorDetailDto(VendorDto Vendor, decimal Payable, decimal Paid, decimal Overdue, string CurrencySymbol,
    IReadOnlyList<ExpenseListItemDto> OpenPayables, IReadOnlyList<VendorPaymentDto> RecentPayments);

// ---------- expenses ----------
public class ExpenseListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? CategoryId { get; set; }
    public long? VendorId { get; set; }
    public ExpenseStatus? Status { get; set; }
    public ExpensePaymentStatus? PaymentStatus { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>Only approved expenses with an unpaid balance.</summary>
    public bool PayableOnly { get; set; }
}

public sealed record ExpenseListItemDto(long Id, string ExpenseNumber, long ProjectId, string ProjectCode, long CategoryId, string CategoryName, long? VendorId, string? VendorName,
    string Description, DateOnly ExpenseDate, DateOnly? DueDate, decimal Amount, decimal AmountPaid, decimal Outstanding, ExpenseStatus Status, ExpensePaymentStatus PaymentStatus, string? CreatedBy);

public sealed record CreateExpenseRequest(long ProjectId, long CategoryId, long? VendorId, string Description, DateOnly ExpenseDate, decimal Amount, DateOnly? DueDate,
    string? Reference, string? Remarks, string? IdempotencyKey);
public sealed record UpdateExpenseRequest(long CategoryId, long? VendorId, string Description, DateOnly ExpenseDate, decimal Amount, DateOnly? DueDate, string? Reference, string? Remarks, string? RowVersion);
public sealed record SubmitExpenseRequest(string? RowVersion);
public sealed record ApproveExpenseRequest(string? Comments, string? RowVersion);
public sealed record RejectExpenseRequest(string Comments, string? RowVersion);
public sealed record CancelExpenseRequest(string Reason, string? RowVersion);
public sealed record PayExpenseRequest(decimal Amount, long PaymentMethodId, DateOnly PaymentDate, string? Reference, string? Remarks, string IdempotencyKey, string? RowVersion = null);
public sealed record ReverseVendorPaymentRequest(string Reason);

public sealed record ExpenseAttachmentDto(long Id, string Title, string FileName, ExpenseDocumentType DocumentType, string ContentType, long SizeBytes, DateTime CreatedAtUtc, string? CreatedBy);
public sealed record ExpenseApprovalDto(long Id, BookingDecision Decision, string ApproverName, string? Comments, DateTime DecidedAtUtc);
public sealed record VendorInvoiceSummaryDto(long Id, string InvoiceNumber, DateOnly InvoiceDate, DateOnly DueDate);

public sealed record ExpenseDetailDto(long Id, string ExpenseNumber, long ProjectId, string ProjectCode, string ProjectName, long CategoryId, string CategoryName, long? VendorId, string? VendorName,
    string Description, DateOnly ExpenseDate, DateOnly? DueDate, decimal Amount, decimal AmountPaid, decimal Outstanding, ExpenseStatus Status, ExpensePaymentStatus PaymentStatus,
    string? Reference, string? Remarks, string? SubmittedBy, DateTime? SubmittedAtUtc, string? ApprovedBy, DateTime? ApprovedAtUtc, string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason,
    string? CreatedBy, DateTime CreatedAtUtc, string CurrencySymbol, VendorInvoiceSummaryDto? Invoice,
    IReadOnlyList<ExpenseAttachmentDto> Attachments, IReadOnlyList<ExpenseApprovalDto> Approvals, IReadOnlyList<VendorPaymentDto> Payments, string RowVersion);

// ---------- payments ----------
public class VendorPaymentListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? VendorId { get; set; }
    public long? ExpenseId { get; set; }
    public VendorPaymentStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public sealed record VendorPaymentDto(long Id, string PaymentNumber, long ExpenseId, string ExpenseNumber, long ProjectId, string ProjectCode, long? VendorId, string? VendorName,
    decimal Amount, DateOnly PaymentDate, long PaymentMethodId, string MethodName, string? Reference, string? Remarks, VendorPaymentStatus Status,
    string? CreatedBy, DateTime CreatedAtUtc, string? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason);

// ---------- vendor invoices ----------
public class VendorInvoiceListRequest : PagedRequest
{
    public long? ProjectId { get; set; }
    public long? VendorId { get; set; }
    public ExpensePaymentStatus? PaymentStatus { get; set; }
    public bool OverdueOnly { get; set; }
}

public sealed record CreateVendorInvoiceRequest(long ProjectId, long VendorId, string InvoiceNumber, long CategoryId, DateOnly InvoiceDate, DateOnly DueDate, decimal Amount,
    string? Description, string? Notes, string? IdempotencyKey);

public sealed record VendorInvoiceListItemDto(long Id, string InvoiceNumber, long VendorId, string VendorName, long ProjectId, string ProjectCode, long ExpenseId, string ExpenseNumber,
    DateOnly InvoiceDate, DateOnly DueDate, decimal Amount, decimal AmountPaid, decimal Outstanding, ExpenseStatus ExpenseStatus, ExpensePaymentStatus PaymentStatus, bool IsOverdue);

// ---------- dashboard ----------
public sealed record ExpenseAmountRowDto(string Label, decimal Amount, int Count);
public sealed record ExpenseDashboardDto(long? ProjectId, DateOnly From, DateOnly To, string CurrencySymbol,
    decimal TotalApproved, decimal TotalPaid, decimal PayablesOutstanding, decimal PayablesOverdue, int PendingApprovalCount, decimal PendingApprovalAmount, int DraftCount,
    decimal GovernmentFeeCompanyBorne, decimal GovernmentFeePayable,
    IReadOnlyList<ExpenseAmountRowDto> ByCategory, IReadOnlyList<ExpenseAmountRowDto> ByProject, IReadOnlyList<ExpenseAmountRowDto> ByMonth, IReadOnlyList<ExpenseAmountRowDto> TopVendorPayables);

// ---------- validators ----------
public class SaveExpenseCategoryRequestValidator : AbstractValidator<SaveExpenseCategoryRequest>
{
    public SaveExpenseCategoryRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Use letters, digits, '-' and '_' only.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class SaveVendorRequestValidator : AbstractValidator<SaveVendorRequest>
{
    public SaveVendorRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactPerson).MaximumLength(150);
        RuleFor(x => x.Phone).MaximumLength(40);
        RuleFor(x => x.Email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.TaxNumber).MaximumLength(50);
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public class CreateExpenseRequestValidator : AbstractValidator<CreateExpenseRequest>
{
    public CreateExpenseRequestValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(3).MaximumLength(500);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.DueDate).GreaterThanOrEqualTo(x => x.ExpenseDate).When(x => x.DueDate.HasValue).WithMessage("The due date cannot be before the expense date.");
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}

public class UpdateExpenseRequestValidator : AbstractValidator<UpdateExpenseRequest>
{
    public UpdateExpenseRequestValidator()
    {
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(3).MaximumLength(500);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.DueDate).GreaterThanOrEqualTo(x => x.ExpenseDate).When(x => x.DueDate.HasValue).WithMessage("The due date cannot be before the expense date.");
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
    }
}

public class RejectExpenseRequestValidator : AbstractValidator<RejectExpenseRequest>
{
    public RejectExpenseRequestValidator() => RuleFor(x => x.Comments).NotEmpty().MinimumLength(3).MaximumLength(1000);
}

public class CancelExpenseRequestValidator : AbstractValidator<CancelExpenseRequest>
{
    public CancelExpenseRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class PayExpenseRequestValidator : AbstractValidator<PayExpenseRequest>
{
    public PayExpenseRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.PaymentMethodId).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.Remarks).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public class ReverseVendorPaymentRequestValidator : AbstractValidator<ReverseVendorPaymentRequest>
{
    public ReverseVendorPaymentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(1000);
}

public class CreateVendorInvoiceRequestValidator : AbstractValidator<CreateVendorInvoiceRequest>
{
    public CreateVendorInvoiceRequestValidator()
    {
        RuleFor(x => x.ProjectId).GreaterThan(0);
        RuleFor(x => x.VendorId).GreaterThan(0);
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.InvoiceNumber).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(MoneyRules.Max).Must(MoneyRules.TwoDecimals).WithMessage("At most two decimal places.");
        RuleFor(x => x.DueDate).GreaterThanOrEqualTo(x => x.InvoiceDate).WithMessage("The due date cannot be before the invoice date.");
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}
