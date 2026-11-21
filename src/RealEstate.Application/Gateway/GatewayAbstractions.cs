using RealEstate.Domain.Enums;

namespace RealEstate.Application.Gateway;

// =====================================================================================================
// Payment provider abstraction (spec 10.4). Provider-specific code (JazzCash, Easypaisa, a bank...) is an adapter in Infrastructure implementing
// IPaymentGateway; nothing in the Domain or in these services knows a provider. Adding a provider means adding an adapter and configuration.
// =====================================================================================================

public enum GatewayPaymentState { Unknown = 0, Pending = 1, Succeeded = 2, Failed = 3, Cancelled = 4 }

public sealed record GatewayInitiationRequest(string TransactionNumber, decimal Amount, string CurrencyCode, string Description);

public sealed record GatewayInitiationResult(bool Accepted, string? ProviderReference, string? RedirectUrl, string? ErrorCode, string? ErrorMessage);

/// <summary>A parsed provider callback. <see cref="SignatureValid"/> is decided by the adapter; an invalid signature is never acted upon.</summary>
public sealed record GatewayCallback(bool SignatureValid, string? EventId, string? ProviderReference, string? TransactionNumber, GatewayPaymentState State, decimal? Amount,
    string? CurrencyCode, string? FailureCode, string? FailureReason);

/// <summary>The provider's own answer to a status query (used to verify callbacks independently and to reconcile).</summary>
public sealed record GatewayStatusResult(bool Found, GatewayPaymentState State, decimal? Amount, string? CurrencyCode, string? FailureReason);

public sealed record GatewayRefundRequest(string ProviderReference, decimal Amount, string Reason);

public sealed record GatewayRefundResult(bool Accepted, string? RefundReference, string? ErrorMessage);

public interface IPaymentGateway
{
    /// <summary>Stable lower-case code stored on transactions (for example "sandbox", later "jazzcash").</summary>
    string ProviderCode { get; }

    /// <summary>False when the provider does not allow refunds through its API.</summary>
    bool SupportsRefunds { get; }

    Task<GatewayInitiationResult> InitiateAsync(GatewayInitiationRequest request, CancellationToken ct = default);

    /// <summary>Verifies the signature of a raw callback and extracts its fields. Must not throw for malformed input: return SignatureValid = false.</summary>
    GatewayCallback ParseCallback(string payload, IReadOnlyDictionary<string, string> headers);

    Task<GatewayStatusResult> QueryStatusAsync(string providerReference, CancellationToken ct = default);

    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default);
}

/// <summary>Which providers exist and whether online payments are switched on (configuration; disabled by default).</summary>
public interface IPaymentGatewayRegistry
{
    bool Enabled { get; }
    string? ActiveProviderCode { get; }
    IPaymentGateway? Find(string providerCode);
}

public enum CallbackOutcome { Processed, Duplicate, InvalidSignature, UnknownProvider, UnknownTransaction, Ignored, Rejected }

public sealed record CallbackResult(CallbackOutcome Outcome, string? Message);

// ---------- DTOs ----------
public sealed record GatewayTransactionDto(
    long Id, string TransactionNumber, string Provider, string? ProviderReference, long CustomerId, string CustomerName, long AgreementId, string AgreementNumber, decimal Amount,
    string CurrencyCode, GatewayTransactionStatus Status, string? FailureCode, string? FailureReason, DateTime InitiatedAtUtc, DateTime? CompletedAtUtc, long? PaymentId, string? PaymentNumber,
    PaymentStatus? PaymentStatus, decimal RefundedAmount, string? RefundReference, DateTime? LastReconciledAtUtc, string? ReconciliationNote, string RowVersion);

public sealed record WebhookLogDto(long Id, string Provider, string? EventId, long? GatewayTransactionId, string? ProviderReference, bool SignatureValid, DateTime ReceivedAtUtc,
    WebhookProcessingStatus ProcessingStatus, string? ProcessingMessage, string? SourceIp);

public sealed record ReconciliationItemDto(long TransactionId, string TransactionNumber, GatewayTransactionStatus Before, GatewayTransactionStatus After, string Note);

public sealed record ReconciliationResultDto(int Checked, int Changed, IReadOnlyList<ReconciliationItemDto> Items);

public sealed record GatewayRefundRequestDto(decimal Amount, string Reason, string? RowVersion);

public sealed class GatewayListRequest : Common.PagedRequest
{
    public GatewayTransactionStatus? Status { get; set; }
    public long? CustomerId { get; set; }
    public long? AgreementId { get; set; }
}

public sealed class WebhookLogListRequest : Common.PagedRequest
{
    public WebhookProcessingStatus? Status { get; set; }
    public string? Provider { get; set; }
}
