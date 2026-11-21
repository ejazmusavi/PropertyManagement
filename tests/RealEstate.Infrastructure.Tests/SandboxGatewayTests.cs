using Microsoft.Extensions.Configuration;
using RealEstate.Application.Gateway;
using RealEstate.Infrastructure.Gateway;

namespace RealEstate.Infrastructure.Tests;

/// <summary>The sandbox provider adapter: signature verification, status, refunds, and the registry configuration (no database needed).</summary>
public class SandboxGatewayTests
{
    private const string Secret = "sandbox-secret-for-tests";

    private static SandboxPaymentGateway Gateway(string? secret = Secret) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["PaymentGateway:Sandbox:SigningSecret"] = secret }).Build());

    private static Dictionary<string, string> Sig(string payload, string secret = Secret) => new() { ["x-signature"] = SandboxPaymentGateway.Sign(secret, payload) };

    [Fact]
    public async Task Callback_WithValidSignature_IsParsed_AndTheProviderStateFollowsIt()
    {
        var g = Gateway();
        var init = await g.InitiateAsync(new GatewayInitiationRequest("GW-000001", 1500m, "PKR", "test"));
        Assert.True(init.Accepted);
        Assert.StartsWith("SBX-", init.ProviderReference);
        Assert.Equal(GatewayPaymentState.Pending, (await g.QueryStatusAsync(init.ProviderReference!)).State);

        var payload = $"{{\"eventId\":\"e1\",\"reference\":\"{init.ProviderReference}\",\"order\":\"GW-000001\",\"status\":\"succeeded\",\"amount\":1500,\"currency\":\"PKR\"}}";
        var cb = g.ParseCallback(payload, Sig(payload));
        Assert.True(cb.SignatureValid);
        Assert.Equal("e1", cb.EventId);
        Assert.Equal(GatewayPaymentState.Succeeded, cb.State);
        Assert.Equal(1500m, cb.Amount);
        var status = await g.QueryStatusAsync(init.ProviderReference!);
        Assert.Equal(GatewayPaymentState.Succeeded, status.State);
        Assert.Equal(1500m, status.Amount);
    }

    [Fact]
    public void Callback_WithBadOrMissingSignature_IsNeverTrusted()
    {
        var payload = "{\"eventId\":\"e2\",\"reference\":\"X\",\"status\":\"succeeded\"}";
        Assert.False(Gateway().ParseCallback(payload, new Dictionary<string, string>()).SignatureValid);
        Assert.False(Gateway().ParseCallback(payload, Sig(payload, "another-secret")).SignatureValid);
        Assert.False(Gateway().ParseCallback(payload + " ", Sig(payload)).SignatureValid); // tampered body
        Assert.False(Gateway(secret: null).ParseCallback(payload, Sig(payload)).SignatureValid); // no secret configured
        Assert.False(Gateway().ParseCallback("not json", Sig("not json")).SignatureValid);
    }

    [Fact]
    public async Task Refund_IsAccepted_OnlyForKnownTransactions()
    {
        var g = Gateway();
        Assert.True(g.SupportsRefunds);
        var init = await g.InitiateAsync(new GatewayInitiationRequest("GW-000002", 10m, "PKR", "t"));
        Assert.True((await g.RefundAsync(new GatewayRefundRequest(init.ProviderReference!, 10m, "x"))).Accepted);
        Assert.False((await g.RefundAsync(new GatewayRefundRequest("UNKNOWN", 10m, "x"))).Accepted);
        Assert.False((await g.QueryStatusAsync("UNKNOWN")).Found);
    }

    [Fact]
    public void Registry_IsDisabledByDefault_AndFindsProvidersByCode()
    {
        var off = new PaymentGatewayRegistry(new ConfigurationBuilder().Build(), [Gateway()]);
        Assert.False(off.Enabled);
        Assert.Null(off.ActiveProviderCode);
        Assert.NotNull(off.Find("SANDBOX"));
        Assert.Null(off.Find("jazzcash"));
        var on = new PaymentGatewayRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PaymentGateway:Enabled"] = "true", ["PaymentGateway:ActiveProvider"] = "Sandbox" }).Build(), [Gateway()]);
        Assert.True(on.Enabled);
        Assert.Equal("sandbox", on.ActiveProviderCode);
    }
}
