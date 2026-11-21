using FluentValidation;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;

namespace RealEstate.Application.Auth;

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

/// <summary>Delivers a password reset code to the user. Real delivery (e-mail/SMS) is a Phase 11 channel; Infrastructure supplies the implementation.</summary>
public interface IPasswordResetNotifier
{
    Task SendAsync(PasswordResetToken token, CancellationToken ct = default);
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(12).MaximumLength(128);
    }
}

/// <summary>
/// Forgot / reset password for every login (staff and customers). The request never reveals whether the e-mail exists. A successful reset revokes all
/// refresh tokens of the user so existing sessions end.
/// </summary>
public class PasswordResetService(IIdentityService identity, IRefreshTokenService refreshTokens, IPasswordResetNotifier notifier, IAuditService audit,
    IValidator<ForgotPasswordRequest> forgotValidator, IValidator<ResetPasswordRequest> resetValidator)
{
    public async Task RequestAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        await forgotValidator.ValidateOrThrowAsync(request, ct);
        var token = await identity.GeneratePasswordResetTokenAsync(request.Email.Trim(), ct);
        if (token is null)
        {
            await audit.RecordAsync(new AuditEntry("Password.ResetRequested", "User", null, NewValues: new { Known = false }, UserName: request.Email.Trim()), ct);
            return;
        }
        await notifier.SendAsync(token, ct);
        await audit.RecordAsync(new AuditEntry("Password.ResetRequested", "User", token.UserId.ToString(), NewValues: new { Known = true }, UserId: token.UserId, UserName: token.Email), ct);
    }

    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        await resetValidator.ValidateOrThrowAsync(request, ct);
        var errors = await identity.ResetPasswordAsync(request.Email.Trim(), request.Token.Trim(), request.NewPassword, ct);
        if (errors.Count > 0)
        {
            await audit.RecordAsync(new AuditEntry("Password.ResetFailed", "User", null, UserName: request.Email.Trim()), ct);
            throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(request.NewPassword)] = [.. errors] });
        }
        var user = await identity.FindByEmailAsync(request.Email.Trim(), ct);
        if (user is not null)
        {
            await refreshTokens.RevokeAllAsync(user.Id, ct);
            await audit.RecordAsync(new AuditEntry("Password.Reset", "User", user.Id.ToString(), UserId: user.Id, UserName: user.Email), ct);
        }
    }
}
