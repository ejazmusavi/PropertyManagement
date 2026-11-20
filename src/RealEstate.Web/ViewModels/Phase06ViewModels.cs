using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class PaymentListViewModel
{
    public PaymentListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<PaymentListItemDto> Payments { get; set; } = default!;
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public string Title { get; set; } = "Payments";
}

/// <summary>One open schedule line with the amount the user wants to allocate to it (0 = none).</summary>
public class AllocationLineViewModel
{
    public long ItemId { get; set; }
    [Range(0, 9_999_999_999_999_999.99)]
    public decimal Amount { get; set; }
}

public class PaymentFormViewModel
{
    public long AgreementId { get; set; }
    public long? CustomerId { get; set; }
    [Display(Name = "Payment method"), Range(1, long.MaxValue, ErrorMessage = "Select the payment method.")]
    public long PaymentMethodId { get; set; }
    public PaymentSource Source { get; set; } = PaymentSource.Office;
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")]
    public decimal Amount { get; set; }
    [Display(Name = "Payment date"), Required]
    public DateOnly PaymentDate { get; set; }
    [StringLength(100)] public string? Reference { get; set; }
    [Display(Name = "Bank"), StringLength(150)] public string? BankName { get; set; }
    [Display(Name = "Cheque / instrument date")] public DateOnly? InstrumentDate { get; set; }
    [Display(Name = "Paid by (name)"), StringLength(200)] public string? PayerName { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    [Display(Name = "Allocation")] public AllocationMode AllocationMode { get; set; } = AllocationMode.OldestDueFirst;
    [Display(Name = "Charge")] public long? ChargeId { get; set; }
    [Display(Name = "Hold any remainder as advance")] public bool RemainderToAdvance { get; set; }
    public List<AllocationLineViewModel> Lines { get; set; } = [];
    /// <summary>Generated when the form is first shown; a double submit returns the same payment.</summary>
    [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");

    public CollectionContextDto? Context { get; set; }
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];

    public IReadOnlyList<AllocationLineInput>? ToLines() =>
        AllocationMode is AllocationMode.Manual or AllocationMode.SpecificInstallment ? Lines.Where(l => l.Amount > 0m).Select(l => new AllocationLineInput(l.ItemId, l.Amount)).ToList() : null;

    public CreatePaymentRequest ToRequest() => new(AgreementId, CustomerId, PaymentMethodId, Source, Amount, PaymentDate, Reference, BankName, InstrumentDate, PayerName, Remarks,
        AllocationMode, AllocationMode == AllocationMode.ChargeSpecific ? ChargeId : null, ToLines(), RemainderToAdvance, IdempotencyKey);
}

public class AgreementPickerViewModel
{
    public string? Term { get; set; }
    public IReadOnlyList<AgreementLookupDto> Results { get; set; } = [];
    public string Action { get; set; } = "Create";
    public string Controller { get; set; } = "Payments";
    public string Title { get; set; } = "Select an agreement";
}

public class PaymentDetailsViewModel
{
    public PaymentDetailDto Payment { get; set; } = default!;
    public bool IsSubmitter { get; set; }
}

/// <summary>Approval (with optional allocation override) or later allocation of a posted payment.</summary>
public class AllocationFormViewModel
{
    public long PaymentId { get; set; }
    public string? RowVersion { get; set; }
    public string? Comments { get; set; }
    /// <summary>Approval only: false = use the instruction given at submission.</summary>
    public bool OverrideAllocation { get; set; }
    public AllocationMode Mode { get; set; } = AllocationMode.OldestDueFirst;
    public long? ChargeId { get; set; }
    public bool IncludeAdvance { get; set; }
    public bool RemainderToAdvance { get; set; }
    public List<AllocationLineViewModel> Lines { get; set; } = [];

    public PaymentDetailDto? Payment { get; set; }
    public AllocationOptionsDto? Options { get; set; }

    public IReadOnlyList<AllocationLineInput>? ToLines() =>
        Mode is AllocationMode.Manual or AllocationMode.SpecificInstallment ? Lines.Where(l => l.Amount > 0m).Select(l => new AllocationLineInput(l.ItemId, l.Amount)).ToList() : null;
}

public class AgreementLedgerViewModel
{
    public AgreementLedgerDto Ledger { get; set; } = default!;
}

public class CustomerLedgerViewModel
{
    public CustomerLedgerDto Ledger { get; set; } = default!;
}

public class PropertyStatementViewModel
{
    public PropertyStatementDto Statement { get; set; } = default!;
}

public class RefundListViewModel
{
    public RefundListRequest Filter { get; set; } = new();
    public Application.Common.PagedResult<RefundListItemDto> Refunds { get; set; } = default!;
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class RefundFormViewModel
{
    public long AgreementId { get; set; }
    [Range(0.01, 9_999_999_999_999_999.99, ErrorMessage = "Enter an amount greater than zero.")]
    public decimal Amount { get; set; }
    [Required, StringLength(1000, MinimumLength = 5)] public string Reason { get; set; } = "";
    [Display(Name = "Approved booking refund")] public long? BookingRefundId { get; set; }
    [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public RefundableDto? Refundable { get; set; }
}

public class RefundDetailsViewModel
{
    public RefundDetailDto Refund { get; set; } = default!;
    public IReadOnlyList<PaymentMethodDto> Methods { get; set; } = [];
    public bool IsRequester { get; set; }
}

/// <summary>Model of the shared allocation editor partial (field names differ between the payment form and the allocation form).</summary>
public sealed record AllocationEditorModel(string ModeField, AllocationMode Mode, long? ChargeId, bool RemainderToAdvance, IReadOnlyList<OpenLineDto> Open, IReadOnlyList<ChargeOptionDto> Charges,
    IReadOnlyList<AllocationLineViewModel> Lines, string CurrencySymbol, bool AllowUnallocated);
