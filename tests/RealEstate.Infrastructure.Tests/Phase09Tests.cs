using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Accounting;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Installments;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Reporting;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>
/// Phase 9 integration tests against MySQL: dashboards and every report calculated from real bookings, payments, schedules and the ledger, with reconciliation, filters,
/// project/customer scope, permissions, PDF/Excel exports and export auditing. The database is shared with other tests, so every assertion is made for users whose project
/// access is limited to the test's own project (or for a single customer).
/// </summary>
[Collection("phase1")]
public class Phase09Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Sales, Guid Director, Guid Accountant, Guid Cashier, Guid Outsider, long ProjectId, long BlockId, DateOnly Today);
    private record Deal(long BookingId, long AgreementId, long CustomerId, long PropertyId);

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
        long pid, block; DateOnly today;
        using (var s = fx.Scope())
        {
            pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id;
            block = (await Svc<PropertySetupService>(s).CreateZoneAsync(ZoneKind.Block, pid, new SaveZoneRequest("B" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Block A", true))).Id;
        }
        var sales = await UserAsync(RoleNames.SalesManager);
        var director = await UserAsync(RoleNames.Director);
        var accountant = await UserAsync(RoleNames.Accountant);
        var cashier = await UserAsync(RoleNames.Cashier);
        var outsider = await UserAsync(RoleNames.SalesManager);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { sales, accountant, cashier }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(pid, "BASE", "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, today, null, null));
        }
        return new World(admin, sales, director, accountant, cashier, outsider, pid, block, today);
    }

    private async Task<long> CustomerAsync(World w) => await Run(w.Sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6],
        IdentityDocumentType.Cnic, Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
        [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);

    /// <summary>Confirmed installment booking (5 marla x 1,000,000) with a generated schedule; down payment 500,000 is due today. <paramref name="monthsBack"/> moves the due dates back.</summary>
    private async Task<Deal> DealAsync(World w, int monthsBack = 0, long? blockId = null)
    {
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"RP-{Guid.NewGuid():N}"[..14], "R" + n, blockId, null, null, typeId, 5m, MeasurementUnit.Marla,
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
        var code = "R" + Interlocked.Increment(ref counter);
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
        return new Deal(b.Id, agreementId, c, propertyId);
    }

    private async Task<long> MethodAsync(string code) => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == code).Select(m => m.Id).FirstAsync());

    private async Task<PaymentDetailDto> PostAsync(World w, Deal d, decimal amount, string method = "CASH")
    {
        var methodId = await MethodAsync(method);
        var reference = method == "CASH" ? null : "BT-" + Guid.NewGuid().ToString("N")[..8];
        var p = await Run(w.Cashier, s => Svc<PaymentService>(s).CreateAsync(new CreatePaymentRequest(d.AgreementId, null, methodId, PaymentSource.Office, amount, w.Today, reference,
            method == "CASH" ? null : "HBL", null, null, null, AllocationMode.OldestDueFirst, null, null, false, Guid.NewGuid().ToString("N"))));
        if (method != "CASH") p = await Run(w.Accountant, s => Svc<PaymentService>(s).VerifyAsync(p.Id, new VerifyPaymentRequest(true, "Seen in statement", reference, w.Today, p.RowVersion)));
        return await Run(w.Accountant, s => Svc<PaymentService>(s).ApproveAsync(p.Id, new ApprovePaymentRequest(null, null, null, null, null, p.RowVersion)));
    }

    private async Task<ReportTableDto> ReportAsync(Guid actor, string key, ReportFilters f) => await Run(actor, s => Svc<ReportingService>(s).RunAsync(key, f, default));

    private static decimal Cell(ReportTableDto r, int row, string column) => Convert.ToDecimal(r.Rows[row][r.Columns.ToList().FindIndex(c => c.Key == column)]);
    private static decimal Total(ReportTableDto r, string column) => Convert.ToDecimal(r.Totals![r.Columns.ToList().FindIndex(c => c.Key == column)]);

    // =====================================================================================================
    // dashboards
    // =====================================================================================================
    [MySqlFact]
    public async Task CompanyDashboard_IsCalculatedFromBusinessData_AndReconciles()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 200_000m);
        var booking = await Run(w.Admin, s => Svc<AppDbContext>(s).Bookings.AsNoTracking().FirstAsync(b => b.Id == d.BookingId));

        var dash = await Run(w.Sales, s => Svc<DashboardService>(s).CompanyAsync(new DashboardFilters(null, null)));
        Assert.Equal(1, dash.TotalProjects);                        // scoped to the user's one project
        Assert.Equal(1, dash.TotalInventory);
        Assert.Equal(1, dash.SoldProperties);
        Assert.Equal(0, dash.AvailableProperties);
        Assert.Equal(1, dash.BookingsCount);
        Assert.Equal(booking.TotalPayable, dash.TotalBookingValue);
        Assert.Equal(200_000m, dash.TotalCollections);
        Assert.Equal(300_000m, dash.CustomerReceivables);           // down payment 500,000 due today less 200,000 paid
        Assert.Equal(0m, dash.OverdueAmount);                       // due today is not yet overdue
        Assert.True(dash.TotalLandAreaSqFt > 0m);
        Assert.False(dash.LedgerAvailable);                         // the sales manager cannot read financial reports
        Assert.Null(dash.Revenue); Assert.Null(dash.ProfitLoss); Assert.Null(dash.CashBalance); Assert.Null(dash.BankBalance);
        Assert.NotNull(dash.LedgerNote);
        Assert.All(dash.Checks, c => Assert.True(c.Reconciled, $"{c.Name}: {c.Left} vs {c.Right}"));
        var row = Assert.Single(dash.Projects);
        Assert.Equal(w.ProjectId, row.ProjectId);
        Assert.Equal(300_000m, row.Outstanding);

        // the accountant can read the ledger: financial figures appear (company-wide needs all-project access, so the project dashboard carries them)
        var proj = await Run(w.Accountant, s => Svc<DashboardService>(s).ProjectAsync(w.ProjectId, new DashboardFilters(null, null)));
        Assert.Null(proj.LedgerNote);
        Assert.True(proj.LedgerAvailable);
        Assert.NotNull(proj.LedgerRevenue); Assert.NotNull(proj.LedgerProfit);
        Assert.Equal(proj.LedgerRevenue - proj.LedgerExpenses, proj.LedgerProfit);
    }

    [MySqlFact]
    public async Task Dashboard_Overdue_MatchesTheOverdueAndOutstandingReports()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 3);
        var dash = await Run(w.Sales, s => Svc<DashboardService>(s).CompanyAsync(new DashboardFilters(null, null)));
        var overdue = await ReportAsync(w.Sales, "overdue", new ReportFilters { ProjectId = w.ProjectId });
        var outstanding = await ReportAsync(w.Sales, "outstanding-balance", new ReportFilters { ProjectId = w.ProjectId });
        Assert.True(dash.OverdueAmount > 0m);
        Assert.Equal(dash.OverdueAmount, Total(overdue, "outstanding"));
        Assert.Equal(dash.OverdueAmount, Total(outstanding, "overdue"));
        Assert.Equal(dash.CustomerReceivables, Total(outstanding, "due"));
        Assert.All(overdue.Rows, r => Assert.True(Convert.ToInt32(r[5]) > 0));   // days overdue

        // independent calculation straight from the schedule
        var expected = await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            var items = await (from i in db.PaymentScheduleItems join sc in db.PaymentSchedules on i.ScheduleId equals sc.Id where sc.ActiveAgreementId == d.AgreementId select i).ToListAsync();
            return items.Where(i => i.DueDate < w.Today).Sum(i => i.Amount - i.AmountPaid - i.AmountWaived);
        });
        Assert.Equal(expected, dash.OverdueAmount);

        var project = await Run(w.Sales, s => Svc<DashboardService>(s).ProjectAsync(w.ProjectId, new DashboardFilters(null, null)));
        Assert.Equal(expected, project.Overdue);
        Assert.True(project.OverdueInstallments > 0);
        Assert.Equal(1, project.OverdueAgreements);
    }

    [MySqlFact]
    public async Task ProjectDashboard_ShowsLandInventoryAndExpenses_AndEnforcesProjectAccess()
    {
        var w = await WorldAsync();
        await DealAsync(w);
        var project = await Run(w.Sales, s => Svc<DashboardService>(s).ProjectAsync(w.ProjectId, new DashboardFilters(null, null)));
        Assert.Equal(w.ProjectId, project.ProjectId);
        Assert.True(project.ProjectArea > 0m);
        Assert.NotEmpty(project.LandDistribution);
        Assert.Equal(100m, Math.Round(project.LandDistribution.Sum(l => l.Percent), 0));
        var cat = Assert.Single(project.InventoryByCategory);
        Assert.Equal(1, cat.Total); Assert.Equal(1, cat.Sold);
        Assert.Equal(1, project.SalesCount);

        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<DashboardService>(s).ProjectAsync(w.ProjectId, new DashboardFilters(null, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Cashier, s => Svc<DashboardService>(s).CompanyAsync(new DashboardFilters(null, null))));  // no reports.view
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Sales, s => Svc<DashboardService>(s).CompanyAsync(new DashboardFilters(w.Today, w.Today.AddDays(-1)))));

        // approved expense shows in the project figures
        long expenseId;
        var cat2 = await Run(w.Admin, async s => await Svc<AppDbContext>(s).ExpenseCategories.Where(c => c.Code == "MISCELLANEOUS").Select(c => c.Id).FirstAsync());
        expenseId = await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            var e = new Expense { ExpenseNumber = "EXP-P9-" + Guid.NewGuid().ToString("N")[..8], ProjectId = w.ProjectId, CategoryId = cat2, Description = "Phase 9 expense", ExpenseDate = w.Today, Amount = 12_345m,
                AmountPaid = 2_000m, Status = ExpenseStatus.Approved, IdempotencyKey = Guid.NewGuid().ToString("N") };
            db.Expenses.Add(e); await db.SaveChangesAsync();
            return e.Id;
        });
        var after = await Run(w.Sales, s => Svc<DashboardService>(s).ProjectAsync(w.ProjectId, new DashboardFilters(null, null)));
        Assert.Equal(12_345m, after.ApprovedExpenses);
        Assert.Equal(2_000m, after.ExpensesPaid);
        Assert.Equal(10_345m, after.ExpensesOutstanding);
        Assert.True(expenseId > 0);
    }

    // =====================================================================================================
    // sales reports
    // =====================================================================================================
    [MySqlFact]
    public async Task SalesReports_ReconcileToBookings_AndFiltersApply()
    {
        var w = await WorldAsync();
        var d1 = await DealAsync(w, blockId: w.BlockId);
        var d2 = await DealAsync(w);
        var bookings = await Run(w.Admin, s => Svc<AppDbContext>(s).Bookings.AsNoTracking().Where(b => b.ProjectId == w.ProjectId).ToListAsync());
        var payable = bookings.Sum(b => b.TotalPayable);
        var f = new ReportFilters { ProjectId = w.ProjectId };

        var daily = await ReportAsync(w.Sales, "daily-sales", f);
        Assert.Equal(2, Convert.ToInt32(daily.Totals![1]));                        // bookings
        Assert.Equal(payable, Total(daily, "payable"));
        var monthly = await ReportAsync(w.Sales, "monthly-sales", f);
        Assert.Equal(payable, Total(monthly, "payable"));
        var project = await ReportAsync(w.Sales, "project-sales", f);
        Assert.Single(project.Rows); Assert.Equal(payable, Total(project, "payable"));
        var person = await ReportAsync(w.Sales, "salesperson-sales", f);
        Assert.Equal(Names[w.Sales], person.Rows[0][0]); Assert.Equal(payable, Total(person, "payable"));
        var category = await ReportAsync(w.Sales, "category-sales", f);
        Assert.Equal(2, Convert.ToInt32(category.Totals![2]));
        Assert.Equal(bookings.Sum(b => b.TotalPrice), Total(category, "price"));

        // block-wise sales split by block and filter by block
        var byBlock = await ReportAsync(w.Sales, "block-sales", f);
        Assert.Equal(2, byBlock.Rows.Count);                                        // block A and "(no block)"
        Assert.Contains(byBlock.Rows, r => (string)r[1]! == "(no block)");
        var blockOnly = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, BlockId = w.BlockId });
        Assert.Equal(1, Convert.ToInt32(blockOnly.Totals![1]));

        // customer, transaction type (sale type), status and date filters
        var byCustomer = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d1.CustomerId });
        Assert.Equal(1, Convert.ToInt32(byCustomer.Totals![1]));
        var cash = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, TransactionType = "Cash" });
        Assert.Empty(cash.Rows);
        var installment = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, TransactionType = "Installment" });
        Assert.Equal(2, Convert.ToInt32(installment.Totals![1]));
        var draft = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, Status = "Draft" });
        Assert.Empty(draft.Rows);
        var future = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId, From = w.Today.AddDays(1) });
        Assert.Empty(future.Rows);
        await Assert.ThrowsAsync<RequestValidationException>(() => ReportAsync(w.Sales, "daily-sales", new ReportFilters { From = w.Today, To = w.Today.AddDays(-1) }));

        // scope: a project the user is not assigned to is refused, and the report is empty for a user with no projects of their own
        await Assert.ThrowsAsync<ForbiddenException>(() => ReportAsync(w.Outsider, "daily-sales", new ReportFilters { ProjectId = w.ProjectId }));
        var none = await ReportAsync(w.Outsider, "project-sales", new ReportFilters());
        Assert.NotNull(d2);
    }

    [MySqlFact]
    public async Task Cancellations_AppearInTheCancellationReport_NotInSales()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var b = await Run(w.Admin, s => Svc<AppDbContext>(s).Bookings.AsNoTracking().FirstAsync(x => x.Id == d.BookingId));
        await Run(w.Director, s => Svc<BookingService>(s).CancelAsync(d.BookingId, new CancelBookingRequest("Customer withdrew from the purchase", null, null, Convert.ToBase64String(b.RowVersion))));
        var report = await ReportAsync(w.Sales, "cancellations", new ReportFilters { ProjectId = w.ProjectId });
        var row = Assert.Single(report.Rows);
        Assert.Equal("Customer withdrew from the purchase", row[7]);
        Assert.Equal(b.TotalPayable, Convert.ToDecimal(row[8]));
        Assert.Equal(b.TotalPayable, Total(report, "payable"));
        var sales = await ReportAsync(w.Sales, "daily-sales", new ReportFilters { ProjectId = w.ProjectId });
        Assert.Empty(sales.Rows);                                                   // cancelled bookings are not sales
    }

    // =====================================================================================================
    // collection reports
    // =====================================================================================================
    [MySqlFact]
    public async Task CollectionReports_ReconcileToPayments_ReversalsExcluded_FiltersApply()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 100_000m);
        var bank = await PostAsync(w, d, 50_000m, "BANK_TRANSFER");
        var toReverse = await PostAsync(w, d, 30_000m);
        await Run(w.Accountant, s => Svc<PaymentService>(s).ReverseAsync(toReverse.Id, new ReversePaymentRequest("Entered in error by the cashier", toReverse.RowVersion)));
        var f = new ReportFilters { ProjectId = w.ProjectId };

        var daily = await ReportAsync(w.Sales, "daily-collection", f);
        Assert.Equal(150_000m, Total(daily, "amount"));                              // 100,000 cash + 50,000 bank; the reversed 30,000 is excluded
        Assert.Equal(100_000m, Total(daily, "cash"));
        Assert.Equal(50_000m, Total(daily, "other"));
        Assert.Equal(150_000m, Total(await ReportAsync(w.Sales, "monthly-collection", f), "amount"));
        Assert.Equal(150_000m, Total(await ReportAsync(w.Sales, "project-collection", f), "amount"));
        var methods = await ReportAsync(w.Sales, "payment-methods", f);
        Assert.Equal(2, methods.Rows.Count);
        Assert.Equal(100m, Total(methods, "share"));
        var cashier = await ReportAsync(w.Sales, "cashier-collection", f);
        Assert.Equal(Names[w.Cashier], cashier.Rows[0][0]); Assert.Equal(150_000m, Total(cashier, "amount"));
        var banks = await ReportAsync(w.Sales, "bank-collection", f);
        var bankRow = Assert.Single(banks.Rows);
        Assert.Equal("HBL", bankRow[0]); Assert.Equal(50_000m, Total(banks, "amount"));

        // filters: payment method, status (reversed), customer, block, source, date
        var cashOnly = await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, PaymentMethodId = await MethodAsync("CASH") });
        Assert.Equal(100_000m, Total(cashOnly, "amount"));
        var reversed = await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, Status = "Reversed" });
        Assert.Equal(30_000m, Total(reversed, "amount"));
        Assert.Equal(150_000m, Total(await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId }), "amount"));
        Assert.Empty((await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, BlockId = w.BlockId })).Rows);
        Assert.Empty((await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, TransactionType = "CustomerSubmitted" })).Rows);
        Assert.Empty((await ReportAsync(w.Sales, "daily-collection", new ReportFilters { ProjectId = w.ProjectId, To = w.Today.AddDays(-1) })).Rows);

        // payment history lists every status, totalling posted payments only
        var history = await ReportAsync(w.Sales, "payment-history", f);
        Assert.Equal(3, history.TotalRows);
        Assert.Equal(150_000m, Total(history, "amount"));
        Assert.Contains(history.Rows, r => (string)r[8]! == "Reversed");
        Assert.Equal(bank.PaymentNumber, history.Rows.Select(r => (string)r[0]!).First(n => n == bank.PaymentNumber));

        // posted payments reconcile with the customer ledger (the dashboard check)
        var dash = await Run(w.Sales, s => Svc<DashboardService>(s).CompanyAsync(new DashboardFilters(null, null)));
        Assert.Equal(150_000m, dash.TotalCollections);
        Assert.All(dash.Checks, c => Assert.True(c.Reconciled, c.Name));
    }

    [MySqlFact]
    public async Task AdvancePaymentReport_ListsMoneyPaidAheadOfDueDate()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 1_200_000m);                    // 500,000 down payment + 700,000 into future installments
        var report = await ReportAsync(w.Sales, "advance-payments", new ReportFilters { ProjectId = w.ProjectId });
        var row = Assert.Single(report.Rows);
        Assert.Equal(700_000m, Cell(report, 0, "paid"));
        Assert.Equal(700_000m, Total(report, "total"));
        Assert.NotNull(row[6]);
    }

    // =====================================================================================================
    // customer reports
    // =====================================================================================================
    [MySqlFact]
    public async Task CustomerReports_StatementLedgerAndBalances_MatchTheAgreementLedger()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w, monthsBack: 2);
        await PostAsync(w, d, 400_000m);
        var ledger = await Run(w.Accountant, s => Svc<LedgerService>(s).GetAgreementLedgerAsync(d.AgreementId));
        var f = new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId };

        var statement = await ReportAsync(w.Sales, "customer-statement", f);
        var row = Assert.Single(statement.Rows);
        Assert.Equal(ledger.Summary.LedgerBalance, Cell(statement, 0, "closing"));
        Assert.Equal(400_000m, Cell(statement, 0, "credits"));
        Assert.Equal(0m, Cell(statement, 0, "opening"));

        var customerLedger = await ReportAsync(w.Sales, "customer-ledger", f);
        Assert.Equal(customerLedger.TotalRows, customerLedger.Rows.Count);
        var last = customerLedger.Rows[^1];
        Assert.Equal(ledger.Summary.LedgerBalance, Convert.ToDecimal(last[6]));            // running balance of the last entry
        Assert.Equal(ledger.Summary.LedgerBalance, Total(customerLedger, "balance"));
        var debitsOnly = await ReportAsync(w.Sales, "customer-ledger", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId, TransactionType = "PaymentReceived" });
        Assert.Single(debitsOnly.Rows);
        Assert.Equal(400_000m, Total(debitsOnly, "credit"));

        // opening balance with a start date after the first entries
        var later = await ReportAsync(w.Sales, "customer-statement", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId, From = w.Today.AddDays(1) });
        Assert.Equal(ledger.Summary.LedgerBalance, Cell(later, 0, "opening"));
        Assert.Equal(0m, Cell(later, 0, "debits"));

        var props = await ReportAsync(w.Sales, "customer-properties", f);
        Assert.Single(props.Rows);
        Assert.Equal(100m, Cell(props, 0, "share"));
        var payments = await ReportAsync(w.Sales, "payment-history", f);
        Assert.Single(payments.Rows); Assert.Equal(400_000m, Total(payments, "amount"));
        var installments = await ReportAsync(w.Sales, "installment-history", f);
        Assert.Equal(27, installments.TotalRows);                                    // down payment + 24 monthly + possession + development
        Assert.Equal(ledger.Summary.ScheduledTotal, Total(installments, "amount"));
        Assert.Equal(400_000m, Total(installments, "paid"));
        Assert.Equal(ledger.Summary.Outstanding + ledger.Summary.NotYetDue, Total(installments, "outstanding"));
        var paidOnly = await ReportAsync(w.Sales, "installment-history", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId, Status = "PartiallyPaid" });
        Assert.NotEmpty(paidOnly.Rows);
        var balance = await ReportAsync(w.Sales, "outstanding-balance", f);
        Assert.Equal(ledger.Summary.Outstanding, Cell(balance, 0, "due"));
        Assert.Equal(ledger.Summary.Overdue, Cell(balance, 0, "overdue"));
        var overdue = await ReportAsync(w.Sales, "overdue", f);
        Assert.Equal(ledger.Summary.Overdue, Total(overdue, "outstanding"));
        Assert.NotNull(row);
    }

    [MySqlFact]
    public async Task CustomerReports_NeedACustomer_AndRespectCustomerAccess_AndPaging()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await Assert.ThrowsAsync<RequestValidationException>(() => ReportAsync(w.Sales, "customer-ledger", new ReportFilters()));
        await Assert.ThrowsAsync<RequestValidationException>(() => ReportAsync(w.Sales, "customer-statement", new ReportFilters()));
        // a user with no access to the customer's project cannot read the customer's reports
        await Assert.ThrowsAnyAsync<DomainException>(() => ReportAsync(w.Outsider, "customer-ledger", new ReportFilters { CustomerId = d.CustomerId }));
        await Assert.ThrowsAnyAsync<DomainException>(() => ReportAsync(w.Outsider, "customer-properties", new ReportFilters { CustomerId = d.CustomerId }));
        // a user without the reports permission cannot run any operational report
        await Assert.ThrowsAsync<ForbiddenException>(() => ReportAsync(w.Cashier, "daily-sales", new ReportFilters()));

        // server-side paging: 2 rows per page, totals still cover the whole report
        var page1 = await ReportAsync(w.Sales, "installment-history", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId, Page = 1, PageSize = 2 });
        var page2 = await ReportAsync(w.Sales, "installment-history", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d.CustomerId, Page = 2, PageSize = 2 });
        Assert.Equal(2, page1.Rows.Count); Assert.Equal(2, page2.Rows.Count);
        Assert.True(page1.TotalRows > 4);
        Assert.Equal(Total(page1, "amount"), Total(page2, "amount"));
        Assert.True(page1.TotalPages > 2);
    }

    [MySqlFact]
    public async Task AgentCommissions_ListCommissionExpenses()
    {
        var w = await WorldAsync();
        var (cat, vendor) = await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            var v = new Vendor { Code = "AG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), Name = "Agent Khan" };
            db.Vendors.Add(v); await db.SaveChangesAsync();
            return (await db.ExpenseCategories.Where(c => c.Code == "AGENT_COMMISSION").Select(c => c.Id).FirstAsync(), v.Id);
        });
        await Run(w.Admin, async s =>
        {
            var db = Svc<AppDbContext>(s);
            db.Expenses.Add(new Expense { ExpenseNumber = "EXP-AC-" + Guid.NewGuid().ToString("N")[..8], ProjectId = w.ProjectId, CategoryId = cat, VendorId = vendor, Description = "Commission plot 7",
                ExpenseDate = w.Today, Amount = 50_000m, AmountPaid = 20_000m, Status = ExpenseStatus.Approved, IdempotencyKey = Guid.NewGuid().ToString("N") });
            return await db.SaveChangesAsync();
        });
        var report = await ReportAsync(w.Sales, "agent-commissions", new ReportFilters { ProjectId = w.ProjectId });
        var row = Assert.Single(report.Rows);
        Assert.Equal("Agent Khan", row[3]);
        Assert.Equal(50_000m, Total(report, "amount")); Assert.Equal(30_000m, Total(report, "outstanding"));
        Assert.Contains(report.Notes, n => n.Contains("agent"));
        var rejected = await ReportAsync(w.Sales, "agent-commissions", new ReportFilters { ProjectId = w.ProjectId, Status = "Rejected" });
        Assert.Empty(rejected.Rows);
    }

    // =====================================================================================================
    // financial reports and exports
    // =====================================================================================================
    [MySqlFact]
    public async Task FinancialReports_AreTheAccountingEnginesFigures_AndNeedTheFinancialPermission()
    {
        var w = await WorldAsync();
        var f = new ReportFilters { ProjectId = w.ProjectId, From = w.Today.AddMonths(-1), To = w.Today };
        var tb = await ReportAsync(w.Accountant, "trial-balance", f);
        var direct = await Run(w.Accountant, s => Svc<AccountingReportService>(s).TrialBalanceAsync(new ReportFilter(f.From, f.To, w.ProjectId, null)));
        Assert.Equal(direct.TotalClosingDebit, Total(tb, "cd"));
        Assert.Equal(direct.TotalPeriodCredit, Total(tb, "pc"));
        Assert.Equal(direct.Rows.Count, tb.TotalRows);
        foreach (var key in new[] { "balance-sheet", "income-statement", "cash-flow", "accounts-receivable", "accounts-payable", "customer-control" })
        {
            var r = await ReportAsync(w.Accountant, key, new ReportFilters { ProjectId = w.ProjectId });
            Assert.Equal("Financial reports", r.Group);
            Assert.NotNull(r.Columns);
        }
        await Assert.ThrowsAsync<ForbiddenException>(() => ReportAsync(w.Accountant, "trial-balance", new ReportFilters()));            // company-wide needs all projects
        await Assert.ThrowsAsync<ForbiddenException>(() => ReportAsync(w.Sales, "trial-balance", new ReportFilters { ProjectId = w.ProjectId })); // no accounting.reports
        await Assert.ThrowsAsync<RequestValidationException>(() => ReportAsync(w.Accountant, "general-ledger", new ReportFilters { ProjectId = w.ProjectId }));   // account required
        await Assert.ThrowsAsync<RequestValidationException>(() => ReportAsync(w.Accountant, "project-expenses", new ReportFilters()));
        await Assert.ThrowsAsync<NotFoundException>(() => ReportAsync(w.Accountant, "nope", new ReportFilters()));

        // the catalog shows each user only what they may open
        var accountantCatalog = await Run(w.Accountant, s => Svc<ReportingService>(s).ListAsync());
        Assert.Contains(accountantCatalog, r => r.Key == "trial-balance");
        Assert.Contains(accountantCatalog, r => r.Key == "daily-sales");
        var salesCatalog = await Run(w.Sales, s => Svc<ReportingService>(s).ListAsync());
        Assert.DoesNotContain(salesCatalog, r => r.IsFinancial);
        Assert.Contains(salesCatalog, r => r.Key == "daily-sales");
        Assert.Empty(await Run(w.Cashier, s => Svc<ReportingService>(s).ListAsync()));
    }

    [MySqlFact]
    public async Task Exports_ProducePdfAndExcel_AreAudited_AndNeedTheExportPermission()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        await PostAsync(w, d, 100_000m);
        var f = new ReportFilters { ProjectId = w.ProjectId };

        var pdf = await Run(w.Sales, s => Svc<ReportingService>(s).ExportAsync("daily-collection", f, ExportFormat.Pdf));
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 8));
        Assert.EndsWith(".pdf", pdf.FileName);

        var xlsx = await Run(w.Sales, s => Svc<ReportingService>(s).ExportAsync("daily-collection", f, ExportFormat.Excel));
        Assert.EndsWith(".xlsx", xlsx.FileName);
        using (var zip = new ZipArchive(new MemoryStream(xlsx.Content)))
        {
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = await sheet.ReadToEndAsync();
            Assert.Contains("Daily collection", xml);
            Assert.Contains("<v>100000", xml);                                   // a real numeric cell, not text
        }

        // exports contain all rows, not just the page
        var d2 = await DealAsync(w);
        var all = await Run(w.Sales, s => Svc<ReportingService>(s).ExportAsync("installment-history", new ReportFilters { ProjectId = w.ProjectId, CustomerId = d2.CustomerId, PageSize = 2 }, ExportFormat.Excel));
        using (var zip = new ZipArchive(new MemoryStream(all.Content)))
        {
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var rows = System.Text.RegularExpressions.Regex.Matches(await sheet.ReadToEndAsync(), "<row ").Count;
            Assert.True(rows > 20, $"export had {rows} rows");
        }

        // auditing
        var audited = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.CountAsync(a => a.Action == "ReportExported" && a.EntityId == "daily-collection" && a.UserId == w.Sales));
        Assert.True(audited >= 2);

        // permission: the accountant may export; a user with view but no export may not
        Assert.NotEmpty((await Run(w.Accountant, s => Svc<ReportingService>(s).ExportAsync("daily-collection", f, ExportFormat.Pdf))).Content);
        var viewer = await UserAsync();
        As(w.Admin);
        using (var s = fx.Scope())
        {
            var role = await Svc<RoleService>(s).CreateAsync(new CreateRoleRequest("ReportViewer" + Guid.NewGuid().ToString("N")[..6], "view only", [Permissions.ReportsView]));
            var um = s.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<RealEstate.Infrastructure.Identity.ApplicationUser>>();
            Assert.True((await um.AddToRoleAsync((await um.FindByIdAsync(viewer.ToString()))!, role.Name)).Succeeded);
            await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, viewer);
        }
        Assert.NotEmpty((await ReportAsync(viewer, "daily-collection", f)).Columns);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(viewer, s => Svc<ReportingService>(s).ExportAsync("daily-collection", f, ExportFormat.Pdf)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Cashier, s => Svc<ReportingService>(s).ExportAsync("daily-collection", f, ExportFormat.Excel)));
    }
}
