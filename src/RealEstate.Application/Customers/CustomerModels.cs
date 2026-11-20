using FluentValidation;
using RealEstate.Application.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Customers;

public sealed record AddressDto(AddressType AddressType, string Line1, string? City, string? Country, bool IsPrimary);
public sealed record ContactDto(string Name, string? Relation, string? Phone, string? Email);
public sealed record NomineeDto(string FullName, string Relationship, string? IdentityNumber, string? Mobile, decimal SharePercent);

public sealed record CustomerListItemDto(long Id, string CustomerCode, string FullName, IdentityDocumentType IdentityType, string IdentityNumber, string Mobile, string? Email,
    CustomerStatus Status, int ActiveOwnerships);

public sealed class CustomerListRequest : PagedRequest
{
    public CustomerStatus? Status { get; set; }
}

public sealed record CustomerLookupDto(long Id, string CustomerCode, string FullName, string Mobile, CustomerStatus Status);

public sealed record RelationshipDto(long Id, long RelatedCustomerId, string RelatedCode, string RelatedName, CustomerRelationshipType RelationshipType, string? Notes);

public sealed record OwnershipDto(long Id, long PropertyId, string PropertyCode, string ProjectCode, long BookingId, string BookingNumber, decimal SharePercent, bool IsActive,
    DateTime StartedAtUtc, DateTime? EndedAtUtc, string? EndReason);

public sealed record CustomerDetailDto(long Id, string CustomerCode, string FullName, IdentityDocumentType IdentityType, string IdentityNumber, string Mobile, string? Email, string? Address,
    CustomerStatus Status, string? Remarks, IReadOnlyList<AddressDto> Addresses, IReadOnlyList<ContactDto> Contacts, IReadOnlyList<NomineeDto> Nominees,
    IReadOnlyList<RelationshipDto> Relationships, IReadOnlyList<OwnershipDto> Ownerships, string RowVersion, DateTime CreatedAtUtc, string? CreatedBy, DateTime? ModifiedAtUtc, string? ModifiedBy);

public sealed record CreateCustomerRequest(string FullName, IdentityDocumentType IdentityType, string IdentityNumber, string Mobile, string? Email, string? Address, string? Remarks,
    IReadOnlyList<AddressDto>? Addresses, IReadOnlyList<ContactDto>? Contacts, IReadOnlyList<NomineeDto>? Nominees);

/// <summary>Code is fixed. A status change needs a reason; the row version rejects stale edits.</summary>
public sealed record UpdateCustomerRequest(string FullName, IdentityDocumentType IdentityType, string IdentityNumber, string Mobile, string? Email, string? Address, string? Remarks,
    CustomerStatus Status, string? StatusReason, IReadOnlyList<AddressDto>? Addresses, IReadOnlyList<ContactDto>? Contacts, IReadOnlyList<NomineeDto>? Nominees, string? RowVersion);

public sealed record AddRelationshipRequest(long RelatedCustomerId, CustomerRelationshipType RelationshipType, string? Notes);

public sealed record CustomerDocumentDto(long Id, string Title, string FileName, string DocumentType, string ContentType, long SizeBytes, DateTime UploadedAtUtc, string? UploadedBy);

