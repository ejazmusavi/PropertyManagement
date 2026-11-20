using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class ExpenseCategoryConfiguration : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> b)
    {
        b.ToTable("ExpenseCategories");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b)
    {
        b.ToTable("Vendors");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContactPerson).HasMaxLength(150);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.TaxNumber).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name);
    }
}

public class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.ToTable("Expenses", t =>
        {
            t.HasCheckConstraint("CK_Expenses_Amount", "`Amount` > 0");
            t.HasCheckConstraint("CK_Expenses_Paid", "`AmountPaid` >= 0 AND `AmountPaid` <= `Amount`");
        });
        b.Audit();
        b.Ignore(x => x.Outstanding);
        b.Ignore(x => x.PaymentStatus);
        b.Property(x => x.ExpenseNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.AmountPaid).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SubmittedBy).HasMaxLength(256);
        b.Property(x => x.SubmittedAtUtc).HasPrecision(6);
        b.Property(x => x.ApprovedBy).HasMaxLength(256);
        b.Property(x => x.ApprovedAtUtc).HasPrecision(6);
        b.Property(x => x.CancelledBy).HasMaxLength(256);
        b.Property(x => x.CancelledAtUtc).HasPrecision(6);
        b.Property(x => x.CancelReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ExpenseNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status, x.ExpenseDate });
        b.HasIndex(x => new { x.VendorId, x.Status });
        b.HasIndex(x => x.CategoryId);
    }
}

public class ExpenseApprovalConfiguration : IEntityTypeConfiguration<ExpenseApproval>
{
    public void Configure(EntityTypeBuilder<ExpenseApproval> b)
    {
        b.ToTable("ExpenseApprovals");
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ApproverName).HasMaxLength(256).IsRequired();
        b.Property(x => x.Comments).HasMaxLength(1000);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.HasOne<Expense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ExpenseId);
    }
}

public class ExpenseAttachmentConfiguration : IEntityTypeConfiguration<ExpenseAttachment>
{
    public void Configure(EntityTypeBuilder<ExpenseAttachment> b)
    {
        b.ToTable("ExpenseAttachments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Expense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ExpenseId);
    }
}

public class VendorInvoiceConfiguration : IEntityTypeConfiguration<VendorInvoice>
{
    public void Configure(EntityTypeBuilder<VendorInvoice> b)
    {
        b.ToTable("VendorInvoices", t => t.HasCheckConstraint("CK_VendorInvoices_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.InvoiceNumber).HasMaxLength(60).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ActiveKey).HasMaxLength(120);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Expense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ExpenseId).IsUnique();
        b.HasIndex(x => x.ActiveKey).IsUnique();
        b.HasIndex(x => new { x.VendorId, x.DueDate });
    }
}

public class VendorPaymentConfiguration : IEntityTypeConfiguration<VendorPayment>
{
    public void Configure(EntityTypeBuilder<VendorPayment> b)
    {
        b.ToTable("VendorPayments", t => t.HasCheckConstraint("CK_VendorPayments_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.PaymentNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReversedBy).HasMaxLength(256);
        b.Property(x => x.ReversedAtUtc).HasPrecision(6);
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Expense>().WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.ExpenseId, x.Status });
        b.HasIndex(x => new { x.VendorId, x.PaymentDate });
    }
}

public class GovernmentFeeTypeConfiguration : IEntityTypeConfiguration<GovernmentFeeType>
{
    public void Configure(EntityTypeBuilder<GovernmentFeeType> b)
    {
        b.ToTable("GovernmentFeeTypes");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.DefaultBearer).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.FixedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Rate).HasColumnType("decimal(18,4)");
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class GovernmentFeeAssessmentConfiguration : IEntityTypeConfiguration<GovernmentFeeAssessment>
{
    public void Configure(EntityTypeBuilder<GovernmentFeeAssessment> b)
    {
        b.ToTable("GovernmentFeeAssessments", t =>
        {
            t.HasCheckConstraint("CK_GovFeeAssessments_Split", "`Amount` > 0 AND `CustomerLiability` >= 0 AND `CompanyBorne` >= 0 AND `CustomerLiability` + `CompanyBorne` = `Amount`");
            t.HasCheckConstraint("CK_GovFeeAssessments_Progress", "`CollectedAmount` >= 0 AND `CollectedAmount` <= `CustomerLiability` AND `PaidAmount` >= 0 AND `PaidAmount` <= `Amount`");
        });
        b.Audit();
        b.Ignore(x => x.CustomerOutstanding);
        b.Ignore(x => x.GovernmentPayable);
        b.Property(x => x.AssessmentNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.BasisAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CustomerLiability).HasColumnType("decimal(18,2)");
        b.Property(x => x.CompanyBorne).HasColumnType("decimal(18,2)");
        b.Property(x => x.CollectedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelledBy).HasMaxLength(256);
        b.Property(x => x.CancelledAtUtc).HasPrecision(6);
        b.Property(x => x.CancelReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.FeeType).WithMany().HasForeignKey(x => x.FeeTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AssessmentNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.ProjectId, x.Status });
        b.HasIndex(x => x.AgreementId);
        b.HasIndex(x => x.CustomerId);
    }
}

public class GovernmentFeeCollectionConfiguration : IEntityTypeConfiguration<GovernmentFeeCollection>
{
    public void Configure(EntityTypeBuilder<GovernmentFeeCollection> b)
    {
        b.ToTable("GovernmentFeeCollections", t => t.HasCheckConstraint("CK_GovFeeCollections_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.CollectionNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReversedBy).HasMaxLength(256);
        b.Property(x => x.ReversedAtUtc).HasPrecision(6);
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.HasOne<GovernmentFeeAssessment>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CollectionNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.AssessmentId, x.Status });
    }
}

public class GovernmentFeePaymentConfiguration : IEntityTypeConfiguration<GovernmentFeePayment>
{
    public void Configure(EntityTypeBuilder<GovernmentFeePayment> b)
    {
        b.ToTable("GovernmentFeePayments", t => t.HasCheckConstraint("CK_GovFeePayments_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.PaymentNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReversedBy).HasMaxLength(256);
        b.Property(x => x.ReversedAtUtc).HasPrecision(6);
        b.Property(x => x.ReversalReason).HasMaxLength(1000);
        b.HasOne<GovernmentFeeAssessment>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.AssessmentId, x.Status });
    }
}

public class GovernmentFeeDocumentConfiguration : IEntityTypeConfiguration<GovernmentFeeDocument>
{
    public void Configure(EntityTypeBuilder<GovernmentFeeDocument> b)
    {
        b.ToTable("GovernmentFeeDocuments");
        b.Document();
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(25);
        b.HasOne<GovernmentFeeAssessment>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AssessmentId);
    }
}
