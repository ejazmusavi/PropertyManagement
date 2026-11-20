using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("Customers");
        b.Audit();
        b.Property(x => x.CustomerCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        b.Property(x => x.IdentityType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.IdentityNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Mobile).HasMaxLength(30).IsRequired();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Remarks).HasMaxLength(2000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.CustomerCode).IsUnique();
        b.HasIndex(x => new { x.IdentityType, x.IdentityNumber }).IsUnique();
        b.HasIndex(x => x.Mobile);
        b.HasIndex(x => x.FullName);
        b.HasMany(x => x.Addresses).WithOne().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Contacts).WithOne().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Nominees).WithOne().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> b)
    {
        b.ToTable("CustomerAddresses");
        b.Audit();
        b.Property(x => x.AddressType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Line1).HasMaxLength(300).IsRequired();
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Country).HasMaxLength(100);
    }
}

public class CustomerContactConfiguration : IEntityTypeConfiguration<CustomerContact>
{
    public void Configure(EntityTypeBuilder<CustomerContact> b)
    {
        b.ToTable("CustomerContacts");
        b.Audit();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Relation).HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Email).HasMaxLength(256);
    }
}

public class CustomerNomineeConfiguration : IEntityTypeConfiguration<CustomerNominee>
{
    public void Configure(EntityTypeBuilder<CustomerNominee> b)
    {
        b.ToTable("CustomerNominees", t => t.HasCheckConstraint("CK_CustomerNominees_Share", "`SharePercent` >= 0 AND `SharePercent` <= 100"));
        b.Audit();
        b.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Relationship).HasMaxLength(100).IsRequired();
        b.Property(x => x.IdentityNumber).HasMaxLength(30);
        b.Property(x => x.Mobile).HasMaxLength(30);
        b.Property(x => x.SharePercent).HasColumnType("decimal(9,4)");
    }
}

public class CustomerRelationshipConfiguration : IEntityTypeConfiguration<CustomerRelationship>
{
    public void Configure(EntityTypeBuilder<CustomerRelationship> b)
    {
        b.ToTable("CustomerRelationships", t => t.HasCheckConstraint("CK_CustomerRelationships_NotSelf", "`CustomerId` <> `RelatedCustomerId`"));
        b.Audit();
        b.Property(x => x.RelationshipType).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.RelatedCustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CustomerId, x.RelatedCustomerId }).IsUnique();
    }
}

public class CustomerDocumentConfiguration : IEntityTypeConfiguration<CustomerDocument>
{
    public void Configure(EntityTypeBuilder<CustomerDocument> b)
    {
        b.ToTable("CustomerDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(30);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CustomerId);
    }
}

public class CustomerOwnershipConfiguration : IEntityTypeConfiguration<CustomerOwnership>
{
    public void Configure(EntityTypeBuilder<CustomerOwnership> b)
    {
        b.ToTable("CustomerOwnerships", t => t.HasCheckConstraint("CK_CustomerOwnerships_Share", "`SharePercent` > 0 AND `SharePercent` <= 100"));
        b.Audit();
        b.Property(x => x.SharePercent).HasColumnType("decimal(9,4)");
        b.Property(x => x.ActiveKey).HasMaxLength(60);
        b.Property(x => x.EndReason).HasMaxLength(1000);
        b.Property(x => x.StartedAtUtc).HasPrecision(6);
        b.Property(x => x.EndedAtUtc).HasPrecision(6);
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Property).WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ActiveKey).IsUnique();
        b.HasIndex(x => new { x.PropertyId, x.IsActive });
        b.HasIndex(x => new { x.CustomerId, x.IsActive });
    }
}

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.ToTable("Bookings", t => t.HasCheckConstraint("CK_Bookings_Amounts", "`TotalPayable` >= 0 AND `BookingAmount` >= 0 AND `BookingAmount` <= `TotalPayable`"));
        b.Audit();
        b.Property(x => x.BookingNumber).HasMaxLength(40).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(64);
        b.Property(x => x.PaymentMode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.TotalPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalExtraDiscount).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalCharges).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalPayable).HasColumnType("decimal(18,2)");
        b.Property(x => x.BookingAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.BalanceAuthorizedBy).HasMaxLength(256);
        b.Property(x => x.BalanceAuthorizationReason).HasMaxLength(1000);
        b.Property(x => x.Remarks).HasMaxLength(2000);
        b.Property(x => x.SubmittedBy).HasMaxLength(256);
        b.Property(x => x.ConfirmedBy).HasMaxLength(256);
        b.Property(x => x.CancelledBy).HasMaxLength(256);
        b.Property(x => x.CancelReason).HasMaxLength(1000);
        b.Property(x => x.SubmittedAtUtc).HasPrecision(6);
        b.Property(x => x.ConfirmedAtUtc).HasPrecision(6);
        b.Property(x => x.CancelledAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Customers).WithOne().HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Properties).WithOne().HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Charges).WithOne().HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status });
        b.HasIndex(x => x.BookingDate);
    }
}

