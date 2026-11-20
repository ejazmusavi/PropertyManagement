using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Application.Administration;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Documents;
using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;
using RealEstate.Application.Inventory;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Tests.Support;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Phase 7 integration tests against MySQL: expense workflow, vendors and invoices, payments, government fees, concurrency, access and constraints.</summary>
[Collection("phase1")]
public class Phase07Tests(Phase1Fixture fx)
{
    private static readonly Dictionary<Guid, string> Names = new();
    private static int counter;
    private record World(Guid Admin, Guid Manager, Guid Accountant, Guid Director, Guid Outsider, long ProjectId, long OtherProjectId, DateOnly Today);

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
        long pid, other; DateOnly today;
        using (var s = fx.Scope()) { pid = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id; other = (await Svc<ProjectService>(s).CreateAsync(fx.NewProject())).Id; }
        var manager = await UserAsync(RoleNames.ProjectManager);
        var accountant = await UserAsync(RoleNames.Accountant);
        var director = await UserAsync(RoleNames.Director);
        var outsider = await UserAsync(RoleNames.Accountant);
        As(admin);
        using (var s = fx.Scope())
        {
            foreach (var u in new[] { manager, accountant }) await Svc<ProjectAccessAdminService>(s).AssignAsync(pid, u);
            today = await Svc<PricingSupport>(s).TodayAsync();
        }
        return new World(admin, manager, accountant, director, outsider, pid, other, today);
    }

    private static FileUpload Pdf(string name = "invoice.pdf") => new(name, "application/pdf", 5, new MemoryStream([1, 2, 3, 4, 5]));

    private async Task<long> CategoryAsync(string code = "OFFICE_RENT") => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).ExpenseCategories.Where(c => c.Code == code).Select(c => c.Id).FirstAsync());
    private async Task<long> MethodAsync(string code = "CASH") => await Run(Names.Keys.First(), async s => await Svc<AppDbContext>(s).PaymentMethods.Where(m => m.Code == code).Select(m => m.Id).FirstAsync());

    private async Task<ExpenseDetailDto> NewExpenseAsync(World w, decimal amount = 100_000m, long? vendorId = null, bool submitted = false, bool approved = false, DateOnly? due = null)
    {
        var cat = await CategoryAsync();
        var date = due is { } dd && dd < w.Today ? dd.AddDays(-10) : w.Today;
        var e = await Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, vendorId, "Office rent " + Guid.NewGuid().ToString("N")[..6], date, amount, due, null, null, Guid.NewGuid().ToString("N"))));
        if (submitted || approved)
        {
            await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Invoice, null, Pdf()));
            e = await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null)));
        }
        if (approved) e = await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest("ok", null)));
        return e;
    }

    private async Task<ExpenseDetailDto> PayAsync(World w, long expenseId, decimal amount, string? key = null, Guid? actor = null)
    {
        var m = await MethodAsync();
        return await Run(actor ?? w.Accountant, s => Svc<ExpenseService>(s).PayAsync(expenseId, new PayExpenseRequest(amount, m, w.Today, null, null, key ?? Guid.NewGuid().ToString("N"))));
    }

    private async Task<List<AccountingPostingRequest>> OutboxAsync(AccountingEventType type, string sourceType, long sourceId) =>
        await Run(w0, async s => await Svc<AppDbContext>(s).AccountingPostingRequests.Where(a => a.EventType == type && a.SourceType == sourceType && a.SourceId == sourceId).ToListAsync());

    private Guid w0 => Names.Keys.First();

    private static async Task<DomainException> Fails(Func<Task> f) => await Assert.ThrowsAnyAsync<DomainException>(f);

    // ===================================================================================== seed data
    [MySqlFact]
    public async Task Seeds_ElevenCategories_AndSevenGovernmentFeeTypes()
    {
        await WorldAsync();
        var cats = await Run(w0, async s => await Svc<VendorService>(s).ListCategoriesAsync());
        foreach (var n in new[] { "Salaries", "Office rent", "Utilities", "Marketing", "Construction", "Land acquisition", "Legal expenses", "Transportation", "Maintenance", "Agent commission", "Miscellaneous" })
            Assert.Contains(cats, c => c.Name == n && c.IsSystem && c.IsActive);
        var types = await Run(w0, async s => await Svc<GovernmentFeeService>(s).ListTypesAsync());
        foreach (var k in new[] { GovernmentFeeKind.StampDuty, GovernmentFeeKind.Registration, GovernmentFeeKind.TransferFee, GovernmentFeeKind.Tax, GovernmentFeeKind.NocFee, GovernmentFeeKind.DevelopmentCharge, GovernmentFeeKind.Other })
            Assert.Contains(types, t => t.Kind == k && t.IsSystem);
    }

    // ===================================================================================== expense workflow
    [MySqlFact]
    public async Task ExpenseWorkflow_Create_Document_Submit_Approve_PartialPay_Pay_AndAccountingEvents()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 100_000m);
        Assert.Equal(ExpenseStatus.Draft, e.Status);
        Assert.StartsWith("EXP-", e.ExpenseNumber);
        Assert.Equal(w.ProjectId, e.ProjectId);

        // a supporting document is required before approval
        var ex = await Fails(() => Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null))));
        Assert.Contains("supporting document", ex.Message);
        var doc = await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Invoice, "Rent invoice", Pdf()));
        Assert.Equal("Rent invoice", doc.Title);
        e = await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null)));
        Assert.Equal(ExpenseStatus.PendingApproval, e.Status);

        // nothing can be paid before approval; the submitter cannot approve
        await Fails(() => PayAsync(w, e.Id, 1m));
        e = await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest("ok", null)));
        Assert.Equal(ExpenseStatus.Approved, e.Status);
        Assert.Single(e.Approvals);
        Assert.Single(await OutboxAsync(AccountingEventType.ExpenseApproved, "Expense", e.Id));

        // unpaid -> partially paid -> paid
        Assert.Equal(ExpensePaymentStatus.Unpaid, e.PaymentStatus);
        e = await PayAsync(w, e.Id, 40_000m);
        Assert.Equal(ExpensePaymentStatus.PartiallyPaid, e.PaymentStatus);
        Assert.Equal(60_000m, e.Outstanding);
        await Fails(() => PayAsync(w, e.Id, 60_000.01m));
        e = await PayAsync(w, e.Id, 60_000m);
        Assert.Equal(ExpensePaymentStatus.Paid, e.PaymentStatus);
        Assert.Equal(0m, e.Outstanding);
        Assert.Equal(2, e.Payments.Count(p => p.Status == VendorPaymentStatus.Posted));
        foreach (var p in e.Payments) Assert.Single(await OutboxAsync(AccountingEventType.ExpensePaid, "VendorPayment", p.Id));
        await Fails(() => PayAsync(w, e.Id, 1m));
        // an expense with payments cannot be cancelled
        await Fails(() => Run(w.Director, s => Svc<ExpenseService>(s).CancelAsync(e.Id, new CancelExpenseRequest("Cancel after paying", null))));
    }

    [MySqlFact]
    public async Task Approval_IsMakerChecker_AndRejectionAllowsEditAndResubmit()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 5_000m, submitted: true);
        // the submitter holds no approve permission; give the maker a second role by acting as an administrator-submitter
        var maker = await UserAsync(RoleNames.Administrator);
        var cat = await CategoryAsync();
        var mine = await Run(maker, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "Maker expense", w.Today, 700m, null, null, null, null)));
        await Run(maker, s => Svc<ExpenseService>(s).UploadAttachmentAsync(mine.Id, ExpenseDocumentType.Receipt, null, Pdf("r.pdf")));
        mine = await Run(maker, s => Svc<ExpenseService>(s).SubmitAsync(mine.Id, new SubmitExpenseRequest(null)));
        var self = await Fails(() => Run(maker, s => Svc<ExpenseService>(s).ApproveAsync(mine.Id, new ApproveExpenseRequest(null, null))));
        Assert.Contains("submitted it", self.Message);
        await Fails(() => Run(maker, s => Svc<ExpenseService>(s).RejectAsync(mine.Id, new RejectExpenseRequest("self reject", null))));

        // another user rejects (comment required); the maker edits and resubmits; it is then approved
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<ExpenseService>(s).RejectAsync(e.Id, new RejectExpenseRequest("", null))));
        e = await Run(w.Accountant, s => Svc<ExpenseService>(s).RejectAsync(e.Id, new RejectExpenseRequest("Amount does not match the invoice", null)));
        Assert.Equal(ExpenseStatus.Rejected, e.Status);
        e = await Run(w.Manager, s => Svc<ExpenseService>(s).UpdateAsync(e.Id, new UpdateExpenseRequest(e.CategoryId, null, "Corrected rent", w.Today, 4_500m, null, null, null, e.RowVersion)));
        Assert.Equal(ExpenseStatus.Draft, e.Status);
        Assert.Equal(4_500m, e.Amount);
        e = await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(e.Id, new SubmitExpenseRequest(null)));
        e = await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest(null, null)));
        Assert.Equal(ExpenseStatus.Approved, e.Status);
        Assert.Equal(2, e.Approvals.Count); // rejection and approval history kept
        await Fails(() => Run(w.Manager, s => Svc<ExpenseService>(s).UpdateAsync(e.Id, new UpdateExpenseRequest(e.CategoryId, null, "late edit", w.Today, 1m, null, null, null, e.RowVersion))));
    }

    [MySqlFact]
    public async Task Cancelling_AnApprovedUnpaidExpense_QueuesAReversal_AndNeedsApprovePermission()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 2_000m, approved: true);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).CancelAsync(e.Id, new CancelExpenseRequest("Manager cannot cancel approved", null))));
        e = await Run(w.Accountant, s => Svc<ExpenseService>(s).CancelAsync(e.Id, new CancelExpenseRequest("Duplicate registration", null)));
        Assert.Equal(ExpenseStatus.Cancelled, e.Status);
        Assert.Single(await OutboxAsync(AccountingEventType.ExpenseCancelled, "Expense", e.Id));
        await Fails(() => PayAsync(w, e.Id, 1m));
        await Fails(() => Run(w.Accountant, s => Svc<ExpenseService>(s).CancelAsync(e.Id, new CancelExpenseRequest("Again", null))));
    }

    [MySqlFact]
    public async Task ReversingAPayment_RestoresThePayable_AndQueuesTheReversal()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 10_000m, approved: true);
        e = await PayAsync(w, e.Id, 10_000m);
        Assert.Equal(ExpensePaymentStatus.Paid, e.PaymentStatus);
        var pay = e.Payments.Single();
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).ReversePaymentAsync(pay.Id, new ReverseVendorPaymentRequest("Not allowed for managers"))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<ExpenseService>(s).ReversePaymentAsync(pay.Id, new ReverseVendorPaymentRequest(""))));
        e = await Run(w.Accountant, s => Svc<ExpenseService>(s).ReversePaymentAsync(pay.Id, new ReverseVendorPaymentRequest("Cheque bounced")));
        Assert.Equal(0m, e.AmountPaid);
        Assert.Equal(10_000m, e.Outstanding);
        Assert.Equal(VendorPaymentStatus.Reversed, e.Payments.Single().Status);
        Assert.Single(await OutboxAsync(AccountingEventType.ExpensePaymentReversed, "VendorPayment", pay.Id));
        await Fails(() => Run(w.Accountant, s => Svc<ExpenseService>(s).ReversePaymentAsync(pay.Id, new ReverseVendorPaymentRequest("Twice"))));
        e = await PayAsync(w, e.Id, 10_000m); // payable again, payment history retained
        Assert.Equal(2, e.Payments.Count);
    }

    // ===================================================================================== idempotency and concurrency
    [MySqlFact]
    public async Task Payment_IsIdempotent_AndAKeyCannotBeReusedForADifferentPayment()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 9_000m, approved: true);
        var key = Guid.NewGuid().ToString("N");
        await PayAsync(w, e.Id, 3_000m, key);
        var again = await PayAsync(w, e.Id, 3_000m, key);
        Assert.Single(again.Payments);
        Assert.Equal(3_000m, again.AmountPaid);
        await Assert.ThrowsAsync<RequestValidationException>(() => PayAsync(w, e.Id, 3_001m, key));
        Assert.Single(await Run(w.Admin, async s => await Svc<AppDbContext>(s).VendorPayments.Where(p => p.ExpenseId == e.Id).ToListAsync()));
        // expense creation is idempotent as well
        var cat = await CategoryAsync();
        var k2 = Guid.NewGuid().ToString("N");
        var a = await Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "Idempotent", w.Today, 10m, null, null, null, k2)));
        var b = await Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "Idempotent", w.Today, 10m, null, null, null, k2)));
        Assert.Equal(a.Id, b.Id);
    }

    [MySqlFact]
    public async Task ConcurrentPayments_NeverOverpay_AndTheIdenticalSubmissionPostsOnce()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 1_000m, approved: true);
        var m = await MethodAsync();
        As(w.Accountant);
        // six different payments of the full amount at once: exactly one fits
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<ExpenseService>(s).PayAsync(e.Id, new PayExpenseRequest(1_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(x => x));
        var after = await Run(w.Accountant, s => Svc<ExpenseService>(s).GetAsync(e.Id));
        Assert.Equal(1_000m, after.AmountPaid);
        Assert.Single(after.Payments);

        // five identical submissions (same key) resolve to one payment
        var e2 = await NewExpenseAsync(w, 500m, approved: true);
        As(w.Accountant);
        var key = Guid.NewGuid().ToString("N");
        var same = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<ExpenseService>(s).PayAsync(e2.Id, new PayExpenseRequest(500m, m, w.Today, null, null, key)); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Contains(true, same);
        var after2 = await Run(w.Accountant, s => Svc<ExpenseService>(s).GetAsync(e2.Id));
        Assert.Single(after2.Payments);
        Assert.Equal(500m, after2.AmountPaid);
    }

    [MySqlFact]
    public async Task ConcurrentApprovals_ApproveOnce()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 800m, submitted: true);
        As(w.Accountant);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest(null, null)); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(x => x));
        Assert.Single(await OutboxAsync(AccountingEventType.ExpenseApproved, "Expense", e.Id));
        Assert.Single((await Run(w.Accountant, s => Svc<ExpenseService>(s).GetAsync(e.Id))).Approvals);
    }

    // ===================================================================================== project access and authorization
    [MySqlFact]
    public async Task ExpenseAccess_IsProjectScoped_AndPermissionChecked()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 300m, approved: true);
        // an accountant who is not assigned to the project cannot see, change or pay the expense
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).GetAsync(e.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).PayAsync(e.Id, new PayExpenseRequest(1m, 1, w.Today, null, null, "k-out"))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).ListAttachmentsAsync(e.Id)));
        var cat = await CategoryAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "Intruder", w.Today, 5m, null, null, null, null))));
        var list = await Run(w.Outsider, s => Svc<ExpenseService>(s).ListAsync(new ExpenseListRequest { PageSize = 100 }));
        Assert.DoesNotContain(list.Items, x => x.Id == e.Id);
        Assert.Contains((await Run(w.Director, s => Svc<ExpenseService>(s).ListAsync(new ExpenseListRequest { PageSize = 100 }))).Items, x => x.Id == e.Id);
        // missing permissions: managers cannot approve or pay; accountants cannot register expenses
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).ApproveAsync(e.Id, new ApproveExpenseRequest(null, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => PayAsync(w, e.Id, 1m, actor: w.Manager));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, null, "No permission", w.Today, 5m, null, null, null, null))));
        // unknown ids look like inaccessible ones for non-privileged users
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).GetAsync(long.MaxValue)));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(w.Admin, s => Svc<ExpenseService>(s).GetAsync(long.MaxValue)));
        // dashboards only count the caller's projects
        var dash = await Run(w.Outsider, s => Svc<ExpenseService>(s).DashboardAsync(null, null, null));
        Assert.Equal(0, dash.PendingApprovalCount);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<ExpenseService>(s).DashboardAsync(w.ProjectId, null, null)));
    }

    [MySqlFact]
    public async Task Attachments_AreStoredSecurely_AndRemovalNeedsAReason()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 300m);
        var d = await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Quotation, null, Pdf("quote.pdf")));
        var file = await Run(w.Manager, s => Svc<ExpenseService>(s).DownloadAttachmentAsync(e.Id, d.Id));
        using (var ms = new MemoryStream()) { await file.Content.CopyToAsync(ms); Assert.Equal(5, ms.Length); }
        await Fails(() => Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(e.Id, ExpenseDocumentType.Other, null, new FileUpload("virus.exe", "application/x-msdownload", 3, new MemoryStream([1, 2, 3])))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Manager, async s => { await Svc<ExpenseService>(s).DeleteAttachmentAsync(e.Id, d.Id, "x"); return 0; }));
        await Run(w.Manager, async s => { await Svc<ExpenseService>(s).DeleteAttachmentAsync(e.Id, d.Id, "Wrong file uploaded"); return 0; });
        Assert.Empty((await Run(w.Manager, s => Svc<ExpenseService>(s).GetAsync(e.Id))).Attachments);
        await Assert.ThrowsAsync<NotFoundException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).DownloadAttachmentAsync(e.Id, d.Id)));
        // the audit trail shows the upload and the removal with the reason
        var audits = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AuditLogs.Where(a => a.EntityType == "ExpenseAttachment" && a.EntityId == d.Id.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Contains("ExpenseAttachment.Uploaded", audits);
        Assert.Contains("ExpenseAttachment.Removed", audits);
    }

    // ===================================================================================== vendors and invoices
    private async Task<VendorDto> VendorAsync(World w, string? name = null) =>
        await Run(w.Accountant, s => Svc<VendorService>(s).CreateAsync(new SaveVendorRequest(name ?? "Vendor " + Guid.NewGuid().ToString("N")[..8], "Contact", "0300-0000000", null, null, "NTN-1", null)));

    [MySqlFact]
    public async Task Vendors_AreManaged_AndPayablesTracked()
    {
        var w = await WorldAsync();
        var v = await VendorAsync(w);
        Assert.StartsWith("VEN-", v.Code);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<VendorService>(s).CreateAsync(new SaveVendorRequest(v.Name, null, null, null, null, null, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<VendorService>(s).CreateAsync(new SaveVendorRequest("Nope", null, null, null, null, null, null))));
        var stale = v.RowVersion;
        v = await Run(w.Accountant, s => Svc<VendorService>(s).UpdateAsync(v.Id, new SaveVendorRequest(v.Name, "New contact", v.Phone, null, null, "NTN-2", null, true, stale)));
        await Assert.ThrowsAsync<ConcurrencyException>(() => Run(w.Accountant, s => Svc<VendorService>(s).UpdateAsync(v.Id, new SaveVendorRequest(v.Name, "Stale", null, null, null, null, null, true, stale))));

        // two approved expenses, one partly paid: payable = outstanding total
        var e1 = await NewExpenseAsync(w, 10_000m, v.Id, approved: true, due: w.Today.AddDays(-5));
        var e2 = await NewExpenseAsync(w, 4_000m, v.Id, approved: true, due: w.Today.AddDays(10));
        await PayAsync(w, e1.Id, 2_500m);
        var d = await Run(w.Accountant, s => Svc<VendorService>(s).GetAsync(v.Id));
        Assert.Equal(11_500m, d.Payable);
        Assert.Equal(7_500m, d.Overdue);
        Assert.Equal(2_500m, d.Paid);
        Assert.Equal(2, d.OpenPayables.Count);
        var row = (await Run(w.Accountant, s => Svc<VendorService>(s).ListAsync(new VendorListRequest { Search = v.Code }))).Items.Single();
        Assert.Equal(11_500m, row.Payable);
        // an inactive vendor takes no new expenses
        await Run(w.Accountant, s => Svc<VendorService>(s).UpdateAsync(v.Id, new SaveVendorRequest(v.Name, null, null, null, null, null, null, false, null)));
        var cat = await CategoryAsync();
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, cat, v.Id, "Inactive vendor", w.Today, 5m, null, null, null, null))));
        // an outsider's payables view excludes projects they cannot access
        var o = await Run(w.Outsider, s => Svc<VendorService>(s).GetAsync(v.Id));
        Assert.Equal(0m, o.Payable);
    }

    [MySqlFact]
    public async Task VendorInvoice_RegistersAPayable_RejectsDuplicates_AndIsPaidThroughTheExpense()
    {
        var w = await WorldAsync();
        var v = await VendorAsync(w);
        var cat = await CategoryAsync("CONSTRUCTION");
        var key = Guid.NewGuid().ToString("N");
        var req = new CreateVendorInvoiceRequest(w.ProjectId, v.Id, "INV-1001", cat, w.Today, w.Today.AddDays(30), 50_000m, null, "first", key);
        var inv = await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req));
        Assert.Equal("INV-1001", inv.InvoiceNumber);
        Assert.Equal(ExpenseStatus.Draft, inv.ExpenseStatus);
        Assert.Equal(inv.Id, (await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req))).Id); // idempotent retry
        // the same invoice number for the same vendor is rejected (case-insensitive); another vendor may reuse it
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req with { InvoiceNumber = "inv-1001", IdempotencyKey = null })));
        var other = await VendorAsync(w);
        await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req with { VendorId = other.Id, IdempotencyKey = null }));
        // vendor and amount of an invoiced expense cannot be edited
        var exp = await Run(w.Accountant, s => Svc<ExpenseService>(s).GetAsync(inv.ExpenseId));
        Assert.Equal(inv.InvoiceNumber, exp.Invoice!.InvoiceNumber);
        Assert.Equal(v.Id, exp.VendorId);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<ExpenseService>(s).UpdateAsync(exp.Id, new UpdateExpenseRequest(exp.CategoryId, exp.VendorId, "x", w.Today, 1m, null, null, null, exp.RowVersion))));
        await Run(w.Manager, s => Svc<ExpenseService>(s).UploadAttachmentAsync(exp.Id, ExpenseDocumentType.Invoice, null, Pdf()));
        await Fails(() => Run(w.Manager, s => Svc<ExpenseService>(s).UpdateAsync(exp.Id, new UpdateExpenseRequest(exp.CategoryId, exp.VendorId, "x", w.Today, 49_999m, null, null, null, exp.RowVersion))));
        await Run(w.Manager, s => Svc<ExpenseService>(s).SubmitAsync(exp.Id, new SubmitExpenseRequest(null)));
        await Run(w.Accountant, s => Svc<ExpenseService>(s).ApproveAsync(exp.Id, new ApproveExpenseRequest(null, null)));
        await PayAsync(w, exp.Id, 20_000m);
        var row = await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).GetAsync(inv.Id));
        Assert.Equal(30_000m, row.Outstanding);
        Assert.Equal(ExpensePaymentStatus.PartiallyPaid, row.PaymentStatus);
        Assert.Contains((await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).ListAsync(new VendorInvoiceListRequest { VendorId = v.Id }))).Items, x => x.Id == inv.Id);
        // a cancelled invoice releases its number
        var draft = await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req with { InvoiceNumber = "INV-2000", IdempotencyKey = null }));
        await Run(w.Manager, s => Svc<ExpenseService>(s).CancelAsync(draft.ExpenseId, new CancelExpenseRequest("Entered twice", null)));
        await Run(w.Accountant, s => Svc<VendorInvoiceService>(s).CreateAsync(req with { InvoiceNumber = "INV-2000", IdempotencyKey = null }));
    }

    [MySqlFact]
    public async Task Dashboard_SummarisesExpensesPayablesAndPending()
    {
        var w = await WorldAsync();
        var v = await VendorAsync(w);
        var a = await NewExpenseAsync(w, 10_000m, v.Id, approved: true, due: w.Today.AddDays(-1));
        await PayAsync(w, a.Id, 4_000m);
        await NewExpenseAsync(w, 2_000m, null, submitted: true);
        await NewExpenseAsync(w, 700m); // draft
        var d = await Run(w.Accountant, s => Svc<ExpenseService>(s).DashboardAsync(w.ProjectId, null, null));
        Assert.Equal(10_000m, d.TotalApproved);
        Assert.Equal(4_000m, d.TotalPaid);
        Assert.Equal(6_000m, d.PayablesOutstanding);
        Assert.Equal(6_000m, d.PayablesOverdue);
        Assert.Equal(1, d.PendingApprovalCount);
        Assert.Equal(2_000m, d.PendingApprovalAmount);
        Assert.Equal(1, d.DraftCount);
        Assert.Equal(10_000m, d.ByCategory.Single(c => c.Label == "Office rent").Amount);
        Assert.Contains(d.TopVendorPayables, x => x.Amount == 6_000m);
        Assert.Equal(10_000m, d.ByMonth.Sum(m => m.Amount));
    }

    // ===================================================================================== categories
    [MySqlFact]
    public async Task Categories_CanBeAdded_StandardOnesAreProtected_AndInactiveOnesCannotBeUsed()
    {
        var w = await WorldAsync();
        var code = "CUSTOM_" + Interlocked.Increment(ref counter) + Guid.NewGuid().ToString("N")[..4];
        var c = await Run(w.Accountant, s => Svc<VendorService>(s).CreateCategoryAsync(new SaveExpenseCategoryRequest(code, "Custom " + code, null)));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<VendorService>(s).CreateCategoryAsync(new SaveExpenseCategoryRequest(code, "Another", null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<VendorService>(s).CreateCategoryAsync(new SaveExpenseCategoryRequest(code + "X", "Nope", null))));
        var rent = (await Run(w.Accountant, s => Svc<VendorService>(s).ListCategoriesAsync())).Single(x => x.Code == "OFFICE_RENT");
        await Fails(() => Run(w.Accountant, s => Svc<VendorService>(s).UpdateCategoryAsync(rent.Id, new SaveExpenseCategoryRequest("OFFICE_RENT", "Office rent", null, false))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<VendorService>(s).UpdateCategoryAsync(rent.Id, new SaveExpenseCategoryRequest("RENT2", "Office rent", null, true))));
        await Run(w.Accountant, s => Svc<VendorService>(s).UpdateCategoryAsync(c.Id, new SaveExpenseCategoryRequest(code, c.Name, "retired", false)));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Manager, s => Svc<ExpenseService>(s).CreateAsync(new CreateExpenseRequest(w.ProjectId, c.Id, null, "Inactive category", w.Today, 5m, null, null, null, null))));
        Assert.DoesNotContain(await Run(w.Manager, s => Svc<VendorService>(s).ListCategoriesAsync(true)), x => x.Id == c.Id);
    }

    // ===================================================================================== government fees
    private record Deal(long AgreementId, long CustomerId, string AgreementNumber);

    private async Task<Deal> DealAsync(World w)
    {
        var propertyId = await Run(w.Admin, async s =>
        {
            var typeId = await Svc<AppDbContext>(s).PropertyTypes.Where(t => t.Code == "RES-PLOT").Select(t => t.Id).FirstAsync();
            var n = Interlocked.Increment(ref counter);
            return (await Svc<PropertyService>(s).CreateAsync(new CreatePropertyRequest(w.ProjectId, $"GF-{Guid.NewGuid():N}"[..14], "G" + n, null, null, null, typeId, 5m, MeasurementUnit.Marla,
                null, null, false, false, false, DevelopmentStatus.Developed, null, null, null))).Id;
        });
        await Run(w.Admin, async s =>
        {
            await Svc<PricingRuleService>(s).CreateRuleAsync(new CreatePricingRuleRequest(w.ProjectId, "B" + Guid.NewGuid().ToString("N")[..8], "Base rule", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
                null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, w.Today, null, null));
            return 0;
        });
        var sales = await UserAsync(RoleNames.SalesManager);
        await Run(w.Admin, async s => { await Svc<ProjectAccessAdminService>(s).AssignAsync(w.ProjectId, sales); return 0; });
        var c = await Run(sales, async s => (await Svc<CustomerService>(s).CreateAsync(new CreateCustomerRequest("Customer " + Guid.NewGuid().ToString("N")[..6], IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", "c@example.com", "House 1, Murree", null,
            [new AddressDto(AddressType.Home, "House 1", "Murree", "Pakistan", true)], [new ContactDto("Brother", "Brother", "0301-7654321", null)], null))).Id);
        var b = await Run(sales, s => Svc<BookingService>(s).CreateAsync(new CreateBookingRequest(w.ProjectId, null, PaymentMode.Installment, [new BookingPropertyInput(propertyId, 0m, null)],
            [new OwnerInput(c, 100m)], null, 500_000m, false, null, null, true, null)));
        b = await Run(w.Director, s => Svc<BookingService>(s).ApproveAsync(b.Id, new ApproveBookingRequest("ok", b.RowVersion)));
        var number = await Run(w.Admin, async s => await Svc<AppDbContext>(s).CustomerAgreements.Where(a => a.Id == b.AgreementId!.Value).Select(a => a.AgreementNumber).FirstAsync());
        return new Deal(b.AgreementId!.Value, c, number);
    }

    private async Task<GovernmentFeeTypeDto> FeeTypeAsync(string code) => (await Run(w0, s => Svc<GovernmentFeeService>(s).ListTypesAsync())).Single(t => t.Code == code);

    private async Task<GovernmentFeeDetailDto> AssessAsync(World w, Deal d, string typeCode = "STAMP_DUTY", decimal? amount = 100_000m, decimal? borne = null, decimal? basis = null, Guid? actor = null, string? key = null)
    {
        var t = await FeeTypeAsync(typeCode);
        return await Run(actor ?? w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, t.Id, basis, amount, borne, null, "Challan 1", null, key ?? Guid.NewGuid().ToString("N"))));
    }

    [MySqlFact]
    public async Task GovernmentFees_KeepTheFivePositionsSeparate_AndNeverTouchTheCustomerLedger()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var ledgerRowsBefore = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AgreementLedgerEntries.CountAsync(e => e.AgreementId == d.AgreementId));
        // fee 100,000: customer owes 60,000, company bears 40,000
        var a = await AssessAsync(w, d, amount: 100_000m, borne: 40_000m);
        Assert.StartsWith("GFA-", a.AssessmentNumber);
        Assert.Equal(60_000m, a.CustomerLiability);
        Assert.Equal(40_000m, a.CompanyBorne);
        Assert.Equal(100_000m, a.Amount);
        Assert.Equal(d.CustomerId, a.CustomerId);
        Assert.Single(await OutboxAsync(AccountingEventType.GovFeeAssessed, "GovernmentFeeAssessment", a.Id));

        var m = await MethodAsync();
        // customer collection (liability only): 25,000 now
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(25_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(25_000m, a.Collected);
        Assert.Equal(35_000m, a.CustomerOutstanding);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(35_000.01m, m, w.Today, null, null, Guid.NewGuid().ToString("N")))));
        // actual payments to government: up to the full fee (company may advance), never more
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(70_000m, m, w.Today, "CH-9", null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(70_000m, a.PaidToGovernment);
        Assert.Equal(30_000m, a.GovernmentPayable);
        Assert.Equal(25_000m, a.Collected); // collection and payment are independent positions
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(30_000.01m, m, w.Today, null, null, Guid.NewGuid().ToString("N")))));
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(30_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(0m, a.GovernmentPayable);
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(35_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(0m, a.CustomerOutstanding);
        foreach (var c in a.Collections) Assert.Single(await OutboxAsync(AccountingEventType.GovFeeCollected, "GovernmentFeeCollection", c.Id));
        foreach (var p in a.Payments) Assert.Single(await OutboxAsync(AccountingEventType.GovFeePaid, "GovernmentFeePayment", p.Id));

        // the Phase 6 agreement ledger is untouched: government fee obligations are separate
        var ledgerRowsAfter = await Run(w.Admin, async s => await Svc<AppDbContext>(s).AgreementLedgerEntries.CountAsync(e => e.AgreementId == d.AgreementId));
        Assert.Equal(ledgerRowsBefore, ledgerRowsAfter);

        // summary: five positions
        var sum = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).SummaryAsync(w.ProjectId));
        Assert.Equal(100_000m, sum.TotalAssessed);
        Assert.Equal(60_000m, sum.CustomerLiability);
        Assert.Equal(60_000m, sum.CustomerCollected);
        Assert.Equal(40_000m, sum.CompanyBorne);
        Assert.Equal(100_000m, sum.PaidToGovernment);
        Assert.Equal(0m, sum.GovernmentPayable);
    }

    [MySqlFact]
    public async Task FeeAmount_IsCalculatedFromTheConfiguredType_AndDefaultBearerApplies()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var code1 = "PCT" + Interlocked.Increment(ref counter) + Guid.NewGuid().ToString("N")[..4];
        var code2 = "FIX" + Interlocked.Increment(ref counter) + Guid.NewGuid().ToString("N")[..4];
        var pct = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CreateTypeAsync(new SaveGovernmentFeeTypeRequest(w.ProjectId, code1.ToUpper(), "Stamp 1.5%", GovernmentFeeKind.StampDuty, FeeCalculationMethod.PercentOfBasis, null, 1.5m, FeeBearer.Customer, null)));
        var fix = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CreateTypeAsync(new SaveGovernmentFeeTypeRequest(w.ProjectId, code2.ToUpper(), "NOC fixed", GovernmentFeeKind.NocFee, FeeCalculationMethod.Fixed, 7_500m, null, FeeBearer.Company, null)));
        var a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, pct.Id, 5_000_000m, null, null, null, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(75_000m, a.Amount); Assert.Equal(75_000m, a.CustomerLiability); Assert.Equal(0m, a.CompanyBorne);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, pct.Id, null, null, null, null, null, null, Guid.NewGuid().ToString("N")))));
        var b = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, fix.Id, null, 1m, null, null, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(7_500m, b.Amount); Assert.Equal(0m, b.CustomerLiability); Assert.Equal(7_500m, b.CompanyBorne); // company-borne by default; the supplied amount is ignored for fixed fees
        await Assert.ThrowsAsync<RequestValidationException>(() => AssessAsync(w, d, amount: 100m, borne: 101m));
        await Assert.ThrowsAsync<RequestValidationException>(() => AssessAsync(w, d, amount: null));
        // inactive and foreign-project types are refused
        await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).UpdateTypeAsync(fix.Id, new SaveGovernmentFeeTypeRequest(w.ProjectId, fix.Code, fix.Name, fix.Kind, FeeCalculationMethod.Fixed, 7_500m, null, FeeBearer.Company, null, false, fix.RowVersion)));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, fix.Id, null, null, null, null, null, null, Guid.NewGuid().ToString("N")))));
    }

    [MySqlFact]
    public async Task GovernmentFee_Idempotency_Reversals_Cancellation_AndFeeTypeScope()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var key = Guid.NewGuid().ToString("N");
        var a = await AssessAsync(w, d, key: key);
        Assert.Equal(a.Id, (await AssessAsync(w, d, key: key)).Id);
        await Assert.ThrowsAsync<RequestValidationException>(() => AssessAsync(w, d, "REGISTRATION", key: key));
        var m = await MethodAsync();
        var ck = Guid.NewGuid().ToString("N");
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(1_000m, m, w.Today, null, null, ck)));
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(1_000m, m, w.Today, null, null, ck)));
        Assert.Single(a.Collections);
        Assert.Equal(1_000m, a.Collected);
        // an assessment with collections cannot be cancelled until they are reversed
        await Fails(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CancelAsync(a.Id, new CancelGovernmentFeeAssessmentRequest("Wrong customer", null))));
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).ReverseCollectionAsync(a.Collections[0].Id, new ReverseFeeEntryRequest(""))));
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).ReverseCollectionAsync(a.Collections[0].Id, new ReverseFeeEntryRequest("Cheque bounced")));
        Assert.Equal(0m, a.Collected);
        Assert.Equal(FeeEntryStatus.Reversed, a.Collections[0].Status);
        Assert.Single(await OutboxAsync(AccountingEventType.GovFeeCollectionReversed, "GovernmentFeeCollection", a.Collections[0].Id));
        await Fails(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).ReverseCollectionAsync(a.Collections[0].Id, new ReverseFeeEntryRequest("Reverse twice"))));
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(10_000m, m, w.Today, "CH-1", null, Guid.NewGuid().ToString("N"))));
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).ReversePaymentAsync(a.Payments[0].Id, new ReverseFeeEntryRequest("Challan rejected by bank")));
        Assert.Equal(0m, a.PaidToGovernment);
        Assert.Single(await OutboxAsync(AccountingEventType.GovFeePaymentReversed, "GovernmentFeePayment", a.Payments[0].Id));
        // cancellable now; cancelled assessments take no money
        a = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CancelAsync(a.Id, new CancelGovernmentFeeAssessmentRequest("Assessed to the wrong agreement", null)));
        Assert.Equal(GovernmentFeeAssessmentStatus.Cancelled, a.Status);
        Assert.Single(await OutboxAsync(AccountingEventType.GovFeeAssessmentCancelled, "GovernmentFeeAssessment", a.Id));
        await Fails(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(1m, m, w.Today, null, null, Guid.NewGuid().ToString("N")))));
        await Fails(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(1m, m, w.Today, null, null, Guid.NewGuid().ToString("N")))));

        // fee types: company-wide types need access to all projects; project types need that project
        var code = "SCOPE" + Interlocked.Increment(ref counter) + Guid.NewGuid().ToString("N")[..4];
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CreateTypeAsync(new SaveGovernmentFeeTypeRequest(null, code.ToUpper(), "Company-wide", GovernmentFeeKind.Other, FeeCalculationMethod.Manual, null, null, FeeBearer.Customer, null))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).CreateTypeAsync(new SaveGovernmentFeeTypeRequest(w.OtherProjectId, code.ToUpper(), "Other project", GovernmentFeeKind.Other, FeeCalculationMethod.Manual, null, null, FeeBearer.Customer, null))));
        var wide = await Run(w.Director, s => Svc<GovernmentFeeService>(s).CreateTypeAsync(new SaveGovernmentFeeTypeRequest(null, code.ToUpper(), "Company-wide", GovernmentFeeKind.Other, FeeCalculationMethod.Manual, null, null, FeeBearer.Customer, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).UpdateTypeAsync(wide.Id, new SaveGovernmentFeeTypeRequest(null, wide.Code, "Renamed", wide.Kind, wide.Method, null, null, wide.DefaultBearer, null, true, wide.RowVersion))));
        // standard fee types cannot be deactivated or re-coded
        var stamp = await FeeTypeAsync("STAMP_DUTY");
        await Fails(() => Run(w.Director, s => Svc<GovernmentFeeService>(s).UpdateTypeAsync(stamp.Id, new SaveGovernmentFeeTypeRequest(null, stamp.Code, stamp.Name, stamp.Kind, stamp.Method, null, null, stamp.DefaultBearer, null, false, stamp.RowVersion))));
        // an unassigned accountant can neither see nor change the assessment
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<GovernmentFeeService>(s).GetAsync(a.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(1m, m, w.Today, null, null, "k-x"))));
        Assert.DoesNotContain((await Run(w.Outsider, s => Svc<GovernmentFeeService>(s).ListAsync(new GovernmentFeeListRequest { PageSize = 100 }))).Items, x => x.Id == a.Id);
        // the sales manager can view but not assess/pay government fees without permission
        var support = await UserAsync(RoleNames.CustomerSupport);
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(support, s => Svc<GovernmentFeeService>(s).ListAsync(new GovernmentFeeListRequest())));
    }

    [MySqlFact]
    public async Task GovernmentFeeDocuments_AreStoredAndAudited()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var a = await AssessAsync(w, d);
        var doc = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).UploadDocumentAsync(a.Id, GovernmentFeeDocumentType.GovernmentReceipt, "Receipt", Pdf("receipt.pdf")));
        var file = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).DownloadDocumentAsync(a.Id, doc.Id));
        using (var ms = new MemoryStream()) { await file.Content.CopyToAsync(ms); Assert.Equal(5, ms.Length); }
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<GovernmentFeeService>(s).DownloadDocumentAsync(a.Id, doc.Id)));
        await Run(w.Accountant, async s => { await Svc<GovernmentFeeService>(s).DeleteDocumentAsync(a.Id, doc.Id, "Wrong document"); return 0; });
        Assert.Empty(await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).ListDocumentsAsync(a.Id)));
    }

    [MySqlFact]
    public async Task ConcurrentCollectionsAndPayments_NeverExceedTheirCaps()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var a = await AssessAsync(w, d, amount: 10_000m, borne: 0m);
        var m = await MethodAsync();
        As(w.Accountant);
        var collect = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<GovernmentFeeService>(s).CollectAsync(a.Id, new FeeCollectionRequest(10_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, collect.Count(x => x));
        var pay = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var s = fx.Scope();
            try { await Svc<GovernmentFeeService>(s).PayAsync(a.Id, new GovernmentPaymentRequest(10_000m, m, w.Today, null, null, Guid.NewGuid().ToString("N"))); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Equal(1, pay.Count(x => x));
        var after = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).GetAsync(a.Id));
        Assert.Equal(10_000m, after.Collected);
        Assert.Equal(10_000m, after.PaidToGovernment);
        Assert.Single(after.Collections);
        Assert.Single(after.Payments);
    }

    [MySqlFact]
    public async Task Fees_CanOnlyBeAssessedForActiveAgreements_InAccessibleProjects()
    {
        var w = await WorldAsync();
        var d = await DealAsync(w);
        var t = await FeeTypeAsync("TAX");
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Outsider, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, t.Id, null, 10m, null, null, null, null, "k-o"))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Run(w.Manager, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, t.Id, null, 10m, null, null, null, null, "k-m"))));
        var lookup = await Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AgreementLookupAsync(d.AgreementNumber));
        Assert.Contains(lookup, x => x.AgreementId == d.AgreementId);
        Assert.DoesNotContain(await Run(w.Outsider, s => Svc<GovernmentFeeService>(s).AgreementLookupAsync(d.AgreementNumber)), x => x.AgreementId == d.AgreementId);
        await Assert.ThrowsAsync<RequestValidationException>(() => Run(w.Accountant, s => Svc<GovernmentFeeService>(s).AssessAsync(new CreateGovernmentFeeAssessmentRequest(d.AgreementId, t.Id, null, 10m, null, w.Today.AddDays(-1), null, null, Guid.NewGuid().ToString("N")))));
    }

    // ===================================================================================== database constraints
    [MySqlFact]
    public async Task Database_Rejects_OverpaidExpenses_NegativeAmounts_AndInconsistentFeeSplits()
    {
        var w = await WorldAsync();
        var e = await NewExpenseAsync(w, 100m, approved: true);
        var d = await DealAsync(w);
        var a = await AssessAsync(w, d, amount: 100m, borne: 0m);
        As(w.Admin);
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var x = await db.Expenses.FirstAsync(q => q.Id == e.Id);
            x.AmountPaid = 101m;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var x = await db.Expenses.FirstAsync(q => q.Id == e.Id);
            x.Amount = 0m;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var x = await db.GovernmentFeeAssessments.FirstAsync(q => q.Id == a.Id);
            x.CompanyBorne = 50m; // liability 100 + borne 50 != amount 100
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var x = await db.GovernmentFeeAssessments.FirstAsync(q => q.Id == a.Id);
            x.PaidAmount = 101m;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        // duplicate payment idempotency keys are rejected by the unique index
        var cat = await CategoryAsync();
        var m = await MethodAsync();
        await PayAsync(w, e.Id, 10m, "dup-key-" + Guid.NewGuid().ToString("N")[..8]);
        using (var s = fx.Scope())
        {
            var db = Svc<AppDbContext>(s);
            var p = await db.VendorPayments.FirstAsync(q => q.ExpenseId == e.Id);
            db.VendorPayments.Add(new VendorPayment { PaymentNumber = "X-" + Guid.NewGuid().ToString("N")[..8], IdempotencyKey = p.IdempotencyKey, RequestHash = "h", ExpenseId = e.Id, ProjectId = p.ProjectId,
                PaymentMethodId = m, Amount = 1m, PaymentDate = w.Today });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        _ = cat;
    }
}