internal static class CustomerRuleSets
{
    public static void Common<T>(AbstractValidator<T> v, Func<T, string> name, Func<T, IdentityDocumentType> idType, Func<T, string> idNumber, Func<T, string> mobile, Func<T, string?> email,
        Func<T, string?> address, Func<T, string?> remarks, Func<T, IReadOnlyList<AddressDto>?> addresses, Func<T, IReadOnlyList<ContactDto>?> contacts, Func<T, IReadOnlyList<NomineeDto>?> nominees)
    {
        v.RuleFor(x => name(x)).NotEmpty().WithName("FullName").WithMessage("The full name is required.").MaximumLength(200);
        v.RuleFor(x => idType(x)).IsInEnum().WithName("IdentityType");
        v.RuleFor(x => idNumber(x)).NotEmpty().WithName("IdentityNumber").WithMessage("The CNIC or passport number is required.")
            .Custom((n, ctx) =>
            {
                var type = idType(ctx.InstanceToValidate);
                if (!string.IsNullOrWhiteSpace(n) && CustomerRules.IdentityError(type, CustomerRules.NormalizeIdentity(type, n)) is { } err) ctx.AddFailure("IdentityNumber", err);
            });
        v.RuleFor(x => mobile(x)).NotEmpty().WithName("Mobile").WithMessage("The mobile number is required.")
            .Must(m => string.IsNullOrWhiteSpace(m) || (m.Count(char.IsDigit) is >= 7 and <= 15 && m.All(c => char.IsDigit(c) || c is '+' or '-' or ' ' or '(' or ')')))
            .WithMessage("Enter a valid mobile number (7 to 15 digits).");
        v.RuleFor(x => email(x)).MaximumLength(256).EmailAddress().When(x => !string.IsNullOrWhiteSpace(email(x))).WithName("Email");
        v.RuleFor(x => address(x)).MaximumLength(500).WithName("Address");
        v.RuleFor(x => remarks(x)).MaximumLength(2000).WithName("Remarks");
        v.RuleFor(x => addresses(x)).Custom((list, ctx) =>
        {
            if (list is null) return;
            if (list.Count > 10) ctx.AddFailure("Addresses", "At most 10 addresses are allowed.");
            if (list.Count(a => a.IsPrimary) > 1) ctx.AddFailure("Addresses", "Only one address can be primary.");
            foreach (var a in list)
            {
                if (!Enum.IsDefined(a.AddressType)) ctx.AddFailure("Addresses", "Unknown address type.");
                if (string.IsNullOrWhiteSpace(a.Line1) || a.Line1.Length > 300) ctx.AddFailure("Addresses", "Each address needs a line of at most 300 characters.");
            }
        });
        v.RuleFor(x => contacts(x)).Custom((list, ctx) =>
        {
            if (list is null) return;
            if (list.Count > 10) ctx.AddFailure("Contacts", "At most 10 contacts are allowed.");
            foreach (var c in list)
            {
                if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200) ctx.AddFailure("Contacts", "Each contact needs a name.");
                if (string.IsNullOrWhiteSpace(c.Phone) && string.IsNullOrWhiteSpace(c.Email)) ctx.AddFailure("Contacts", "Each contact needs a phone number or an email.");
            }
        });
        v.RuleFor(x => nominees(x)).Custom((list, ctx) =>
        {
            if (list is null) return;
            if (list.Count > 10) ctx.AddFailure("Nominees", "At most 10 nominees are allowed.");
            foreach (var n in list)
            {
                if (string.IsNullOrWhiteSpace(n.FullName) || string.IsNullOrWhiteSpace(n.Relationship)) ctx.AddFailure("Nominees", "Each nominee needs a name and a relationship.");
                if (n.SharePercent is < 0 or > 100 || decimal.Round(n.SharePercent, 4) != n.SharePercent) ctx.AddFailure("Nominees", "A nominee share must be between 0 and 100 with at most 4 decimals.");
            }
            if (list.Sum(n => n.SharePercent) > 100m) ctx.AddFailure("Nominees", "The nominee shares cannot total more than 100%.");
        });
    }
}

public class CreateCustomerRequestValidator : AbstractValidator<CreateCustomerRequest>
{
    public CreateCustomerRequestValidator() =>
        CustomerRuleSets.Common(this, x => x.FullName, x => x.IdentityType, x => x.IdentityNumber, x => x.Mobile, x => x.Email, x => x.Address, x => x.Remarks, x => x.Addresses, x => x.Contacts, x => x.Nominees);
}

public class UpdateCustomerRequestValidator : AbstractValidator<UpdateCustomerRequest>
{
    public UpdateCustomerRequestValidator()
    {
        CustomerRuleSets.Common(this, x => x.FullName, x => x.IdentityType, x => x.IdentityNumber, x => x.Mobile, x => x.Email, x => x.Address, x => x.Remarks, x => x.Addresses, x => x.Contacts, x => x.Nominees);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.StatusReason).MaximumLength(1000);
    }
}

public class AddRelationshipRequestValidator : AbstractValidator<AddRelationshipRequest>
{
    public AddRelationshipRequestValidator()
    {
        RuleFor(x => x.RelatedCustomerId).GreaterThan(0).WithMessage("Choose the related customer.");
        RuleFor(x => x.RelationshipType).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}
