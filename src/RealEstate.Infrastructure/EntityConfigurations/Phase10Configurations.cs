using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure.EntityConfigurations;

public class CustomerAccountConfiguration : IEntityTypeConfiguration<CustomerAccount>
{
    public void Configure(EntityTypeBuilder<CustomerAccount> b)
    {
        b.ToTable("CustomerAccounts");
        b.Audit();
        b.Property(x => x.DisabledReason).HasMaxLength(500);
        b.Property(x => x.DisabledAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        // One login per customer and one customer per login: the customer is derived from the login, never from client input.
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasIndex(x => x.CustomerId).IsUnique();
    }
}

public class PaymentProofDocumentConfiguration : IEntityTypeConfiguration<PaymentProofDocument>
{
    public void Configure(EntityTypeBuilder<PaymentProofDocument> b)
    {
        b.ToTable("PaymentProofDocuments");
        b.Document();
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PaymentId);
    }
}

public class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("Announcements");
        b.Audit();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.PublishFromUtc).HasPrecision(6);
        b.Property(x => x.ExpiresAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IsActive, x.PublishFromUtc });
    }
}

public class CustomerNotificationConfiguration : IEntityTypeConfiguration<CustomerNotification>
{
    public void Configure(EntityTypeBuilder<CustomerNotification> b)
    {
        b.ToTable("CustomerNotifications");
        b.Audit();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        b.Property(x => x.RelatedType).HasMaxLength(40);
        b.Property(x => x.ReadAtUtc).HasPrecision(6);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CustomerId, x.ReadAtUtc });
    }
}

public class SupportRequestConfiguration : IEntityTypeConfiguration<SupportRequest>
{
    public void Configure(EntityTypeBuilder<SupportRequest> b)
    {
        b.ToTable("SupportRequests");
        b.Audit();
        b.Property(x => x.RequestNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AssignedTo).HasMaxLength(256);
        b.Property(x => x.LastActivityAtUtc).HasPrecision(6);
        b.Property(x => x.ResolvedAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.SupportRequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestNumber).IsUnique();
        b.HasIndex(x => new { x.CustomerId, x.LastActivityAtUtc });
        b.HasIndex(x => x.Status);
    }
}

public class SupportRequestMessageConfiguration : IEntityTypeConfiguration<SupportRequestMessage>
{
    public void Configure(EntityTypeBuilder<SupportRequestMessage> b)
    {
        b.ToTable("SupportRequestMessages");
        b.Property(x => x.AuthorType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AuthorName).HasMaxLength(256).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasPrecision(6);
    }
}

public class GatewayTransactionConfiguration : IEntityTypeConfiguration<GatewayTransaction>
{
    public void Configure(EntityTypeBuilder<GatewayTransaction> b)
    {
        b.ToTable("GatewayTransactions", t =>
        {
            t.HasCheckConstraint("CK_GatewayTransactions_Amount", "`Amount` > 0");
            t.HasCheckConstraint("CK_GatewayTransactions_Refund", "`RefundedAmount` >= 0 AND `RefundedAmount` <= `Amount`");
        });
        b.Audit();
        b.Property(x => x.TransactionNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        b.Property(x => x.ProviderReference).HasMaxLength(100);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.RefundedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RedirectUrl).HasMaxLength(1000);
        b.Property(x => x.FailureCode).HasMaxLength(50);
        b.Property(x => x.FailureReason).HasMaxLength(500);
        b.Property(x => x.RefundReference).HasMaxLength(100);
        b.Property(x => x.ReconciliationNote).HasMaxLength(500);
        b.Property(x => x.InitiatedAtUtc).HasPrecision(6);
        b.Property(x => x.CompletedAtUtc).HasPrecision(6);
        b.Property(x => x.LastReconciledAtUtc).HasPrecision(6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CustomerAgreement>().WithMany().HasForeignKey(x => x.AgreementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TransactionNumber).IsUnique();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.Provider, x.ProviderReference }).IsUnique();
        b.HasIndex(x => x.PaymentId).IsUnique();
        b.HasIndex(x => new { x.Status, x.InitiatedAtUtc });
    }
}

public class PaymentWebhookLogConfiguration : IEntityTypeConfiguration<PaymentWebhookLog>
{
    public void Configure(EntityTypeBuilder<PaymentWebhookLog> b)
    {
        b.ToTable("PaymentWebhookLogs");
        b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        b.Property(x => x.EventId).HasMaxLength(100);
        b.Property(x => x.ProviderReference).HasMaxLength(100);
        b.Property(x => x.Payload).HasColumnType("longtext").IsRequired();
        b.Property(x => x.SourceIp).HasMaxLength(64);
        b.Property(x => x.ReceivedAtUtc).HasPrecision(6);
        b.Property(x => x.ProcessedAtUtc).HasPrecision(6);
        b.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ProcessingMessage).HasMaxLength(500);
        b.HasOne<GatewayTransaction>().WithMany().HasForeignKey(x => x.GatewayTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        b.HasIndex(x => x.ReceivedAtUtc);
    }
}
