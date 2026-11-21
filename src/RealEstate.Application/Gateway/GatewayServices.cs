using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Portal;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Gateway;

/// <summary>
/// Starts an online payment through the active provider. The customer is already authorized by the caller (customer application service). A retry with the same
/// idempotency key returns the original transaction; the same key with other values is rejected. The transaction row is committed before the provider is called,
/// so a crash leaves an Initiated row that reconciliation can close.
/// </summary>
public class PaymentInitiationService(IAppDbContext db, IPaymentGatewayRegistry registry, PaymentSupport support, IAuditService audit)
{
    public async Task<GatewayTransaction> InitiateAsync(long customerId, Guid userId, long agreementId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        var provider = registry.Enabled && registry.ActiveProviderCode is { } code ? registry.Find(code) : null;
        if (provider is null) throw new DomainException("Online payments are not available at the moment.");
        var key = idempotencyKey.Trim();
        var hash = PaymentSupport.Sha256(string.Join('|', customerId, agreementId, amount.ToString("F2", CultureInfo.InvariantCulture), provider.ProviderCode));

        var existing = await db.GatewayTransactions.FirstOrDefaultAsync(t => t.IdempotencyKey == key, ct);
        if (existing is not null) return Replay(existing, customerId, hash);

        var ag = await support.InfoAsync(agreementId, ct);
        if (ag.Status != AgreementStatus.Active) throw Invalid(nameof(agreementId), "Payments can be made only against active agreements.");
        var items = await support.ActiveItemsAsync(agreementId, tracked: false, ct);
        var outstanding = items.Sum(i => i.Outstanding);
        if (amount > outstanding) throw Invalid("Amount", $"The amount exceeds the outstanding balance of the agreement ({outstanding:N2}).");

        var now = support.Now;
        var t = new GatewayTransaction
        {
            TransactionNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, RequestHash = hash, Provider = provider.ProviderCode, CustomerId = customerId,
            AgreementId = agreementId, ProjectId = ag.ProjectId, InitiatedByUserId = userId, Amount = amount, CurrencyCode = await support.CurrencyCodeAsync(ct), InitiatedAtUtc = now,
        };
        db.GatewayTransactions.Add(t);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex))
        {
            db.Entry(t).State = EntityState.Detached;
            var raced = await db.GatewayTransactions.FirstOrDefaultAsync(x => x.IdempotencyKey == key, ct) ?? throw new ConcurrencyException();
            return Replay(raced, customerId, hash);
        }
        t.TransactionNumber = $"GW-{t.Id:D6}";
        await db.SaveChangesAsync(ct);

        GatewayInitiationResult result;
        try { result = await provider.InitiateAsync(new GatewayInitiationRequest(t.TransactionNumber, amount, t.CurrencyCode, $"Agreement {ag.AgreementNumber}"), ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            result = new GatewayInitiationResult(false, null, null, "PROVIDER_UNAVAILABLE", "The payment provider could not be reached.");
        }
        if (result.Accepted)
        {
            t.ProviderReference = result.ProviderReference; t.RedirectUrl = result.RedirectUrl; t.Move(GatewayTransactionStatus.Pending, support.Now);
        }
        else
        {
            t.FailureCode = result.ErrorCode; t.FailureReason = Trunc(result.ErrorMessage ?? "The provider rejected the payment.", 500); t.Move(GatewayTransactionStatus.Failed, support.Now);
        }
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Gateway.Initiated", "GatewayTransaction", t.Id.ToString(),
            NewValues: new { t.TransactionNumber, t.Provider, t.AgreementId, t.Amount, t.Status, t.ProviderReference }, UserId: userId), ct);
        return t;
    }

    private static GatewayTransaction Replay(GatewayTransaction existing, long customerId, string hash)
    {
        if (existing.CustomerId != customerId || existing.RequestHash != hash) throw Invalid("IdempotencyKey", "This idempotency key was already used for a different payment.");
        return existing;
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static string Trunc(string v, int max) => v.Length > max ? v[..max] : v;
}

