using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 6 payment, allocation, ledger and refund screens (rendered-HTML tests; shares the setup helpers of the recovery tests).</summary>
public partial class Phase06RecoveryWebTests
{
    private static long IdFrom(HttpResponseMessage r, string pattern) => long.Parse(Regex.Match(r.Headers.Location!.ToString(), pattern).Groups[1].Value);

    private async Task<long> MethodIdAsync(string code) => await DbAsync(async db => await db.PaymentMethods.Where(m => m.Code == code).Select(m => m.Id).FirstAsync());

    private static async Task<HttpResponseMessage> SubmitPaymentAsync(HttpClient c, long agreementId, long methodId, string amount, string mode = "OldestDueFirst", string? reference = null, params (string, string)[] extra)
    {
        var form = await c.GetStringAsync($"/Payments/Create?agreementId={agreementId}");
        var fields = new List<(string, string)>
        {
            ("AgreementId", agreementId.ToString()), ("IdempotencyKey", Regex.Match(form, "name=\"IdempotencyKey\"[^>]*value=\"([^\"]+)\"").Groups[1].Value), ("PaymentMethodId", methodId.ToString()),
            ("Source", "Office"), ("Amount", amount), ("PaymentDate", Today), ("AllocationMode", mode), ("Reference", reference ?? ""),
        };
        fields.AddRange(extra);
        return await PostAsync(c, $"/Payments/Create?agreementId={agreementId}", "/Payments/Create", fields.ToArray());
    }

    [MySqlFact]
    public async Task PaymentCollection_Approval_Receipt_AndLedgers_WorkThroughTheUi()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (_, agreementId, number) = await OverdueAgreementAsync(maker.Client, checker.Client);
        var c = maker.Client;

        // agreement picker, then the collection form with balances and open lines
        Dbg.Has(await GetOkAsync(c, $"/Payments/Create?term={number}"), number);
        var form = await GetOkAsync(c, $"/Payments/Create?agreementId={agreementId}");
        foreach (var t in new[] { "Collect payment", "Due to date (unpaid)", "Overdue", "How to apply the money", "Oldest due", "Submit payment" }) Dbg.Has(form, t);

        // validation: zero amount, bank transfer without reference
        var cash = await MethodIdAsync("CASH");
        var bad = await SubmitPaymentAsync(c, agreementId, cash, "0");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Enter an amount greater than zero");
        var noRef = await SubmitPaymentAsync(c, agreementId, await MethodIdAsync("BANK_TRANSFER"), "1000");
        Dbg.Has(await BodyAsync(noRef), "A reference is required");

        // submit (maker) -> the maker cannot approve; checker approves -> posted with receipt
        var created = await SubmitPaymentAsync(c, agreementId, cash, "600000");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var paymentId = IdFrom(created, @"/Payments/Details/(\d+)");
        var details = await GetOkAsync(c, $"/Payments/Details/{paymentId}");
        Dbg.Has(details, "Pending approval"); Dbg.Has(details, "another user must approve");
        Assert.DoesNotContain("Approve and post", details);
        Dbg.Has(await GetOkAsync(checker.Client, "/Payments?status=PendingApproval"), "Payment approval");
        var approveForm = await GetOkAsync(checker.Client, $"/Payments/Approve/{paymentId}");
        Dbg.Has(approveForm, "Change the allocation");
        var approved = await PostAsync(checker.Client, $"/Payments/Approve/{paymentId}", $"/Payments/Approve/{paymentId}", ("RowVersion", Rv(approveForm)), ("OverrideAllocation", "false"), ("Comments", "ok"));
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        details = await GetOkAsync(c, $"/Payments/Details/{paymentId}");
        Dbg.Has(details, "Posted"); Dbg.Has(details, "RCP-"); Dbg.Has(details, "Applied to installments"); Dbg.Has(details, "PaymentPosted: Pending");

        // ledgers
        var ledger = await GetOkAsync(c, $"/Ledgers/Agreement/{agreementId}");
        foreach (var t in new[] { "Agreement ledger", "Reconciled", "Installment due", "Payment received", "Contract total", "Penalties accrued", "Scheduled, not yet due" }) Dbg.Has(ledger, t);
        var customerId = await DbAsync(async db => await db.Payments.Where(p => p.Id == paymentId).Select(p => p.CustomerId).FirstAsync());
        var customerLedger = await GetOkAsync(c, $"/Ledgers/Customer/{customerId}");
        Dbg.Has(customerLedger, "Customer ledger"); Dbg.Has(customerLedger, number); Dbg.Has(customerLedger, "Reconciled");
        var propertyId = await DbAsync(async db => await (from a in db.CustomerAgreements join bp in db.BookingProperties on a.BookingId equals bp.BookingId where a.Id == agreementId select bp.PropertyId).FirstAsync());
        Dbg.Has(await GetOkAsync(c, $"/Ledgers/Property/{propertyId}"), "Property statement");

        // lists
        var list = await GetOkAsync(c, $"/Payments?search={number}");
        Dbg.Has(list, "PAY-"); Dbg.Has(list, "Posted");

