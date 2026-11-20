using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Inventory;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

public class AddressInput
{
    public AddressType AddressType { get; set; } = AddressType.Home;
    [StringLength(300)] public string? Line1 { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(100)] public string? Country { get; set; }
    public bool IsPrimary { get; set; }
}

public class ContactInput
{
    [StringLength(200)] public string? Name { get; set; }
    [StringLength(100)] public string? Relation { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(256)] public string? Email { get; set; }
}

public class NomineeInput
{
    [StringLength(200)] public string? FullName { get; set; }
    [StringLength(100)] public string? Relationship { get; set; }
    [StringLength(30)] public string? IdentityNumber { get; set; }
    [StringLength(30)] public string? Mobile { get; set; }
    public decimal? SharePercent { get; set; }
}

public class CustomerFormViewModel
{
    public long? Id { get; set; }
    public string? RowVersion { get; set; }
    public string? CustomerCode { get; set; }
    [Required, StringLength(200), Display(Name = "Full name")] public string FullName { get; set; } = "";
    [Display(Name = "Identity document")] public IdentityDocumentType IdentityType { get; set; } = IdentityDocumentType.Cnic;
    [Required, StringLength(30), Display(Name = "CNIC / passport number")] public string IdentityNumber { get; set; } = "";
    [Required, StringLength(30)] public string Mobile { get; set; } = "";
    [StringLength(256), EmailAddress] public string? Email { get; set; }
    [StringLength(500), Display(Name = "Primary address")] public string? Address { get; set; }
    [StringLength(2000)] public string? Remarks { get; set; }
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    [StringLength(1000), Display(Name = "Reason for status change")] public string? StatusReason { get; set; }
    public CustomerStatus? OriginalStatus { get; set; }
    public List<AddressInput> Addresses { get; set; } = [];
    public List<ContactInput> Contacts { get; set; } = [];
    public List<NomineeInput> Nominees { get; set; } = [];

    public IReadOnlyList<AddressDto> ToAddresses() => Addresses.Where(a => !string.IsNullOrWhiteSpace(a.Line1)).Select(a => new AddressDto(a.AddressType, a.Line1!, a.City, a.Country, a.IsPrimary)).ToList();
    public IReadOnlyList<ContactDto> ToContacts() => Contacts.Where(a => !string.IsNullOrWhiteSpace(a.Name) || !string.IsNullOrWhiteSpace(a.Phone) || !string.IsNullOrWhiteSpace(a.Email)).Select(a => new ContactDto(a.Name ?? "", a.Relation, a.Phone, a.Email)).ToList();
    public IReadOnlyList<NomineeDto> ToNominees() => Nominees.Where(a => !string.IsNullOrWhiteSpace(a.FullName)).Select(a => new NomineeDto(a.FullName!, a.Relationship ?? "", a.IdentityNumber, a.Mobile, a.SharePercent ?? 0m)).ToList();
}

public class CustomerListViewModel
{
    public PagedResult<CustomerListItemDto> Customers { get; set; } = default!;
    public CustomerListRequest Filter { get; set; } = new();
    public bool Descending { get; set; }
}

public class CustomerDetailsViewModel
{
    public CustomerDetailDto Customer { get; set; } = default!;
    public IReadOnlyList<CustomerDocumentDto> Documents { get; set; } = [];
}

public class BookingListViewModel
{
    public PagedResult<BookingListItemDto> Bookings { get; set; } = default!;
    public BookingListRequest Filter { get; set; } = new();
    public bool Descending { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class WizardLine
{
    public long PropertyId { get; set; }
    [Range(0, 999999999999)] public decimal ExtraDiscount { get; set; }
    [StringLength(1000)] public string? ExtraDiscountReason { get; set; }
}

public class WizardOwner
{
    public long CustomerId { get; set; }
    public decimal SharePercent { get; set; }
    public string? CustomerLabel { get; set; }
}

public class WizardCharge
{
    public BookingChargeType ChargeType { get; set; } = BookingChargeType.Registration;
    [StringLength(200)] public string? Description { get; set; }
    public decimal Amount { get; set; }
}

public class BookingWizardViewModel
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string? Q { get; set; }
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Installment;
    public decimal BookingAmount { get; set; }
    public bool AuthorizeOutstandingBalance { get; set; }
    [StringLength(1000)] public string? BalanceAuthorizationReason { get; set; }
    [StringLength(2000)] public string? Remarks { get; set; }
    public bool SubmitForApproval { get; set; } = true;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public List<WizardLine> Lines { get; set; } = [];
    public List<WizardOwner> Owners { get; set; } = [];
    public List<WizardCharge> Charges { get; set; } = [];

    public IReadOnlyList<PropertyListItemDto> Available { get; set; } = [];
    public bool CanDiscount { get; set; }
    public bool CanAuthorizeBalance { get; set; }
    public string CurrencySymbol { get; set; } = "Rs";
    public int DecimalPlaces { get; set; } = 2;

    public CreateBookingRequest ToRequest() => new(ProjectId, null, PaymentMode, Lines.Select(l => new BookingPropertyInput(l.PropertyId, l.ExtraDiscount, l.ExtraDiscountReason)).ToList(),
        Owners.Select(o => new OwnerInput(o.CustomerId, o.SharePercent)).ToList(),
        Charges.Where(c => !string.IsNullOrWhiteSpace(c.Description) || c.Amount > 0).Select(c => new ChargeInput(c.ChargeType, c.Description ?? "", c.Amount)).ToList(),
        BookingAmount, AuthorizeOutstandingBalance, BalanceAuthorizationReason, Remarks, SubmitForApproval, IdempotencyKey);
}

public class BookingDetailsViewModel
{
    public BookingDetailDto Booking { get; set; } = default!;
    public string? CurrentUserName { get; set; }
    public Guid? CurrentUserId { get; set; }
}

public class BookingApprovalsViewModel
{
    public PagedResult<BookingListItemDto> Bookings { get; set; } = default!;
    public string? CurrentUserName { get; set; }
    public IReadOnlyDictionary<long, string?> Submitters { get; set; } = new Dictionary<long, string?>();
}

public class CancelBookingViewModel
{
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever, Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public BookingDetailDto Booking { get; set; } = default!;
    public string RowVersion { get; set; } = "";
    [Required, StringLength(1000, MinimumLength = 5), Display(Name = "Reason for cancellation")] public string Reason { get; set; } = "";
    public bool RequestRefund { get; set; }
    public decimal? RefundAmount { get; set; }
    [StringLength(1000)] public string? RefundReason { get; set; }
    public bool CanRefund { get; set; }
}

public class TransferBookingViewModel
{
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever, Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public BookingDetailDto Booking { get; set; } = default!;
    public string RowVersion { get; set; } = "";
    [Required, StringLength(1000, MinimumLength = 5), Display(Name = "Reason for the change")] public string Reason { get; set; } = "";
    public List<WizardOwner> Owners { get; set; } = [];
}

public class AgreementViewModel
{
    public AgreementDto Agreement { get; set; } = default!;
    public BookingDetailDto Booking { get; set; } = default!;
}