/// <summary>
/// Confirms a transaction with the provider itself (never trusting the content of a callback) and applies the result. A confirmed success creates exactly one
/// Payment awaiting approval (verified by the gateway) in a locked transaction, so duplicate callbacks and reconciliation runs cannot double-credit.
/// </summary>
public class PaymentVerificationService(IAppDbContext db, IPaymentGatewayRegistry registry, PaymentSupport support, IAuditService audit)
{
    /// <summary>Returns the transaction's status before and after, with a note. Safe to call repeatedly.</summary>
    public async Task<ReconciliationItemDto> VerifyAsync(long transactionId, string source, CancellationToken ct = default)
    {
        var t = await db.GatewayTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == transactionId, ct) ?? throw new NotFoundException("Transaction not found.");
        var before = t.Status;
        if (t.Status is GatewayTransactionStatus.Succeeded or GatewayTransactionStatus.Refunded)
            return await EnsurePaymentAsync(t, before, source, ct);
        if (t.Status is GatewayTransactionStatus.Failed or GatewayTransactionStatus.Cancelled)
            return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, before, "Already closed.");
        if (string.IsNullOrEmpty(t.ProviderReference))
            return await NoteAsync(t, before, "No provider reference yet; nothing to verify.", ct);
        var gateway = registry.Find(t.Provider);
        if (gateway is null) return await NoteAsync(t, before, $"Provider '{t.Provider}' is not configured.", ct);

        GatewayStatusResult status;
        try { status = await gateway.QueryStatusAsync(t.ProviderReference, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return await NoteAsync(t, before, "The provider could not be reached; try again later.", ct);
        }
        if (!status.Found) return await NoteAsync(t, before, "The provider does not know this transaction.", ct);

        switch (status.State)
        {
            case GatewayPaymentState.Succeeded:
                if (status.Amount != t.Amount || !string.Equals(status.CurrencyCode ?? t.CurrencyCode, t.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                {
                    var note = $"Amount mismatch: provider reports {status.Amount:N2} {status.CurrencyCode}, expected {t.Amount:N2} {t.CurrencyCode}. No payment was created.";
                    await audit.RecordAsync(new AuditEntry("Gateway.AmountMismatch", "GatewayTransaction", t.Id.ToString(), NewValues: new { t.TransactionNumber, status.Amount, status.CurrencyCode, source }), ct);
                    return await NoteAsync(t, before, note, ct);
                }
                return await ApplySuccessAsync(t.Id, before, source, ct);
            case GatewayPaymentState.Failed or GatewayPaymentState.Cancelled:
                return await ApplyFailureAsync(t.Id, before, status, source, ct);
            default:
                return await NoteAsync(t, before, "Still pending at the provider.", ct);
        }
    }

    private async Task<ReconciliationItemDto> EnsurePaymentAsync(GatewayTransaction t, GatewayTransactionStatus before, string source, CancellationToken ct)
    {
        if (t.PaymentId is not null) return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, before, "Already applied.");
        return await ApplySuccessAsync(t.Id, before, source, ct);
    }

    private async Task<ReconciliationItemDto> NoteAsync(GatewayTransaction snapshot, GatewayTransactionStatus before, string note, CancellationToken ct)
    {
        var t = await db.GatewayTransactions.FirstAsync(x => x.Id == snapshot.Id, ct);
        t.LastReconciledAtUtc = support.Now; t.ReconciliationNote = note.Length > 500 ? note[..500] : note;
        await db.SaveWithConcurrencyAsync(ct);
        return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, note);
    }

    private async Task<ReconciliationItemDto> ApplyFailureAsync(long id, GatewayTransactionStatus before, GatewayStatusResult status, string source, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GatewayTransaction>(id, ct);
        var t = await db.GatewayTransactions.FirstAsync(x => x.Id == id, ct);
        await db.Entry(t).ReloadAsync(ct);
        if (t.Status is not (GatewayTransactionStatus.Initiated or GatewayTransactionStatus.Pending))
            return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, "Already closed.");
        var now = support.Now;
        var to = status.State == GatewayPaymentState.Cancelled ? GatewayTransactionStatus.Cancelled : GatewayTransactionStatus.Failed;
        t.Move(to, now);
        t.FailureReason = Trunc(status.FailureReason ?? $"The provider reported the payment as {to}.", 500);
        t.LastReconciledAtUtc = now; t.ReconciliationNote = $"Closed by {source}.";
        PortalNotifications.Add(db, t.CustomerId, NotificationKind.Payment, "Online payment not completed",
            $"Your online payment {t.TransactionNumber} of {t.Amount:N2} {t.CurrencyCode} was not completed. No money was recorded.", "GatewayTransaction", t.Id, now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Gateway.Closed", "GatewayTransaction", t.Id.ToString(), new { Status = before.ToString() }, new { Status = to.ToString(), source }), ct);
        await tx.CommitAsync(ct);
        return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, $"Closed as {to}.");
    }

    private async Task<ReconciliationItemDto> ApplySuccessAsync(long id, GatewayTransactionStatus before, string source, CancellationToken ct)
    {
        var method = await db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.Kind == PaymentMethodKind.PaymentGateway && m.IsActive, ct)
            ?? throw new DomainException("No active payment method of kind 'Payment gateway' exists; the confirmed payment cannot be recorded yet.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockForUpdateAsync<GatewayTransaction>(id, ct);
        var t = await db.GatewayTransactions.FirstAsync(x => x.Id == id, ct);
        await db.Entry(t).ReloadAsync(ct);
        if (t.PaymentId is not null) return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, "Already applied.");
        if (t.Status is GatewayTransactionStatus.Failed or GatewayTransactionStatus.Cancelled)
            return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, "Closed before the success was seen; investigate manually.");

        var now = support.Now;
        var today = await support.TodayAsync(ct);
        var ag = await support.InfoAsync(t.AgreementId, ct);
        var reference = t.ProviderReference ?? t.TransactionNumber;
        var payment = new Payment
        {
            PaymentNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = "GW:" + t.TransactionNumber, RequestHash = PaymentSupport.Sha256("GW:" + t.IdempotencyKey),
            ProjectId = t.ProjectId, AgreementId = t.AgreementId, BookingId = ag.BookingId, CustomerId = t.CustomerId, PaymentMethodId = method.Id, MethodKind = method.Kind,
            Source = PaymentSource.CustomerSubmitted, Amount = t.Amount, CurrencyCode = t.CurrencyCode, PaymentDate = today, Reference = reference,
            ActiveReferenceKey = $"{method.Id}:{new string(reference.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()}",
            Remarks = $"Online payment {t.TransactionNumber} via {t.Provider}", RequestedAllocationMode = AllocationMode.OldestDueFirst, RequestedRemainderToAdvance = false,
            Status = PaymentStatus.PendingApproval, VerificationRequired = true, SubmittedByUserId = t.InitiatedByUserId, SubmittedBy = "Customer (online payment)", SubmittedAtUtc = now,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        payment.PaymentNumber = $"PAY-{payment.Id:D6}";
        db.PaymentVerifications.Add(new PaymentVerification
        {
            PaymentId = payment.Id, Result = VerificationResult.Verified, VerifiedByUserId = Guid.Empty, VerifiedBy = $"Gateway {t.Provider}", VerifiedAtUtc = now,
            Notes = $"Confirmed by the payment provider (reference {reference}) through {source}.", BankReference = reference, ClearedOn = today,
        });
        t.PaymentId = payment.Id;
        t.Move(GatewayTransactionStatus.Succeeded, now);
        t.LastReconciledAtUtc = now; t.ReconciliationNote = $"Confirmed by {source}.";
        PortalNotifications.Add(db, t.CustomerId, NotificationKind.Payment, "Online payment received",
            $"Your online payment {t.TransactionNumber} of {t.Amount:N2} {t.CurrencyCode} was confirmed and is awaiting approval ({payment.PaymentNumber}).", "Payment", payment.Id, now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Gateway.Succeeded", "GatewayTransaction", t.Id.ToString(), new { Status = before.ToString() },
            new { Status = "Succeeded", payment.PaymentNumber, t.Amount, reference, source }), ct);
        await tx.CommitAsync(ct);
        return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, $"Payment {payment.PaymentNumber} created and awaiting approval.");
    }

    private static string Trunc(string v, int max) => v.Length > max ? v[..max] : v;
}

