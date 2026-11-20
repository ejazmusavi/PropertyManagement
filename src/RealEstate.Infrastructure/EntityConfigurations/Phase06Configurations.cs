using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> b)
    {
        b.ToTable("PaymentMethods");
        b.Audit();
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(25);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("Payments", t => t.HasCheckConstraint("CK_Payments_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.PaymentNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.MethodKind).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.BankName).HasMaxLength(150);
        b.Property(x => x.PayerName).HasMaxLength(200);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.ActiveReferenceKey).HasMaxLength(150);
        b.Property(x => x.RequestedAllocationMode).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.RequestedAllocationJson).HasColumnType("longtext");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.SubmittedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.SubmittedAtUtc).HasPrecision(6);
        b.Property(x => x.ApprovedBy).HasMaxLength(256);
        b.Property(x => x.ApprovedAtUtc).HasPrecision(6);
        b.Property(x => x.PostedAtUtc).HasPrecision(6);
        b.Property(x => x.RejectedBy).HasMaxLength(256);
        b.Property(x => x.RejectedAtUtc).HasPrecision(6);
        b.Property(x => x.RejectReason).HasMaxLength(1000);
        b.Property(x => x.ReversedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.PaymentMethod).WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => x.ActiveReferenceKey).IsUnique();
        b.HasIndex(x => new { x.Status, x.PaymentDate });
        b.HasIndex(x => x.Reference);
    }
}

public class PaymentReceiptConfiguration : IEntityTypeConfiguration<PaymentReceipt>
{
    public void Configure(EntityTypeBuilder<PaymentReceipt> b)
    {
        b.ToTable("PaymentReceipts");
        b.Audit();
        b.Property(x => x.ReceiptNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.IssuedAtUtc).HasPrecision(6);
        b.Property(x => x.IssuedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.LastPrintedAtUtc).HasPrecision(6);
        b.Property(x => x.LastPrintedBy).HasMaxLength(256);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentId).IsUnique();
        b.HasIndex(x => x.ReceiptNumber).IsUnique();
    }
}

public class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> b)
    {
        b.ToTable("PaymentAllocations", t => t.HasCheckConstraint("CK_PaymentAllocations_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AllocatedAtUtc).HasPrecision(6);
        b.Property(x => x.AllocatedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.ClosedAtUtc).HasPrecision(6);
        b.Property(x => x.ClosedBy).HasMaxLength(256);
        b.Property(x => x.CloseReason).HasMaxLength(500);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentScheduleItem>().WithMany().HasForeignKey(x => x.ScheduleItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementCharge>().WithMany().HasForeignKey(x => x.AgreementChargeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentReversal>().WithMany().HasForeignKey(x => x.ReversalId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PaymentId, x.Status });
        b.HasIndex(x => new { x.AgreementId, x.Status });
    }
}

public class PaymentVerificationConfiguration : IEntityTypeConfiguration<PaymentVerification>
{
    public void Configure(EntityTypeBuilder<PaymentVerification> b)
    {
        b.ToTable("PaymentVerifications");
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.VerifiedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.VerifiedAtUtc).HasPrecision(6);
        b.Property(x => x.Notes).HasMaxLength(1000).IsRequired();
        b.Property(x => x.BankReference).HasMaxLength(100);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PaymentReversalConfiguration : IEntityTypeConfiguration<PaymentReversal>
{
    public void Configure(EntityTypeBuilder<PaymentReversal> b)
    {
        b.ToTable("PaymentReversals");
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ReversedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.ReversedAtUtc).HasPrecision(6);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentId).IsUnique();
    }
}

public class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("Refunds", t => t.HasCheckConstraint("CK_Refunds_Amount", "`Amount` > 0"));
        b.Audit();
        b.Property(x => x.RefundNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.RequestedAtUtc).HasPrecision(6);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.PaidBy).HasMaxLength(256);
        b.Property(x => x.PaidAtUtc).HasPrecision(6);
        b.Property(x => x.CancelledBy).HasMaxLength(256);
        b.Property(x => x.CancelledAtUtc).HasPrecision(6);
        b.Property(x => x.CancelReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BookingRefund>().WithMany().HasForeignKey(x => x.BookingRefundId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RefundNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => x.Status);
    }
}

