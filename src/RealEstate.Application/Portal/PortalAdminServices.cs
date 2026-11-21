using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Administration;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Application.Portal;

/// <summary>
/// Office-side management of customer application logins. A login is created for one customer the office user may access; the Customer role is assigned only here
/// (never through user administration). Disabling keeps the row and the history; a disabled login can neither sign in nor use the customer API.
/// </summary>
public class CustomerAccountService(IAppDbContext db, IAdministrationStore store, IIdentityService identity, IRefreshTokenService refreshTokens, CustomerService customers,
    IPermissionService permissions, ICurrentUser user, IAuditService audit, TimeProvider clock,
    IValidator<CreateCustomerAccountRequest> createValidator, IValidator<DisableCustomerAccountRequest> disableValidator, IValidator<ResetCustomerPasswordRequest> resetValidator)
{
    public async Task<CustomerAccountDto?> GetAsync(long customerId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalAccountsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        return await QueryAsync(customerId, ct);
    }

    public async Task<CustomerAccountDto> CreateAsync(long customerId, CreateCustomerAccountRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalAccountsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId, ct);
        if (customer.Status == CustomerStatus.Blocked) throw new DomainException("A blocked customer cannot get an application login.");
        if (await db.CustomerAccounts.AnyAsync(a => a.CustomerId == customerId, ct)) throw new DomainException("This customer already has an application login.");

        var (userId, errors) = await store.CreateUserAsync(r.Email.Trim(), customer.FullName, customer.Mobile, r.Password, true, [RoleNames.Customer], ct);
        if (userId is null) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(r.Email)] = [.. errors] });
        db.CustomerAccounts.Add(new CustomerAccount { UserId = userId.Value, CustomerId = customerId, IsActive = true });
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("CustomerAccount.Created", "Customer", customerId.ToString(), NewValues: new { Email = r.Email.Trim(), UserId = userId }, UserId: user.UserId), ct);
        return (await QueryAsync(customerId, ct))!;
    }

    public async Task<CustomerAccountDto> DisableAsync(long customerId, DisableCustomerAccountRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalAccountsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        await disableValidator.ValidateOrThrowAsync(r, ct);
        var a = await db.CustomerAccounts.FirstOrDefaultAsync(x => x.CustomerId == customerId, ct) ?? throw new NotFoundException("This customer has no application login.");
        if (!a.IsActive) throw new DomainException("The login is already disabled.");
        db.UseRowVersion(a, r.RowVersion);
        a.IsActive = false; a.DisabledReason = r.Reason.Trim(); a.DisabledAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveWithConcurrencyAsync(ct);
        await SetLoginActiveAsync(a.UserId, false, ct);
        await refreshTokens.RevokeAllAsync(a.UserId, ct);
        await audit.RecordAsync(new AuditEntry("CustomerAccount.Disabled", "Customer", customerId.ToString(), new { IsActive = true }, new { IsActive = false }, r.Reason.Trim(), UserId: user.UserId), ct);
        return (await QueryAsync(customerId, ct))!;
    }

    public async Task<CustomerAccountDto> EnableAsync(long customerId, string? rowVersion, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalAccountsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        var a = await db.CustomerAccounts.FirstOrDefaultAsync(x => x.CustomerId == customerId, ct) ?? throw new NotFoundException("This customer has no application login.");
        if (a.IsActive) throw new DomainException("The login is already enabled.");
        db.UseRowVersion(a, rowVersion);
        a.IsActive = true; a.DisabledReason = null; a.DisabledAtUtc = null;
        await db.SaveWithConcurrencyAsync(ct);
        await SetLoginActiveAsync(a.UserId, true, ct);
        await audit.RecordAsync(new AuditEntry("CustomerAccount.Enabled", "Customer", customerId.ToString(), new { IsActive = false }, new { IsActive = true }, UserId: user.UserId), ct);
        return (await QueryAsync(customerId, ct))!;
    }

    /// <summary>Sets a new password chosen by the office user (to be handed to the customer) and ends every session of the customer.</summary>
    public async Task ResetPasswordAsync(long customerId, ResetCustomerPasswordRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalAccountsManage, ct);
        await customers.EnsureAccessAsync(customerId, ct);
        await resetValidator.ValidateOrThrowAsync(r, ct);
        var a = await db.CustomerAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerId == customerId, ct) ?? throw new NotFoundException("This customer has no application login.");
        var errors = await identity.SetPasswordAsync(a.UserId, r.NewPassword, ct);
        if (errors.Count > 0) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(r.NewPassword)] = [.. errors] });
        await refreshTokens.RevokeAllAsync(a.UserId, ct);
        await audit.RecordAsync(new AuditEntry("CustomerAccount.PasswordReset", "Customer", customerId.ToString(), UserId: user.UserId), ct);
    }

    private async Task SetLoginActiveAsync(Guid userId, bool active, CancellationToken ct)
    {
        var u = await store.GetUserAsync(userId, ct) ?? throw new NotFoundException("Login not found.");
        var errors = await store.UpdateUserAsync(userId, u.FullName, u.PhoneNumber, active, u.Roles, ct);
        if (errors.Count > 0) throw new DomainException(string.Join("; ", errors));
    }

    private async Task<CustomerAccountDto?> QueryAsync(long customerId, CancellationToken ct)
    {
        var a = await (from x in db.CustomerAccounts.AsNoTracking()
                       join c in db.Customers.AsNoTracking() on x.CustomerId equals c.Id
                       where x.CustomerId == customerId
                       select new { x, c.FullName, c.CustomerCode }).FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var login = await store.GetUserAsync(a.x.UserId, ct);
        return new CustomerAccountDto(a.x.Id, a.x.CustomerId, a.FullName, a.CustomerCode, a.x.UserId, login?.Email ?? "", a.x.IsActive, a.x.DisabledReason, a.x.DisabledAtUtc, login?.LastLoginAtUtc,
            a.x.RowVersion.ToToken());
    }
}