/// <summary>
/// Receives provider callbacks (anonymous endpoint). Every delivery is logged first; unsigned or wrongly signed deliveries are rejected; repeated provider event
/// ids are recognised and not processed twice. The callback only identifies the transaction: its state is then confirmed with the provider.
/// </summary>
public class PaymentCallbackHandler(IAppDbContext db, IPaymentGatewayRegistry registry, PaymentVerificationService verification, TimeProvider clock)
{
    private const int MaxPayload = 60_000;

    public async Task<CallbackResult> HandleAsync(string providerCode, string payload, IReadOnlyDictionary<string, string> headers, string? sourceIp, CancellationToken ct = default)
    {
        var code = providerCode.Trim().ToLowerInvariant();
        var gateway = registry.Enabled ? registry.Find(code) : null;
        var now = clock.GetUtcNow().UtcDateTime;
        var body = payload.Length > MaxPayload ? payload[..MaxPayload] : payload;
        if (gateway is null)
        {
            db.PaymentWebhookLogs.Add(new PaymentWebhookLog { Provider = Trunc(code, 40), Payload = body, SourceIp = sourceIp, ReceivedAtUtc = now, ProcessingStatus = WebhookProcessingStatus.Rejected, ProcessingMessage = "Unknown or disabled provider.", ProcessedAtUtc = now });
            await db.SaveChangesAsync(ct);
            return new CallbackResult(CallbackOutcome.UnknownProvider, "Unknown provider.");
        }

        var cb = gateway.ParseCallback(payload, headers);
        var log = new PaymentWebhookLog
        {
            Provider = gateway.ProviderCode, EventId = cb.SignatureValid ? cb.EventId : null, ProviderReference = cb.ProviderReference, SignatureValid = cb.SignatureValid, Payload = body,
            SourceIp = sourceIp, ReceivedAtUtc = now,
        };
        db.PaymentWebhookLogs.Add(log);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (PaymentSupport.IsDuplicate(ex))
        {
            // The same provider event was already received: acknowledge without processing again.
            db.Entry(log).State = EntityState.Detached;
            db.PaymentWebhookLogs.Add(new PaymentWebhookLog
            {
                Provider = gateway.ProviderCode, ProviderReference = cb.ProviderReference, SignatureValid = true, Payload = body, SourceIp = sourceIp, ReceivedAtUtc = now,
                ProcessingStatus = WebhookProcessingStatus.Duplicate, ProcessingMessage = $"Duplicate of event {cb.EventId}.", ProcessedAtUtc = now,
            });
            await db.SaveChangesAsync(ct);
            return new CallbackResult(CallbackOutcome.Duplicate, "Event already received.");
        }

        if (!cb.SignatureValid)
            return await FinishAsync(log, WebhookProcessingStatus.Rejected, "Invalid signature.", CallbackOutcome.InvalidSignature, ct);

        var tx = await db.GatewayTransactions.AsNoTracking().Where(t => t.Provider == gateway.ProviderCode
                && ((cb.ProviderReference != null && t.ProviderReference == cb.ProviderReference) || (cb.TransactionNumber != null && t.TransactionNumber == cb.TransactionNumber)))
            .Select(t => new { t.Id }).FirstOrDefaultAsync(ct);
        if (tx is null)
            return await FinishAsync(log, WebhookProcessingStatus.Ignored, "No matching transaction.", CallbackOutcome.UnknownTransaction, ct);

        log.GatewayTransactionId = tx.Id;
        try
        {
            var item = await verification.VerifyAsync(tx.Id, "callback", ct);
            return await FinishAsync(log, WebhookProcessingStatus.Processed, item.Note, CallbackOutcome.Processed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FinishAsync(log, WebhookProcessingStatus.Failed, ex.Message, CallbackOutcome.Rejected, ct);
            throw;
        }
    }

    private async Task<CallbackResult> FinishAsync(PaymentWebhookLog log, WebhookProcessingStatus status, string message, CallbackOutcome outcome, CancellationToken ct)
    {
        log.ProcessingStatus = status; log.ProcessingMessage = Trunc(message, 500); log.ProcessedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new CallbackResult(outcome, message);
    }

    private static string Trunc(string v, int max) => v.Length > max ? v[..max] : v;
}

/// <summary>
/// Reconciliation (spec 10.4): confirms every transaction still open after a grace period with the provider, closes abandoned ones, and creates the payment of
/// any confirmed success whose callback never arrived. A scheduler can call this in Phase 11; until then an authorized user runs it.
/// </summary>
public class GatewayReconciliationService(IAppDbContext db, PaymentVerificationService verification, IPermissionService permissions, ICurrentUser user, PaymentSupport support, IAuditService audit)
{
    public const int GraceMinutes = 10;
    public const int AbandonAfterMinutes = 60;

