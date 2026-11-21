using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

/// <summary>
/// Links one login (Identity user with the Customer role) to one customer. The customer application derives the customer only from this link;
/// no customer or project id sent by the mobile app is ever trusted. Rows are never deleted: an account is disabled with a reason.
/// </summary>
public class CustomerAccount : AuditableEntity<long>
{
    public Guid UserId { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public bool IsActive { get; set; } = true;
    public string? DisabledReason { get; set; }
    public DateTime? DisabledAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A file (bank slip, screenshot) attached by the customer to a payment submission.</summary>
public class PaymentProofDocument : StoredDocument
{
    public long PaymentId { get; set; }
    public long CustomerId { get; set; }
}

public class Announcement : AuditableEntity<long>
{
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    /// <summary>Null = company-wide; otherwise only customers with an active agreement in the project see it.</summary>
    public long? ProjectId { get; set; }
    public DateTime PublishFromUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>In-app notification of one customer (the minimal Phase 10 approach; channels such as SMS/e-mail/push are Phase 11).</summary>
public class CustomerNotification : AuditableEntity<long>
{
    public long CustomerId { get; set; }
    public NotificationKind Kind { get; set; }
    public string Title { get; set; } = default!;
    public string Message { get; set; } = default!;
    public string? RelatedType { get; set; }
    public long? RelatedId { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}

public class SupportRequest : AuditableEntity<long>
{
    public string RequestNumber { get; set; } = default!;
    public long CustomerId { get; set; }
    public long? AgreementId { get; set; }
    public SupportCategory Category { get; set; }
    public string Subject { get; set; } = default!;
    public SupportRequestStatus Status { get; set; } = SupportRequestStatus.Open;
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<SupportRequestMessage> Messages { get; set; } = [];
}

/// <summary>Append-only conversation of a support request.</summary>
public class SupportRequestMessage : Entity<long>
{
    public long SupportRequestId { get; set; }
    public SupportAuthorType AuthorType { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = default!;
    public string Body { get; set; } = default!;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// One online payment attempt through a provider. Provider specifics live only in the provider adapter (<c>IPaymentGateway</c>); this row is provider-neutral.
/// A succeeded transaction creates exactly one pending-approval Payment (<see cref="PaymentId"/>), never a posted one.
/// </summary>
public class GatewayTransaction : AuditableEntity<long>
{
    public string TransactionNumber { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public string Provider { get; set; } = default!;
    /// <summary>The provider's own id for the transaction (unique per provider once known).</summary>
    public string? ProviderReference { get; set; }
    public long CustomerId { get; set; }
    public long AgreementId { get; set; }
    public long ProjectId { get; set; }
    public Guid InitiatedByUserId { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public GatewayTransactionStatus Status { get; private set; } = GatewayTransactionStatus.Initiated;
    public string? RedirectUrl { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public DateTime InitiatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>The Payment created from a verified success (null until then).</summary>
    public long? PaymentId { get; set; }
    public decimal RefundedAmount { get; set; }
    public string? RefundReference { get; set; }
    public DateTime? LastReconciledAtUtc { get; set; }
    public string? ReconciliationNote { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public static bool CanTransition(GatewayTransactionStatus from, GatewayTransactionStatus to) => (from, to) switch
    {
        (GatewayTransactionStatus.Initiated, GatewayTransactionStatus.Pending) => true,
        (GatewayTransactionStatus.Initiated, GatewayTransactionStatus.Failed) => true,
        (GatewayTransactionStatus.Initiated, GatewayTransactionStatus.Cancelled) => true,
        (GatewayTransactionStatus.Initiated, GatewayTransactionStatus.Succeeded) => true,
        (GatewayTransactionStatus.Pending, GatewayTransactionStatus.Succeeded) => true,
        (GatewayTransactionStatus.Pending, GatewayTransactionStatus.Failed) => true,
        (GatewayTransactionStatus.Pending, GatewayTransactionStatus.Cancelled) => true,
        (GatewayTransactionStatus.Succeeded, GatewayTransactionStatus.Refunded) => true,
        _ => false,
    };

    public void Move(GatewayTransactionStatus to, DateTime nowUtc)
    {
        if (Status == to) return;
        if (!CanTransition(Status, to)) throw new DomainException($"A gateway transaction cannot move from {Status} to {to}.");
        Status = to;
        if (to is GatewayTransactionStatus.Succeeded or GatewayTransactionStatus.Failed or GatewayTransactionStatus.Cancelled) CompletedAtUtc = nowUtc;
    }
}

/// <summary>Every callback received from a provider, stored before it is processed (audit trail and replay protection by provider event id).</summary>
public class PaymentWebhookLog : Entity<long>
{
    public string Provider { get; set; } = default!;
    /// <summary>The provider's unique event id; a repeated delivery is recognised by (Provider, EventId).</summary>
    public string? EventId { get; set; }
    public long? GatewayTransactionId { get; set; }
    public string? ProviderReference { get; set; }
    public bool SignatureValid { get; set; }
    public string Payload { get; set; } = default!;
    public string? SourceIp { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public WebhookProcessingStatus ProcessingStatus { get; set; } = WebhookProcessingStatus.Received;
    public string? ProcessingMessage { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