/// <summary>Office-side support desk: list and handle customer support requests of customers the office user may access.</summary>
public class SupportDeskService(IAppDbContext db, CustomerService customers, IPermissionService permissions, ICurrentUser user, IAuditService audit, TimeProvider clock,
    IValidator<SupportStaffReplyRequest> replyValidator)
{
    public async Task<PagedResult<SupportListItemDto>> ListAsync(SupportListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.SupportView, ct);
        var scoped = await customers.ScopedAsync(ct);
        var q = from s in db.SupportRequests.AsNoTracking()
                join c in scoped on s.CustomerId equals c.Id
                select new { s, c.FullName, c.CustomerCode };
        if (r.Status is { } st) q = q.Where(x => x.s.Status == st);
        if (r.CustomerId is { } cid) q = q.Where(x => x.s.CustomerId == cid);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(x => x.s.RequestNumber.Contains(t) || x.s.Subject.Contains(t) || x.FullName.Contains(t) || x.CustomerCode.Contains(t));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.s.LastActivityAtUtc).Skip((r.Page - 1) * r.PageSize).Take(r.PageSize)
            .Select(x => new SupportListItemDto(x.s.Id, x.s.RequestNumber, x.s.CustomerId, x.FullName, x.CustomerCode, x.s.Category, x.s.Subject, x.s.Status, x.s.AssignedTo, x.s.CreatedAtUtc,
                x.s.LastActivityAtUtc)).ToListAsync(ct);
        return new PagedResult<SupportListItemDto>(rows, r.Page, r.PageSize, total);
    }

    public async Task<SupportDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.SupportView, ct);
        return await BuildAsync(id, ct);
    }

    public async Task<SupportDetailDto> ReplyAsync(long id, SupportStaffReplyRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.SupportManage, ct);
        await replyValidator.ValidateOrThrowAsync(r, ct);
        var s = await LoadAsync(id, ct);
        if (s.Status == SupportRequestStatus.Closed) throw new DomainException("The request is closed.");
        db.UseRowVersion(s, r.RowVersion);
        var now = clock.GetUtcNow().UtcDateTime;
        var actor = user.UserName ?? "staff";
        db.SupportRequestMessages.Add(new SupportRequestMessage { SupportRequestId = id, AuthorType = SupportAuthorType.Staff, AuthorUserId = user.UserId, AuthorName = actor, Body = r.Message.Trim(), CreatedAtUtc = now });
        s.LastActivityAtUtc = now;
        s.AssignedToUserId ??= user.UserId; s.AssignedTo ??= actor;
        var before = s.Status;
        ApplyStatus(s, r.Status ?? (s.Status == SupportRequestStatus.Open ? SupportRequestStatus.InProgress : s.Status), now);
        PortalNotifications.Add(db, s.CustomerId, NotificationKind.Support, $"Reply to {s.RequestNumber}", $"The office replied to your request \"{s.Subject}\".", "SupportRequest", s.Id, now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Support.Replied", "SupportRequest", id.ToString(), new { Status = before.ToString() }, new { Status = s.Status.ToString() }, UserId: user.UserId), ct);
        return await BuildAsync(id, ct);
    }

    public async Task<SupportDetailDto> SetStatusAsync(long id, SupportStatusRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.SupportManage, ct);
        if (!Enum.IsDefined(r.Status)) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(r.Status)] = ["Unknown status."] });
        var s = await LoadAsync(id, ct);
        db.UseRowVersion(s, r.RowVersion);
        var before = s.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        s.AssignedToUserId ??= user.UserId; s.AssignedTo ??= user.UserName;
        ApplyStatus(s, r.Status, now);
        s.LastActivityAtUtc = now;
        if (before != s.Status)
            PortalNotifications.Add(db, s.CustomerId, NotificationKind.Support, $"{s.RequestNumber} is now {s.Status}", $"Your request \"{s.Subject}\" was marked as {s.Status}.", "SupportRequest", s.Id, now);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Support.StatusChanged", "SupportRequest", id.ToString(), new { Status = before.ToString() }, new { Status = s.Status.ToString() }, UserId: user.UserId), ct);
        return await BuildAsync(id, ct);
    }

    private static void ApplyStatus(SupportRequest s, SupportRequestStatus to, DateTime now)
    {
        s.Status = to;
        s.ResolvedAtUtc = to is SupportRequestStatus.Resolved or SupportRequestStatus.Closed ? s.ResolvedAtUtc ?? now : null;
    }

    private async Task<SupportRequest> LoadAsync(long id, CancellationToken ct)
    {
        var s = await db.SupportRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Support request not found.");
        await customers.EnsureAccessAsync(s.CustomerId, ct);
        return s;
    }

    private async Task<SupportDetailDto> BuildAsync(long id, CancellationToken ct)
    {
        var s = await LoadAsync(id, ct);
        var c = await db.Customers.AsNoTracking().Where(x => x.Id == s.CustomerId).Select(x => new { x.FullName, x.CustomerCode }).FirstAsync(ct);
        var agreement = s.AgreementId is null ? null : await db.CustomerAgreements.AsNoTracking().Where(a => a.Id == s.AgreementId).Select(a => a.AgreementNumber).FirstOrDefaultAsync(ct);
        var messages = await db.SupportRequestMessages.AsNoTracking().Where(m => m.SupportRequestId == id).OrderBy(m => m.Id)
            .Select(m => new PortalSupportMessageDto(m.Id, m.AuthorType, m.AuthorName, m.Body, m.CreatedAtUtc)).ToListAsync(ct);
        var head = new SupportListItemDto(s.Id, s.RequestNumber, s.CustomerId, c.FullName, c.CustomerCode, s.Category, s.Subject, s.Status, s.AssignedTo, s.CreatedAtUtc, s.LastActivityAtUtc);
        return new SupportDetailDto(head, s.AgreementId, agreement, messages, s.RowVersion.ToToken());
    }
}