        // reversal through the UI
        details = await GetOkAsync(checker.Client, $"/Payments/Details/{paymentId}");
        var reversed = await PostAsync(checker.Client, $"/Payments/Details/{paymentId}", $"/Payments/Reverse/{paymentId}", ("reason", "Recorded in error"), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, reversed.StatusCode);
        details = await GetOkAsync(c, $"/Payments/Details/{paymentId}");
        Dbg.Has(details, "Reversed"); Dbg.Has(details, "Recorded in error");
        Dbg.Has(await GetOkAsync(c, $"/Ledgers/Agreement/{agreementId}"), "Payment reversed");
    }

    [MySqlFact]
    public async Task Verification_Allocation_AndRefund_WorkThroughTheUi()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var (_, agreementId, _) = await OverdueAgreementAsync(maker.Client, checker.Client, backdate: false);
        var c = maker.Client;

        // bank transfer: verification first
        var created = await SubmitPaymentAsync(c, agreementId, await MethodIdAsync("BANK_TRANSFER"), "300000", "Unallocated", "TRX-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = IdFrom(created, @"/Payments/Details/(\d+)");
        Dbg.Has(await GetOkAsync(checker.Client, "/Payments?status=PendingVerification"), "Payment verification");
        var details = await GetOkAsync(checker.Client, $"/Payments/Details/{id}");
        Dbg.Has(details, "Not yet verified");
        var verified = await PostAsync(checker.Client, $"/Payments/Details/{id}", $"/Payments/Verify/{id}", ("verified", "true"), ("notes", "Seen in statement"), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        var approveForm = await GetOkAsync(checker.Client, $"/Payments/Approve/{id}");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker.Client, $"/Payments/Approve/{id}", $"/Payments/Approve/{id}", ("RowVersion", Rv(approveForm)), ("OverrideAllocation", "false"))).StatusCode);
        details = await GetOkAsync(c, $"/Payments/Details/{id}");
        Dbg.Has(details, "Allocate balance");

        // allocate part of the unallocated balance manually, keep the rest as advance
        var allocForm = await GetOkAsync(c, $"/Payments/Allocate/{id}");
        Dbg.Has(allocForm, "Save allocation");
        var itemId = Regex.Match(allocForm, "name=\"Lines\\[0\\]\\.ItemId\" value=\"(\\d+)\"").Groups[1].Value;
        var allocated = await PostAsync(c, $"/Payments/Allocate/{id}", $"/Payments/Allocate/{id}", ("RowVersion", Rv(allocForm)), ("Mode", "Manual"), ("RemainderToAdvance", "true"),
            ("Lines[0].ItemId", itemId), ("Lines[0].Amount", "200000"));
        Assert.Equal(HttpStatusCode.Redirect, allocated.StatusCode);
        details = await GetOkAsync(c, $"/Payments/Details/{id}");
        Dbg.Has(details, "Held as advance"); Dbg.Has(details, "100,000.00");

        // refund the advance: request (maker), approve and pay (checker)
        var refundForm = await GetOkAsync(c, $"/Refunds/Create?agreementId={agreementId}");
        Dbg.Has(refundForm, "Available now");
        var tooMuch = await PostAsync(c, $"/Refunds/Create?agreementId={agreementId}", "/Refunds/Create", ("AgreementId", agreementId.ToString()), ("Amount", "150000"), ("Reason", "Customer request"),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Dbg.Has(await BodyAsync(tooMuch), "exceeds the money available");
        var requested = await PostAsync(c, $"/Refunds/Create?agreementId={agreementId}", "/Refunds/Create", ("AgreementId", agreementId.ToString()), ("Amount", "60000"), ("Reason", "Customer request"),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, requested.StatusCode);
        var refundId = IdFrom(requested, @"/Refunds/Details/(\d+)");
        var rd = await GetOkAsync(c, $"/Refunds/Details/{refundId}");
        Dbg.Has(rd, "another user must approve");
        rd = await GetOkAsync(checker.Client, $"/Refunds/Details/{refundId}");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker.Client, $"/Refunds/Details/{refundId}", $"/Refunds/Approve/{refundId}", ("rowVersion", Rv(rd, "rowVersion")))).StatusCode);
        rd = await GetOkAsync(checker.Client, $"/Refunds/Details/{refundId}");
        Dbg.Has(rd, "Record payout");
        var cash = await MethodIdAsync("CASH");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(checker.Client, $"/Refunds/Details/{refundId}", $"/Refunds/Pay/{refundId}", ("paymentMethodId", cash.ToString()), ("paidOn", Today),
            ("rowVersion", Rv(rd, "rowVersion")))).StatusCode);
        rd = await GetOkAsync(c, $"/Refunds/Details/{refundId}");
        Dbg.Has(rd, "Paid"); Dbg.Has(rd, "Paid from");
        Dbg.Has(await GetOkAsync(c, "/Refunds?status=Paid"), "RFD-");
        Dbg.Has(await GetOkAsync(c, $"/Ledgers/Agreement/{agreementId}"), "Refund paid");
    }

    [MySqlFact]
    public async Task PaymentScreens_RequirePermissions_AndAntiForgery()
    {
        var plain = await f.LoggedInAsync(administrator: false);
        foreach (var url in new[] { "/Payments", "/Payments/Create", "/Refunds", "/Refunds/Create", "/Ledgers/Agreement/1", "/Ledgers/Customer/1" })
        {
            var r = await plain.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        var cashier = await f.LoggedInAsRoleAsync(RoleNames.Cashier);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/Payments/Create")).StatusCode);
        var r2 = await cashier.GetAsync("/Payments/Approve/1");
        Assert.Contains("AccessDenied", r2.Headers.Location!.ToString());
        var admin = await NewAdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsync("/Payments/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["AgreementId"] = "1" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsync("/Refunds/Pay/1", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }
}