    public async Task<ReconciliationResultDto> ReconcileAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayManage, ct);
        var now = support.Now;
        var cutoff = now.AddMinutes(-GraceMinutes);
        var ids = await db.GatewayTransactions.AsNoTracking()
            .Where(t => (t.Status == GatewayTransactionStatus.Initiated || t.Status == GatewayTransactionStatus.Pending || (t.Status == GatewayTransactionStatus.Succeeded && t.PaymentId == null)) && t.InitiatedAtUtc <= cutoff)
            .OrderBy(t => t.Id).Select(t => t.Id).Take(200).ToListAsync(ct);
        var items = new List<ReconciliationItemDto>();
        foreach (var id in ids) items.Add(await ReconcileOneCoreAsync(id, now, ct));
        var changed = items.Count(i => i.Before != i.After);
        await audit.RecordAsync(new AuditEntry("Gateway.Reconciled", "GatewayTransaction", null, NewValues: new { Checked = items.Count, Changed = changed }, UserId: user.UserId), ct);
        return new ReconciliationResultDto(items.Count, changed, items);
    }

    public async Task<ReconciliationItemDto> ReconcileOneAsync(long transactionId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayManage, ct);
        return await ReconcileOneCoreAsync(transactionId, support.Now, ct);
    }

    private async Task<ReconciliationItemDto> ReconcileOneCoreAsync(long id, DateTime now, CancellationToken ct)
    {
        var t = await db.GatewayTransactions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Transaction not found.");
        if (t.Status == GatewayTransactionStatus.Initiated && string.IsNullOrEmpty(t.ProviderReference) && t.InitiatedAtUtc <= now.AddMinutes(-AbandonAfterMinutes))
        {
            var before = t.Status;
            t.FailureCode = "ABANDONED"; t.FailureReason = "The payment was never sent to the provider."; t.LastReconciledAtUtc = now; t.ReconciliationNote = "Closed as abandoned by reconciliation.";
            t.Move(GatewayTransactionStatus.Failed, now);
            await db.SaveWithConcurrencyAsync(ct);
            return new ReconciliationItemDto(t.Id, t.TransactionNumber, before, t.Status, "Closed as abandoned.");
        }
        return await verification.VerifyAsync(id, "reconciliation", ct);
    }
}

