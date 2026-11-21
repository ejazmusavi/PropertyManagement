using RealEstate.Application.Auth;
using RealEstate.Application.Gateway;
using RealEstate.Application.Portal;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Tests;

public class Phase10ValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Fact]
    public void ProofSubmission_RequiresPositiveTwoDecimalAmount_AndIdempotencyKey()
    {
        var v = new SubmitPaymentProofRequestValidator();
        SubmitPaymentProofRequest R(decimal amount = 1000m, string key = "k1", long agreement = 1, long method = 2) => new(agreement, method, amount, Today, "REF", "HBL", null, null, key);
        Assert.True(v.Validate(R()).IsValid);
        Assert.False(v.Validate(R(0m)).IsValid);
        Assert.False(v.Validate(R(-1m)).IsValid);
        Assert.False(v.Validate(R(10.005m)).IsValid);
        Assert.False(v.Validate(R(key: "")).IsValid);
        Assert.False(v.Validate(R(agreement: 0)).IsValid);
        Assert.False(v.Validate(R(method: 0)).IsValid);
    }

    [Fact]
    public void OnlinePayment_RequiresAmountAndKey()
    {
        var v = new InitiateOnlinePaymentRequestValidator();
        Assert.True(v.Validate(new InitiateOnlinePaymentRequest(1, 500m, "k")).IsValid);
        Assert.False(v.Validate(new InitiateOnlinePaymentRequest(1, 0m, "k")).IsValid);
        Assert.False(v.Validate(new InitiateOnlinePaymentRequest(1, 500.123m, "k")).IsValid);
        Assert.False(v.Validate(new InitiateOnlinePaymentRequest(1, 500m, " ")).IsValid);
    }

    [Fact]
    public void SupportRequest_NeedsSubjectAndMessage()
    {
        var v = new CreateSupportRequestRequestValidator();
        Assert.True(v.Validate(new CreateSupportRequestRequest(SupportCategory.Payment, "Receipt", "Where is it?", null)).IsValid);
        Assert.False(v.Validate(new CreateSupportRequestRequest(SupportCategory.Payment, "", "Where is it?", null)).IsValid);
        Assert.False(v.Validate(new CreateSupportRequestRequest(SupportCategory.Payment, "Receipt", "", null)).IsValid);
        Assert.False(v.Validate(new CreateSupportRequestRequest((SupportCategory)99, "Receipt", "Where?", null)).IsValid);
        Assert.False(new SupportReplyRequestValidator().Validate(new SupportReplyRequest("")).IsValid);
    }

    [Fact]
    public void Profile_ValidatesMobileAndEmail()
    {
        var v = new UpdatePortalProfileRequestValidator();
        Assert.True(v.Validate(new UpdatePortalProfileRequest("0300-1234567", "a@b.com", "Street 1")).IsValid);
        Assert.True(v.Validate(new UpdatePortalProfileRequest("+92 300 1234567", null, null)).IsValid);
        Assert.False(v.Validate(new UpdatePortalProfileRequest("abc", null, null)).IsValid);
        Assert.False(v.Validate(new UpdatePortalProfileRequest("", null, null)).IsValid);
        Assert.False(v.Validate(new UpdatePortalProfileRequest("0300-1234567", "not-an-email", null)).IsValid);
    }

    [Fact]
    public void PasswordRules_MatchTheIdentityPolicy()
    {
        Assert.True(new ChangePasswordRequestDtoValidator().Validate(new ChangePasswordRequestDto("old", "Longer#Passw0rd1")).IsValid);
        Assert.False(new ChangePasswordRequestDtoValidator().Validate(new ChangePasswordRequestDto("old", "short")).IsValid);
        Assert.False(new CreateCustomerAccountRequestValidator().Validate(new CreateCustomerAccountRequest("a@b.com", "short")).IsValid);
        Assert.False(new CreateCustomerAccountRequestValidator().Validate(new CreateCustomerAccountRequest("bad", "Longer#Passw0rd1")).IsValid);
        Assert.True(new ForgotPasswordRequestValidator().Validate(new ForgotPasswordRequest("a@b.com")).IsValid);
        Assert.False(new ForgotPasswordRequestValidator().Validate(new ForgotPasswordRequest("nope")).IsValid);
        Assert.False(new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest("a@b.com", "", "Longer#Passw0rd1")).IsValid);
        Assert.False(new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest("a@b.com", "tok", "short")).IsValid);
        Assert.True(new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest("a@b.com", "tok", "Longer#Passw0rd1")).IsValid);
    }

    [Fact]
    public void AccountDisable_Announcement_AndRefund_RequireTheirFields()
    {
        Assert.False(new DisableCustomerAccountRequestValidator().Validate(new DisableCustomerAccountRequest("", null)).IsValid);
        Assert.True(new DisableCustomerAccountRequestValidator().Validate(new DisableCustomerAccountRequest("left", null)).IsValid);
        var a = new SaveAnnouncementRequestValidator();
        var now = DateTime.UtcNow;
        Assert.True(a.Validate(new SaveAnnouncementRequest("T", "B", null, now, now.AddDays(1), true, null)).IsValid);
        Assert.False(a.Validate(new SaveAnnouncementRequest("T", "B", null, now, now.AddDays(-1), true, null)).IsValid);
        Assert.False(a.Validate(new SaveAnnouncementRequest("", "B", null, null, null, true, null)).IsValid);
        var r = new GatewayRefundRequestDtoValidator();
        Assert.True(r.Validate(new GatewayRefundRequestDto(100m, "customer paid twice", null)).IsValid);
        Assert.False(r.Validate(new GatewayRefundRequestDto(0m, "customer paid twice", null)).IsValid);
        Assert.False(r.Validate(new GatewayRefundRequestDto(10.555m, "customer paid twice", null)).IsValid);
        Assert.False(r.Validate(new GatewayRefundRequestDto(10m, "no", null)).IsValid);
    }
}