public class RefundApprovalConfiguration : IEntityTypeConfiguration<RefundApproval>
{
    public void Configure(EntityTypeBuilder<RefundApproval> b)
    {
        b.ToTable("RefundApprovals");
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ApproverName).HasMaxLength(256).IsRequired();
        b.Property(x => x.Comments).HasMaxLength(1000);
        b.Property(x => x.DecidedAtUtc).HasPrecision(6);
        b.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AgreementLedgerEntryConfiguration : IEntityTypeConfiguration<AgreementLedgerEntry>
{
    public void Configure(EntityTypeBuilder<AgreementLedgerEntry> b)
    {
        b.ToTable("AgreementLedgerEntries", t => t.HasCheckConstraint("CK_AgreementLedgerEntries_Side",
            "`Debit` >= 0 AND `Credit` >= 0 AND ((`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0))"));
        b.Property(x => x.PostedAtUtc).HasPrecision(6);
        b.Property(x => x.PostedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Debit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.UniqueKey).HasMaxLength(80);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UniqueKey).IsUnique();
        b.HasIndex(x => new { x.AgreementId, x.EntryDate });
        b.HasIndex(x => new { x.AgreementId, x.ScheduleItemRootId });
    }
}

public class CustomerLedgerEntryConfiguration : IEntityTypeConfiguration<CustomerLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CustomerLedgerEntry> b)
    {
        b.ToTable("CustomerLedgerEntries", t => t.HasCheckConstraint("CK_CustomerLedgerEntries_Side",
            "`Debit` >= 0 AND `Credit` >= 0 AND ((`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0))"));
        b.Property(x => x.PostedAtUtc).HasPrecision(6);
        b.Property(x => x.PostedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.Debit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Credit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgreementLedgerEntry>().WithMany().HasForeignKey(x => x.AgreementLedgerEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AgreementLedgerEntryId).IsUnique();
        b.HasIndex(x => new { x.CustomerId, x.EntryDate });
        b.HasIndex(x => new { x.CustomerId, x.AgreementId });
    }
}

public class AccountingPostingRequestConfiguration : IEntityTypeConfiguration<AccountingPostingRequest>
{
    public void Configure(EntityTypeBuilder<AccountingPostingRequest> b)
    {
        b.ToTable("AccountingPostingRequests");
        b.Property(x => x.EventType).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PayloadJson).HasColumnType("longtext").IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
        b.Property(x => x.ProcessedAtUtc).HasPrecision(6);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EventType, x.SourceType, x.SourceId }).IsUnique();
        b.HasIndex(x => x.Status);
    }
}

public class RecoveryAssignmentConfiguration : IEntityTypeConfiguration<RecoveryAssignment>
{
    public void Configure(EntityTypeBuilder<RecoveryAssignment> b)
    {
        b.ToTable("RecoveryAssignments");
        b.Audit();
        b.Property(x => x.OfficerName).HasMaxLength(256).IsRequired();
        b.Property(x => x.AssignedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.OverdueAtAssignment).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ClosedBy).HasMaxLength(256);
        b.Property(x => x.CloseReason).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ActiveAgreementId).IsUnique();
        b.HasIndex(x => new { x.OfficerUserId, x.Status });
    }
}

public class CustomerFollowUpConfiguration : IEntityTypeConfiguration<CustomerFollowUp>
{
    public void Configure(EntityTypeBuilder<CustomerFollowUp> b)
    {
        b.ToTable("CustomerFollowUps", t => t.HasCheckConstraint("CK_CustomerFollowUps_Promise", "`PromisedAmount` IS NULL OR `PromisedAmount` > 0"));
        b.Audit();
        b.Property(x => x.OfficerName).HasMaxLength(256).IsRequired();
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(25);
        b.Property(x => x.PromisedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.OverdueAtFollowUp).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(2000).IsRequired();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RecoveryAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AgreementId, x.FollowUpDate });
        b.HasIndex(x => new { x.OfficerUserId, x.FollowUpDate });
    }
}