public class BookingCustomerConfiguration : IEntityTypeConfiguration<BookingCustomer>
{
    public void Configure(EntityTypeBuilder<BookingCustomer> b)
    {
        b.ToTable("BookingCustomers", t => t.HasCheckConstraint("CK_BookingCustomers_Share", "`SharePercent` > 0 AND `SharePercent` <= 100"));
        b.Audit();
        b.Property(x => x.SharePercent).HasColumnType("decimal(9,4)");
        b.Property(x => x.EffectiveFromUtc).HasPrecision(6);
        b.Property(x => x.EffectiveToUtc).HasPrecision(6);
        b.Property(x => x.EndReason).HasMaxLength(1000);
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BookingId, x.IsActive });
        b.HasIndex(x => x.CustomerId);
    }
}

public class BookingPropertyConfiguration : IEntityTypeConfiguration<BookingProperty>
{
    public void Configure(EntityTypeBuilder<BookingProperty> b)
    {
        b.ToTable("BookingProperties");
        b.Audit();
        b.Property(x => x.ExtraDiscount).HasColumnType("decimal(18,2)");
        b.Property(x => x.LinePrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.ExtraDiscountReason).HasMaxLength(1000);
        b.HasOne(x => x.Property).WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PricingSnapshot).WithOne().HasForeignKey<BookingPricingSnapshot>(s => s.BookingPropertyId).OnDelete(DeleteBehavior.Restrict);
        // Double-booking protection at the database: at most one live booking per property.
        b.HasIndex(x => x.ActivePropertyId).IsUnique();
        b.HasIndex(x => x.PropertyId);
        b.HasIndex(x => new { x.BookingId, x.PropertyId }).IsUnique();
    }
}

public class BookingPricingSnapshotConfiguration : IEntityTypeConfiguration<BookingPricingSnapshot>
{
    public void Configure(EntityTypeBuilder<BookingPricingSnapshot> b)
    {
        b.ToTable("BookingPricingSnapshots");
        b.Property(x => x.SnapshotJson).HasColumnType("longtext").IsRequired();
        b.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Unit).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Size).HasColumnType("decimal(18,4)");
        b.Property(x => x.BasePrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalPremiums).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalDiscounts).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalPricingCharges).HasColumnType("decimal(18,2)");
        b.Property(x => x.FinalPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.CapturedAtUtc).HasPrecision(6);
        b.HasIndex(x => x.BookingPropertyId).IsUnique();
    }
}

public class BookingChargeConfiguration : IEntityTypeConfiguration<BookingCharge>
{
    public void Configure(EntityTypeBuilder<BookingCharge> b)
    {
        b.ToTable("BookingCharges", t => t.HasCheckConstraint("CK_BookingCharges_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.ChargeType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
    }
}

public class BookingApprovalConfiguration : IEntityTypeConfiguration<BookingApproval>
{
    public void Configure(EntityTypeBuilder<BookingApproval> b)
    {
        b.ToTable("BookingApprovals");
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ApproverName).HasMaxLength(256).IsRequired();
        b.Property(x => x.Comments).HasMaxLength(1000);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId);
    }
}

public class BookingStatusHistoryConfiguration : IEntityTypeConfiguration<BookingStatusHistory>
{
    public void Configure(EntityTypeBuilder<BookingStatusHistory> b)
    {
        b.ToTable("BookingStatusHistories");
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.Property(x => x.ChangedAtUtc).HasPrecision(6);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BookingId, x.ChangedAtUtc });
    }
}

public class BookingRefundConfiguration : IEntityTypeConfiguration<BookingRefund>
{
    public void Configure(EntityTypeBuilder<BookingRefund> b)
    {
        b.ToTable("BookingRefunds", t => t.HasCheckConstraint("CK_BookingRefunds_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.DecidedBy).HasMaxLength(256);
        b.Property(x => x.DecisionComments).HasMaxLength(1000);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BookingId, x.Status });
    }
}

public class CustomerAgreementConfiguration : IEntityTypeConfiguration<CustomerAgreement>
{
    public void Configure(EntityTypeBuilder<CustomerAgreement> b)
    {
        b.ToTable("CustomerAgreements");
        b.Audit();
        b.Property(x => x.AgreementNumber).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(1000);
        b.Property(x => x.GeneratedAtUtc).HasPrecision(6);
        b.Property(x => x.CancelledAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Booking>().WithOne().HasForeignKey<CustomerAgreement>(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AgreementNumber).IsUnique();
        b.HasIndex(x => x.BookingId).IsUnique();
    }
}

public class AgreementVersionConfiguration : IEntityTypeConfiguration<AgreementVersion>
{
    public void Configure(EntityTypeBuilder<AgreementVersion> b)
    {
        b.ToTable("AgreementVersions");
        b.Audit();
        b.Property(x => x.Content).HasColumnType("longtext").IsRequired();
        b.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => new { x.AgreementId, x.VersionNo }).IsUnique();
    }
}

public class AgreementDocumentConfiguration : IEntityTypeConfiguration<AgreementDocument>
{
    public void Configure(EntityTypeBuilder<AgreementDocument> b)
    {
        b.ToTable("AgreementDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(30);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AgreementId);
    }
}

public class AgreementAmendmentConfiguration : IEntityTypeConfiguration<AgreementAmendment>
{
    public void Configure(EntityTypeBuilder<AgreementAmendment> b)
    {
        b.ToTable("AgreementAmendments");
        b.Audit();
        b.Property(x => x.AmendmentType).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Details).HasColumnType("longtext").IsRequired();
        b.Property(x => x.ApprovedBy).HasMaxLength(256).IsRequired();
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AgreementId, x.ToVersionNo }).IsUnique();
    }
}
