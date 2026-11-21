using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Audit;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Gateway;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Portal;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 10 integration tests against MySQL: customer isolation, ledger reconciliation, proof submission, receipts, support, accounts, password reset and the payment gateway abstraction.</summary>
[Collection("phase1")]
public class Phase10Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Sales, Guid Director, Guid Accountant, Guid Support, long ProjectId, DateOnly Today);
    private record Deal(long BookingId, long AgreementId, long CustomerId);

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();
    private void As(Guid id) { fx.Act(id); lock (Names) fx.Actor.UserName = Names[id]; }

    private async Task<Guid> UserAsync(params string[] roles)
    {
        var id = await fx.CreateUserAsync(roles);
        lock (Names) Names[id] = fx.Actor.UserName!;
        return id;
    }

    private async Task<T> Run<T>(Guid actor, Func<IServiceScope, Task<T>> f)
    {
        As(actor);
        using var s = fx.Scope();
        return await f(s);
    }

    private async Task Do(Guid actor, Func<IServiceScope, Task> f)
    {
        As(actor);
        using var s = fx.Scope();
        await f(s);
    }

    private async Task<World> WorldAsync()
    {
        var admin = await UserAsync(RoleNames.Administrator);
        As(admin);
        long pid; DateOnly today;
        using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        var support = await UserAsync(RoleNames.CustomerSupport);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { sales, accountant, support }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, accountant, support, pid, today);
    }

    private async Task<long> CustomerAsync(World w) => await Run(w.Sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
        [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed installment booking (5 marla = 5,000,000; down payment 500,000 today, 24 monthly installments, possession and development lines) with a schedule.</summary>
    private async Task<Deal> DealAsync(World w, int monthsBack = 0)
    {
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"PX-{Guid.NewGuid():N}"[..14], "X" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
        var c = await CustomerAsync(w);
        var b = await Run(w.Sales, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, PaymentMode.Installment, [new BookingPropertyInput(propertyId, 0m, null)],
            [new OwnerInput(c, 100m)], null, 500_000m, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        var agreementId = b.AgreementId!.Value;
        var types = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ChargeTypes.ToDictionaryAsync(t => t.Code, t => t.Id));
        PlanComponentInput Comp(string type, string name, AmountBasis basis, decimal? amount, decimal? pct, ScheduleFrequency f, int? count, int offset, ChargeCategory cat = ChargeCategory.SaleConsideration) =>
            new(types[type], name, cat, basis, amount, pct, f, count, null, offset, 0, null, null, 5, null, true, 50m, TaxTreatment.NotApplicable, 0m, null, null);
        var code = "P10-" + Interlocked.Increment(ref counter);
        var plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).CreateAsync(new CreateInstallmentPlanRequest(w.ProjectId, code, "Plan " + code, null, InstallmentPlanType.StandardProject,
            null, null, null,
            [
                Comp("DOWN_PAYMENT", "Down payment", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 0),
                Comp("MONTHLY", "Monthly installment", AmountBasis.Remainder, null, null, ScheduleFrequency.Monthly, 24, 1),
                Comp("POSSESSION", "Possession", AmountBasis.PercentOfPrice, null, 10m, ScheduleFrequency.OneTime, null, 30),
                Comp("DEVELOPMENT", "Development charges", AmountBasis.FixedAmount, 100_000m, null, ScheduleFrequency.OneTime, null, 12, ChargeCategory.DevelopmentCharges),
            ])));
        plan = await Run(w.Director, s => Svc<InstallmentPlanService>(s).ActivateAsync(plan.Id, new PlanRowVersionRequest(plan.RowVersion)));
        var ag = await Run(w.Sales, s => Svc<AgreementScheduleService>(s).ApplyPlanAsync(agreementId, new ApplyPlanRequest(plan.Id, null, null)));
        await Run(w.Sales, s => Svc<AgreementScheduleService>(s).GenerateAsync(agreementId, new GenerateScheduleRequest(ag.RowVersion, null)));
        if (monthsBack > 0)
            await Run(w.Admin, async s =>
            {
                var db = Svc<AppDbContext>(s);
                var items = await (from i in db.PaymentScheduleItems join sc in db.PaymentSchedules on i.ScheduleId equals sc.Id where sc.AgreementId == agreementId select i).ToListAsync();
                foreach (var i in items) { i.DueDate = i.DueDate.AddMonths(-monthsBack); i.GraceEndDate = i.GraceEndDate.AddMonths(-monthsBack); }
                return await db.SaveChangesAsync();
            });
        return new Deal(b.Id, agreementId, c);
    }

    /// <summary>A login with the Customer role linked to the customer (what the office does through CustomerAccountService).</summary>
    private async Task<Guid> LoginAsync(long customerId, bool active = true)
    {
        var id = await UserAsync(RoleNames.Customer);
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        db.CustomerAccounts.Add(new CustomerAccount { UserId = id, CustomerId = customerId, IsActive = active });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<long> MethodAsync(string code) => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == code).Select(m => m.Id).FirstAsync());

    private static FileUpload Slip(string name = "slip.png") => new(name, "image/png", 5, new MemoryStream([1, 2, 3, 4, 5]));

    private async Task<PortalPaymentDetailDto> ProofAsync(Guid customerUser, Deal d, World w, decimal amount, string key, string? reference = "TRX-1001", string method = "BANK_TRANSFER")
    {
        var methodId = await MethodAsync(method);
        return await Run(customerUser, s => Svc<CustomerPortalService>(s).SubmitProofAsync(new SubmitPaymentProofRequest(d.AgreementId, methodId, amount, w.Today, reference, "HBL", null, "paid by transfer", key), Slip()));
    }

    // ------------------------------------------------------------------------------------------------ catalog
    [MySqlFact]
    public async Task Phase10Permissions_AndCustomerRole_AreSeeded()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        Assert.Equal([Permissions.PortalAccess], await Perms(RoleNames.Customer));
        Assert.Contains(Permissions.PortalAccountsManage, await Perms(RoleNames.CustomerSupport));
        Assert.Contains(Permissions.GatewayManage, await Perms(RoleNames.Accountant));
        Assert.DoesNotContain(Permissions.PortalAccess, await Perms(RoleNames.Director));
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
        Assert.Contains(await db.Permissions.Select(p => p.Code).ToListAsync(), c => c == Permissions.SupportManage);
    }

    // ------------------------------------------------------------------------------------------------ isolation
    [MySqlFact]
    public async Task Portal_ShowsOnlyOwnRecords_AndTreatsForeignIdsAsNotFound()
    {
        var w = await WorldAsync();
        var a = await DealAsync(w);
        var b = await DealAsync(w);
        var userA = await LoginAsync(a.CustomerId);
        var userB = await LoginAsync(b.CustomerId);
        var methodId = await MethodAsync("BANK_TRANSFER");

        // each customer sees exactly their own property and agreement
        var propsA = await Run(userA, s => Svc<CustomerPortalService>(s).PropertiesAsync());
        Assert.Single(propsA);
        Assert.Equal(a.AgreementId, propsA[0].AgreementId);
        Assert.Equal(100m, propsA[0].SharePercent);
        Assert.Equal(b.AgreementId, (await Run(userB, s => Svc<CustomerPortalService>(s).PropertiesAsync())).Single().AgreementId);

        // a payment and a support request of customer B
        var paymentB = await ProofAsync(userB, b, w, 100_000m, "B-" + Guid.NewGuid().ToString("N"), "REF-B1");
        var supportB = await Run(userB, s => Svc<CustomerPortalService>(s).CreateSupportRequestAsync(new CreateSupportRequestRequest(SupportCategory.General, "Question", "Hello", b.AgreementId)));
        var posted = await Run(w.Accountant, async s =>
        {
            var pay = Svc<PaymentService>(s);
            var v = await pay.VerifyAsync(paymentB.Payment.Id, new VerifyPaymentRequest(true, "bank statement checked", "REF-B1", w.Today, null));
            return await pay.ApproveAsync(v.Id, new ApprovePaymentRequest(null, null, null, null, null, v.RowVersion));
        });

        // customer A cannot reach any of B's records by id (not found, not forbidden: the existence of B's records is not revealed)
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).AgreementAsync(b.AgreementId)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).ScheduleAsync(b.AgreementId)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).AgreementLedgerAsync(b.AgreementId, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).PaymentAsync(paymentB.Payment.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).ReceiptPdfAsync(posted.Receipt!.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).ProofAsync(paymentB.Payment.Id, paymentB.Proofs[0].Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).SupportDetailAsync(supportB.Request.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).ReplySupportAsync(supportB.Request.Id, new SupportReplyRequest("hi"))));
        // submitting proof or starting an online payment against someone else's agreement is refused the same way
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).SubmitProofAsync(
            new SubmitPaymentProofRequest(b.AgreementId, methodId, 1_000m, w.Today, "X1", null, null, null, Guid.NewGuid().ToString("N")), Slip())));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(userA, s => Svc<CustomerPortalService>(s).CreateSupportRequestAsync(new CreateSupportRequestRequest(SupportCategory.General, "Hello", "Hi there", b.AgreementId))));
        // lists only contain own rows
        Assert.Empty((await Run(userA, s => Svc<CustomerPortalService>(s).PaymentsAsync(new PortalPaymentListRequest()))).Items);
        Assert.Single((await Run(userB, s => Svc<CustomerPortalService>(s).PaymentsAsync(new PortalPaymentListRequest()))).Items);
        Assert.Empty(await Run(userA, s => Svc<CustomerPortalService>(s).SupportRequestsAsync()));
        var ledgerA = await Run(userA, s => Svc<CustomerPortalService>(s).LedgerAsync(null));
        Assert.All(ledgerA.Entries.Items, e => Assert.Equal(a.AgreementId, e.AgreementId));
        Assert.Equal(a.CustomerId, ledgerA.CustomerId);

        // staff without a customer link, a disabled account and a blocked customer cannot use the customer API; customers cannot use staff services
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<CustomerPortalService>(s).DashboardAsync()));
        var disabled = await LoginAsync((await DealAsync(w)).CustomerId, active: false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(disabled, s => Svc<CustomerPortalService>(s).DashboardAsync()));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(userA, s => Svc<PaymentService>(s).ListAsync(new PaymentListRequest())));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(userA, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(b.CustomerId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(userA, s => Svc<CustomerService>(s).GetAsync(b.CustomerId)));
        var blocked = await DealAsync(w);
        var blockedUser = await LoginAsync(blocked.CustomerId);
        await Run(w.Admin, async s => { var db = Svc<AppDbContext>(s); (await db.Customers.FirstAsync(c => c.Id == blocked.CustomerId)).Status = CustomerStatus.Blocked; return await db.SaveChangesAsync(); });
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(blockedUser, s => Svc<CustomerPortalService>(s).DashboardAsync()));
    }

    // ------------------------------------------------------------------------------------------------ reconciliation
    [MySqlFact]
    public async Task Dashboard_Schedule_AndLedgers_ReconcileWithTheBackendLedger()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 3);
        var user = await LoginAsync(d.CustomerId);
        var p = await ProofAsync(user, d, w, 600_000m, "R-" + Guid.NewGuid().ToString("N"), "REF-R1");
        await Run(w.Accountant, async s =>
        {
            var pay = Svc<PaymentService>(s);
            var v = await pay.VerifyAsync(p.Payment.Id, new VerifyPaymentRequest(true, "statement checked", "REF-R1", w.Today, null));
            return await pay.ApproveAsync(v.Id, new ApprovePaymentRequest(null, null, null, null, null, v.RowVersion));
        });

        var dash = await Run(user, s => Svc<CustomerPortalService>(s).DashboardAsync());
        var staffLedger = await Run(w.Accountant, s => Svc<LedgerService>(s).GetAgreementLedgerAsync(d.AgreementId));
        var sum = staffLedger.Summary;
        Assert.Equal(1, dash.TotalProperties);
        Assert.Equal(1, dash.ActiveAgreements);
        Assert.Equal(sum.ContractTotal, dash.TotalAgreementValue);
        Assert.Equal(sum.PaymentsReceived - sum.PaymentsReversed, dash.PaidAmount);
        Assert.Equal(600_000m, dash.PaidAmount);
        Assert.Equal(sum.Outstanding, dash.OutstandingAmount);
        Assert.Equal(sum.Overdue, dash.OverdueAmount);
        Assert.True(dash.OverdueAmount > 0m);
        Assert.Equal(sum.NotYetDue, dash.NotYetDueAmount);
        Assert.NotEmpty(dash.UpcomingDues);
        Assert.True(dash.UpcomingDues[0].IsOverdue);
        Assert.True(dash.UpcomingDues.Zip(dash.UpcomingDues.Skip(1)).All(x => x.First.DueDate <= x.Second.DueDate));

        var schedule = await Run(user, s => Svc<CustomerPortalService>(s).ScheduleAsync(d.AgreementId));
        Assert.Equal(sum.Outstanding, schedule.Summary.OutstandingDue);
        Assert.Equal(sum.Overdue, schedule.Summary.Overdue);
        Assert.Equal(sum.ScheduledTotal, schedule.Summary.TotalAmount);
        Assert.Equal(schedule.Summary.TotalAmount, schedule.Summary.Paid + schedule.Summary.Waived + schedule.Summary.Outstanding);
        Assert.Equal(schedule.Items.Count(i => i.Status == "Overdue"), schedule.Summary.OverdueCount);
        Assert.Contains(schedule.Items, i => i.Status == "Paid");
        Assert.Contains(schedule.Items, i => i.Status == "Overdue");
        Assert.Contains(schedule.Items, i => i.Status == "Upcoming");

        var ledger = await Run(user, s => Svc<CustomerPortalService>(s).LedgerAsync(null));
        Assert.True(ledger.Reconciled);
        Assert.Equal(staffLedger.Summary.LedgerBalance, ledger.Balance);
        var agreementLedger = await Run(user, s => Svc<CustomerPortalService>(s).AgreementLedgerAsync(d.AgreementId, null));
        Assert.True(agreementLedger.Summary.Reconciled);
        Assert.Equal(sum.LedgerBalance, agreementLedger.Summary.LedgerBalance);

        var agreement = await Run(user, s => Svc<CustomerPortalService>(s).AgreementAsync(d.AgreementId));
        Assert.True(agreement.IsHolder);
        Assert.Single(agreement.Properties);
        Assert.False(string.IsNullOrWhiteSpace(agreement.AgreementText));
    }

    // ------------------------------------------------------------------------------------------------ proof, receipt
    [MySqlFact]
    public async Task ProofSubmission_IsIdempotent_Verified_Approved_AndTheCustomerDownloadsTheReceipt()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var key = "K-" + Guid.NewGuid().ToString("N");

        var p = await ProofAsync(user, d, w, 250_000m, key, "REF-P1");
        Assert.Equal(PaymentStatus.PendingVerification, p.Payment.Status);
        Assert.Equal(PaymentSource.CustomerSubmitted, p.Payment.Source);
        Assert.Single(p.Proofs);
        Assert.Equal(0m, p.Allocated);
        // nothing is credited by the submission
        Assert.Equal(0m, (await Run(user, s => Svc<CustomerPortalService>(s).ScheduleAsync(d.AgreementId))).Summary.Paid);

        // a retry with the same key returns the same payment and does not attach the file twice
        var again = await ProofAsync(user, d, w, 250_000m, key, "REF-P1");
        Assert.Equal(p.Payment.Id, again.Payment.Id);
        Assert.Single(again.Proofs);
        Assert.Equal(1, await Run(w.Admin, async s => await Svc<AppDbContext>(s).Payments.CountAsync(x => x.IdempotencyKey == key)));
        // the same key with other values is refused
        await Assert.ThrowsAsync<RequestValidationException>(() => ProofAsync(user, d, w, 260_000m, key, "REF-P1"));

        // validation: amount above the outstanding balance, gateway method, missing file
        await Assert.ThrowsAsync<RequestValidationException>(() => ProofAsync(user, d, w, 50_000_000m, "BIG-" + Guid.NewGuid().ToString("N"), "REF-BIG"));
        var gatewayMethod = await MethodAsync("GATEWAY");
        var bankMethod = await MethodAsync("BANK_TRANSFER");
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(user, s => Svc<CustomerPortalService>(s).SubmitProofAsync(
            new SubmitPaymentProofRequest(d.AgreementId, gatewayMethod, 1_000m, w.Today, "G1", null, null, null, Guid.NewGuid().ToString("N")), Slip())));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(user, s => Svc<CustomerPortalService>(s).SubmitProofAsync(
            new SubmitPaymentProofRequest(d.AgreementId, bankMethod, 1_000m, w.Today, "N1", null, null, null, Guid.NewGuid().ToString("N")), null)));
        await Assert.ThrowsAsync<DomainException>(() => Run(user, s => Svc<CustomerPortalService>(s).SubmitProofAsync(
            new SubmitPaymentProofRequest(d.AgreementId, bankMethod, 1_000m, w.Today, "N2", null, null, null, Guid.NewGuid().ToString("N")), Slip("virus.exe"))));

        // the office sees the proof, verifies and approves; nobody can approve before verification
        var proofs = await Run(w.Accountant, s => Svc<PaymentProofAdminService>(s).ListAsync(p.Payment.Id));
        Assert.Single(proofs);
        var file = await Run(w.Accountant, s => Svc<PaymentProofAdminService>(s).DownloadAsync(p.Payment.Id, proofs[0].Id));
        await using (file.Content) Assert.Equal("slip.png", file.FileName);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(p.Payment.Id, new ApprovePaymentRequest(null, null, null, null, null, null))));
        var posted = await Run(w.Accountant, async s =>
        {
            var pay = Svc<PaymentService>(s);
            var v = await pay.VerifyAsync(p.Payment.Id, new VerifyPaymentRequest(true, "slip matches bank statement", "REF-P1", w.Today, null));
            return await pay.ApproveAsync(v.Id, new ApprovePaymentRequest(null, null, null, null, null, v.RowVersion));
        });
        Assert.Equal(PaymentStatus.Posted, posted.Status);

        var detail = await Run(user, s => Svc<CustomerPortalService>(s).PaymentAsync(p.Payment.Id));
        Assert.Equal(posted.Receipt!.ReceiptNumber, detail.Payment.ReceiptNumber);
        Assert.Equal(250_000m, detail.Allocated + detail.Advance + detail.Unallocated);
        var pdf = await Run(user, s => Svc<CustomerPortalService>(s).ReceiptPdfAsync(detail.Payment.ReceiptId!.Value));
        using var ms = new MemoryStream();
        await pdf.Content.CopyToAsync(ms);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
        var own = await Run(user, s => Svc<CustomerPortalService>(s).ProofAsync(p.Payment.Id, detail.Proofs[0].Id));
        await using (own.Content) Assert.Equal("slip.png", own.FileName);

        // the customer was notified of the approval
        var notes = await Run(user, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()));
        Assert.Contains(notes.Notifications.Items, n => n.Kind == NotificationKind.Payment && n.Title == "Payment received");
        Assert.True(notes.Unread >= 2);
        await Do(user, s => Svc<CustomerPortalService>(s).MarkAllNotificationsReadAsync());
        Assert.Equal(0, (await Run(user, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()))).Unread);
    }

    // ------------------------------------------------------------------------------------------------ profile, instructions, announcements
    [MySqlFact]
    public async Task Profile_Instructions_Announcements_AndNotifications()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var other = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);

        var profile = await Run(user, s => Svc<CustomerPortalService>(s).ProfileAsync());
        Assert.StartsWith("*********", profile.IdentityNumberMasked);
        var updated = await Run(user, s => Svc<CustomerPortalService>(s).UpdateProfileAsync(new UpdatePortalProfileRequest("0345-1112223", "new@example.com", "Street 5")));
        Assert.Equal("0345-1112223", updated.Mobile);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(user, s => Svc<CustomerPortalService>(s).UpdateProfileAsync(new UpdatePortalProfileRequest("abc", null, null))));
        Assert.True(await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.AnyAsync(a => a.Action == "Customer.ProfileUpdatedByCustomer" && a.EntityId == d.CustomerId.ToString())));

        // payment instructions (company-wide note: needs all-project access) and active bank accounts
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Support, s => Svc<PortalContentService>(s).SaveInstructionsAsync(new PaymentInstructionsNoteDto("x"))));
        await Run(w.Director, s => Svc<PortalContentService>(s).SaveInstructionsAsync(new PaymentInstructionsNoteDto("Pay to the company account and send the slip.")));
        var instructions = await Run(user, s => Svc<CustomerPortalService>(s).InstructionsAsync());
        Assert.Equal("Pay to the company account and send the slip.", instructions.Note);
        Assert.Contains(instructions.Methods, m => m.Code == "BANK_TRANSFER");
        Assert.DoesNotContain(instructions.Methods, m => m.Kind is PaymentMethodKind.PaymentGateway or PaymentMethodKind.Card);
        Assert.False(instructions.OnlinePaymentsEnabled);

        // announcements: company-wide and project-specific are visible, expired/inactive and other projects are not
        await Run(w.Director, async s =>
        {
            var c = Svc<PortalContentService>(s);
            await c.SaveAnnouncementAsync(null, new SaveAnnouncementRequest("Office closed", "Closed on Friday", null, null, null, true, null));
            await c.SaveAnnouncementAsync(null, new SaveAnnouncementRequest("Project news", "Roads done", w.ProjectId, null, null, true, null));
            await c.SaveAnnouncementAsync(null, new SaveAnnouncementRequest("Hidden", "inactive", null, null, null, false, null));
            await c.SaveAnnouncementAsync(null, new SaveAnnouncementRequest("Future", "later", null, DateTime.UtcNow.AddDays(3), null, true, null));
            return 0;
        });
        var titles = (await Run(user, s => Svc<CustomerPortalService>(s).AnnouncementsAsync())).Select(a => a.Title).ToList();
        Assert.Contains("Office closed", titles);
        Assert.Contains("Project news", titles);
        Assert.DoesNotContain("Hidden", titles);
        Assert.DoesNotContain("Future", titles);
        // a customer of another project would not see "Project news"
        var outsider = await Run(w.Admin, async s =>
        {
            var pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
            await Svc<AppDbContext>(s).Announcements.AddAsync(new Announcement { Title = "Elsewhere", Body = "x", ProjectId = pid, PublishFromUtc = DateTime.UtcNow.AddMinutes(-1), IsActive = true });
            await Svc<AppDbContext>(s).SaveChangesAsync();
            return pid;
        });
        Assert.DoesNotContain("Elsewhere", (await Run(user, s => Svc<CustomerPortalService>(s).AnnouncementsAsync())).Select(a => a.Title));
        Assert.True(outsider > 0);

        // an office notification reaches only the addressed customer
        await Do(w.Support, s => Svc<PortalContentService>(s).SendNotificationAsync(new CreateNotificationRequest(d.CustomerId, "Hello", "A message from the office")));
        Assert.Contains((await Run(user, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()))).Notifications.Items, n => n.Title == "Hello");
        var otherUser = await LoginAsync(other.CustomerId);
        Assert.DoesNotContain((await Run(otherUser, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()))).Notifications.Items, n => n.Title == "Hello");
        var mine = (await Run(user, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()))).Notifications.Items.First(n => n.Title == "Hello");
        await Assert.ThrowsAsync<NotFoundException>(() => Do(otherUser, s => Svc<CustomerPortalService>(s).MarkNotificationReadAsync(mine.Id)));
    }

    // ------------------------------------------------------------------------------------------------ customer accounts and password reset
    [MySqlFact]
    public async Task CustomerAccounts_AreCreatedAndDisabledByTheOffice_AndUserAdministrationCannotTouchThem()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var email = $"cust{Guid.NewGuid():N}@example.com";
        const string Password = "Cust0mer#Passw0rd!";

        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<CustomerAccountService>(s).CreateAsync(d.CustomerId, new CreateCustomerAccountRequest(email, Password))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Support, s => Svc<CustomerAccountService>(s).CreateAsync(d.CustomerId, new CreateCustomerAccountRequest(email, "short"))));
        var account = await Run(w.Support, s => Svc<CustomerAccountService>(s).CreateAsync(d.CustomerId, new CreateCustomerAccountRequest(email, Password)));
        lock (Names) Names[account.UserId] = email;
        Assert.True(account.IsActive);
        Assert.Equal(email, account.LoginEmail);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Support, s => Svc<CustomerAccountService>(s).CreateAsync(d.CustomerId, new CreateCustomerAccountRequest("other" + email, Password))));

        using (var s = fx.Scope())
        {
            var identity = Svc<IIdentityService>(s);
            var (status, info) = await identity.CheckPasswordAsync(email, Password);
            Assert.Equal(CredentialStatus.Valid, status);
            Assert.Contains(RoleNames.Customer, await identity.GetRolesAsync(info!.Id));
            // the login works for the customer API and only for it
            fx.Act(info.Id); fx.Actor.UserName = email;
            Assert.StartsWith("CUS-", (await Svc<CustomerPortalService>(s).ProfileAsync()).CustomerCode);
        }
        // the Customer role cannot be assigned or edited through user administration
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Admin, s => Svc<UserAdministrationService>(s).CreateAsync(new CreateUserRequest($"x{email}", "Staff", null, Password, true, [RoleNames.Customer]))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Admin, s => Svc<UserAdministrationService>(s).UpdateAsync(account.UserId, new UpdateUserRequest("Renamed", null, true, []))));
        Assert.DoesNotContain(await Run(w.Admin, s => Svc<UserAdministrationService>(s).ListAssignableRolesAsync()), r => r.Name == RoleNames.Customer);

        // disabling blocks sign-in and the customer API; enabling restores both
        var disabled = await Run(w.Support, s => Svc<CustomerAccountService>(s).DisableAsync(d.CustomerId, new DisableCustomerAccountRequest("left the project", account.RowVersion)));
        Assert.False(disabled.IsActive);
        using (var s = fx.Scope())
        {
            Assert.Equal(CredentialStatus.Inactive, (await Svc<IIdentityService>(s).CheckPasswordAsync(email, Password)).Status);
            fx.Act(account.UserId); fx.Actor.UserName = email;
            await Assert.ThrowsAsync<ForbiddenException>(() => Svc<CustomerPortalService>(s).DashboardAsync());
        }
        var enabled = await Run(w.Support, s => Svc<CustomerAccountService>(s).EnableAsync(d.CustomerId, disabled.RowVersion));
        Assert.True(enabled.IsActive);
        using (var s = fx.Scope()) Assert.Equal(CredentialStatus.Valid, (await Svc<IIdentityService>(s).CheckPasswordAsync(email, Password)).Status);

        // password change by the customer and administrative reset
        await Do(account.UserId, s => Svc<CustomerPortalService>(s).ChangePasswordAsync(new ChangePasswordRequestDto(Password, "Ch@nged-Passw0rd-1")));
        using (var s = fx.Scope()) Assert.Equal(CredentialStatus.Valid, (await Svc<IIdentityService>(s).CheckPasswordAsync(email, "Ch@nged-Passw0rd-1")).Status);
        await Assert.ThrowsAsync<RequestValidationException>(() => Do(account.UserId, s => Svc<CustomerPortalService>(s).ChangePasswordAsync(new ChangePasswordRequestDto("wrong-password", "An0ther#Passw0rd!"))));
        await Do(w.Support, s => Svc<CustomerAccountService>(s).ResetPasswordAsync(d.CustomerId, new ResetCustomerPasswordRequest("Reset#Passw0rd-77")));
        using (var s = fx.Scope()) Assert.Equal(CredentialStatus.Valid, (await Svc<IIdentityService>(s).CheckPasswordAsync(email, "Reset#Passw0rd-77")).Status);
    }

    private sealed class CapturingNotifier : IPasswordResetNotifier
    {
        public PasswordResetToken? Last { get; private set; }
        public int Count { get; private set; }
        public Task SendAsync(PasswordResetToken token, CancellationToken ct = default) { Last = token; Count++; return Task.CompletedTask; }
    }

    [MySqlFact]
    public async Task ForgotPassword_NeverRevealsAccounts_AndResetEndsSessions()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var email = $"reset{Guid.NewGuid():N}@example.com";
        await Run(w.Support, s => Svc<CustomerAccountService>(s).CreateAsync(d.CustomerId, new CreateCustomerAccountRequest(email, "Fir5t#Passw0rd-xyz")));
        var notifier = new CapturingNotifier();
        PasswordResetService Service(IServiceScope s) => new(Svc<IIdentityService>(s), Svc<IRefreshTokenService>(s), notifier, Svc<IAuditService>(s),
            Svc<FluentValidation.IValidator<ForgotPasswordRequest>>(s), Svc<FluentValidation.IValidator<ResetPasswordRequest>>(s));

        // unknown e-mail: same outcome (no exception, nothing sent)
        using (var s = fx.Scope()) await Service(s).RequestAsync(new ForgotPasswordRequest("nobody@example.com"));
        Assert.Equal(0, notifier.Count);
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Service(s).RequestAsync(new ForgotPasswordRequest("not-an-email")));

        using (var s = fx.Scope()) await Service(s).RequestAsync(new ForgotPasswordRequest(email));
        Assert.Equal(1, notifier.Count);
        var token = notifier.Last!.Token;

        // a session exists before the reset
        TokenResponse? session;
        using (var s = fx.Scope()) session = (await Svc<AuthService>(s).LoginAsync(new LoginRequest(email, "Fir5t#Passw0rd-xyz"))).Value;
        Assert.NotNull(session);

        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Service(s).ResetAsync(new ResetPasswordRequest(email, "bad-token", "N3w#Passw0rd-xyz!")));
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Service(s).ResetAsync(new ResetPasswordRequest(email, token, "weak")));
        using (var s = fx.Scope()) await Service(s).ResetAsync(new ResetPasswordRequest(email, token, "N3w#Passw0rd-xyz!"));
        using (var s = fx.Scope())
        {
            var identity = Svc<IIdentityService>(s);
            Assert.Equal(CredentialStatus.Valid, (await identity.CheckPasswordAsync(email, "N3w#Passw0rd-xyz!")).Status);
            Assert.Equal(CredentialStatus.Invalid, (await identity.CheckPasswordAsync(email, "Fir5t#Passw0rd-xyz")).Status);
            // the old refresh token no longer works
            Assert.False((await Svc<AuthService>(s).RefreshAsync(new RefreshRequest(session!.RefreshToken))).Succeeded);
        }
        // the code can be used only once
        using (var s = fx.Scope()) await Assert.ThrowsAsync<RequestValidationException>(() => Service(s).ResetAsync(new ResetPasswordRequest(email, token, "An0ther#Passw0rd-1!")));
    }

    // ------------------------------------------------------------------------------------------------ support
    [MySqlFact]
    public async Task SupportRequests_FlowBetweenCustomerAndOffice_WithNotifications()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var created = await Run(user, s => Svc<CustomerPortalService>(s).CreateSupportRequestAsync(new CreateSupportRequestRequest(SupportCategory.Payment, "Receipt missing", "I did not get my receipt.", d.AgreementId)));
        Assert.StartsWith("SUP-", created.Request.RequestNumber);
        Assert.Equal(SupportRequestStatus.Open, created.Request.Status);
        Assert.Single(created.Messages);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(user, s => Svc<CustomerPortalService>(s).CreateSupportRequestAsync(new CreateSupportRequestRequest(SupportCategory.General, "x", "", null))));

        // the desk sees it (project-scoped), the cashier role does not have access
        var list = await Run(w.Support, s => Svc<SupportDeskService>(s).ListAsync(new SupportListRequest()));
        Assert.Contains(list.Items, x => x.Id == created.Request.Id && x.CustomerCode.StartsWith("CUS-"));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<SupportDeskService>(s).ListAsync(new SupportListRequest())));

        var replied = await Run(w.Support, s => Svc<SupportDeskService>(s).ReplyAsync(created.Request.Id, new SupportStaffReplyRequest("We will email it today.", SupportRequestStatus.Resolved, created.RowVersion)));
        Assert.Equal(SupportRequestStatus.Resolved, replied.Request.Status);
        Assert.Equal(2, replied.Messages.Count);
        var mine = await Run(user, s => Svc<CustomerPortalService>(s).SupportDetailAsync(created.Request.Id));
        Assert.Equal(SupportAuthorType.Staff, mine.Messages[1].AuthorType);
        Assert.Contains((await Run(user, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()))).Notifications.Items, n => n.Kind == NotificationKind.Support);

        // a stale desk update fails, a customer reply reopens a resolved request, a closed request takes no replies
        await Assert.ThrowsAsync<ConcurrencyException>(() => Run(w.Support, s => Svc<SupportDeskService>(s).SetStatusAsync(created.Request.Id, new SupportStatusRequest(SupportRequestStatus.Closed, created.RowVersion))));
        var reopened = await Run(user, s => Svc<CustomerPortalService>(s).ReplySupportAsync(created.Request.Id, new SupportReplyRequest("Still nothing, please check.")));
        Assert.Equal(SupportRequestStatus.Open, reopened.Request.Status);
        var closed = await Run(w.Support, s => Svc<SupportDeskService>(s).SetStatusAsync(created.Request.Id, new SupportStatusRequest(SupportRequestStatus.Closed, reopened.RowVersion)));
        Assert.Equal(SupportRequestStatus.Closed, closed.Request.Status);
        await Assert.ThrowsAsync<DomainException>(() => Run(user, s => Svc<CustomerPortalService>(s).ReplySupportAsync(created.Request.Id, new SupportReplyRequest("more"))));
    }

    // ------------------------------------------------------------------------------------------------ payment gateway
    private sealed class FakeGateway : IPaymentGateway
    {
        public string ProviderCode => "fake";
        public bool SupportsRefunds { get; set; } = true;
        public GatewayPaymentState ProviderState { get; set; } = GatewayPaymentState.Pending;
        public decimal? ProviderAmount { get; set; }
        public bool Reachable { get; set; } = true;
        public int RefundCalls { get; private set; }
        public Task<GatewayInitiationResult> InitiateAsync(GatewayInitiationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new GatewayInitiationResult(true, "FK-" + Guid.NewGuid().ToString("N")[..12], "https://pay.example/" + request.TransactionNumber, null, null));
        public GatewayCallback ParseCallback(string payload, IReadOnlyDictionary<string, string> headers)
        {
            var ok = headers.TryGetValue("X-Signature", out var sig) && sig == "valid";
            var parts = payload.Split('|'); // eventId|reference|state
            return ok ? new GatewayCallback(true, parts[0], parts[1], null, Enum.Parse<GatewayPaymentState>(parts[2]), null, null, null, null)
                      : new GatewayCallback(false, parts[0], parts[1], null, GatewayPaymentState.Unknown, null, null, null, null);
        }
        public Task<GatewayStatusResult> QueryStatusAsync(string providerReference, CancellationToken ct = default) =>
            Reachable ? Task.FromResult(new GatewayStatusResult(true, ProviderState, ProviderAmount, "PKR", ProviderState == GatewayPaymentState.Failed ? "Card declined" : null))
                      : throw new HttpRequestException("down");
        public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default) { RefundCalls++; return Task.FromResult(new GatewayRefundResult(true, "RF-" + RefundCalls, null)); }
    }

    private sealed class FakeRegistry(FakeGateway gateway, bool enabled = true) : IPaymentGatewayRegistry
    {
        public bool Enabled => enabled;
        public string? ActiveProviderCode => "fake";
        public IPaymentGateway? Find(string providerCode) => providerCode == "fake" ? gateway : null;
    }

    private static readonly Dictionary<string, string> Valid = new() { ["X-Signature"] = "valid" };

    private async Task<GatewayTransaction> InitiateAsync(Guid customerUser, FakeRegistry registry, Deal d, decimal amount, string key)
    {
        using var s = fx.Scope();
        As(customerUser);
        var service = new PaymentInitiationService(Svc<IAppDbContext>(s), registry, Svc<PaymentSupport>(s), Svc<IAuditService>(s));
        return await service.InitiateAsync(d.CustomerId, customerUser, d.AgreementId, amount, key);
    }

    private async Task<CallbackResult> CallbackAsync(FakeRegistry registry, string eventId, string reference, string state, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var s = fx.Scope();
        var verification = new PaymentVerificationService(Svc<IAppDbContext>(s), registry, Svc<PaymentSupport>(s), Svc<IAuditService>(s));
        var handler = new PaymentCallbackHandler(Svc<IAppDbContext>(s), registry, verification, TimeProvider.System);
        return await handler.HandleAsync("fake", $"{eventId}|{reference}|{state}", headers ?? Valid, "10.0.0.1");
    }

    private async Task<List<Payment>> PaymentsOfAsync(Deal d) => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).Payments.AsNoTracking().Where(p => p.AgreementId == d.AgreementId).ToListAsync());

    [MySqlFact]
    public async Task Gateway_Initiation_IsIdempotent_AndRespectsConfiguration()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var gw = new FakeGateway();
        var key = "I-" + Guid.NewGuid().ToString("N");

        var t = await InitiateAsync(user, new FakeRegistry(gw), d, 100_000m, key);
        Assert.StartsWith("GW-", t.TransactionNumber);
        Assert.Equal(GatewayTransactionStatus.Pending, t.Status);
        Assert.NotNull(t.ProviderReference);
        Assert.NotNull(t.RedirectUrl);
        var again = await InitiateAsync(user, new FakeRegistry(gw), d, 100_000m, key);
        Assert.Equal(t.Id, again.Id);
        await Assert.ThrowsAsync<RequestValidationException>(() => InitiateAsync(user, new FakeRegistry(gw), d, 90_000m, key));
        await Assert.ThrowsAsync<RequestValidationException>(() => InitiateAsync(user, new FakeRegistry(gw), d, 90_000_000m, "BIG-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<DomainException>(() => InitiateAsync(user, new FakeRegistry(gw, enabled: false), d, 1_000m, "OFF-" + Guid.NewGuid().ToString("N")));
        // another customer cannot replay the key
        var other = await DealAsync(w);
        var otherUser = await LoginAsync(other.CustomerId);
        await Assert.ThrowsAsync<RequestValidationException>(() => InitiateAsync(otherUser, new FakeRegistry(gw), other, 100_000m, key));
        // nothing was credited
        Assert.Empty(await PaymentsOfAsync(d));
        // by default (no provider configured) the customer API reports online payments as unavailable
        await Assert.ThrowsAsync<DomainException>(() => Run(user, s => Svc<CustomerPortalService>(s).InitiateOnlinePaymentAsync(new InitiateOnlinePaymentRequest(d.AgreementId, 1_000m, Guid.NewGuid().ToString("N")))));
    }

    [MySqlFact]
    public async Task Gateway_Callbacks_AreVerified_Logged_AndCreateExactlyOnePaymentAwaitingApproval()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var gw = new FakeGateway { ProviderAmount = 100_000m };
        var reg = new FakeRegistry(gw);
        var t = await InitiateAsync(user, reg, d, 100_000m, "C-" + Guid.NewGuid().ToString("N"));

        // wrong signature: rejected, logged, nothing created
        var bad = await CallbackAsync(reg, "E-bad-" + t.Id, t.ProviderReference!, "Succeeded", new Dictionary<string, string> { ["X-Signature"] = "forged" });
        Assert.Equal(CallbackOutcome.InvalidSignature, bad.Outcome);
        Assert.Empty(await PaymentsOfAsync(d));
        // unknown provider and unknown transaction
        using (var s = fx.Scope())
        {
            var handler = new PaymentCallbackHandler(Svc<IAppDbContext>(s), reg, new PaymentVerificationService(Svc<IAppDbContext>(s), reg, Svc<PaymentSupport>(s), Svc<IAuditService>(s)), TimeProvider.System);
            Assert.Equal(CallbackOutcome.UnknownProvider, (await handler.HandleAsync("other", "e|r|Succeeded", Valid, null)).Outcome);
        }
        Assert.Equal(CallbackOutcome.UnknownTransaction, (await CallbackAsync(reg, "E-unk-" + t.Id, "NO-SUCH-REF", "Succeeded")).Outcome);

        // the callback claims success but the provider still says pending: the provider wins
        var claim = await CallbackAsync(reg, "E-claim-" + t.Id, t.ProviderReference!, "Succeeded");
        Assert.Equal(CallbackOutcome.Processed, claim.Outcome);
        Assert.Empty(await PaymentsOfAsync(d));

        // the provider confirms: one payment awaiting approval, verified by the gateway
        gw.ProviderState = GatewayPaymentState.Succeeded;
        Assert.Equal(CallbackOutcome.Processed, (await CallbackAsync(reg, "E-ok-" + t.Id, t.ProviderReference!, "Succeeded")).Outcome);
        Assert.Equal(CallbackOutcome.Duplicate, (await CallbackAsync(reg, "E-ok-" + t.Id, t.ProviderReference!, "Succeeded")).Outcome);
        Assert.Equal(CallbackOutcome.Processed, (await CallbackAsync(reg, "E-ok2-" + t.Id, t.ProviderReference!, "Succeeded")).Outcome);
        var payments = await PaymentsOfAsync(d);
        var p = Assert.Single(payments);
        Assert.Equal(PaymentStatus.PendingApproval, p.Status);
        Assert.Equal(PaymentSource.CustomerSubmitted, p.Source);
        Assert.Equal(PaymentMethodKind.PaymentGateway, p.MethodKind);
        Assert.Equal(100_000m, p.Amount);
        Assert.Equal(t.ProviderReference, p.Reference);
        Assert.Equal(d.CustomerId, p.CustomerId);

        var (tx, verifications, logs) = await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            return (await db.GatewayTransactions.AsNoTracking().FirstAsync(x => x.Id == t.Id), await db.PaymentVerifications.AsNoTracking().Where(v => v.PaymentId == p.Id).ToListAsync(),
                await db.PaymentWebhookLogs.AsNoTracking().Where(l => l.Provider == "fake").ToListAsync());
        });
        Assert.Equal(GatewayTransactionStatus.Succeeded, tx.Status);
        Assert.Equal(p.Id, tx.PaymentId);
        Assert.Single(verifications);
        Assert.Equal(VerificationResult.Verified, verifications[0].Result);
        Assert.Contains(logs, l => l.ProcessingStatus == WebhookProcessingStatus.Rejected && !l.SignatureValid);
        Assert.Contains(logs, l => l.ProcessingStatus == WebhookProcessingStatus.Duplicate);
        Assert.Contains(logs, l => l.ProcessingStatus == WebhookProcessingStatus.Processed);
        // nothing is credited until the office approves
        Assert.Equal(0m, (await Run(user, s => Svc<CustomerPortalService>(s).ScheduleAsync(d.AgreementId))).Summary.Paid);
        var posted = await Run(w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(p.Id, new ApprovePaymentRequest(null, null, null, null, null, null)));
        Assert.Equal(PaymentStatus.Posted, posted.Status);
        Assert.Equal(100_000m, (await Run(user, s => Svc<CustomerPortalService>(s).ScheduleAsync(d.AgreementId))).Summary.Paid);
        var shown = await Run(user, s => Svc<CustomerPortalService>(s).OnlinePaymentAsync(t.Id));
        Assert.Equal(GatewayTransactionStatus.Succeeded, shown.Status);
        Assert.Equal(posted.PaymentNumber, shown.PaymentNumber);
        var stranger = await LoginAsync((await DealAsync(w)).CustomerId);
        await Assert.ThrowsAsync<NotFoundException>(() => Run(stranger, s => Svc<CustomerPortalService>(s).OnlinePaymentAsync(t.Id)));

        // the office can list transactions and callbacks (callbacks need company-wide access)
        var list = await Run(w.Accountant, s => Svc<GatewayQueryService>(s).ListAsync(new GatewayListRequest { AgreementId = d.AgreementId }));
        Assert.Equal(t.Id, Assert.Single(list.Items).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<GatewayQueryService>(s).WebhooksAsync(new WebhookLogListRequest())));
        Assert.NotEmpty((await Run(w.Admin, s => Svc<GatewayQueryService>(s).WebhooksAsync(new WebhookLogListRequest { Provider = "fake" }))).Items);
    }

    [MySqlFact]
    public async Task Gateway_ConcurrentCallbacks_CreateOnePayment_AndMismatchesCreateNone()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var gw = new FakeGateway { ProviderAmount = 120_000m, ProviderState = GatewayPaymentState.Succeeded };
        var reg = new FakeRegistry(gw);
        var t = await InitiateAsync(user, reg, d, 120_000m, "Z-" + Guid.NewGuid().ToString("N"));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => CallbackAsync(reg, $"E-par-{t.Id}-{i}", t.ProviderReference!, "Succeeded"))));
        Assert.All(results, r => Assert.Equal(CallbackOutcome.Processed, r.Outcome));
        Assert.Single(await PaymentsOfAsync(d));

        // the provider reports another amount than expected: no payment, a note for the office
        var d2 = await DealAsync(w);
        var user2 = await LoginAsync(d2.CustomerId);
        var gw2 = new FakeGateway { ProviderAmount = 99_999m, ProviderState = GatewayPaymentState.Succeeded };
        var reg2 = new FakeRegistry(gw2);
        var t2 = await InitiateAsync(user2, reg2, d2, 100_000m, "M-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(CallbackOutcome.Processed, (await CallbackAsync(reg2, "E-mis-" + t2.Id, t2.ProviderReference!, "Succeeded")).Outcome);
        Assert.Empty(await PaymentsOfAsync(d2));
        var note = await Run(w.Admin, async s => (await Svc<AppDbContext>(s).GatewayTransactions.AsNoTracking().FirstAsync(x => x.Id == t2.Id)).ReconciliationNote);
        Assert.Contains("mismatch", note, StringComparison.OrdinalIgnoreCase);

        // a failed payment closes the transaction and tells the customer
        var d3 = await DealAsync(w);
        var user3 = await LoginAsync(d3.CustomerId);
        var gw3 = new FakeGateway { ProviderState = GatewayPaymentState.Failed };
        var reg3 = new FakeRegistry(gw3);
        var t3 = await InitiateAsync(user3, reg3, d3, 50_000m, "F-" + Guid.NewGuid().ToString("N"));
        await CallbackAsync(reg3, "E-fail-" + t3.Id, t3.ProviderReference!, "Failed");
        Assert.Empty(await PaymentsOfAsync(d3));
        var failed = await Run(user3, s => Svc<CustomerPortalService>(s).NotificationsAsync(new PagedRequest()));
        Assert.Contains(failed.Notifications.Items, n => n.Title.Contains("not completed"));
        Assert.Equal(GatewayTransactionStatus.Failed, (await Run(w.Admin, async s => await Svc<AppDbContext>(s).GatewayTransactions.AsNoTracking().FirstAsync(x => x.Id == t3.Id))).Status);
    }

    [MySqlFact]
    public async Task Gateway_Reconciliation_FindsMissedCallbacks_ClosesAbandoned_AndRefundsOnlyReversedPayments()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var user = await LoginAsync(d.CustomerId);
        var gw = new FakeGateway { ProviderAmount = 80_000m, ProviderState = GatewayPaymentState.Succeeded };
        var reg = new FakeRegistry(gw);
        var missed = await InitiateAsync(user, reg, d, 80_000m, "RC-" + Guid.NewGuid().ToString("N"));
        var young = await InitiateAsync(user, reg, d, 10_000m, "RY-" + Guid.NewGuid().ToString("N"));
        // an initiation that never reached the provider and is old
        long abandonedId;
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var support = Svc<PaymentSupport>(s);
            var ag = await support.InfoAsync(d.AgreementId);
            var ab = new GatewayTransaction
            {
                TransactionNumber = "NEW-" + Guid.NewGuid().ToString("N")[..20], IdempotencyKey = "AB-" + Guid.NewGuid().ToString("N"), RequestHash = new string('a', 64), Provider = "fake", CustomerId = d.CustomerId,
                AgreementId = d.AgreementId, ProjectId = ag.ProjectId, InitiatedByUserId = user, Amount = 5_000m, CurrencyCode = "PKR", InitiatedAtUtc = DateTime.UtcNow.AddHours(-3),
            };
            db.GatewayTransactions.Add(ab);
            await db.SaveChangesAsync();
            ab.TransactionNumber = $"GW-{ab.Id:D6}";
            // the missed one is old enough to be reconciled, the young one is within the grace period
            (await db.GatewayTransactions.FirstAsync(x => x.Id == missed.Id)).InitiatedAtUtc = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
            abandonedId = ab.Id;
        }
        ReconciliationResultDto result;
        using (var s = fx.Scope())
        {
            As(w.Accountant);
            var verification = new PaymentVerificationService(Svc<IAppDbContext>(s), reg, Svc<PaymentSupport>(s), Svc<IAuditService>(s));
            var service = new GatewayReconciliationService(Svc<IAppDbContext>(s), verification, Svc<IPermissionService>(s), Svc<ICurrentUser>(s), Svc<PaymentSupport>(s), Svc<IAuditService>(s));
            result = await service.ReconcileAsync();
            await Assert.ThrowsAsync<ForbiddenException>(async () => { As(w.Sales); await service.ReconcileAsync(); });
        }
        Assert.Equal(2, result.Checked);
        Assert.Contains(result.Items, i => i.TransactionId == missed.Id && i.After == GatewayTransactionStatus.Succeeded);
        Assert.Contains(result.Items, i => i.TransactionId == abandonedId && i.After == GatewayTransactionStatus.Failed);
        var payment = Assert.Single(await PaymentsOfAsync(d));
        Assert.Equal(80_000m, payment.Amount);
        Assert.Equal(GatewayTransactionStatus.Pending, (await Run(w.Admin, async s => await Svc<AppDbContext>(s).GatewayTransactions.AsNoTracking().FirstAsync(x => x.Id == young.Id))).Status);
        // running it again changes nothing and never duplicates the payment
        using (var s = fx.Scope())
        {
            As(w.Accountant);
            var verification = new PaymentVerificationService(Svc<IAppDbContext>(s), reg, Svc<PaymentSupport>(s), Svc<IAuditService>(s));
            var service = new GatewayReconciliationService(Svc<IAppDbContext>(s), verification, Svc<IPermissionService>(s), Svc<ICurrentUser>(s), Svc<PaymentSupport>(s), Svc<IAuditService>(s));
            Assert.Equal(0, (await service.ReconcileAsync()).Changed);
        }
        Assert.Single(await PaymentsOfAsync(d));

        // refund: not while the ERP payment is pending or posted; allowed after the payment is reversed
        async Task<GatewayTransactionDto> Refund(FakeGateway g, long id, decimal amount)
        {
            using var s = fx.Scope();
            As(w.Accountant);
            var query = Svc<GatewayQueryService>(s);
            var service = new GatewayRefundService(Svc<IAppDbContext>(s), new FakeRegistry(g), Svc<IPermissionService>(s), Svc<ICurrentUser>(s), Svc<PaymentSupport>(s), Svc<IAuditService>(s), query,
                new GatewayRefundRequestDtoValidator());
            return await service.RefundAsync(id, new GatewayRefundRequestDto(amount, "customer paid twice", null));
        }
        await Assert.ThrowsAsync<DomainException>(() => Refund(gw, missed.Id, 80_000m)); // payment still pending approval
        var posted = await Run(w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(payment.Id, new ApprovePaymentRequest(null, null, null, null, null, null)));
        await Assert.ThrowsAsync<DomainException>(() => Refund(gw, missed.Id, 80_000m)); // posted: the money is in the ledger
        await Run(w.Accountant, s => Svc<PaymentService>(s).ReverseAsync(posted.Id, new ReversePaymentRequest("duplicate payment", posted.RowVersion)));
        await Assert.ThrowsAsync<DomainException>(() => Refund(new FakeGateway { SupportsRefunds = false }, missed.Id, 10_000m));
        await Assert.ThrowsAsync<RequestValidationException>(() => Refund(gw, missed.Id, 90_000m));
        var partial = await Refund(gw, missed.Id, 30_000m);
        Assert.Equal(30_000m, partial.RefundedAmount);
        Assert.Equal(GatewayTransactionStatus.Succeeded, partial.Status);
        var full = await Refund(gw, missed.Id, 50_000m);
        Assert.Equal(80_000m, full.RefundedAmount);
        Assert.Equal(GatewayTransactionStatus.Refunded, full.Status);
        Assert.Equal(2, gw.RefundCalls);
        await Assert.ThrowsAsync<DomainException>(() => Refund(gw, missed.Id, 1m));
    }
}
