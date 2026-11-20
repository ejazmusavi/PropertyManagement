using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RealEstate.Application.Audit;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Installments;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Pricing;
using RealEstate.Domain.Sales;

namespace RealEstate.Application.Sales;

/// <summary>
/// The sales lifecycle: booking (price snapshot, ownership structure, charges, payment mode), approval, confirmation with agreement and ownership,
/// cancellation with property release and refund request, and customer transfer / ownership change with preserved history.
/// Money collection, installment schedules and accounting postings belong to later phases; no payment is recorded or implied here.
/// </summary>
public class BookingService(
    IAppDbContext db, IPermissionService permissions, ICurrentUser user, ProjectAccessService access, IAuditService audit, CustomerService customers,
    PricingCalculationService pricing, PricingSupport support, DocumentUploader uploader, IFileStorage storage, TimeProvider clock,
    IValidator<CreateBookingRequest> createValidator, IValidator<CancelBookingRequest> cancelValidator, IValidator<TransferBookingRequest> transferValidator,
    IValidator<RejectBookingRequest> rejectValidator)
{
    private Guid RequireUser() => user.UserId ?? throw new ForbiddenException("Authentication required.");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // =====================================================================================================
    // queries
    // =====================================================================================================
    public async Task<PagedResult<BookingListItemDto>> ListAsync(BookingListRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsView, ct);
        var projects = await access.ScopeAsync(db.Projects.AsNoTracking(), ct);
        var q = db.Bookings.AsNoTracking().Where(b => projects.Any(p => p.Id == b.ProjectId));
        if (r.ProjectId is { } pid) q = q.Where(b => b.ProjectId == pid);
        if (r.Status is { } s) q = q.Where(b => b.Status == s);
        if (r.PaymentMode is { } pm) q = q.Where(b => b.PaymentMode == pm);
        if (r.CustomerId is { } cid) q = q.Where(b => b.Customers.Any(c => c.CustomerId == cid));
        if (r.From is { } from) q = q.Where(b => b.BookingDate >= from);
        if (r.To is { } to) q = q.Where(b => b.BookingDate <= to);
        if (!string.IsNullOrWhiteSpace(r.Search))
        {
            var t = r.Search.Trim();
            q = q.Where(b => b.BookingNumber.Contains(t) || b.Customers.Any(c => c.Customer!.FullName.Contains(t) || c.Customer.CustomerCode.Contains(t) || c.Customer.Mobile.Contains(t))
                             || b.Properties.Any(p => p.Property!.PropertyCode.Contains(t)));
        }
        var total = await q.CountAsync(ct);
        q = (r.SortBy?.ToLowerInvariant(), r.Descending) switch
        {
            ("date", false) => q.OrderBy(b => b.BookingDate).ThenBy(b => b.Id),
            ("amount", false) => q.OrderBy(b => b.TotalPayable).ThenBy(b => b.Id),
            ("amount", true) => q.OrderByDescending(b => b.TotalPayable).ThenBy(b => b.Id),
            ("status", false) => q.OrderBy(b => b.Status).ThenBy(b => b.Id),
            ("status", true) => q.OrderByDescending(b => b.Status).ThenBy(b => b.Id),
            (_, false) => q.OrderByDescending(b => b.BookingDate).ThenByDescending(b => b.Id),
            _ => q.OrderBy(b => b.BookingDate).ThenBy(b => b.Id),
        };
        var page = await q.Skip((r.Page - 1) * r.PageSize).Take(r.PageSize).Select(b => new
        {
            b.Id, b.BookingNumber, b.ProjectId, b.BookingDate, b.Status, b.PaymentMode, b.TotalPayable, b.SubmittedBy,
            Primary = b.Customers.Where(c => c.IsActive && c.IsPrimary).Select(c => c.Customer!.FullName).FirstOrDefault(),
            Owners = b.Customers.Count(c => c.IsActive),
            Props = b.Properties.Select(p => p.Property!.PropertyCode).ToList(),
        }).ToListAsync(ct);
        var codes = await db.Projects.AsNoTracking().Where(x => page.Select(p => p.ProjectId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ProjectCode, ct);
        var items = page.Select(b => new BookingListItemDto(b.Id, b.BookingNumber, b.ProjectId, codes.GetValueOrDefault(b.ProjectId, ""), b.BookingDate, b.Status, b.PaymentMode,
            b.Primary, b.Owners, string.Join(", ", b.Props), b.TotalPayable, b.SubmittedBy)).ToList();
        return new PagedResult<BookingListItemDto>(items, r.Page, r.PageSize, total);
    }

    public async Task<BookingDetailDto> GetAsync(long id, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsView, ct);
        await EnsureBookingAccessAsync(id, ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<BookingSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var userId = RequireUser();
        var company = await db.Companies.AsNoTracking().Select(c => new { c.CurrencySymbol, c.CurrencyDecimalPlaces }).FirstOrDefaultAsync(ct);
        return new BookingSettingsDto(company?.CurrencySymbol ?? "Rs", Math.Clamp(company?.CurrencyDecimalPlaces ?? 2, 0, 4),
            await permissions.HasPermissionAsync(userId, Permissions.BookingsDiscount, ct), await permissions.HasPermissionAsync(userId, Permissions.BookingsAuthorizeBalance, ct),
            await permissions.HasPermissionAsync(userId, Permissions.BookingsRefund, ct), userId, user.UserName);
    }

    /// <summary>Price calculation for the wizard. The authoritative price is always recalculated and stored when the booking is created.</summary>
    public async Task<PriceCalculationDto> QuoteAsync(long propertyId, long? customerId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsCreate, ct);
        await support.EnsurePropertyAccessAsync(propertyId, ct);
        if (customerId is { } c) await customers.EnsureAccessAsync(c, ct);
        return await pricing.ComputeAsync(propertyId, customerId, null, ct);
    }

    private async Task<BookingDetailDto> BuildDetailAsync(long id, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().Include(x => x.Customers).ThenInclude(c => c.Customer).Include(x => x.Properties).ThenInclude(p => p.Property)
                    .Include(x => x.Properties).ThenInclude(p => p.PricingSnapshot).Include(x => x.Charges).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Booking not found.");
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == b.ProjectId, ct);
        var approvals = await db.BookingApprovals.AsNoTracking().Where(a => a.BookingId == id).OrderBy(a => a.Id).Select(a => new BookingApprovalDto(a.Decision, a.ApproverName, a.Comments, a.DecidedAtUtc)).ToListAsync(ct);
        var history = await db.BookingStatusHistories.AsNoTracking().Where(h => h.BookingId == id).OrderBy(h => h.Id).Select(h => new BookingStatusHistoryDto(h.FromStatus, h.ToStatus, h.Reason, h.ChangedAtUtc, h.ChangedBy)).ToListAsync(ct);
        var refunds = await db.BookingRefunds.AsNoTracking().Where(h => h.BookingId == id).OrderBy(h => h.Id)
            .Select(h => new BookingRefundDto(h.Id, h.Amount, h.Reason, h.Status, h.RequestedBy, h.DecidedBy, h.DecidedAtUtc, h.DecisionComments, h.CreatedAtUtc, "")).ToListAsync(ct);
        var refundVersions = await db.BookingRefunds.AsNoTracking().Where(h => h.BookingId == id).OrderBy(h => h.Id).Select(h => h.RowVersion).ToListAsync(ct);
        refunds = refunds.Select((x, i) => x with { RowVersion = refundVersions[i].ToToken() }).ToList();
        var ag = await db.CustomerAgreements.AsNoTracking().FirstOrDefaultAsync(a => a.BookingId == id, ct);
        var props = b.Properties.OrderBy(p => p.Id).Select(p =>
        {
            var s = p.PricingSnapshot!;
            return new BookingPropertyDto(p.Id, p.PropertyId, p.Property!.PropertyCode, p.Property.PropertyNumber, s.Size, s.Unit, s.BasePrice, s.TotalPremiums, s.TotalDiscounts, s.TotalPricingCharges,
                s.IsOverridden, s.FinalPrice, p.ExtraDiscount, p.ExtraDiscountReason, p.LinePrice, s.SnapshotHash, s.AsOfDate, s.CapturedAtUtc, p.Property.Status);
        }).ToList();
        return new BookingDetailDto(b.Id, b.BookingNumber, b.ProjectId, project.ProjectCode, project.ProjectName, b.BookingDate, b.PaymentMode, b.Status, b.TotalPrice, b.TotalExtraDiscount, b.TotalCharges,
            b.TotalPayable, b.BookingAmount, b.TotalPayable - b.BookingAmount, b.BalanceAuthorizedBy, b.BalanceAuthorizationReason, b.Remarks, b.SubmittedBy, b.SubmittedAtUtc, b.ConfirmedBy, b.ConfirmedAtUtc,
            b.CancelledBy, b.CancelledAtUtc, b.CancelReason, props,
            b.Customers.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.IsPrimary).ThenBy(c => c.Id)
                .Select(c => new BookingCustomerDto(c.CustomerId, c.Customer!.CustomerCode, c.Customer.FullName, c.SharePercent, c.IsPrimary, c.IsActive, c.EffectiveFromUtc, c.EffectiveToUtc, c.EndReason)).ToList(),
            b.Charges.OrderBy(c => c.Id).Select(c => new BookingChargeDto(c.ChargeType, c.Description, c.Amount)).ToList(), approvals, history, refunds,
            ag?.Id, ag?.AgreementNumber, ag?.Status, ag?.CurrentVersionNo, b.OwnershipRevision, b.RowVersion.ToToken(), b.CreatedAtUtc, b.CreatedBy);
    }

    // =====================================================================================================
    // create / submit
    // =====================================================================================================
    public async Task<BookingDetailDto> CreateAsync(CreateBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsCreate, ct);
        var userId = RequireUser();
        await access.EnsureAccessAsync(r.ProjectId, ct);
        await createValidator.ValidateOrThrowAsync(r, ct);

        var key = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim();
        if (key is not null)
        {
            var existing = await db.Bookings.AsNoTracking().Where(b => b.IdempotencyKey == key).Select(b => new { b.Id, b.CreatedBy }).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.CreatedBy != user.UserName) throw Invalid("IdempotencyKey", "This idempotency key was already used.");
                return await BuildDetailAsync(existing.Id, ct);
            }
        }

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == r.ProjectId, ct);
        if (project.Status is not (ProjectStatus.Planning or ProjectStatus.Active)) throw new DomainException($"Bookings can be created only while the project is Planning or Active (it is {project.Status}).");
        var today = await support.TodayAsync(ct);
        var date = r.BookingDate ?? today;
        if (date > today) throw Invalid("BookingDate", "The booking date cannot be in the future.");
        var (decimals, mode) = await support.RoundingAsync(ct);

        // --- owners (scope-checked: never trust client customer ids) ---
        var ownerIds = r.Owners.Select(o => o.CustomerId).ToList();
        var found = await (await customers.ScopedAsync(ct)).Where(c => ownerIds.Contains(c.Id)).ToListAsync(ct);
        if (found.Count != ownerIds.Count) throw Invalid("Owners", "One or more customers were not found or you do not have access to them.");
        foreach (var c in found.Where(c => c.Status != CustomerStatus.Active)) throw Invalid("Owners", $"Customer {c.CustomerCode} is {c.Status} and cannot be booked.");
        var primaryId = r.Owners[0].CustomerId;

        // --- properties ---
        var propIds = r.Properties.Select(p => p.PropertyId).ToList();
        var props = await db.Properties.Where(p => propIds.Contains(p.Id)).ToListAsync(ct);
        if (props.Count != propIds.Count || props.Any(p => p.ProjectId != r.ProjectId)) throw Invalid("Properties", "One or more properties do not belong to the selected project.");
        var reservations = await db.PropertyReservations.Where(x => propIds.Contains(x.PropertyId) && x.Status == ReservationStatus.Active).ToListAsync(ct);
        var canOverrideUnlinked = await permissions.HasPermissionAsync(userId, Permissions.InventoryReserve, ct);
        foreach (var p in props)
        {
            if (p.Status is not (PropertyStatus.Available or PropertyStatus.Reserved)) throw new DomainException($"Property {p.PropertyCode} is {p.Status} and cannot be booked.");
            var res = reservations.FirstOrDefault(x => x.PropertyId == p.Id);
            if (p.Status == PropertyStatus.Reserved && res is not null && !res.IsExpiredOn(today))
            {
                var allowed = res.CustomerId is { } rc ? ownerIds.Contains(rc) : canOverrideUnlinked;
                if (!allowed) throw new DomainException($"Property {p.PropertyCode} is reserved for {res.ReservedForName} until {res.ExpiryDate:yyyy-MM-dd}; only that customer can book it.");
            }
        }

        // --- pricing snapshot per property, extra discounts, charges, totals ---
        var canDiscount = await permissions.HasPermissionAsync(userId, Permissions.BookingsDiscount, ct);
        var lines = new List<(BookingProperty Row, BookingRules.LineTotals Totals)>();
        foreach (var input in r.Properties)
        {
            if (input.ExtraDiscount > 0 && !canDiscount) throw new ForbiddenException("You do not have permission to apply a booking discount.");
            var calc = await pricing.ComputeAsync(input.PropertyId, primaryId, null, ct);
            var totals = BookingRules.Line(calc.FinalPrice, input.ExtraDiscount, decimals, mode);
            var json = PricingSnapshot.Serialize(calc);
            lines.Add((new BookingProperty
            {
                PropertyId = input.PropertyId, ActivePropertyId = input.PropertyId, ExtraDiscount = totals.ExtraDiscount, ExtraDiscountReason = N(input.ExtraDiscountReason), LinePrice = totals.LinePrice,
                PricingSnapshot = new BookingPricingSnapshot
                {
                    SnapshotVersion = calc.SnapshotVersion, SnapshotJson = json, SnapshotHash = PricingSnapshot.Hash(json), AsOfDate = calc.AsOfDate, CustomerId = primaryId, Size = calc.Size, Unit = calc.Unit,
                    BasePrice = calc.BasePrice, TotalPremiums = calc.TotalPremiums, TotalDiscounts = calc.TotalDiscounts, TotalPricingCharges = calc.TotalCharges, IsOverridden = calc.IsOverridden,
                    FinalPrice = calc.FinalPrice, CapturedAtUtc = Now,
                },
            }, totals));
        }
        var charges = (r.Charges ?? []).Select(c => new BookingCharge { ChargeType = c.ChargeType, Description = c.Description.Trim(), Amount = MoneyRounding.Round(c.Amount, decimals, mode) }).ToList();
        var sum = BookingRules.Sum(lines.Select(l => l.Totals), charges.Select(c => c.Amount));

        var needsAuthorization = r.PaymentMode == PaymentMode.Cash && r.BookingAmount < sum.TotalPayable;
        Guid? authorizedBy = null; string? authorizedByName = null; string? authReason = null;
        if (needsAuthorization)
        {
            if (!r.AuthorizeOutstandingBalance) throw Invalid("AuthorizeOutstandingBalance", "A cash sale must be paid in full at booking unless an authorized user approves an outstanding balance.");
            if (!await permissions.HasPermissionAsync(userId, Permissions.BookingsAuthorizeBalance, ct)) throw new ForbiddenException("You do not have permission to authorize an outstanding balance on a cash sale.");
            if ((r.BalanceAuthorizationReason?.Trim().Length ?? 0) < 5) throw Invalid("BalanceAuthorizationReason", "A reason of at least 5 characters is required to authorize an outstanding balance.");
            authorizedBy = userId; authorizedByName = user.UserName; authReason = r.BalanceAuthorizationReason!.Trim();
        }
        BookingRules.ValidateBookingAmount(r.PaymentMode, r.BookingAmount, sum.TotalPayable, authorizedBy is not null);

        var now = Now;
        var booking = new Booking
        {
            BookingNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = key, ProjectId = r.ProjectId, BookingDate = date, PaymentMode = r.PaymentMode,
            TotalPrice = sum.TotalPrice, TotalExtraDiscount = sum.TotalExtraDiscount, TotalCharges = sum.TotalCharges, TotalPayable = sum.TotalPayable, BookingAmount = r.BookingAmount,
            BalanceAuthorizedByUserId = authorizedBy, BalanceAuthorizedBy = authorizedByName, BalanceAuthorizationReason = authReason, Remarks = N(r.Remarks),
        };
        for (var i = 0; i < r.Owners.Count; i++)
            booking.Customers.Add(new BookingCustomer { CustomerId = r.Owners[i].CustomerId, SharePercent = r.Owners[i].SharePercent, IsPrimary = i == 0, IsActive = true, EffectiveFromUtc = now });
        foreach (var l in lines) booking.Properties.Add(l.Row);
        foreach (var c in charges) booking.Charges.Add(c);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Bookings.Add(booking);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex, "IdempotencyKey") && key is not null)
        {
            await tx.RollbackAsync(ct);
            db.Entry(booking).State = EntityState.Detached;
            var existingId = await db.Bookings.AsNoTracking().Where(b => b.IdempotencyKey == key).Select(b => b.Id).FirstAsync(ct);
            return await BuildDetailAsync(existingId, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        { throw new DomainException("One of the selected properties was just booked by someone else. Choose another property."); }

        booking.BookingNumber = $"BK-{booking.Id:D6}";
        db.BookingStatusHistories.Add(new BookingStatusHistory { Booking = booking, FromStatus = null, ToStatus = BookingStatus.Draft, Reason = "Booking created", ChangedAtUtc = now, ChangedByUserId = userId, ChangedBy = user.UserName });
        foreach (var p in props)
        {
            long? reservationId = null;
            var res = reservations.FirstOrDefault(x => x.PropertyId == p.Id);
            if (p.Status == PropertyStatus.Reserved && res is not null)
            {
                if (res.IsExpiredOn(today)) res.Close(ReservationStatus.Expired, now, "system", "Reservation expired");
                else res.Close(ReservationStatus.Converted, now, user.UserName ?? "unknown", $"Converted to booking {booking.BookingNumber}");
                reservationId = res.Id;
            }
            db.PropertyStatusHistories.Add(p.ChangeStatus(PropertyStatus.Booked, $"Booked: {booking.BookingNumber}", now, userId, user.UserName, reservationId));
        }
        if (r.SubmitForApproval) DoSubmit(booking, now, userId);
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw new DomainException("One of the selected properties was just booked by someone else. Choose another property."); }
        await audit.RecordAsync(new AuditEntry("Booking.Created", "Booking", booking.Id.ToString(), NewValues: new
        {
            booking.BookingNumber, booking.ProjectId, PaymentMode = booking.PaymentMode.ToString(), booking.TotalPrice, booking.TotalExtraDiscount, booking.TotalCharges, booking.TotalPayable, booking.BookingAmount,
            Properties = props.Select(p => p.PropertyCode).ToArray(), Owners = found.Select(c => $"{c.CustomerCode}:{r.Owners.First(o => o.CustomerId == c.Id).SharePercent}").ToArray(),
            BalanceAuthorizedBy = authorizedByName, Status = booking.Status.ToString(),
        }), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(booking.Id, ct);
    }

    public async Task<BookingDetailDto> SubmitAsync(long id, SubmitBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsCreate, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Booking not found.");
        db.UseRowVersion(b, r.RowVersion);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        DoSubmit(b, Now, userId);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry("Booking.Submitted", "Booking", id.ToString(), new { Status = "Draft" }, new { Status = "PendingApproval" }), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    private void DoSubmit(Booking b, DateTime now, Guid userId)
    {
        db.BookingStatusHistories.Add(b.Move(BookingStatus.PendingApproval, "Submitted for approval", now, userId, user.UserName));
        b.SubmittedByUserId = userId; b.SubmittedBy = user.UserName; b.SubmittedAtUtc = now;
    }

    // =====================================================================================================
    // approve / reject
    // =====================================================================================================
    public async Task<BookingDetailDto> ApproveAsync(long id, ApproveBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsApprove, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        var b = await LoadTrackedAsync(id, ct);
        if (b.Status != BookingStatus.PendingApproval) throw new DomainException($"Only bookings pending approval can be approved (this one is {b.Status}).");
        if (b.SubmittedByUserId == userId) throw new DomainException("A booking cannot be approved by the user who submitted it.");
        BookingRules.ValidateBookingAmount(b.PaymentMode, b.BookingAmount, b.TotalPayable, b.BalanceAuthorizedByUserId is not null);
        db.UseRowVersion(b, r.RowVersion);
        var now = Now;
        var comments = N(r.Comments);

        var propIds = b.Properties.Select(p => p.PropertyId).ToList();
        var props = await db.Properties.Where(p => propIds.Contains(p.Id)).ToListAsync(ct);
        var owners = b.Customers.Where(c => c.IsActive).ToList();
        BookingRules.ValidateOwnership(owners.Select(o => o.SharePercent).ToList());

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BookingStatusHistories.Add(b.Move(BookingStatus.Confirmed, comments ?? "Approved", now, userId, user.UserName));
        b.ConfirmedBy = user.UserName; b.ConfirmedAtUtc = now;
        db.BookingApprovals.Add(new BookingApproval { BookingId = id, Decision = BookingDecision.Approved, ApproverUserId = userId, ApproverName = user.UserName ?? "unknown", Comments = comments, DecidedAtUtc = now });

        var ag = new CustomerAgreement { AgreementNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], BookingId = id, Status = AgreementStatus.Active, CurrentVersionNo = 1, GeneratedAtUtc = now };
        db.CustomerAgreements.Add(ag);
        await db.SaveWithConcurrencyAsync(ct);
        ag.AgreementNumber = $"AGR-{ag.Id:D6}";
        var content = AgreementText.BuildAgreement(await BuildAgreementDataAsync(b, ag, 1, "Agreement generated on booking approval", now, ct));
        db.AgreementVersions.Add(new AgreementVersion { AgreementId = ag.Id, VersionNo = 1, Content = content, ContentSha256 = AgreementText.Sha256(content), Reason = "Agreement generated on booking approval" });

        foreach (var p in props)
        {
            var existing = await db.CustomerOwnerships.Where(o => o.PropertyId == p.Id && o.IsActive).SumAsync(o => (decimal?)o.SharePercent, ct) ?? 0m;
            BookingRules.EnsureAllocationWithinLimit(existing, owners.Sum(o => o.SharePercent));
            foreach (var o in owners)
                db.CustomerOwnerships.Add(new CustomerOwnership
                {
                    CustomerId = o.CustomerId, PropertyId = p.Id, BookingId = id, AgreementId = ag.Id, SharePercent = o.SharePercent, IsActive = true,
                    ActiveKey = CustomerOwnership.KeyFor(p.Id, o.CustomerId), StartedAtUtc = now,
                });
            db.PropertyStatusHistories.Add(p.ChangeStatus(PropertyStatus.Sold, $"Sale confirmed: {b.BookingNumber}", now, userId, user.UserName));
        }
        await SaveAsync(ct);
        await audit.RecordAsync(new AuditEntry("Booking.Approved", "Booking", id.ToString(), new { Status = "PendingApproval" },
            new { Status = "Confirmed", Agreement = ag.AgreementNumber, Properties = props.Select(p => p.PropertyCode).ToArray() }, comments, UserId: userId), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<BookingDetailDto> RejectAsync(long id, RejectBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsApprove, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        await rejectValidator.ValidateOrThrowAsync(r, ct);
        var b = await LoadTrackedAsync(id, ct);
        if (b.Status != BookingStatus.PendingApproval) throw new DomainException($"Only bookings pending approval can be rejected (this one is {b.Status}).");
        if (b.SubmittedByUserId == userId) throw new DomainException("A booking cannot be rejected by the user who submitted it; cancel it instead.");
        db.UseRowVersion(b, r.RowVersion);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BookingStatusHistories.Add(b.Move(BookingStatus.Rejected, r.Comments.Trim(), now, userId, user.UserName));
        db.BookingApprovals.Add(new BookingApproval { BookingId = id, Decision = BookingDecision.Rejected, ApproverUserId = userId, ApproverName = user.UserName ?? "unknown", Comments = r.Comments.Trim(), DecidedAtUtc = now });
        await ReleasePropertiesAsync(b, $"Booking rejected: {r.Comments.Trim()}", now, userId, ct);
        await SaveAsync(ct);
        await audit.RecordAsync(new AuditEntry("Booking.Rejected", "Booking", id.ToString(), new { Status = "PendingApproval" }, new { Status = "Rejected" }, r.Comments.Trim()), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    // =====================================================================================================
    // cancel (booking and agreement) with property release and refund request
    // =====================================================================================================
    public async Task<BookingDetailDto> CancelAsync(long id, CancelBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsCancel, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        await cancelValidator.ValidateOrThrowAsync(r, ct);
        var b = await LoadTrackedAsync(id, ct);
        if (!BookingRules.CanTransition(b.Status, BookingStatus.Cancelled)) throw new DomainException($"A booking that is {b.Status} cannot be cancelled.");
        if (r.RefundAmount is { } amount)
        {
            if (!await permissions.HasPermissionAsync(userId, Permissions.BookingsRefund, ct)) throw new ForbiddenException("You do not have permission to request a refund.");
            if (amount > b.TotalPayable) throw Invalid("RefundAmount", "The refund cannot exceed the total payable of the booking.");
        }
        db.UseRowVersion(b, r.RowVersion);
        var now = Now;
        var reason = r.Reason.Trim();
        var wasConfirmed = b.Status == BookingStatus.Confirmed;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BookingStatusHistories.Add(b.Move(BookingStatus.Cancelled, reason, now, userId, user.UserName));
        b.CancelledBy = user.UserName; b.CancelledAtUtc = now; b.CancelReason = reason;
        if (wasConfirmed)
        {
            var ag = await db.CustomerAgreements.FirstOrDefaultAsync(a => a.BookingId == id, ct);
            if (ag is { Status: AgreementStatus.Active }) { ag.Status = AgreementStatus.Cancelled; ag.CancelledAtUtc = now; ag.CancelReason = reason; }
            foreach (var o in await db.CustomerOwnerships.Where(o => o.BookingId == id && o.IsActive).ToListAsync(ct)) o.Close(now, $"Booking cancelled: {reason}");
            if (ag is not null)
            {
                // Phase 5: the schedule stops (kept as Cancelled, never deleted) and open amendment/waiver requests lapse.
                foreach (var s in await db.PaymentSchedules.Where(s => s.ActiveAgreementId == ag.Id).ToListAsync(ct)) s.Supersede(now, PaymentScheduleStatus.Cancelled);
                foreach (var a in await db.AgreementAmendments.Where(a => a.PendingAgreementId == ag.Id).ToListAsync(ct))
                { a.Status = AmendmentStatus.Cancelled; a.PendingAgreementId = null; a.DecidedAtUtc = now; a.DecisionComments = $"Agreement cancelled: {reason}"; }
                foreach (var w in await db.Waivers.Where(w => w.AgreementId == ag.Id && w.Status == WaiverStatus.Pending).ToListAsync(ct))
                { w.Status = WaiverStatus.Cancelled; w.DecidedAtUtc = now; w.DecisionComments = $"Agreement cancelled: {reason}"; }
            }
        }
        await ReleasePropertiesAsync(b, $"Booking cancelled: {reason}", now, userId, ct);
        if (r.RefundAmount is { } refund)
            db.BookingRefunds.Add(new BookingRefund { BookingId = id, Amount = refund, Reason = N(r.RefundReason) ?? reason, Status = RefundStatus.Requested, RequestedByUserId = userId, RequestedBy = user.UserName ?? "unknown" });
        await SaveAsync(ct);
        await audit.RecordAsync(new AuditEntry(wasConfirmed ? "Agreement.Cancelled" : "Booking.Cancelled", "Booking", id.ToString(), new { Status = wasConfirmed ? "Confirmed" : "Open" },
            new { Status = "Cancelled", RefundRequested = r.RefundAmount }, reason), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    public async Task<BookingDetailDto> DecideRefundAsync(long id, long refundId, DecideRefundRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsApprove, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        var refund = await db.BookingRefunds.FirstOrDefaultAsync(x => x.Id == refundId && x.BookingId == id, ct) ?? throw new NotFoundException("Refund not found.");
        if (refund.Status != RefundStatus.Requested) throw new DomainException($"The refund is already {refund.Status}.");
        if (refund.RequestedByUserId == userId) throw new DomainException("A refund cannot be decided by the user who requested it.");
        if (!r.Approve && string.IsNullOrWhiteSpace(r.Comments)) throw Invalid("Comments", "A comment is required when rejecting a refund.");
        db.UseRowVersion(refund, r.RowVersion);
        refund.Status = r.Approve ? RefundStatus.Approved : RefundStatus.Rejected;
        refund.DecidedBy = user.UserName; refund.DecidedAtUtc = Now; refund.DecisionComments = N(r.Comments);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveWithConcurrencyAsync(ct);
        await audit.RecordAsync(new AuditEntry(r.Approve ? "Booking.RefundApproved" : "Booking.RefundRejected", "BookingRefund", refundId.ToString(), new { Status = "Requested" },
            new { Status = refund.Status.ToString(), refund.Amount }, N(r.Comments)), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    // =====================================================================================================
    // transfer / ownership change (history preserved; new agreement version)
    // =====================================================================================================
    public async Task<BookingDetailDto> TransferAsync(long id, TransferBookingRequest r, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsTransfer, ct);
        var userId = RequireUser();
        await EnsureBookingAccessAsync(id, ct);
        await transferValidator.ValidateOrThrowAsync(r, ct);
        var b = await LoadTrackedAsync(id, ct);
        if (b.Status != BookingStatus.Confirmed) throw new DomainException("Only confirmed bookings can be transferred.");
        var ag = await db.CustomerAgreements.FirstOrDefaultAsync(a => a.BookingId == id, ct) ?? throw new DomainException("The booking has no agreement.");
        if (ag.Status != AgreementStatus.Active) throw new DomainException("The agreement is not active.");

        var ids = r.Owners.Select(o => o.CustomerId).ToList();
        var found = await (await customers.ScopedAsync(ct)).Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        if (found.Count != ids.Count) throw Invalid("Owners", "One or more customers were not found or you do not have access to them.");
        var oldOwners = b.Customers.Where(c => c.IsActive).ToList();
        var oldKey = oldOwners.OrderBy(o => o.CustomerId).Select(o => $"{o.CustomerId}:{o.SharePercent:0.####}");
        var newKey = r.Owners.OrderBy(o => o.CustomerId).Select(o => $"{o.CustomerId}:{o.SharePercent:0.####}");
        if (oldKey.SequenceEqual(newKey)) throw new DomainException("The new ownership is identical to the current ownership.");
        foreach (var c in found.Where(c => c.Status != CustomerStatus.Active && !oldOwners.Any(o => o.CustomerId == c.Id))) throw Invalid("Owners", $"Customer {c.CustomerCode} is {c.Status} and cannot receive a transfer.");
        var type = oldOwners.Select(o => o.CustomerId).Order().SequenceEqual(ids.Order()) ? AmendmentType.OwnershipChange : AmendmentType.Transfer;
        var reason = r.Reason.Trim();
        db.UseRowVersion(b, r.RowVersion);
        var now = Now;
        var propIds = b.Properties.Select(p => p.PropertyId).ToList();
        var oldDescription = oldOwners.Select(o => new { o.CustomerId, o.SharePercent }).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var o in oldOwners) { o.IsActive = false; o.EffectiveToUtc = now; o.EndReason = $"{type}: {reason}"; }
        foreach (var o in await db.CustomerOwnerships.Where(o => o.BookingId == id && o.IsActive).ToListAsync(ct)) o.Close(now, $"{type}: {reason}");
        b.OwnershipRevision++;
        await db.SaveWithConcurrencyAsync(ct); // closes old rows first so the unique ActiveKey can be reused by the same customer

        for (var i = 0; i < r.Owners.Count; i++)
            b.Customers.Add(new BookingCustomer { BookingId = id, CustomerId = r.Owners[i].CustomerId, SharePercent = r.Owners[i].SharePercent, IsPrimary = i == 0, IsActive = true, EffectiveFromUtc = now });
        foreach (var pid in propIds)
        {
            var existing = await db.CustomerOwnerships.Where(o => o.PropertyId == pid && o.IsActive).SumAsync(o => (decimal?)o.SharePercent, ct) ?? 0m;
            BookingRules.EnsureAllocationWithinLimit(existing, r.Owners.Sum(o => o.SharePercent));
            foreach (var o in r.Owners)
                db.CustomerOwnerships.Add(new CustomerOwnership
                {
                    CustomerId = o.CustomerId, PropertyId = pid, BookingId = id, AgreementId = ag.Id, SharePercent = o.SharePercent, IsActive = true, ActiveKey = CustomerOwnership.KeyFor(pid, o.CustomerId), StartedAtUtc = now,
                });
        }
        var next = ag.CurrentVersionNo + 1;
        var versionReason = $"{(type == AmendmentType.Transfer ? "Customer transfer" : "Ownership change")}: {reason}";
        var content = AgreementText.BuildAgreement(await BuildAgreementDataAsync(b, ag, next, versionReason, now, ct, r.Owners, found));
        db.AgreementVersions.Add(new AgreementVersion { AgreementId = ag.Id, VersionNo = next, Content = content, ContentSha256 = AgreementText.Sha256(content), Reason = versionReason });
        var details = JsonSerializer.Serialize(new { Previous = oldDescription, New = r.Owners.Select(o => new { o.CustomerId, o.SharePercent }) });
        db.AgreementAmendments.Add(new AgreementAmendment { AgreementId = ag.Id, AmendmentType = type, FromVersionNo = ag.CurrentVersionNo, ToVersionNo = next, Reason = reason, Details = details, ApprovedByUserId = userId, ApprovedBy = user.UserName ?? "unknown" });
        ag.CurrentVersionNo = next;
        await SaveAsync(ct);
        await audit.RecordAsync(new AuditEntry($"Booking.{type}", "Booking", id.ToString(), new { Owners = oldDescription }, new { Owners = r.Owners.Select(o => new { o.CustomerId, o.SharePercent }), AgreementVersion = next }, reason), ct);
        await tx.CommitAsync(ct);
        return await BuildDetailAsync(id, ct);
    }

    // =====================================================================================================
    // agreement
    // =====================================================================================================
    public async Task<AgreementDto> GetAgreementAsync(long bookingId, int? versionNo, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsView, ct);
        await EnsureBookingAccessAsync(bookingId, ct);
        var ag = await db.CustomerAgreements.AsNoTracking().Include(a => a.Versions).FirstOrDefaultAsync(a => a.BookingId == bookingId, ct)
                 ?? throw new NotFoundException("This booking has no agreement yet. An agreement is generated when the booking is approved.");
        var viewing = versionNo ?? ag.CurrentVersionNo;
        var v = ag.Versions.FirstOrDefault(x => x.VersionNo == viewing) ?? throw new NotFoundException("Agreement version not found.");
        var booking = await db.Bookings.AsNoTracking().Include(x => x.Customers).Include(x => x.Properties).ThenInclude(p => p.PricingSnapshot).Include(x => x.Charges).FirstAsync(x => x.Id == bookingId, ct);
        var latest = ag.Versions.First(x => x.VersionNo == ag.CurrentVersionNo);
        var receipt = AgreementText.BuildReceipt(await BuildAgreementDataAsync(booking, ag, ag.CurrentVersionNo, latest.Reason, ag.GeneratedAtUtc, ct));
        var amendments = await db.AgreementAmendments.AsNoTracking().Where(a => a.AgreementId == ag.Id && a.Status == AmendmentStatus.Approved && a.ToVersionNo != null).OrderBy(a => a.ToVersionNo)
            .Select(a => new AgreementAmendmentDto(a.AmendmentType, a.FromVersionNo, a.ToVersionNo!.Value, a.Reason, a.Details, a.ApprovedBy ?? "", a.DecidedAtUtc ?? a.CreatedAtUtc)).ToListAsync(ct);
        var docs = await db.AgreementDocuments.AsNoTracking().Where(d => d.AgreementId == ag.Id && !d.IsDeleted).OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new AgreementDocumentDto(d.Id, d.Title, d.FileName, d.DocumentType.ToString(), d.ContentType, d.SizeBytes, d.CreatedAtUtc, d.CreatedBy)).ToListAsync(ct);
        return new AgreementDto(ag.Id, ag.AgreementNumber, bookingId, booking.BookingNumber, ag.Status, ag.CurrentVersionNo, ag.GeneratedAtUtc, ag.CancelReason, viewing, v.Content,
            AgreementText.Sha256(v.Content) == v.ContentSha256, receipt, ag.Versions.OrderBy(x => x.VersionNo).Select(x => new AgreementVersionDto(x.VersionNo, x.Reason, x.CreatedAtUtc, x.CreatedBy, x.ContentSha256)).ToList(), amendments, docs);
    }

    public async Task<AgreementDocumentDto> UploadAgreementDocumentAsync(long bookingId, AgreementDocumentType type, string? title, FileUpload file, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsDocuments, ct);
        await EnsureBookingAccessAsync(bookingId, ct);
        if (!Enum.IsDefined(type)) throw Invalid("DocumentType", "Invalid document type.");
        var ag = await db.CustomerAgreements.AsNoTracking().FirstOrDefaultAsync(a => a.BookingId == bookingId, ct) ?? throw new NotFoundException("This booking has no agreement.");
        var stored = await uploader.StoreAsync(file, $"agreements/{ag.Id}", ct);
        var t = string.IsNullOrWhiteSpace(title) ? stored.SafeFileName : title.Trim();
        var doc = new AgreementDocument
        {
            AgreementId = ag.Id, DocumentType = type, Title = t[..Math.Min(t.Length, 200)], FileName = stored.SafeFileName, StorageKey = stored.File.Key,
            ContentType = stored.ContentType, SizeBytes = stored.File.SizeBytes, Sha256 = stored.File.Sha256,
        };
        db.AgreementDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("AgreementDocument.Uploaded", "AgreementDocument", doc.Id.ToString(), NewValues: new { bookingId, ag.AgreementNumber, doc.Title, doc.FileName, doc.Sha256 }), ct);
        return new AgreementDocumentDto(doc.Id, doc.Title, doc.FileName, doc.DocumentType.ToString(), doc.ContentType, doc.SizeBytes, doc.CreatedAtUtc, doc.CreatedBy);
    }

    public async Task<DownloadedFile> DownloadAgreementDocumentAsync(long bookingId, long documentId, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.BookingsView, ct);
        await EnsureBookingAccessAsync(bookingId, ct);
        var d = await (from doc in db.AgreementDocuments.AsNoTracking()
                       join a in db.CustomerAgreements.AsNoTracking() on doc.AgreementId equals a.Id
                       where doc.Id == documentId && a.BookingId == bookingId && !doc.IsDeleted
                       select doc).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Document not found.");
        return new DownloadedFile(await storage.OpenReadAsync(d.StorageKey, ct), d.FileName, d.ContentType);
    }

    public async Task DeleteAgreementDocumentAsync(long bookingId, long documentId, string reason, CancellationToken ct = default)
    {
        await permissions.EnsureAsync(user, Permissions.AgreementsDocuments, ct);
        await EnsureBookingAccessAsync(bookingId, ct);
        var d = await (from doc in db.AgreementDocuments
                       join a in db.CustomerAgreements on doc.AgreementId equals a.Id
                       where doc.Id == documentId && a.BookingId == bookingId && !doc.IsDeleted
                       select doc).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Document not found.");
        d.MarkDeleted(Now, user.UserName ?? "unknown", reason);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("AgreementDocument.Removed", "AgreementDocument", d.Id.ToString(), new { bookingId, d.Title, d.FileName }, Reason: reason.Trim()), ct);
    }

    // =====================================================================================================
    // helpers
    // =====================================================================================================
    private async Task EnsureBookingAccessAsync(long bookingId, CancellationToken ct)
    {
        var projectId = await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => (long?)b.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null)
        {
            if (!await access.HasAllAccessAsync(ct)) throw new ForbiddenException("You do not have access to this booking.");
            throw new NotFoundException("Booking not found.");
        }
        await access.EnsureAccessAsync(projectId.Value, ct);
    }

    private async Task<Booking> LoadTrackedAsync(long id, CancellationToken ct) =>
        await db.Bookings.Include(x => x.Customers).Include(x => x.Properties).ThenInclude(p => p.PricingSnapshot).Include(x => x.Charges).FirstOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new NotFoundException("Booking not found.");

    /// <summary>Frees every property of the booking: the live-booking marker is cleared and the property goes (through Cancelled) back to Available.</summary>
    private async Task ReleasePropertiesAsync(Booking b, string reason, DateTime now, Guid userId, CancellationToken ct)
    {
        var ids = b.Properties.Select(p => p.PropertyId).ToList();
        var props = await db.Properties.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var bp in b.Properties)
        {
            bp.ActivePropertyId = null;
            var p = props.Single(x => x.Id == bp.PropertyId);
            if (p.Status is not (PropertyStatus.Available or PropertyStatus.Cancelled))
                db.PropertyStatusHistories.Add(p.ChangeStatus(PropertyStatus.Cancelled, $"{b.BookingNumber}: {reason}", now, userId, user.UserName));
            if (p.Status == PropertyStatus.Cancelled)
                db.PropertyStatusHistories.Add(p.ChangeStatus(PropertyStatus.Available, $"Released after {b.BookingNumber} ended", now, userId, user.UserName));
        }
    }

    private async Task<AgreementData> BuildAgreementDataAsync(Booking b, CustomerAgreement ag, int versionNo, string reason, DateTime generatedAtUtc, CancellationToken ct,
        IReadOnlyList<OwnerInput>? ownerOverride = null, IReadOnlyList<Customer>? ownerCustomers = null)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == b.ProjectId, ct);
        var ownerShares = ownerOverride is not null
            ? ownerOverride.Select((o, i) => (o.CustomerId, o.SharePercent, IsPrimary: i == 0)).ToList()
            : b.Customers.Where(c => c.IsActive).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Id).Select(c => (c.CustomerId, c.SharePercent, c.IsPrimary)).ToList();
        var cids = ownerShares.Select(o => o.CustomerId).ToList();
        var cust = ownerCustomers?.ToDictionary(c => c.Id) ?? await db.Customers.AsNoTracking().Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var propIds = b.Properties.Select(p => p.PropertyId).ToList();
        var props = await db.Properties.AsNoTracking().Where(p => propIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var owners = ownerShares.Select(o => new AgreementOwner(cust[o.CustomerId].CustomerCode, cust[o.CustomerId].FullName, cust[o.CustomerId].IdentityNumber, o.SharePercent, o.IsPrimary)).ToList();
        var lines = b.Properties.OrderBy(p => p.Id).Select(p => new AgreementLine(props[p.PropertyId].PropertyCode, props[p.PropertyId].PropertyNumber, p.PricingSnapshot!.Size, p.PricingSnapshot.Unit,
            p.PricingSnapshot.FinalPrice, p.ExtraDiscount, p.LinePrice, p.PricingSnapshot.SnapshotHash)).ToList();
        var auth = b.BalanceAuthorizedBy is null ? null : $"{b.BalanceAuthorizedBy} - {b.BalanceAuthorizationReason}";
        var (scheduleVersion, schedule, extra) = ag.Id == 0 ? (null, null, null) : await InstallmentSupport.AgreementScheduleTextAsync(db, ag.Id, ct);
        return new AgreementData(company?.Name ?? "Company", ag.AgreementNumber, versionNo, reason, generatedAtUtc, b.BookingNumber, b.BookingDate, project.ProjectCode, project.ProjectName, b.PaymentMode,
            company?.CurrencySymbol ?? "Rs", owners, lines, b.Charges.OrderBy(c => c.Id).Select(c => new AgreementChargeLine($"{c.ChargeType}: {c.Description}", c.Amount)).ToList(),
            b.TotalPrice, b.TotalExtraDiscount, b.TotalCharges, b.TotalPayable, b.BookingAmount, auth, scheduleVersion, schedule, extra);
    }

    /// <summary>
    /// Phase 5: adds agreement version N+1 reflecting the current state (including the active payment schedule, which must already be saved).
    /// The caller saves and commits inside its own transaction.
    /// </summary>
    public async Task<AgreementVersion> AddAgreementVersionAsync(CustomerAgreement ag, string reason, DateTime nowUtc, CancellationToken ct = default)
    {
        var b = await db.Bookings.AsNoTracking().Include(x => x.Customers).Include(x => x.Properties).ThenInclude(p => p.PricingSnapshot).Include(x => x.Charges).FirstAsync(x => x.Id == ag.BookingId, ct);
        var next = ag.CurrentVersionNo + 1;
        var trimmed = reason.Length > 1000 ? reason[..1000] : reason;
        var content = AgreementText.BuildAgreement(await BuildAgreementDataAsync(b, ag, next, trimmed, nowUtc, ct));
        var v = new AgreementVersion { AgreementId = ag.Id, VersionNo = next, Content = content, ContentSha256 = AgreementText.Sha256(content), Reason = trimmed };
        db.AgreementVersions.Add(v);
        ag.CurrentVersionNo = next;
        return v;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveWithConcurrencyAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex)) { throw new ConcurrencyException(); }
    }

    private static bool IsDuplicate(DbUpdateException ex, string? contains = null)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) && (contains is null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