/// <summary>Refunds an online payment at the provider where the provider allows it. Money is returned only after the ERP payment was reversed or rejected.</summary>
public class GatewayRefundService(IAppDbContext db, IPaymentGatewayRegistry registry, IPermissionService permissions, ICurrentUser user, PaymentSupport support, IAuditService audit, GatewayQueryService query,
    IValidator<GatewayRefundRequestDto> validator)
{
    public async Task<GatewayTransactionDto> RefundAsync(long transactionId, GatewayRefundRequestDto r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayManage, ct);
        await validator.ValidateOrThrowAsync(r, ct);
        var t = await db.GatewayTransactions.FirstOrDefaultAsync(x => x.Id == transactionId, ct) ?? throw new NotFoundException("Transaction not found.");
        if (t.Status != GatewayTransactionStatus.Succeeded) throw new DomainException("Only a succeeded transaction can be refunded.");
        var gateway = registry.Find(t.Provider) ?? throw new DomainException($"Provider '{t.Provider}' is not configured.");
        if (!gateway.SupportsRefunds) throw new DomainException("This provider does not allow refunds through its API; refund the customer through the provider's portal or by bank transfer.");
        var paymentStatus = await db.Payments.AsNoTracking().Where(p => p.Id == t.PaymentId).Select(p => (PaymentStatus?)p.Status).FirstOrDefaultAsync(ct);
        if (paymentStatus is not (PaymentStatus.Reversed or PaymentStatus.Rejected))
            throw new DomainException("Reverse or reject the ERP payment first: the customer is refunded only for money that is not kept in the agreement ledger.");
        if (r.Amount > t.Amount - t.RefundedAmount) throw new RequestValidationException(new Dictionary<string, string[]> { ["Amount"] = [$"At most {t.Amount - t.RefundedAmount:N2} can still be refunded."] });
        db.UseRowVersion(t, r.RowVersion);

        var result = await gateway.RefundAsync(new GatewayRefundRequest(t.ProviderReference ?? throw new DomainException("The transaction has no provider reference."), r.Amount, r.Reason.Trim()), ct);
        if (!result.Accepted) throw new DomainException($"The provider rejected the refund: {result.ErrorMessage ?? "unknown reason"}");
        var now = support.Now;
        t.RefundedAmount += r.Amount; t.RefundReference = result.RefundReference;
        if (t.RefundedAmount == t.Amount) t.Move(GatewayTransactionStatus.Refunded, now);
        PortalNotifications.Add(db, t.CustomerId, NotificationKind.Payment, "Online payment refunded", $"{r.Amount:N2} {t.CurrencyCode} of your online payment {t.TransactionNumber} was refunded.", "GatewayTransaction", t.Id, now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Gateway.Refunded", "GatewayTransaction", t.Id.ToString(), NewValues: new { r.Amount, t.RefundedAmount, t.RefundReference, t.Status }, Reason: r.Reason.Trim(), UserId: user.UserId), ct);
        return await query.GetAsync(t.Id, ct);
    }
}