/// <summary>Announcements shown in the customer application, in-app notifications sent by the office, and the payment instructions note.</summary>
public class PortalContentService(IAppDbContext db, CustomerService customers, ProjectAccessService access, IPermissionService permissions, ICurrentUser user, IAuditService audit,
    TimeProvider clock, IValidator<SaveAnnouncementRequest> announcementValidator, IValidator<CreateNotificationRequest> notificationValidator)
{
    public async Task<IReadOnlyList<AnnouncementDto>> ListAnnouncementsAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        var all = await access.HasAllAccessAsync(ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = from a in db.Announcements.AsNoTracking()
                join p in db.Projects.AsNoTracking() on a.ProjectId equals p.Id into pj
                from p in pj.DefaultIfEmpty()
                where (a.ProjectId == null && all) || (a.ProjectId != null && projects.Any(x => x.Id == a.ProjectId))
                orderby a.PublishFromUtc descending
                select new AnnouncementDto(a.Id, a.Title, a.Body, a.ProjectId, p == null ? null : p.ProjectCode, a.PublishFromUtc, a.ExpiresAtUtc, a.IsActive, Convert.ToBase64String(a.RowVersion));
        return await q.Take(200).ToListAsync(ct);
    }

    public async Task<AnnouncementDto> GetAnnouncementAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        var a = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Announcement not found.");
        await EnsureScopeAsync(a.ProjectId, ct);
        return await ToDtoAsync(a, ct);
    }

    public async Task<AnnouncementDto> SaveAnnouncementAsync(long? id, SaveAnnouncementRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        await announcementValidator.ValidateOrThrowAsync(r, ct);
        await EnsureScopeAsync(r.ProjectId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        Announcement a;
        object? before = null;
        if (id is null)
        {
            a = new Announcement();
            db.Announcements.Add(a);
        }
        else
        {
            a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Announcement not found.");
            await EnsureScopeAsync(a.ProjectId, ct);
            db.UseRowVersion(a, r.RowVersion);
            before = new { a.Title, a.Body, a.ProjectId, a.PublishFromUtc, a.ExpiresAtUtc, a.IsActive };
        }
        a.Title = r.Title.Trim(); a.Body = r.Body.Trim(); a.ProjectId = r.ProjectId; a.PublishFromUtc = r.PublishFromUtc ?? (id is null ? now : a.PublishFromUtc); a.ExpiresAtUtc = r.ExpiresAtUtc; a.IsActive = r.IsActive;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(id is null ? "Announcement.Created" : "Announcement.Updated", "Announcement", a.Id.ToString(), before,
            new { a.Title, a.ProjectId, a.PublishFromUtc, a.ExpiresAtUtc, a.IsActive }, UserId: user.UserId), ct);
        return await ToDtoAsync(a, ct);
    }

    public async Task SendNotificationAsync(CreateNotificationRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        await notificationValidator.ValidateOrThrowAsync(r, ct);
        await customers.EnsureAccessAsync(r.CustomerId, ct);
        PortalNotifications.Add(db, r.CustomerId, NotificationKind.General, r.Title.Trim(), r.Message.Trim(), null, null, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("CustomerNotification.Sent", "Customer", r.CustomerId.ToString(), NewValues: new { r.Title }, UserId: user.UserId), ct);
    }

    public async Task<PaymentInstructionsNoteDto> GetInstructionsAsync(CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        return new PaymentInstructionsNoteDto(await db.ApplicationSettings.AsNoTracking().Where(s => s.Key == CustomerPortalService.PaymentInstructionsKey).Select(s => s.Value).FirstOrDefaultAsync(ct));
    }

    public async Task<PaymentInstructionsNoteDto> SaveInstructionsAsync(PaymentInstructionsNoteDto r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.PortalContentManage, ct);
        if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Payment instructions are company-wide and need access to all projects.");
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note is { Length: > 2000 }) throw new RequestValidationException(new Dictionary<string, string[]> { [nameof(r.Note)] = ["The note can have at most 2000 characters."] });
        var s = await db.ApplicationSettings.FirstOrDefaultAsync(x => x.Key == CustomerPortalService.PaymentInstructionsKey, ct);
        var before = s?.Value;
        if (s is null) db.ApplicationSettings.Add(new ApplicationSetting { Key = CustomerPortalService.PaymentInstructionsKey, Value = note, Description = "Payment instructions shown to customers" });
        else s.Value = note;
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Portal.PaymentInstructionsChanged", "ApplicationSetting", CustomerPortalService.PaymentInstructionsKey, new { Note = before }, new { Note = note }, UserId: user.UserId), ct);
        return new PaymentInstructionsNoteDto(note);
    }

    private async Task EnsureScopeAsync(long? projectId, CancellationToken ct)
    {
        if (projectId is { } pid) await access.EnsureAccessAsync(pid, ct);
        else if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("Company-wide announcements need access to all projects.");
    }

    private async Task<AnnouncementDto> ToDtoAsync(Announcement a, CancellationToken ct)
    {
        var code = a.ProjectId is null ? null : await db.Projects.AsNoTracking().Where(p => p.Id == a.ProjectId).Select(p => p.ProjectCode).FirstOrDefaultAsync(ct);
        return new AnnouncementDto(a.Id, a.Title, a.Body, a.ProjectId, code, a.PublishFromUtc, a.ExpiresAtUtc, a.IsActive, a.RowVersion.ToToken());
    }
}

/// <summary>Lets the office see (and download) the proof files customers attached to a payment while verifying it. Access follows the payment's project access.</summary>
public class PaymentProofAdminService(IAppDbContext db, PaymentService payments, IFileStorage storage)
{
    public async Task<IReadOnlyList<PaymentProofAdminDto>> ListAsync(long paymentId, CancellationToken ct = default)
    {
        await payments.GetAsync(paymentId, ct); // permission and project access
        return await db.PaymentProofDocuments.AsNoTracking().Where(d => d.PaymentId == paymentId && !d.IsDeleted).OrderBy(d => d.Id)
            .Select(d => new PaymentProofAdminDto(d.Id, d.FileName, d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy ?? "customer")).ToListAsync(ct);
    }

    public async Task<DownloadedFile> DownloadAsync(long paymentId, long documentId, CancellationToken ct = default)
    {
        await payments.GetAsync(paymentId, ct);
        var d = await db.PaymentProofDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.PaymentId == paymentId && !x.IsDeleted, ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }
}
