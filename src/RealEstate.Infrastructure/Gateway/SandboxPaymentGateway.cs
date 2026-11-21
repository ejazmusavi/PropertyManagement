using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RealEstate.Application.Gateway;

namespace RealEstate.Infrastructure.Gateway;

/// <summary>
/// Reads which providers are available and whether online payments are switched on (<c>PaymentGateway:Enabled</c>, off by default) and which one is active
/// (<c>PaymentGateway:ActiveProvider</c>). Real providers (JazzCash, Easypaisa, a bank) are added as further <see cref="IPaymentGateway"/> adapters.
/// </summary>
public sealed class PaymentGatewayRegistry(IConfiguration config, IEnumerable<IPaymentGateway> gateways) : IPaymentGatewayRegistry
{
    public bool Enabled => config.GetValue<bool>("PaymentGateway:Enabled");
    public string? ActiveProviderCode => config["PaymentGateway:ActiveProvider"]?.Trim().ToLowerInvariant();
    public IPaymentGateway? Find(string providerCode) => gateways.FirstOrDefault(g => string.Equals(g.ProviderCode, providerCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Development/test provider adapter without any real provider behind it. It exercises the whole abstraction: HMAC-SHA256 signature verification of callbacks
/// (header <c>X-Signature</c>, hex, secret <c>PaymentGateway:Sandbox:SigningSecret</c>), provider-side status held in memory (a validly signed callback plays the
/// provider's own state change), status queries and refunds. Without a secret every signature is invalid.
/// </summary>
public sealed class SandboxPaymentGateway(IConfiguration config) : IPaymentGateway
{
    private sealed record Entry(decimal Amount, string Currency, GatewayPaymentState State);
    private static readonly ConcurrentDictionary<string, Entry> Store = new();

    public string ProviderCode => "sandbox";
    public bool SupportsRefunds => true;

    public Task<GatewayInitiationResult> InitiateAsync(GatewayInitiationRequest request, CancellationToken ct = default)
    {
        var reference = "SBX-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        Store[reference] = new Entry(request.Amount, request.CurrencyCode, GatewayPaymentState.Pending);
        var checkout = config["PaymentGateway:Sandbox:CheckoutUrl"] ?? "https://sandbox.invalid/checkout";
        return Task.FromResult(new GatewayInitiationResult(true, reference, $"{checkout}?reference={reference}&order={request.TransactionNumber}", null, null));
    }

    public GatewayCallback ParseCallback(string payload, IReadOnlyDictionary<string, string> headers)
    {
        var invalid = new GatewayCallback(false, null, null, null, GatewayPaymentState.Unknown, null, null, null, null);
        var secret = config["PaymentGateway:Sandbox:SigningSecret"];
        if (string.IsNullOrEmpty(secret)) return invalid;
        var presented = headers.FirstOrDefault(h => string.Equals(h.Key, "X-Signature", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(presented) || !FixedTimeEquals(Sign(secret, payload), presented.Trim().ToLowerInvariant())) return invalid;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var state = S("status")?.ToLowerInvariant() switch
            {
                "succeeded" => GatewayPaymentState.Succeeded, "failed" => GatewayPaymentState.Failed, "cancelled" => GatewayPaymentState.Cancelled, "pending" => GatewayPaymentState.Pending,
                _ => GatewayPaymentState.Unknown,
            };
            decimal? amount = root.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var d) ? d : null;
            var reference = S("reference");
            if (reference is not null && Store.TryGetValue(reference, out var e) && state != GatewayPaymentState.Unknown)
                Store[reference] = e with { State = state, Amount = amount ?? e.Amount };
            return new GatewayCallback(true, S("eventId"), reference, S("order"), state, amount, S("currency"), S("failureCode"), S("failureReason"));
        }
        catch (JsonException) { return invalid; }
    }

    public Task<GatewayStatusResult> QueryStatusAsync(string providerReference, CancellationToken ct = default) =>
        Task.FromResult(Store.TryGetValue(providerReference, out var e) ? new GatewayStatusResult(true, e.State, e.Amount, e.Currency, null) : new GatewayStatusResult(false, GatewayPaymentState.Unknown, null, null, null));

    public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default) =>
        Task.FromResult(Store.ContainsKey(request.ProviderReference)
            ? new GatewayRefundResult(true, "SBX-RF-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), null)
            : new GatewayRefundResult(false, null, "Unknown transaction."));

    public static string Sign(string secret, string payload) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
