using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Recovery;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 6 integration tests against MySQL: payment workflow, allocation, idempotency, concurrency, receipts, ledgers, refunds and recovery.</summary>
[Collection("phase1")]
public class Phase06Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Sales, Guid Director, Guid Accountant, Guid Cashier, Guid Officer, long ProjectId, DateOnly Today);
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

    private async Task<World> WorldAsync()
    {
        var admin = await UserAsync(RoleNames.Administrator);
        As(admin);
        long pid; DateOnly today;
        using (var s = fx.Scope()) pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        var cashier = await UserAsync(RoleNames.Cashier);
        var officer = await UserAsync(RoleNames.RecoveryOfficer);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { sales, accountant, cashier, officer }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, accountant, cashier, officer, pid, today);
    }

    private async Task<long> CustomerAsync(World w) => await Run(w.Sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
        [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed installment booking (5 marla x 1,000,000 = 5,000,000, down payment 500,000) with a generated schedule:
    /// down payment 500,000 today, 24 monthly installments of 162,500 from next month, possession 500,000 at month 30, development 100,000 at month 12.
    /// With <paramref name="monthsBack"/> the due dates are moved back so that some lines are overdue.</summary>
    private async Task<Deal> DealAsync(World w, int monthsBack = 0, IReadOnlyList<OwnerInput>? owners = null)
    {
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"PY-{Guid.NewGuid():N}"[..14], "Y" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
        var c = owners?[0].CustomerId ?? await CustomerAsync(w);
        var b = await Run(w.Sales, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, PaymentMode.Installment, [new BookingPropertyInput(propertyId, 0m, null)],
            owners ?? [new OwnerInput(c, 100m)], null, 500_000m, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        var agreementId = b.AgreementId!.Value;
        var types = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ChargeTypes.ToDictionaryAsync(t => t.Code, t => t.Id));
        PlanComponentInput Comp(string type, string name, AmountBasis basis, decimal? amount, decimal? pct, ScheduleFrequency f, int? count, int offset, ChargeCategory cat = ChargeCategory.SaleConsideration) =>
            new(types[type], name, cat, basis, amount, pct, f, count, null, offset, 0, null, null, 5, null, true, 50m, TaxTreatment.NotApplicable, 0m, null, null);
        var code = "Q" + Interlocked.Increment(ref counter);
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

    private async Task<long> MethodAsync(string code) => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == code).Select(m => m.Id).FirstAsync());

    private async Task<PaymentDetailDto> SubmitAsync(World w, Deal d, decimal amount, string method = "CASH", string? reference = null, AllocationMode mode = AllocationMode.OldestDueFirst,
        IReadOnlyList<AllocationLineInput>? lines = null, long? chargeId = null, string? key = null, Guid? actor = null, PaymentSource source = PaymentSource.Office, bool toAdvance = false)
    {
        var methodId = await MethodAsync(method);
        return await Run(actor ?? w.Cashier, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, null, methodId, source, amount, w.Today, reference,
            method == "CHEQUE" ? "HBL" : null, method == "CHEQUE" ? w.Today : null, null, null, mode, chargeId, lines, toAdvance, key ?? Guid.NewGuid().ToString("N"))));
    }

    private async Task<PaymentDetailDto> ApproveAsync(World w, PaymentDetailDto p, Guid? actor = null, ApprovePaymentRequest? r = null) =>
        await Run(actor ?? w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(p.Id, r ?? new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion)));

    private async Task<PaymentDetailDto> PostAsync(World w, Deal d, decimal amount, AllocationMode mode = AllocationMode.OldestDueFirst, IReadOnlyList<AllocationLineInput>? lines = null,
        long? chargeId = null, bool toAdvance = false) => await ApproveAsync(w, await SubmitAsync(w, d, amount, mode: mode, lines: lines, chargeId: chargeId, toAdvance: toAdvance));

    private async Task<AgreementLedgerDto> LedgerAsync(World w, Deal d) => await Run(w.Accountant, s => Svc<LedgerService>(s).GetAgreementLedgerAsync(d.AgreementId));

    private async Task<List<PaymentScheduleItem>> ItemsAsync(World w, Deal d) => await Run(w.Admin, async s =>
    {
        var db = Svc<AppDbContext>(s);
        return await (from i in db.PaymentScheduleItems.AsNoTracking() join sc in db.PaymentSchedules on i.ScheduleId equals sc.Id where sc.ActiveAgreementId == d.AgreementId orderby i.DueDate, i.LineNo select i).ToListAsync();
    });

    /// <summary>Invariant: active installment allocations of posted payments equal the paid amounts on the active schedule.</summary>
    private async Task AssertAllocationsMatchScheduleAsync(World w, Deal d)
    {
        var items = await ItemsAsync(w, d);
        var allocated = await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            return await (from a in db.PaymentAllocations join p in db.Payments on a.PaymentId equals p.Id
                          where a.AgreementId == d.AgreementId && a.Status == AllocationStatus.Active && a.Kind == AllocationKind.Installment && p.Status == PaymentStatus.Posted
                          select a.Amount).SumAsync();
        });
        Assert.Equal(allocated, items.Sum(i => i.AmountPaid));
        Assert.All(items, i => Assert.True(i.AmountPaid + i.AmountWaived <= i.Amount));
    }

    // ------------------------------------------------------------------------------------------------ catalog
    [MySqlFact]
    public async Task Phase6Permissions_AndPaymentMethods_AreSeeded()
    {
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        async Task<List<string>> Perms(string role) => await db.RolePermissions.Where(rp => db.Roles.Any(r => r.Id == rp.RoleId && r.Name == role)).Select(rp => rp.Permission.Code).ToListAsync();
        var cashier = await Perms(RoleNames.Cashier);
        Assert.Contains(Permissions.PaymentsCollect, cashier);
        Assert.DoesNotContain(Permissions.PaymentsApprove, cashier);
        Assert.Contains(Permissions.PaymentsApprove, await Perms(RoleNames.Accountant));
        Assert.Contains(Permissions.RecoveryFollowUp, await Perms(RoleNames.RecoveryOfficer));
        Assert.Equal(Permissions.All.Count, (await Perms(RoleNames.Administrator)).Count);
        var methods = await db.PaymentMethods.ToListAsync();
        Assert.Equal(Enum.GetValues<PaymentMethodKind>().Length, methods.Count(m => m.IsSystem));
        Assert.False(methods.Single(m => m.Code == "CASH").RequiresVerification);
        Assert.True(methods.Single(m => m.Code == "CHEQUE").RequiresVerification);
    }

    // ------------------------------------------------------------------------------------------------ workflow
    [MySqlFact]
    public async Task CashPayment_IsApprovedByAnotherUser_Posted_Allocated_Receipted_AndLedgerReconciles()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await SubmitAsync(w, d, 600_000m);
        Assert.Equal(PaymentStatus.PendingApproval, p.Status);
        Assert.False(p.VerificationRequired);
        Assert.StartsWith("PAY-", p.PaymentNumber);
        Assert.Null(p.Receipt);
        // nothing financial happens before approval
        Assert.Equal(0m, (await ItemsAsync(w, d)).Sum(i => i.AmountPaid));

        await Assert.ThrowsAsync<ForbiddenException>(() => ApproveAsync(w, p, w.Cashier)); // cashiers collect, they do not approve
        var own = await SubmitAsync(w, d, 1_000m, actor: w.Director);
        var self = await Assert.ThrowsAsync<DomainException>(() => ApproveAsync(w, own, w.Director)); // maker-checker
        Assert.Contains("submitted", self.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => ApproveAsync(w, p, w.Sales));

        p = await ApproveAsync(w, p);
        Assert.Equal(PaymentStatus.Posted, p.Status);
        Assert.StartsWith("RCP-", p.Receipt!.ReceiptNumber);
        Assert.Equal(600_000m, p.Allocated);
        Assert.Equal(0m, p.Unallocated);
        // oldest due first: down payment 500,000 then 100,000 of the first monthly installment (paid in advance)
        var items = await ItemsAsync(w, d);
        Assert.Equal(ScheduleItemStatus.Paid, items[0].Status);
        Assert.Equal(100_000m, items[1].AmountPaid);
        Assert.True(items[1].IsPaidInAdvance);
        await AssertAllocationsMatchScheduleAsync(w, d);

        var ledger = await LedgerAsync(w, d);
        var sum = ledger.Summary;
        Assert.Equal(500_000m, sum.DueToDate); // only the down payment has fallen due (scheduled installments are not receivables)
        Assert.Equal(600_000m, sum.PaymentsReceived);
        Assert.Equal(0m, sum.Outstanding);
        Assert.Equal(100_000m, sum.AdvanceBalance);
        Assert.Equal(-100_000m, sum.LedgerBalance);
        Assert.Equal(5_100_000m, sum.ContractTotal);
        Assert.True(sum.Reconciled);
        Assert.Contains(ledger.Entries.Items, e => e.EntryType == LedgerEntryType.InstallmentDue && e.Debit == 500_000m);
        Assert.Contains(ledger.Entries.Items, e => e.EntryType == LedgerEntryType.PaymentReceived && e.Credit == 600_000m && e.PaymentNumber == p.PaymentNumber);

        var customer = await Run(w.Accountant, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(d.CustomerId));
        Assert.Equal(-100_000m, customer.Balance);
        Assert.True(customer.Reconciled);

        await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            var outbox = await db.AccountingPostingRequests.SingleAsync(o => o.SourceType == "Payment" && o.SourceId == p.Id);
            Assert.Equal(AccountingEventType.PaymentPosted, outbox.EventType);
            Assert.Equal(AccountingPostingStatus.Pending, outbox.Status);
            Assert.Equal(600_000m, outbox.Amount);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Payment.Posted" && a.EntityId == p.Id.ToString()));
            return 0;
        });
        // approving again is refused and posts nothing twice
        await Assert.ThrowsAsync<DomainException>(() => ApproveAsync(w, p));
        Assert.Single((await LedgerAsync(w, d)).Entries.Items, e => e.EntryType == LedgerEntryType.PaymentReceived);
    }

    [MySqlFact]
    public async Task CustomerSubmittedProof_MustBeVerified_BeforeApproval_AndCanBeRejected()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await SubmitAsync(w, d, 100_000m, "BANK_TRANSFER", "TRX-" + Guid.NewGuid().ToString("N")[..8], source: PaymentSource.CustomerSubmitted);
        Assert.Equal(PaymentStatus.PendingVerification, p.Status);
        await Assert.ThrowsAsync<DomainException>(() => ApproveAsync(w, p)); // unverified proof never becomes a payment
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).VerifyAsync(p.Id, new VerifyPaymentRequest(true, "Seen in statement", null, null, p.RowVersion))));
        var mine = await SubmitAsync(w, d, 1_000m, "BANK_TRANSFER", "TRX-" + Guid.NewGuid().ToString("N")[..8], actor: w.Director);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<PaymentService>(s).VerifyAsync(mine.Id, new VerifyPaymentRequest(true, "Own submission", null, null, mine.RowVersion))));
        p = await Run(w.Accountant, s => Svc<PaymentService>(s).VerifyAsync(p.Id, new VerifyPaymentRequest(true, "Seen in bank statement", "BS-7", w.Today, p.RowVersion)));
        Assert.Equal(PaymentStatus.PendingApproval, p.Status);
        Assert.Single(p.Verifications);
        p = await ApproveAsync(w, p, w.Director);
        Assert.Equal(PaymentStatus.Posted, p.Status);

        var q = await SubmitAsync(w, d, 50_000m, "CHEQUE", "CHQ-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(PaymentStatus.PendingVerification, q.Status);
        q = await Run(w.Accountant, s => Svc<PaymentService>(s).VerifyAsync(q.Id, new VerifyPaymentRequest(false, "Cheque bounced", null, null, q.RowVersion)));
        Assert.Equal(PaymentStatus.Rejected, q.Status);
        Assert.Equal(100_000m, (await LedgerAsync(w, d)).Summary.PaymentsReceived);
        // a rejected payment's reference can be used again
        var again = await SubmitAsync(w, d, 50_000m, "CHEQUE", q.Reference);
        Assert.Equal(PaymentStatus.PendingVerification, again.Status);
    }

    // ------------------------------------------------------------------------------------------------ duplicates
    [MySqlFact]
    public async Task DuplicateSubmissions_DoNotCreateDuplicatePayments()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var key = Guid.NewGuid().ToString("N");
        var first = await SubmitAsync(w, d, 200_000m, key: key);
        var retry = await SubmitAsync(w, d, 200_000m, key: key);
        Assert.Equal(first.Id, retry.Id);
        var changed = await Assert.ThrowsAsync<RequestValidationException>(() => SubmitAsync(w, d, 250_000m, key: key));
        Assert.Contains("IdempotencyKey", changed.Errors.Keys);
        await Assert.ThrowsAsync<RequestValidationException>(() => SubmitAsync(w, d, 200_000m, key: key, actor: w.Sales));

        // concurrent identical submissions -> one payment
        var key2 = Guid.NewGuid().ToString("N");
        var methodId = await MethodAsync("CASH");
        As(w.Cashier);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var s = fx.Scope();
            try { return (await Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, null, methodId, PaymentSource.Office, 75_000m, w.Today, null, null, null, null, null,
                AllocationMode.OldestDueFirst, null, null, false, key2))).Id; }
            catch (ConcurrencyException) { return 0L; }
        }));
        var count = await Run(w.Admin, async s => await Svc<AppDbContext>(s).Payments.CountAsync(p => p.IdempotencyKey == key2));
        Assert.Equal(1, count);
        Assert.All(results.Where(r => r != 0), r => Assert.Equal(results.First(x => x != 0), r));

        // the same bank reference cannot be recorded twice while live
        var reference = "TRX-" + Guid.NewGuid().ToString("N")[..8];
        await SubmitAsync(w, d, 10_000m, "BANK_TRANSFER", reference);
        var dup = await Assert.ThrowsAsync<RequestValidationException>(() => SubmitAsync(w, d, 10_000m, "BANK_TRANSFER", reference.ToLowerInvariant()));
        Assert.Contains("Reference", dup.Errors.Keys);
    }

    [MySqlFact]
    public async Task ConcurrentApprovals_PostOnce_AndConcurrentAllocations_NeverOverAllocate()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await SubmitAsync(w, d, 300_000m);
        As(w.Accountant);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<PaymentService>(s).ApproveAsync(p.Id, new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion)); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(x => x));
        var ledger = await LedgerAsync(w, d);
        Assert.Single(ledger.Entries.Items, e => e.EntryType == LedgerEntryType.PaymentReceived);
        Assert.Single(await Run(w.Admin, async s => await Svc<AppDbContext>(s).PaymentReceipts.Where(r => r.PaymentId == p.Id).ToListAsync()));

        // three unallocated payments allocated concurrently to the same installments
        var payments = new List<PaymentDetailDto>();
        for (var i = 0; i < 3; i++) payments.Add(await PostAsync(w, d, 400_000m, AllocationMode.Unallocated));
        As(w.Cashier);
        var done = await Task.WhenAll(payments.Select(async x =>
        {
            using var s = fx.Scope();
            try { await Svc<PaymentService>(s).AllocateAsync(x.Id, new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, false, false, null)); return true; }
            catch (ConcurrencyException) { return false; }
        }));
        Assert.True(done.Count(x => x) >= 1);
        await AssertAllocationsMatchScheduleAsync(w, d);
        var sum = (await LedgerAsync(w, d)).Summary;
        Assert.True(sum.Reconciled);
        Assert.Equal(1_500_000m, sum.PaymentsReceived);
    }

    [MySqlFact]
    public async Task PaymentAndAmendment_Racing_NeverLosePaymentProgress()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 500_000m); // down payment settled
        var items = await ItemsAsync(w, d);
        var monthly = items.Where(i => i.Description.StartsWith("Monthly")).OrderBy(i => i.DueDate).ToList();
        var amendment = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(d.AgreementId, new CreateAmendmentRequest(AmendmentType.Deferment, "Customer asked to defer",
            Deferment: new DefermentInput([monthly[2].Id], 2, false))));
        var pay = await SubmitAsync(w, d, 162_500m, mode: AllocationMode.SpecificInstallment, lines: [new AllocationLineInput(monthly[0].Id, 162_500m)]);
        var t1 = Task.Run(async () =>
        {
            using var s = fx.Scope();
            try { As(w.Accountant); await Svc<PaymentService>(s).ApproveAsync(pay.Id, new ApprovePaymentRequest(null, null, null, null, null, pay.RowVersion)); return "pay"; }
            catch (DomainException) { return "pay-failed"; }
        });
        var t2 = Task.Run(async () =>
        {
            using var s = fx.Scope();
            try { As(w.Director); await Svc<AgreementAmendmentService>(s).ApproveAsync(d.AgreementId, amendment.Id, new DecideAmendmentRequest("ok", amendment.RowVersion)); return "amend"; }
            catch (DomainException) { return "amend-failed"; }
        });
        var results = await Task.WhenAll(t1, t2);
        Assert.Contains(results, r => r is "pay" or "amend");
        // whatever the order, the schedule shows exactly what was allocated
        await AssertAllocationsMatchScheduleAsync(w, d);
        Assert.True((await LedgerAsync(w, d)).Summary.Reconciled);
    }

    // ------------------------------------------------------------------------------------------------ allocation modes
    [MySqlFact]
    public async Task AllocationModes_Manual_Specific_Charge_Advance_Unallocated_AndNoOverAllocation()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var items = await ItemsAsync(w, d);
        var down = items[0];
        var dev = items.Single(i => i.Description.StartsWith("Development"));

        // manual: amounts above a line's outstanding are refused when the payment is submitted
        await Assert.ThrowsAsync<RequestValidationException>(() => SubmitAsync(w, d, 600_000m, mode: AllocationMode.Manual, lines: [new AllocationLineInput(down.Id, 600_000m)]));
        var manual = await PostAsync(w, d, 300_000m, AllocationMode.Manual, [new AllocationLineInput(down.Id, 200_000m), new AllocationLineInput(dev.Id, 50_000m)]);
        Assert.Equal(250_000m, manual.Allocated);
        Assert.Equal(50_000m, manual.Unallocated);

        // charge-specific: development charges only
        var charge = await PostAsync(w, d, 80_000m, AllocationMode.ChargeSpecific, chargeId: dev.AgreementChargeId);
        Assert.Equal(50_000m, charge.Allocated); // only 50,000 left on the development line
        Assert.Equal(30_000m, charge.Unallocated);

        // specific installment
        var specific = await PostAsync(w, d, 300_000m, AllocationMode.SpecificInstallment, [new AllocationLineInput(down.Id, 300_000m)]);
        Assert.Equal(ScheduleItemStatus.Paid, (await ItemsAsync(w, d))[0].Status);

        // advance: held, not applied
        var advance = await PostAsync(w, d, 100_000m, AllocationMode.Advance);
        Assert.Equal(100_000m, advance.Advance);
        Assert.Equal(0m, advance.Allocated);

        // later allocation of the unallocated balance; the advance only when asked
        var after = await Run(w.Cashier, s => Svc<PaymentService>(s).AllocateAsync(manual.Id, new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, false, false, null)));
        Assert.Equal(0m, after.Unallocated);
        var none = await Assert.ThrowsAsync<DomainException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).AllocateAsync(advance.Id,
            new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, false, false, null))));
        Assert.Contains("advance", none.Message);
        var reapplied = await Run(w.Cashier, s => Svc<PaymentService>(s).AllocateAsync(advance.Id, new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, true, false, null)));
        Assert.Equal(100_000m, reapplied.Allocated);
        Assert.Contains(reapplied.Allocations, a => a.Kind == AllocationKind.Advance && a.Status == AllocationStatus.Released);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).AllocateAsync(advance.Id,
            new AllocatePaymentRequest(AllocationMode.OldestDueFirst, null, null, true, false, null))));

        await AssertAllocationsMatchScheduleAsync(w, d);
        var sum = (await LedgerAsync(w, d)).Summary;
        Assert.True(sum.Reconciled);
        Assert.Equal(780_000m, sum.PaymentsReceived);
        // allocations can never exceed what was received
        var totals = await Run(w.Admin, async s => await Svc<AppDbContext>(s).PaymentAllocations.Where(a => a.AgreementId == d.AgreementId && a.Status == AllocationStatus.Active).SumAsync(a => a.Amount));
        Assert.True(totals <= 780_000m);
    }

    // ------------------------------------------------------------------------------------------------ reversal, receipts
    [MySqlFact]
    public async Task Reversal_UndoesAllocations_AddsOffsettingEntries_AndKeepsHistory()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await PostAsync(w, d, 650_000m);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).ReverseAsync(p.Id, new ReversePaymentRequest("Cheque bounced", p.RowVersion))));
        var r = await Run(w.Accountant, s => Svc<PaymentService>(s).ReverseAsync(p.Id, new ReversePaymentRequest("Cheque bounced", p.RowVersion)));
        Assert.Equal(PaymentStatus.Reversed, r.Status);
        Assert.NotNull(r.Reversal);
        Assert.All(r.Allocations, a => Assert.Equal(AllocationStatus.Reversed, a.Status));
        Assert.Equal(0m, (await ItemsAsync(w, d)).Sum(i => i.AmountPaid));
        var ledger = await LedgerAsync(w, d);
        Assert.Contains(ledger.Entries.Items, e => e.EntryType == LedgerEntryType.PaymentReversed && e.Debit == 650_000m);
        Assert.Equal(500_000m, ledger.Summary.Outstanding);
        Assert.True(ledger.Summary.Reconciled);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Accountant, s => Svc<PaymentService>(s).ReverseAsync(p.Id, new ReversePaymentRequest("Again", null))));
        // posted records are never deleted
        Assert.Equal(2, await Run(w.Admin, async s => await Svc<AppDbContext>(s).AccountingPostingRequests.CountAsync(o => o.SourceType == "Payment" && o.SourceId == p.Id)));
        Assert.NotNull(r.Receipt);
    }

    [MySqlFact]
    public async Task Receipts_AreUnique_FirstCopyOriginal_ThenDuplicates()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var a = await PostAsync(w, d, 100_000m);
        var b = await PostAsync(w, d, 100_000m);
        Assert.NotEqual(a.Receipt!.ReceiptNumber, b.Receipt!.ReceiptNumber);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Officer, s => Svc<ReceiptService>(s).PdfAsync(a.Receipt.Id)));
        var first = await Run(w.Cashier, s => Svc<ReceiptService>(s).PdfAsync(a.Receipt.Id));
        var second = await Run(w.Cashier, s => Svc<ReceiptService>(s).PdfAsync(a.Receipt.Id));
        Assert.Equal("application/pdf", first.ContentType);
        Assert.DoesNotContain("copy", first.FileName);
        Assert.Contains("copy1", second.FileName);
        var receipt = await Run(w.Cashier, s => Svc<ReceiptService>(s).GetAsync(a.Receipt.Id));
        Assert.Equal(2, receipt.CopiesPrinted);
        Assert.True(await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.AnyAsync(l => l.Action == "Receipt.DuplicatePrinted" && l.EntityId == a.Receipt.Id.ToString())));
    }

    // ------------------------------------------------------------------------------------------------ ledgers
    [MySqlFact]
    public async Task Receivables_ArePostedOnlyWhenDue_AndReversedWhenDeferred()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 3); // down payment + 3 monthly installments are now due
        var monthly = (await ItemsAsync(w, d)).First(i => i.Description.StartsWith("Monthly")).Amount;
        var ledger = await LedgerAsync(w, d);
        Assert.Equal(500_000m + 3 * monthly, ledger.Summary.DueToDate);
        Assert.Equal(ledger.Summary.DueToDate, ledger.Summary.Outstanding);
        Assert.True(ledger.Summary.Overdue > 0m);
        Assert.Equal(4, ledger.Entries.Items.Count(e => e.EntryType == LedgerEntryType.InstallmentDue));
        // reading again posts nothing new (idempotent)
        Assert.Equal(ledger.Entries.TotalCount, (await LedgerAsync(w, d)).Entries.TotalCount);

        // defer an overdue unpaid installment beyond today: its receivable is reversed
        var items = await ItemsAsync(w, d);
        var overdueMonthly = items.Where(i => i.Description.StartsWith("Monthly") && i.DueDate < w.Today).OrderBy(i => i.DueDate).Last();
        var m = await Run(w.Sales, s => Svc<AgreementAmendmentService>(s).CreateAsync(d.AgreementId, new CreateAmendmentRequest(AmendmentType.DueDateChange, "Correct the due date",
            DueDateChanges: [new DueDateChangeInput(overdueMonthly.Id, w.Today.AddMonths(1))])));
        await Run(w.Director, s => Svc<AgreementAmendmentService>(s).ApproveAsync(d.AgreementId, m.Id, new DecideAmendmentRequest("ok", m.RowVersion)));
        ledger = await LedgerAsync(w, d);
        Assert.Equal(500_000m + 2 * monthly, ledger.Summary.DueToDate);
        Assert.Contains(ledger.Entries.Items, e => e.EntryType == LedgerEntryType.DueAdjustment && e.Credit == monthly);
        Assert.True(ledger.Summary.Reconciled);
    }

    [MySqlFact]
    public async Task CustomerLedger_FollowsTransfers_AndReconcilesAcrossAgreements()
    {
        var w = await WorldAsync();
        var c = await CustomerAsync(w);
        var d1 = await DealAsync(w, owners: [new OwnerInput(c, 100m)]);
        var d2 = await DealAsync(w, owners: [new OwnerInput(c, 100m)]);
        await PostAsync(w, d1, 500_000m);
        await PostAsync(w, d2, 200_000m);
        var ledger = await Run(w.Accountant, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(c));
        Assert.Equal(2, ledger.Agreements.Count);
        Assert.Equal(300_000m, ledger.Outstanding); // 500,000 + 500,000 due, 700,000 paid
        Assert.Equal(300_000m, ledger.Balance);
        Assert.True(ledger.Reconciled);

        // transfer agreement 2 to another customer: its balance moves with it
        var other = await CustomerAsync(w);
        var booking = await Run(w.Sales, s => Svc<BookingService>(s).GetAsync(d2.BookingId));
        await Run(w.Director, s => Svc<BookingService>(s).TransferAsync(d2.BookingId, new TransferBookingRequest("Sold on", [new OwnerInput(other, 100m)], booking.RowVersion)));
        ledger = await Run(w.Accountant, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(c));
        Assert.Equal(0m, ledger.Agreements.Single(a => a.AgreementId == d2.AgreementId).Balance);
        Assert.Equal(0m, ledger.Balance);
        Assert.True(ledger.Reconciled);
        var newOwner = await Run(w.Accountant, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(other));
        Assert.Equal(300_000m, newOwner.Balance);
        Assert.Contains(newOwner.Entries.Items, e => e.EntryType == LedgerEntryType.BalanceTransferIn);
        Assert.True(newOwner.Reconciled);
    }

    // ------------------------------------------------------------------------------------------------ refunds
    [MySqlFact]
    public async Task Refunds_AreLimitedToCredit_MakerChecker_AndPaidFromPayments()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 500_000m);
        var advance = await PostAsync(w, d, 120_000m, AllocationMode.Advance);
        var refundable = await Run(w.Accountant, s => Svc<RefundService>(s).RefundableAsync(d.AgreementId));
        Assert.Equal(120_000m, refundable.Available);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Sales, s => Svc<RefundService>(s).CreateAsync(new CreateRefundRequest(d.AgreementId, 150_000m, "Customer request", null, null))));
        var r = await Run(w.Sales, s => Svc<RefundService>(s).CreateAsync(new CreateRefundRequest(d.AgreementId, 70_000m, "Customer request", null, "rk-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(PaymentRefundStatus.Requested, r.Status);
        // reserved by the open request
        Assert.Equal(50_000m, (await Run(w.Accountant, s => Svc<RefundService>(s).RefundableAsync(d.AgreementId))).Available);
        var cash = await MethodAsync("CASH");
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<RefundService>(s).PayAsync(r.Id, new PayRefundRequest(cash, w.Today, null, r.RowVersion))));
        var notApproved = await Assert.ThrowsAsync<DomainException>(() => Run(w.Accountant, s => Svc<RefundService>(s).PayAsync(r.Id, new PayRefundRequest(cash, w.Today, null, r.RowVersion))));
        Assert.Contains("approved", notApproved.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Sales, s => Svc<RefundService>(s).ApproveAsync(r.Id, new ApproveRefundRequest("self", r.RowVersion))));
        r = await Run(w.Accountant, s => Svc<RefundService>(s).ApproveAsync(r.Id, new ApproveRefundRequest("ok", r.RowVersion)));
        Assert.Equal(PaymentRefundStatus.Approved, r.Status);
        r = await Run(w.Accountant, s => Svc<RefundService>(s).PayAsync(r.Id, new PayRefundRequest(cash, w.Today, null, r.RowVersion)));
        Assert.Equal(PaymentRefundStatus.Paid, r.Status);
        Assert.Equal(70_000m, r.Sources.Where(x => x.Kind == AllocationKind.Refund).Sum(x => x.Amount));
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Accountant, s => Svc<RefundService>(s).PayAsync(r.Id, new PayRefundRequest(cash, w.Today, null, null))));
        var p = await Run(w.Accountant, s => Svc<PaymentService>(s).GetAsync(advance.Id));
        Assert.Equal(50_000m, p.Advance);
        Assert.Equal(70_000m, p.Refunded);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Accountant, s => Svc<PaymentService>(s).ReverseAsync(advance.Id, new ReversePaymentRequest("Refunded payment", null))));
        var sum = (await LedgerAsync(w, d)).Summary;
        Assert.Equal(70_000m, sum.Refunds);
        Assert.True(sum.Reconciled);
    }

    [MySqlFact]
    public async Task CancelledAgreement_ReversesReceivables_AndAllPaymentsCanBeRefunded()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 2);
        await PostAsync(w, d, 700_000m);
        var booking = await Run(w.Sales, s => Svc<BookingService>(s).GetAsync(d.BookingId));
        await Run(w.Director, s => Svc<BookingService>(s).CancelAsync(d.BookingId, new CancelBookingRequest("Customer withdrew", null, null, booking.RowVersion)));
        var ledger = await LedgerAsync(w, d);
        Assert.Equal(0m, ledger.Summary.DueToDate);
        Assert.Equal(-700_000m, ledger.Summary.LedgerBalance);
        Assert.True(ledger.Summary.Reconciled);
        await Assert.ThrowsAsync<DomainException>(() => SubmitAsync(w, d, 1_000m));
        var refundable = await Run(w.Accountant, s => Svc<RefundService>(s).RefundableAsync(d.AgreementId));
        Assert.Equal(700_000m, refundable.Available);
        var r = await Run(w.Director, s => Svc<RefundService>(s).CreateAsync(new CreateRefundRequest(d.AgreementId, 700_000m, "Cancellation refund", null, null)));
        var self = await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<RefundService>(s).ApproveAsync(r.Id, new ApproveRefundRequest(null, r.RowVersion))));
        Assert.Contains("requested", self.Message);
        r = await Run(w.Accountant, s => Svc<RefundService>(s).ApproveAsync(r.Id, new ApproveRefundRequest(null, r.RowVersion)));
        var cash = await MethodAsync("CASH");
        r = await Run(w.Director, s => Svc<RefundService>(s).PayAsync(r.Id, new PayRefundRequest(cash, w.Today, null, r.RowVersion)));
        Assert.Equal(PaymentRefundStatus.Paid, r.Status);
        ledger = await LedgerAsync(w, d);
        Assert.Equal(0m, ledger.Summary.LedgerBalance);
        Assert.True(ledger.Summary.Reconciled);
    }

    // ------------------------------------------------------------------------------------------------ recovery
    [MySqlFact]
    public async Task Recovery_ReportsAreAccurate_AndOfficersSeeOnlyTheirAssignments()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 3);
        var paid = await DealAsync(w, monthsBack: 3);
        var monthly = (await ItemsAsync(w, d)).First(i => i.Description.StartsWith("Monthly")).Amount;
        await PostAsync(w, paid, 500_000m + 3 * monthly); // everything due up to today is paid
        var overdue = await Run(w.Accountant, s => Svc<RecoveryService>(s).OverdueAsync(new DueListRequest { ProjectId = w.ProjectId }));
        Assert.Equal(3, overdue.Lines.TotalCount); // the unpaid deal: down payment + 2 monthly lines are past due (the third is due today)
        Assert.Equal(500_000m + 2 * monthly, overdue.TotalOutstanding);
        Assert.All(overdue.Lines.Items, l => Assert.Equal(d.AgreementId, l.AgreementId));
        Assert.Equal(overdue.TotalOutstanding, overdue.Aging!.Total);

        var defaulters = await Run(w.Accountant, s => Svc<RecoveryService>(s).DefaultersAsync(new DefaulterListRequest { ProjectId = w.ProjectId, MinDaysOverdue = 60, MinInstallments = 2 }));
        Assert.Single(defaulters.Items);
        Assert.Equal(3, defaulters.Items[0].OverdueInstallments);

        var upcoming = await Run(w.Accountant, s => Svc<RecoveryService>(s).UpcomingAsync(new DueListRequest { ProjectId = w.ProjectId, Days = 40 }));
        Assert.All(upcoming.Lines.Items, l => Assert.InRange(l.DueDate, w.Today, w.Today.AddDays(40)));

        // officer without assignments sees nothing; after assignment, only that agreement
        Assert.Equal(0, (await Run(w.Officer, s => Svc<RecoveryService>(s).OverdueAsync(new DueListRequest()))).Lines.TotalCount);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Officer, s => Svc<RecoveryService>(s).CreateFollowUpAsync(new CreateFollowUpRequest(d.AgreementId, w.Today, FollowUpMethod.Call,
            FollowUpOutcome.NoAnswer, null, null, null, "No answer"))));
        var assignment = await Run(w.Director, s => Svc<RecoveryService>(s).AssignAsync(new CreateAssignmentRequest(d.AgreementId, w.Officer, "Priority")));
        Assert.Equal(overdue.TotalOutstanding, assignment.OverdueAtAssignment);
        await Assert.ThrowsAsync<DomainException>(() => Run(w.Director, s => Svc<RecoveryService>(s).AssignAsync(new CreateAssignmentRequest(d.AgreementId, w.Officer, null))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Director, s => Svc<RecoveryService>(s).AssignAsync(new CreateAssignmentRequest(paid.AgreementId, w.Cashier, null))));
        Assert.Equal(3, (await Run(w.Officer, s => Svc<RecoveryService>(s).OverdueAsync(new DueListRequest()))).Lines.TotalCount);

        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Officer, s => Svc<RecoveryService>(s).CreateFollowUpAsync(new CreateFollowUpRequest(d.AgreementId, w.Today,
            FollowUpMethod.Call, FollowUpOutcome.PromiseToPay, null, null, null, "Will pay"))));
        var f = await Run(w.Officer, s => Svc<RecoveryService>(s).CreateFollowUpAsync(new CreateFollowUpRequest(d.AgreementId, w.Today, FollowUpMethod.Call, FollowUpOutcome.PromiseToPay,
            300_000m, w.Today.AddDays(7), w.Today.AddDays(8), "Will pay next week")));
        Assert.Equal(overdue.TotalOutstanding, f.OverdueAtFollowUp);
        await PostAsync(w, d, 300_000m);

        var perf = await Run(w.Accountant, s => Svc<RecoveryService>(s).OfficerPerformanceAsync(new OfficerPerformanceRequest(null, null, w.ProjectId)));
        var mine = perf.Single(x => x.OfficerUserId == w.Officer);
        Assert.Equal(1, mine.ActiveAssignments);
        Assert.Equal(1, mine.FollowUps);
        Assert.Equal(1, mine.Promises);
        Assert.Equal(300_000m, mine.Collected);
        Assert.Equal(overdue.TotalOutstanding - 300_000m, mine.CurrentOverdue);

        var dash = await Run(w.Accountant, s => Svc<RecoveryService>(s).DashboardAsync(w.ProjectId));
        Assert.Equal(overdue.TotalOutstanding - 300_000m, dash.OverdueAmount);
        Assert.Equal(1, dash.OverdueAgreements);
        Assert.Equal(0, dash.UnassignedOverdueAgreements);
        var history = await Run(w.Officer, s => Svc<RecoveryService>(s).ListFollowUpsAsync(new FollowUpListRequest { AgreementId = d.AgreementId }));
        Assert.Single(history.Items);

        var closed = await Run(w.Director, s => Svc<RecoveryService>(s).CloseAssignmentAsync(assignment.Id, new CloseAssignmentRequest("Reassigned", assignment.RowVersion)));
        Assert.Equal(RecoveryAssignmentStatus.Closed, closed.Status);
        Assert.Equal(0, (await Run(w.Officer, s => Svc<RecoveryService>(s).OverdueAsync(new DueListRequest()))).Lines.TotalCount);
    }

    // ------------------------------------------------------------------------------------------------ authorization and database
    [MySqlFact]
    public async Task ProjectAndCustomerScope_AreEnforced()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await PostAsync(w, d, 100_000m);
        var outsider = await UserAsync(RoleNames.Cashier); // not assigned to the project
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<PaymentService>(s).GetAsync(p.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => SubmitAsync(w, d, 1_000m, actor: outsider));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<LedgerService>(s).GetAgreementLedgerAsync(d.AgreementId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<LedgerService>(s).GetCustomerLedgerAsync(d.CustomerId)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(outsider, s => Svc<PaymentService>(s).GetAsync(long.MaxValue)));
        Assert.Equal(0, (await Run(outsider, s => Svc<PaymentService>(s).ListAsync(new PaymentListRequest()))).TotalCount);
        // the payer must be a party of the booking (client ids are not trusted)
        var stranger = await CustomerAsync(w);
        var cash = await MethodAsync("CASH");
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, stranger, cash,
            PaymentSource.Office, 1_000m, w.Today, null, null, null, null, null, AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N")))));
        Assert.Contains("CustomerId", ex.Errors.Keys);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Cashier, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, null, cash,
            PaymentSource.Office, 1_000m, w.Today.AddDays(1), null, null, null, null, null, AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N")))));
    }

    [MySqlFact]
    public async Task DatabaseConstraints_ProtectPaymentsAllocationsAndLedgers()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var p = await PostAsync(w, d, 100_000m);
        using var s = fx.Scope();
        var db = Svc<AppDbContext>(s);
        var original = await db.Payments.AsNoTracking().FirstAsync(x => x.Id == p.Id);
        // duplicate idempotency key
        db.Payments.Add(new Payment
        {
            PaymentNumber = "X-" + Guid.NewGuid().ToString("N")[..10], IdempotencyKey = original.IdempotencyKey, RequestHash = "x", ProjectId = original.ProjectId, AgreementId = original.AgreementId,
            BookingId = original.BookingId, CustomerId = original.CustomerId, PaymentMethodId = original.PaymentMethodId, MethodKind = original.MethodKind, Amount = 1m, CurrencyCode = "PKR",
            PaymentDate = w.Today, Status = PaymentStatus.PendingApproval, SubmittedBy = "t", SubmittedAtUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // duplicate one-off ledger key
        db.AgreementLedgerEntries.Add(new AgreementLedgerEntry
        {
            AgreementId = d.AgreementId, ProjectId = w.ProjectId, CustomerId = d.CustomerId, EntryDate = w.Today, PostedAtUtc = DateTime.UtcNow, PostedBy = "t",
            EntryType = LedgerEntryType.PaymentReceived, Credit = 1m, Description = "dup", UniqueKey = $"PAY:{p.Id}:RECEIVED",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // a ledger entry must have exactly one side
        db.AgreementLedgerEntries.Add(new AgreementLedgerEntry
        {
            AgreementId = d.AgreementId, ProjectId = w.ProjectId, CustomerId = d.CustomerId, EntryDate = w.Today, PostedAtUtc = DateTime.UtcNow, PostedBy = "t",
            EntryType = LedgerEntryType.DueAdjustment, Debit = 1m, Credit = 1m, Description = "both",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // allocations must be positive; one receipt per payment; one outbox event per source
        db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = p.Id, AgreementId = d.AgreementId, Kind = AllocationKind.Advance, Mode = AllocationMode.Advance, Amount = 0m, AllocatedAtUtc = DateTime.UtcNow, AllocatedBy = "t" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.PaymentReceipts.Add(new PaymentReceipt { PaymentId = p.Id, ReceiptNumber = "X-" + Guid.NewGuid().ToString("N")[..10], Amount = 1m, IssuedAtUtc = DateTime.UtcNow, IssuedBy = "t" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.AccountingPostingRequests.Add(new AccountingPostingRequest { EventType = AccountingEventType.PaymentPosted, SourceType = "Payment", SourceId = p.Id, ProjectId = w.ProjectId, Amount = 1m, EventDate = w.Today, PayloadJson = "{}", CreatedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
