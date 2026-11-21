using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Gateway;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Portal;

/// <summary>
/// Use cases of the customer application. Every method first resolves the customer from the authenticated login (<see cref="PortalContext"/>) and then reads or
/// writes only that customer's own records; ids received from the client are checked against that scope and unknown or foreign ids return "not found".
/// Money figures are produced by the same ledger code the office uses, so the application reconciles with the backend.
/// </summary>
public class CustomerPortalService(
    IAppDbContext db, PortalContext portal, PaymentSupport support, LedgerService ledger, PaymentService payments, ReceiptService receipts,
    DocumentUploader uploader, IFileStorage storage, IAuditService audit, PaymentInitiationService initiation, PaymentVerificationService verification,
    IPaymentGatewayRegistry gateways, IIdentityService identity,
    IValidator<SubmitPaymentProofRequest> proofValidator, IValidator<InitiateOnlinePaymentRequest> onlineValidator, IValidator<UpdatePortalProfileRequest> profileValidator,
    IValidator<ChangePasswordRequestDto> passwordValidator, IValidator<CreateSupportRequestRequest> supportCreateValidator, IValidator<SupportReplyRequest> supportReplyValidator)
{
    public const string PaymentInstructionsKey = "Portal.PaymentInstructions";
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    // =====================================================================================================
    // dashboard, properties, agreements
    // =====================================================================================================
    public async Task<PortalDashboardDto> DashboardAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var agreements = await db.CustomerAgreements.AsNoTracking().Where(a => portal.OwnAgreementIds(me.CustomerId).Contains(a.Id)).Select(a => new { a.Id, a.Status, a.BookingId }).ToListAsync(ct);
        var today = await support.TodayAsync(ct);
        decimal value = 0, paid = 0, outstanding = 0, overdue = 0, notYetDue = 0, advance = 0;
        var dues = new List<PortalDueDto>();
        var active = agreements.Where(a => a.Status == AgreementStatus.Active).ToList();
        foreach (var a in active)
        {
            await support.EnsureLedgerCurrentAsync(a.Id, ct);
            var info = await support.InfoAsync(a.Id, ct);
            var s = await ledger.SummaryAsync(info, ct);
            value += s.ContractTotal; paid += s.PaymentsReceived - s.PaymentsReversed; outstanding += s.Outstanding; overdue += s.Overdue; notYetDue += s.NotYetDue;
            advance += s.AdvanceBalance + s.UnallocatedBalance;
            foreach (var i in (await support.ActiveItemsAsync(a.Id, tracked: false, ct)).Where(i => i.Outstanding > 0m))
                dues.Add(new PortalDueDto(a.Id, info.AgreementNumber, i.Id, i.LineNo, i.Description, i.DueDate, i.Amount, i.Outstanding, i.DueDate < today));
        }
        var bookingIds = active.Select(a => a.BookingId).ToList();
        var properties = await db.BookingProperties.AsNoTracking().CountAsync(bp => bookingIds.Contains(bp.BookingId) && bp.ActivePropertyId != null, ct);
        var unread = await db.CustomerNotifications.AsNoTracking().CountAsync(n => n.CustomerId == me.CustomerId && n.ReadAtUtc == null, ct);
        return new PortalDashboardDto(me.FullName, me.CustomerCode, await support.CurrencySymbolAsync(ct), properties, active.Count, value, paid, outstanding, overdue, notYetDue, advance, unread,
            gateways.Enabled && gateways.ActiveProviderCode is not null, dues.OrderBy(d => d.DueDate).ThenBy(d => d.LineNo).Take(5).ToList());
    }

    public async Task<IReadOnlyList<PortalPropertyDto>> PropertiesAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        return await (from bc in db.BookingCustomers.AsNoTracking()
                      join b in db.Bookings.AsNoTracking() on bc.BookingId equals b.Id
                      join a in db.CustomerAgreements.AsNoTracking() on b.Id equals a.BookingId
                      join bp in db.BookingProperties.AsNoTracking() on b.Id equals bp.BookingId
                      join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                      join pt in db.PropertyTypes.AsNoTracking() on p.PropertyTypeId equals pt.Id
                      join cat in db.PropertyCategories.AsNoTracking() on pt.CategoryId equals cat.Id
                      join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id
                      where bc.CustomerId == me.CustomerId && bc.IsActive
                      orderby a.Id descending, p.PropertyCode
                      select new PortalPropertyDto(p.Id, p.PropertyCode, p.PropertyNumber, pr.ProjectCode, pr.ProjectName, cat.Name, pt.Name, p.Size, p.MeasurementUnit, bc.SharePercent, a.Id,
                          a.AgreementNumber, b.BookingNumber, b.Status, a.Status, b.PaymentMode, b.BookingDate, bp.LinePrice)).ToListAsync(ct);
    }

    public async Task<PortalAgreementDto> AgreementAsync(long agreementId, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await portal.RequireAgreementAsync(me.CustomerId, agreementId, ct);
        await support.EnsureLedgerCurrentAsync(agreementId, ct);
        var info = await support.InfoAsync(agreementId, ct);
        var x = await (from a in db.CustomerAgreements.AsNoTracking()
                       join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                       join pr in db.Projects.AsNoTracking() on b.ProjectId equals pr.Id
                       join bc in db.BookingCustomers.AsNoTracking() on b.Id equals bc.BookingId
                       where a.Id == agreementId && bc.CustomerId == me.CustomerId && bc.IsActive
                       select new { a.Status, a.CurrentVersionNo, a.GeneratedAtUtc, b.BookingNumber, BookingStatus = b.Status, b.BookingDate, b.PaymentMode, pr.ProjectCode, pr.ProjectName, bc.SharePercent, BookingId = b.Id })
            .FirstAsync(ct);
        var properties = await (from bp in db.BookingProperties.AsNoTracking()
                                join p in db.Properties.AsNoTracking() on bp.PropertyId equals p.Id
                                where bp.BookingId == x.BookingId
                                orderby p.PropertyCode
                                select new PortalAgreementPropertyDto(p.Id, p.PropertyCode, p.PropertyNumber, p.Size, p.MeasurementUnit, bp.LinePrice)).ToListAsync(ct);
        var text = await db.AgreementVersions.AsNoTracking().Where(v => v.AgreementId == agreementId && v.VersionNo == x.CurrentVersionNo).Select(v => v.Content).FirstOrDefaultAsync(ct);
        var summary = await ledger.SummaryAsync(info, ct);
        return new PortalAgreementDto(agreementId, info.AgreementNumber, x.Status, x.CurrentVersionNo, x.GeneratedAtUtc, x.BookingNumber, x.BookingStatus, x.BookingDate, x.PaymentMode, x.ProjectCode,
            x.ProjectName, await support.CurrencySymbolAsync(ct), x.SharePercent, info.HolderCustomerId == me.CustomerId, properties, summary, text);
    }

    // =====================================================================================================
    // installments
    // =====================================================================================================
    public async Task<PortalScheduleDto> ScheduleAsync(long agreementId, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await portal.RequireAgreementAsync(me.CustomerId, agreementId, ct);
        await support.EnsureLedgerCurrentAsync(agreementId, ct);
        var info = await support.InfoAsync(agreementId, ct);
        var today = await support.TodayAsync(ct);
        var items = await support.ActiveItemsAsync(agreementId, tracked: false, ct);
        var rows = items.Select(i => new PortalInstallmentDto(i.Id, i.LineNo, i.InstallmentNo, i.InstallmentCount, i.Description, i.DueDate, i.Amount, i.AmountPaid, i.AmountWaived, i.Outstanding,
            i.LastPaymentDate, StatusOf(i, today), i.Outstanding > 0m && i.DueDate < today)).ToList();
        var summary = new PortalScheduleSummaryDto(items.Sum(i => i.Amount), items.Sum(i => i.AmountPaid), items.Sum(i => i.AmountWaived), items.Sum(i => i.Outstanding),
            items.Where(i => i.DueDate <= today).Sum(i => i.Outstanding), items.Where(i => i.DueDate < today).Sum(i => i.Outstanding), items.Where(i => i.DueDate > today).Sum(i => i.Outstanding),
            items.Count(i => i.Outstanding == 0m), items.Count(i => i.Outstanding > 0m), rows.Count(r => r.IsOverdue));
        return new PortalScheduleDto(agreementId, info.AgreementNumber, info.Status, await support.CurrencySymbolAsync(ct), summary, rows);
    }

    internal static string StatusOf(PaymentScheduleItem i, DateOnly today) =>
        i.Outstanding == 0m ? (i.AmountPaid == 0m && i.AmountWaived > 0m ? "Waived" : "Paid")
        : i.DueDate < today ? "Overdue"
        : i.AmountPaid > 0m ? "PartiallyPaid" : "Upcoming";

    // =====================================================================================================
    // payments, receipts, proof, instructions
    // =====================================================================================================
    public async Task<PagedResult<PortalPaymentDto>> PaymentsAsync(PortalPaymentListRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var q = from p in db.Payments.AsNoTracking()
                join a in db.CustomerAgreements.AsNoTracking() on p.AgreementId equals a.Id
                join m in db.PaymentMethods.AsNoTracking() on p.PaymentMethodId equals m.Id
                where p.CustomerId == me.CustomerId
                select new { p, a.AgreementNumber, MethodName = m.Name };
        if (r.AgreementId is { } aid) q = q.Where(x => x.p.AgreementId == aid);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.p.PaymentDate).ThenByDescending(x => x.p.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(x => new
        {
            x.p.Id, x.p.PaymentNumber, x.p.AgreementId, x.AgreementNumber, x.p.Amount, x.p.CurrencyCode, x.p.PaymentDate, x.MethodName, x.p.Reference, x.p.Status, x.p.Source, x.p.SubmittedAtUtc,
            x.p.RejectReason,
            ReceiptId = db.PaymentReceipts.Where(rc => rc.PaymentId == x.p.Id).Select(rc => (long?)rc.Id).FirstOrDefault(),
            ReceiptNumber = db.PaymentReceipts.Where(rc => rc.PaymentId == x.p.Id).Select(rc => rc.ReceiptNumber).FirstOrDefault(),
            Proofs = db.PaymentProofDocuments.Count(d => d.PaymentId == x.p.Id && !d.IsDeleted),
        }).ToListAsync(ct);
        return new PagedResult<PortalPaymentDto>(rows.Select(x => new PortalPaymentDto(x.Id, x.PaymentNumber, x.AgreementId, x.AgreementNumber, x.Amount, x.CurrencyCode, x.PaymentDate, x.MethodName,
            x.Reference, x.Status, x.Source, x.SubmittedAtUtc, x.ReceiptId, x.ReceiptNumber, x.RejectReason, x.Proofs)).ToList(), r.Page, r.PageSize, total);
    }

    public async Task<PortalPaymentDetailDto> PaymentAsync(long paymentId, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        return await BuildPaymentAsync(me.CustomerId, paymentId, ct);
    }

    private async Task<PortalPaymentDetailDto> BuildPaymentAsync(long customerId, long paymentId, CancellationToken ct)
    {
        if (!await db.Payments.AsNoTracking().AnyAsync(p => p.Id == paymentId && p.CustomerId == customerId, ct)) throw new NotFoundException("Payment not found.");
        var d = await payments.BuildAsync(paymentId, ct);
        var proofs = await db.PaymentProofDocuments.AsNoTracking().Where(x => x.PaymentId == paymentId && !x.IsDeleted).OrderBy(x => x.Id)
            .Select(x => new PortalProofDto(x.Id, x.FileName, x.ContentType, x.SizeBytes, x.CreatedAtUtc)).ToListAsync(ct);
        var head = new PortalPaymentDto(d.Id, d.PaymentNumber, d.AgreementId, d.AgreementNumber, d.Amount, d.CurrencyCode, d.PaymentDate, d.MethodName, d.Reference, d.Status, d.Source, d.SubmittedAtUtc,
            d.Receipt?.Id, d.Receipt?.ReceiptNumber, d.RejectReason, proofs.Count);
        var allocations = d.Allocations.Where(a => a.Status == AllocationStatus.Active).Select(a => new PortalAllocationDto(a.Kind switch
        {
            AllocationKind.Installment => $"Line {a.LineNo}: {a.Description}",
            AllocationKind.Advance => "Advance balance",
            _ => "Refund",
        }, a.DueDate, a.Amount, a.Status.ToString())).ToList();
        var note = d.Status == PaymentStatus.PendingVerification ? "Waiting for the office to verify your payment."
            : d.Status == PaymentStatus.PendingApproval ? "Verified. Waiting for approval." : null;
        return new PortalPaymentDetailDto(head, d.CurrencySymbol, d.Allocated, d.Advance, d.Unallocated, allocations, proofs, note);
    }

    /// <summary>The receipt PDF of one of the customer's own posted payments (the first copy is the original, later copies are marked as duplicates).</summary>
    public async Task<DownloadedFile> ReceiptPdfAsync(long receiptId, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var paymentId = await (from rc in db.PaymentReceipts.AsNoTracking()
                               join p in db.Payments.AsNoTracking() on rc.PaymentId equals p.Id
                               where rc.Id == receiptId && p.CustomerId == me.CustomerId
                               select (long?)p.Id).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Receipt not found.");
        var detail = await payments.BuildAsync(paymentId, ct);
        return await receipts.RenderForCustomerAsync(receiptId, detail, ct);
    }

    public async Task<DownloadedFile> ProofAsync(long paymentId, long documentId, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var doc = await db.PaymentProofDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.PaymentId == paymentId && d.CustomerId == me.CustomerId && !d.IsDeleted, ct)
            ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(doc.StorageKey, ct), doc.FileName, doc.ContentType);
    }

    public async Task<PortalPaymentInstructionsDto> InstructionsAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var note = await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == PaymentInstructionsKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var myProjects = await (from a in db.CustomerAgreements.AsNoTracking()
                                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                                where portal.OwnAgreementIds(me.CustomerId).Contains(a.Id)
                                select b.ProjectId).Distinct().ToListAsync(ct);
        var banks = await db.BankAccounts.AsNoTracking().Where(b => b.IsActive && (b.ProjectId == null || myProjects.Contains(b.ProjectId.Value))).OrderBy(b => b.BankName).ThenBy(b => b.AccountTitle)
            .Select(b => new PortalBankAccountDto(b.BankName, b.Branch, b.AccountTitle, b.AccountNumber, b.Iban, b.CurrencyCode)).ToListAsync(ct);
        return new PortalPaymentInstructionsDto(note, banks, await MethodsAsync(ct), gateways.Enabled && gateways.ActiveProviderCode is not null);
    }

    public async Task<IReadOnlyList<PortalPaymentMethodDto>> MethodsAsync(CancellationToken ct = default)
    {
        await portal.RequireAsync(ct);
        var kinds = new[] { PaymentMethodKind.BankTransfer, PaymentMethodKind.Cheque, PaymentMethodKind.OnlineTransfer, PaymentMethodKind.Cash };
        return await db.PaymentMethods.AsNoTracking().Where(m => m.IsActive && kinds.Contains(m.Kind)).OrderBy(m => m.Kind).ThenBy(m => m.Name)
            .Select(m => new PortalPaymentMethodDto(m.Id, m.Code, m.Name, m.Kind, m.RequiresReference)).ToListAsync(ct);
    }

    /// <summary>
    /// Payment proof submission: records a customer-submitted payment (always verified by the office before approval, so it never posts on its own) with the
    /// attached slip. A retry with the same idempotency key returns the original submission and attaches the file only if it is still missing.
    /// </summary>
    public async Task<PortalPaymentDetailDto> SubmitProofAsync(SubmitPaymentProofRequest r, FileUpload? file, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await proofValidator.ValidateOrThrowAsync(r, ct);
        if (file is null) throw Invalid("Proof", "Attach the payment proof (bank slip or screenshot).");
        await portal.RequireAgreementAsync(me.CustomerId, r.AgreementId, ct);
        var method = await db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.PaymentMethodId && m.IsActive, ct);
        if (method is null || method.Kind is PaymentMethodKind.PaymentGateway or PaymentMethodKind.Card) throw Invalid(nameof(r.PaymentMethodId), "Choose one of the listed payment methods.");
        var outstanding = (await support.ActiveItemsAsync(r.AgreementId, tracked: false, ct)).Sum(i => i.Outstanding);
        if (r.Amount > outstanding)
            throw Invalid(nameof(r.Amount), $"The amount exceeds the outstanding balance of the agreement ({outstanding:N2}). Contact the office to pay in advance.");

        var userId = me.UserId;
        var created = await payments.CreatePortalAsync(new CreatePaymentRequest(r.AgreementId, me.CustomerId, r.PaymentMethodId, PaymentSource.CustomerSubmitted, r.Amount, r.PaymentDate, r.Reference,
            r.BankName, r.InstrumentDate, me.FullName, r.Remarks, AllocationMode.OldestDueFirst, null, null, false, r.IdempotencyKey.Trim()), userId, ct);

        if (!await db.PaymentProofDocuments.AnyAsync(d => d.PaymentId == created.Id && !d.IsDeleted, ct))
        {
            var stored = await uploader.StoreAsync(file, $"payment-proofs/{created.Id}", ct);
            db.PaymentProofDocuments.Add(new PaymentProofDocument
            {
                PaymentId = created.Id, CustomerId = me.CustomerId, Title = "Payment proof", FileName = stored.SafeFileName, StorageKey = stored.File.Key, ContentType = stored.ContentType,
                SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
            });
            PortalNotifications.Add(db, me.CustomerId, NotificationKind.Payment, "Payment proof received",
                $"We received your payment proof {created.PaymentNumber} of {created.Amount:N2} {created.CurrencyCode}. The office will verify it.", "Payment", created.Id, support.Now);
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(new AuditEntry("Payment.ProofAttached", "Payment", created.Id.ToString(), NewValues: new { created.PaymentNumber, stored.SafeFileName, stored.File.SizeBytes, stored.File.Sha256 }, UserId: userId), ct);
        }
        return await BuildPaymentAsync(me.CustomerId, created.Id, ct);
    }

    // =====================================================================================================
    // online payment (when enabled)
    // =====================================================================================================
    public async Task<PortalOnlinePaymentDto> InitiateOnlinePaymentAsync(InitiateOnlinePaymentRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await onlineValidator.ValidateOrThrowAsync(r, ct);
        await portal.RequireAgreementAsync(me.CustomerId, r.AgreementId, ct);
        var t = await initiation.InitiateAsync(me.CustomerId, me.UserId, r.AgreementId, r.Amount, r.IdempotencyKey, ct);
        return await OnlineDtoAsync(t.Id, me.CustomerId, ct);
    }

    /// <summary>Status of one of the customer's online payments. A transaction still open is confirmed with the provider on each poll, so the app does not depend on the callback.</summary>
    public async Task<PortalOnlinePaymentDto> OnlinePaymentAsync(long id, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var status = await db.GatewayTransactions.AsNoTracking().Where(t => t.Id == id && t.CustomerId == me.CustomerId).Select(t => (GatewayTransactionStatus?)t.Status).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Online payment not found.");
        if (status is GatewayTransactionStatus.Pending or GatewayTransactionStatus.Initiated)
        {
            try { await verification.VerifyAsync(id, "customer-poll", ct); }
            catch (Exception ex) when (ex is DomainException or DbUpdateException) { /* the next poll or reconciliation retries */ }
        }
        return await OnlineDtoAsync(id, me.CustomerId, ct);
    }

    private async Task<PortalOnlinePaymentDto> OnlineDtoAsync(long id, long customerId, CancellationToken ct) =>
        await (from t in db.GatewayTransactions.AsNoTracking()
               join p in db.Payments.AsNoTracking() on t.PaymentId equals p.Id into pj
               from p in pj.DefaultIfEmpty()
               where t.Id == id && t.CustomerId == customerId
               select new PortalOnlinePaymentDto(t.Id, t.TransactionNumber, t.AgreementId, t.Amount, t.CurrencyCode, t.Status, t.RedirectUrl, t.FailureReason, t.InitiatedAtUtc, t.CompletedAtUtc,
                   p == null ? null : p.PaymentNumber)).FirstAsync(ct);

    // =====================================================================================================
    // ledger
    // =====================================================================================================
    public async Task<CustomerLedgerDto> LedgerAsync(LedgerPageRequest? page, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var ids = await (from a in db.CustomerAgreements.AsNoTracking()
                         where db.BookingCustomers.Any(bc => bc.BookingId == a.BookingId && bc.CustomerId == me.CustomerId)
                               || db.CustomerLedgerEntries.Any(e => e.AgreementId == a.Id && e.CustomerId == me.CustomerId)
                         orderby a.Id
                         select a.Id).ToListAsync(ct);
        return await ledger.BuildCustomerLedgerAsync(me.CustomerId, ids, page, ct);
    }

    public async Task<AgreementLedgerDto> AgreementLedgerAsync(long agreementId, LedgerPageRequest? page, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await portal.RequireAgreementAsync(me.CustomerId, agreementId, ct);
        await support.EnsureLedgerCurrentAsync(agreementId, ct);
        return await ledger.BuildAgreementLedgerAsync(agreementId, page ?? new LedgerPageRequest(), ct);
    }

    // =====================================================================================================
    // notifications and announcements
    // =====================================================================================================
    public async Task<PortalNotificationListDto> NotificationsAsync(PagedRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var q = db.CustomerNotifications.AsNoTracking().Where(n => n.CustomerId == me.CustomerId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.Id).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(n => new PortalNotificationDto(n.Id, n.Kind, n.Title, n.Message, n.RelatedType, n.RelatedId, n.CreatedAtUtc, n.ReadAtUtc != null)).ToListAsync(ct);
        return new PortalNotificationListDto(new PagedResult<PortalNotificationDto>(rows, r.Page, r.PageSize, total), await q.CountAsync(n => n.ReadAtUtc == null, ct));
    }

    public async Task MarkNotificationReadAsync(long id, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var n = await db.CustomerNotifications.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == me.CustomerId, ct) ?? throw new NotFoundException("Notification not found.");
        if (n.ReadAtUtc is null) { n.ReadAtUtc = support.Now; await db.SaveChangesAsync(ct); }
    }

    public async Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var now = support.Now;
        await db.CustomerNotifications.Where(n => n.CustomerId == me.CustomerId && n.ReadAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), ct);
    }

    public async Task<IReadOnlyList<PortalAnnouncementDto>> AnnouncementsAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var now = support.Now;
        var myProjects = await (from a in db.CustomerAgreements.AsNoTracking()
                                join b in db.Bookings.AsNoTracking() on a.BookingId equals b.Id
                                where portal.OwnAgreementIds(me.CustomerId).Contains(a.Id)
                                select b.ProjectId).Distinct().ToListAsync(ct);
        return await db.Announcements.AsNoTracking()
            .Where(a => a.IsActive && a.PublishFromUtc <= now && (a.ExpiresAtUtc == null || a.ExpiresAtUtc > now) && (a.ProjectId == null || myProjects.Contains(a.ProjectId.Value)))
            .OrderByDescending(a => a.PublishFromUtc).Take(50).Select(a => new PortalAnnouncementDto(a.Id, a.Title, a.Body, a.PublishFromUtc, a.ExpiresAtUtc)).ToListAsync(ct);
    }

    // =====================================================================================================
    // profile
    // =====================================================================================================
    public async Task<PortalProfileDto> ProfileAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var c = await db.Customers.AsNoTracking().FirstAsync(x => x.Id == me.CustomerId, ct);
        var id = c.IdentityNumber;
        return new PortalProfileDto(c.CustomerCode, c.FullName, id.Length <= 4 ? new string('*', id.Length) : new string('*', id.Length - 4) + id[^4..], c.Mobile, c.Email, c.Address, me.LoginEmail ?? "");
    }

    /// <summary>Customers may change only how the office reaches them (mobile, e-mail, address). Name and identity changes stay with the office. Audited.</summary>
    public async Task<PortalProfileDto> UpdateProfileAsync(UpdatePortalProfileRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await profileValidator.ValidateOrThrowAsync(r, ct);
        var c = await db.Customers.FirstAsync(x => x.Id == me.CustomerId, ct);
        var before = new { c.Mobile, c.Email, c.Address };
        c.Mobile = r.Mobile.Trim(); c.Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim(); c.Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim();
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Customer.ProfileUpdatedByCustomer", "Customer", c.Id.ToString(), before, new { c.Mobile, c.Email, c.Address }, UserId: me.UserId), ct);
        return await ProfileAsync(ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequestDto r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await passwordValidator.ValidateOrThrowAsync(r, ct);
        var errors = await identity.ChangePasswordAsync(me.UserId, r.CurrentPassword, r.NewPassword, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(r.NewPassword)] = [.. errors] });
        await audit.RecordAsync(new AuditEntry("User.PasswordChanged", "User", me.UserId.ToString(), UserId: me.UserId), ct);
    }

    // =====================================================================================================
    // support requests (customer side)
    // =====================================================================================================
    public async Task<PortalSupportRequestDetailDto> CreateSupportRequestAsync(CreateSupportRequestRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await supportCreateValidator.ValidateOrThrowAsync(r, ct);
        if (r.AgreementId is { } aid) await portal.RequireAgreementAsync(me.CustomerId, aid, ct);
        var now = support.Now;
        var s = new SupportRequest
        {
            RequestNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], CustomerId = me.CustomerId, AgreementId = r.AgreementId, Category = r.Category, Subject = r.Subject.Trim(),
            Status = SupportRequestStatus.Open, LastActivityAtUtc = now,
        };
        s.Messages.Add(new SupportRequestMessage { AuthorType = SupportAuthorType.Customer, AuthorUserId = me.UserId, AuthorName = me.FullName, Body = r.Message.Trim(), CreatedAtUtc = now });
        db.SupportRequests.Add(s);
        await db.SaveChangesAsync(ct);
        s.RequestNumber = $"SUP-{s.Id:D6}";
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("Support.Created", "SupportRequest", s.Id.ToString(), NewValues: new { s.RequestNumber, s.Category, s.Subject }, UserId: me.UserId), ct);
        return await SupportDetailAsync(s.Id, ct);
    }

    public async Task<IReadOnlyList<PortalSupportRequestDto>> SupportRequestsAsync(CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var rows = await SupportQuery(db.SupportRequests.AsNoTracking().Where(s => s.CustomerId == me.CustomerId).OrderByDescending(s => s.LastActivityAtUtc).Take(100)).ToListAsync(ct);
        return rows.OrderByDescending(s => s.LastActivityAtUtc).ToList();
    }

    public async Task<PortalSupportRequestDetailDto> SupportDetailAsync(long id, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        var head = await SupportQuery(db.SupportRequests.AsNoTracking().Where(s => s.Id == id && s.CustomerId == me.CustomerId)).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Support request not found.");
        var messages = await db.SupportRequestMessages.AsNoTracking().Where(m => m.SupportRequestId == id).OrderBy(m => m.Id)
            .Select(m => new PortalSupportMessageDto(m.Id, m.AuthorType, m.AuthorName, m.Body, m.CreatedAtUtc)).ToListAsync(ct);
        var rv = await db.SupportRequests.AsNoTracking().Where(s => s.Id == id).Select(s => s.RowVersion).FirstAsync(ct);
        return new PortalSupportRequestDetailDto(head, messages, rv.ToToken());
    }

    public async Task<PortalSupportRequestDetailDto> ReplySupportAsync(long id, SupportReplyRequest r, CancellationToken ct = default)
    {
        var me = await portal.RequireAsync(ct);
        await supportReplyValidator.ValidateOrThrowAsync(r, ct);
        var s = await db.SupportRequests.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == me.CustomerId, ct) ?? throw new NotFoundException("Support request not found.");
        if (s.Status == SupportRequestStatus.Closed) throw new DomainException("This request is closed. Open a new request.");
        var now = support.Now;
        db.SupportRequestMessages.Add(new SupportRequestMessage { SupportRequestId = id, AuthorType = SupportAuthorType.Customer, AuthorUserId = me.UserId, AuthorName = me.FullName, Body = r.Message.Trim(), CreatedAtUtc = now });
        s.LastActivityAtUtc = now;
        if (s.Status == SupportRequestStatus.Resolved) { s.Status = SupportRequestStatus.Open; s.ResolvedAtUtc = null; }
        await db.SaveWithConcurrencyAsync(ct);
        return await SupportDetailAsync(id, ct);
    }

    private IQueryable<PortalSupportRequestDto> SupportQuery(IQueryable<SupportRequest> q) =>
        from s in q
        join a in db.CustomerAgreements.AsNoTracking() on s.AgreementId equals a.Id into aj
        from a in aj.DefaultIfEmpty()
        select new PortalSupportRequestDto(s.Id, s.RequestNumber, s.Category, s.Subject, s.Status, s.AgreementId, a == null ? null : a.AgreementNumber, s.CreatedAtUtc, s.LastActivityAtUtc,
            db.SupportRequestMessages.Count(m => m.SupportRequestId == s.Id));
}