public class GatewayRefundRequestDtoValidator : AbstractValidator<GatewayRefundRequestDto>
{
    public GatewayRefundRequestDtoValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).Must(v => v == decimal.Round(v, 2)).WithMessage("The amount can have at most two decimals.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500);
    }
}

public class GatewayQueryService(IAppDbContext db, IPermissionService permissions, ICurrentUser user, PaymentSupport support)
{
    public async Task<PagedResult<GatewayTransactionDto>> ListAsync(GatewayListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayView, ct);
        var projects = await support.ProjectsAsync(ct);
        var q = from t in db.GatewayTransactions.AsNoTracking()
                join p in projects on t.ProjectId equals p.Id
                select t;
        if (r.Status is { } s) q = q.Where(t => t.Status == s);
        if (r.CustomerId is { } c) q = q.Where(t => t.CustomerId == c);
        if (r.AgreementId is { } a) q = q.Where(t => t.AgreementId == a);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var term = r.Search.Trim();
            q = q.Where(t => t.TransactionNumber.Contains(term) || (t.ProviderReference != null && t.ProviderReference.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var ids = await q.OrderByDescending(t => t.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(t => t.Id).ToListAsync(ct);
        var rows = await ProjectAsync(db.GatewayTransactions.AsNoTracking().Where(t => ids.Contains(t.Id)), ct);
        return new PagedResult<GatewayTransactionDto>(rows.OrderByDescending(x => x.Id).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<GatewayTransactionDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayView, ct);
        var projectId = await db.GatewayTransactions.AsNoTracking().Where(t => t.Id == id).Select(t => (long?)t.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null) throw new NotFoundException("Transaction not found.");
        if (!await (await support.ProjectsAsync(ct)).AnyAsync(p => p.Id == projectId, ct)) throw new ForbiddenException("You do not have access to this transaction.");
        return (await ProjectAsync(db.GatewayTransactions.AsNoTracking().Where(t => t.Id == id), ct)).Single();
    }

    public async Task<PagedResult<WebhookLogDto>> WebhooksAsync(WebhookLogListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.GatewayView, ct);
        // Webhook logs are not project-scoped (they can precede the transaction match), so they need company-wide access.
        if (!await permissions.HasPermissionAsync(user.UserId ?? Guid.Empty, Permissions.ProjectsAccessAll, ct)) throw new ForbiddenException("Webhook logs require access to all projects.");
        var q = db.PaymentWebhookLogs.AsNoTracking().AsQueryable();
        if (r.Status is { } s) q = q.Where(l => l.ProcessingStatus == s);
        if (!string.IsNullOrWhiteSpace(r.Provider)) q = q.Where(l => l.Provider == r.Provider);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(l => l.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(l => new WebhookLogDto(l.Id, l.Provider, l.EventId, l.GatewayTransactionId, l.ProviderReference, l.SignatureValid, l.ReceivedAtUtc, l.ProcessingStatus, l.ProcessingMessage, l.SourceIp)).ToListAsync(ct);
        return new PagedResult<WebhookLogDto>(rows, r.Page, r.PageSize, total);
    }

    private async Task<List<GatewayTransactionDto>> ProjectAsync(IQueryable<GatewayTransaction> q, CancellationToken ct) =>
        await (from t in q
               join c in db.Customers.AsNoTracking() on t.CustomerId equals c.Id
               join a in db.CustomerAgreements.AsNoTracking() on t.AgreementId equals a.Id
               join p in db.Payments.AsNoTracking() on t.PaymentId equals p.Id into pj
               from p in pj.DefaultIfEmpty()
               select new GatewayTransactionDto(t.Id, t.TransactionNumber, t.Provider, t.ProviderReference, t.CustomerId, c.FullName, t.AgreementId, a.AgreementNumber, t.Amount, t.CurrencyCode,
                   t.Status, t.FailureCode, t.FailureReason, t.InitiatedAtUtc, t.CompletedAtUtc, t.PaymentId, p == null ? null : p.PaymentNumber, p == null ? null : p.Status,
                   t.RefundedAmount, t.RefundReference, t.LastReconciledAtUtc, t.ReconciliationNote, Convert.ToBase64String(t.RowVersion))).ToListAsync(ct);
}
