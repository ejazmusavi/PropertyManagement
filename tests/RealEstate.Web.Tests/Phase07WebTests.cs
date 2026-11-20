using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

/// <summary>Phase 7 expense, vendor and government fee screens (rendered-HTML tests; shares the setup helpers of the recovery tests).</summary>
public partial class Phase06RecoveryWebTests
{
    private static async Task<string> FollowAsync(HttpClient c, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return await GetOkAsync(c, r.Headers.Location!.ToString());
    }

    private async Task<long> CategoryIdAsync(string code) => await DbAsync(async db => await db.ExpenseCategories.Where(c => c.Code == code).Select(c => c.Id).FirstAsync());

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string formPage, string action, string fileName = "invoice.pdf", params (string, string)[] extra)
    {
        var content = new MultipartFormDataContent { { new StringContent(await WebFactory.TokenAsync(c, formPage)), "__RequestVerificationToken" } };
        foreach (var (k, v) in extra) content.Add(new StringContent(v), k);
        var file = new ByteArrayContent([1, 2, 3, 4, 5]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", fileName);
        return await c.PostAsync(action, content);
    }

    private async Task<(long ProjectId, string Code)> NewProjectAsync(HttpClient c)
    {
        var code = "E" + Guid.NewGuid().ToString("N")[..9];
        var r = await PostAsync(c, "/Projects/Create", "/Projects/Create", ("ProjectCode", code), ("ProjectName", "Expenses UI " + code), ("City", "Murree"), ("InitialArea", "50"), ("MeasurementUnit", "Kanal"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return (IdFrom(r, @"/Projects/Details/(\d+)"), code);
    }

    [MySqlFact]
    public async Task ExpenseScreens_Dashboard_Registration_Approval_Payment_AndReversal_WorkThroughTheUi()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var c = maker.Client;
        var (projectId, projectCode) = await NewProjectAsync(c);
        var rent = await CategoryIdAsync("OFFICE_RENT");

        // dashboard and categories
        var dash = await GetOkAsync(c, $"/Expenses/Dashboard?projectId={projectId}");
        foreach (var t in new[] { "Expense dashboard", "Approved expenses", "Payables outstanding", "Pending approval", "Government fees" }) Dbg.Has(dash, t);
        var cats = await GetOkAsync(c, "/ExpenseCategories");
        foreach (var t in new[] { "Salaries", "Office rent", "Utilities", "Marketing", "Construction", "Land acquisition", "Legal expenses", "Transportation", "Maintenance", "Agent commission", "Miscellaneous" }) Dbg.Has(cats, t);

        // registration form + validation
        var form = await GetOkAsync(c, "/Expenses/Create");
        Dbg.Has(form, "Register expense"); Dbg.Has(form, projectCode); Dbg.Has(form, "Office rent");
        var bad = await PostAsync(c, "/Expenses/Create", "/Expenses/Create", ("ProjectId", projectId.ToString()), ("CategoryId", rent.ToString()), ("Description", "Rent"), ("Amount", "0"), ("ExpenseDate", Today), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Enter an amount greater than zero");
        var created = await PostAsync(c, "/Expenses/Create", "/Expenses/Create", ("ProjectId", projectId.ToString()), ("CategoryId", rent.ToString()), ("Description", "Office rent November"), ("Amount", "100000"),
            ("ExpenseDate", Today), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = IdFrom(created, @"/Expenses/Details/(\d+)");
        var details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        foreach (var t in new[] { "EXP-", "Draft", "Supporting documents", "Submit for approval", "Office rent November" }) Dbg.Has(details, t);

        // submitting without a document is refused; edit works; upload then submit
        var refused = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/Submit/{id}", ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal("Draft", await DbAsync(async db => (await db.Expenses.Where(e => e.Id == id).Select(e => e.Status).FirstAsync()).ToString()));
        var edit = await GetOkAsync(c, $"/Expenses/Edit/{id}");
        var edited = await PostAsync(c, $"/Expenses/Edit/{id}", $"/Expenses/Edit/{id}", ("ProjectId", projectId.ToString()), ("CategoryId", rent.ToString()), ("Description", "Office rent November (revised)"), ("Amount", "100000"), ("ExpenseDate", Today), ("RowVersion", Rv(edit)));
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        var up = await UploadAsync(c, $"/Expenses/Details/{id}", $"/Expenses/UploadDocument/{id}", "rent-invoice.pdf", ("type", "Invoice"), ("title", "Rent invoice"));
        Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Rent invoice"); Dbg.Has(details, "rent-invoice.pdf"); Dbg.Has(details, "revised");
        var docId = await DbAsync(async db => await db.ExpenseAttachments.Where(d => d.ExpenseId == id).Select(d => d.Id).FirstAsync());
        var dl = await c.GetAsync($"/Expenses/DownloadDocument/{id}?documentId={docId}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(5, (await dl.Content.ReadAsByteArrayAsync()).Length);
        var noFile = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/UploadDocument/{id}", ("type", "Invoice"));
        Assert.Equal(HttpStatusCode.Redirect, noFile.StatusCode);
        var submitted = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/Submit/{id}", ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Pending approval"); Dbg.Has(details, "another user must approve it");
        Assert.DoesNotContain("data-bs-target=\"#approveModal\"", details);

        // the maker cannot approve (service-level maker-checker); the checker sees it in the queue and rejects, then approves after resubmission
        var selfApprove = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/Approve/{id}", ("comments", "self"), ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, selfApprove.StatusCode);
        Assert.Equal("PendingApproval", await DbAsync(async db => (await db.Expenses.Where(e => e.Id == id).Select(e => e.Status).FirstAsync()).ToString()));
        var queue = await GetOkAsync(checker.Client, "/Expenses/Approvals");
        Dbg.Has(queue, "Expense approvals"); Dbg.Has(queue, "EXP-");
        var cd = await GetOkAsync(checker.Client, $"/Expenses/Details/{id}");
        Dbg.Has(cd, "Approve"); Dbg.Has(cd, "Reject");
        var noComment = await PostAsync(checker.Client, $"/Expenses/Details/{id}", $"/Expenses/Reject/{id}", ("comments", ""), ("rowVersion", Rv(cd, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, noComment.StatusCode);
        Assert.Equal("PendingApproval", await DbAsync(async db => (await db.Expenses.Where(e => e.Id == id).Select(e => e.Status).FirstAsync()).ToString()));
        var rejected = await PostAsync(checker.Client, $"/Expenses/Details/{id}", $"/Expenses/Reject/{id}", ("comments", "Please attach the signed contract"), ("rowVersion", Rv(cd, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Rejected"); Dbg.Has(details, "Please attach the signed contract");
        var resub = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/Submit/{id}", ("rowVersion", Rv(details, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, resub.StatusCode);
        cd = await GetOkAsync(checker.Client, $"/Expenses/Details/{id}");
        var approved = await PostAsync(checker.Client, $"/Expenses/Details/{id}", $"/Expenses/Approve/{id}", ("comments", "ok"), ("rowVersion", Rv(cd, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Approved"); Dbg.Has(details, "Unpaid"); Dbg.Has(details, "Record payment"); Dbg.Has(details, "Queued for posting");
        Assert.Equal(1, await DbAsync(async db => await db.AccountingPostingRequests.CountAsync(a => a.EventType == AccountingEventType.ExpenseApproved && a.SourceId == id)));

        // payments: partial, then the vendor payment screens, then the rest; over-payment refused
        var cash = await MethodIdAsync("CASH");
        var pay1 = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/Pay/{id}", ("Amount", "40000"), ("PaymentMethodId", cash.ToString()), ("PaymentDate", Today), ("IdempotencyKey", Guid.NewGuid().ToString("N")), ("RowVersion", Rv(details)));
        Assert.Equal(HttpStatusCode.Redirect, pay1.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Partially paid"); Dbg.Has(details, "VPY-");
        var payForm = await GetOkAsync(c, $"/VendorPayments/Create?expenseId={id}");
        Dbg.Has(payForm, "Record payment for EXP-"); Dbg.Has(payForm, "60,000.00");
        var over = await PostAsync(c, $"/VendorPayments/Create?expenseId={id}", "/VendorPayments/Create", ("ExpenseId", id.ToString()), ("Amount", "60000.01"), ("PaymentMethodId", cash.ToString()), ("PaymentDate", Today),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        Dbg.Has(await BodyAsync(over), "exceeds the outstanding balance");
        var noRef = await PostAsync(c, $"/VendorPayments/Create?expenseId={id}", "/VendorPayments/Create", ("ExpenseId", id.ToString()), ("Amount", "100"), ("PaymentMethodId", (await MethodIdAsync("BANK_TRANSFER")).ToString()), ("PaymentDate", Today),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Dbg.Has(await BodyAsync(noRef), "A reference is required");
        Dbg.Has(await GetOkAsync(c, "/VendorPayments/Create"), "Record vendor payment");
        var rest = await PostAsync(c, $"/VendorPayments/Create?expenseId={id}", "/VendorPayments/Create", ("ExpenseId", id.ToString()), ("Amount", "60000"), ("PaymentMethodId", cash.ToString()), ("PaymentDate", Today),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, rest.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Paid"); Dbg.Has(details, "Reverse");
        var list = await GetOkAsync(c, $"/VendorPayments?projectId={projectId}");
        Dbg.Has(list, "VPY-"); Dbg.Has(list, "Posted");
        Dbg.Has(await GetOkAsync(c, $"/Expenses?projectId={projectId}&paymentStatus=Paid"), "EXP-");
        Dbg.Has(await GetOkAsync(c, $"/Expenses?projectId={projectId}&payableOnly=true"), "No expenses found");

        // reversal restores the payable
        var payId = await DbAsync(async db => await db.VendorPayments.Where(p => p.ExpenseId == id && p.Amount == 60000m).Select(p => p.Id).FirstAsync());
        var rev = await PostAsync(c, $"/Expenses/Details/{id}", $"/Expenses/ReversePayment/{id}", ("paymentId", payId.ToString()), ("reason", "Paid twice by mistake"));
        Assert.Equal(HttpStatusCode.Redirect, rev.StatusCode);
        details = await GetOkAsync(c, $"/Expenses/Details/{id}");
        Dbg.Has(details, "Reversed"); Dbg.Has(details, "Paid twice by mistake"); Dbg.Has(details, "Partially paid");
        Dbg.Has(await GetOkAsync(c, $"/Expenses?projectId={projectId}&payableOnly=true"), "EXP-");

        // dashboard now reflects it
        dash = await GetOkAsync(c, $"/Expenses/Dashboard?projectId={projectId}");
        Dbg.Has(dash, "100,000.00"); Dbg.Has(dash, "Office rent");
        Dbg.Has(await GetOkAsync(c, "/Expenses"), "EXP-");
        Dbg.Has(await GetOkAsync(c, $"/Expenses?search=revised&projectId={projectId}"), "EXP-");
    }

    [MySqlFact]
    public async Task VendorScreens_Vendors_Invoices_AndCategories_WorkThroughTheUi()
    {
        var c = (await NewAdminAsync()).Client;
        var (projectId, _) = await NewProjectAsync(c);
        var construction = await CategoryIdAsync("CONSTRUCTION");

        // vendor registration with validation
        Dbg.Has(await GetOkAsync(c, "/Vendors/Create"), "New vendor");
        var bad = await PostAsync(c, "/Vendors/Create", "/Vendors/Create", ("Name", ""), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "required");
        var name = "Acme Builders " + Guid.NewGuid().ToString("N")[..6];
        var created = await PostAsync(c, "/Vendors/Create", "/Vendors/Create", ("Name", name), ("ContactPerson", "Mr Ali"), ("Phone", "0300-1112223"), ("TaxNumber", "NTN-77"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var vendorId = IdFrom(created, @"/Vendors/Details/(\d+)");
        var dup = await PostAsync(c, "/Vendors/Create", "/Vendors/Create", ("Name", name), ("IsActive", "true"));
        Dbg.Has(await BodyAsync(dup), "already exists");
        var vd = await GetOkAsync(c, $"/Vendors/Details/{vendorId}");
        foreach (var t in new[] { name, "VEN-", "Mr Ali", "Outstanding payable", "Register invoice" }) Dbg.Has(vd, t);
        var edit = await GetOkAsync(c, $"/Vendors/Edit/{vendorId}");
        var edited = await PostAsync(c, $"/Vendors/Edit/{vendorId}", $"/Vendors/Edit/{vendorId}", ("Name", name), ("ContactPerson", "Mrs Ali"), ("IsActive", "true"), ("RowVersion", Rv(edit)));
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/Vendors?search={name}"), "Mrs Ali");

        // invoice registration: validation, success (creates the payable expense), duplicate refused
        var form = await GetOkAsync(c, $"/VendorInvoices/Create?vendorId={vendorId}");
        Dbg.Has(form, "Register vendor invoice"); Dbg.Has(form, name);
        var invalid = await PostAsync(c, "/VendorInvoices/Create", "/VendorInvoices/Create", ("ProjectId", projectId.ToString()), ("VendorId", vendorId.ToString()), ("InvoiceNumber", "INV-9"), ("CategoryId", construction.ToString()),
            ("InvoiceDate", Today), ("DueDate", DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd")), ("Amount", "25000"), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Dbg.Has(await BodyAsync(invalid), "due date cannot be before the invoice date");
        string[] fields = [Today, DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd")];
        (string, string)[] Inv(string number, string key) => [("ProjectId", projectId.ToString()), ("VendorId", vendorId.ToString()), ("InvoiceNumber", number), ("CategoryId", construction.ToString()),
            ("InvoiceDate", fields[0]), ("DueDate", fields[1]), ("Amount", "25000"), ("IdempotencyKey", key)];
        var ok = await PostAsync(c, "/VendorInvoices/Create", "/VendorInvoices/Create", Inv("INV-5001", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var expenseId = IdFrom(ok, @"/Expenses/Details/(\d+)");
        var ed = await GetOkAsync(c, $"/Expenses/Details/{expenseId}");
        Dbg.Has(ed, "INV-5001"); Dbg.Has(ed, "Vendor invoice"); Dbg.Has(ed, "set by the vendor invoice".Length > 0 ? "Draft" : "");
        var dupInv = await PostAsync(c, "/VendorInvoices/Create", "/VendorInvoices/Create", Inv("inv-5001", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, dupInv.StatusCode);
        Dbg.Has(await BodyAsync(dupInv), "already registered");
        var il = await GetOkAsync(c, $"/VendorInvoices?projectId={projectId}&vendorId={vendorId}");
        Dbg.Has(il, "INV-5001"); Dbg.Has(il, name); Dbg.Has(il, "25,000.00");
        var invId = await DbAsync(async db => await db.VendorInvoices.Where(i => i.ExpenseId == expenseId).Select(i => i.Id).FirstAsync());
        var redirect = await c.GetAsync($"/VendorInvoices/Details/{invId}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Contains($"/Expenses/Details/{expenseId}", redirect.Headers.Location!.ToString());
        Dbg.Has(await GetOkAsync(c, "/VendorInvoices?overdueOnly=true"), "Vendor invoices");

        // categories: add, duplicate, edit, standard categories are protected
        var code = "CUSTOM_" + Guid.NewGuid().ToString("N")[..5].ToUpper();
        var cat = await PostAsync(c, "/ExpenseCategories", "/ExpenseCategories/Save", ("Form.Code", code), ("Form.Name", "Custom " + code), ("Form.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, cat.StatusCode);
        Dbg.Has(await GetOkAsync(c, "/ExpenseCategories"), "Custom " + code);
        var dupCat = await PostAsync(c, "/ExpenseCategories", "/ExpenseCategories/Save", ("Form.Code", code), ("Form.Name", "Other name"), ("Form.IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, dupCat.StatusCode);
        Dbg.Has(await BodyAsync(dupCat), "already used");
        var catId = await CategoryIdAsync(code);
        Dbg.Has(await GetOkAsync(c, $"/ExpenseCategories?edit={catId}"), "Edit category");
        var rentId = await CategoryIdAsync("OFFICE_RENT");
        var sys = await PostAsync(c, "/ExpenseCategories", "/ExpenseCategories/Save", ("Form.Id", rentId.ToString()), ("Form.IsSystem", "true"), ("Form.Code", "OFFICE_RENT"), ("Form.Name", "Office rent"), ("Form.IsActive", "false"));
        Assert.Equal(HttpStatusCode.Redirect, sys.StatusCode); // the standard category is kept active
        Assert.True(await DbAsync(async db => await db.ExpenseCategories.Where(x => x.Id == rentId).Select(x => x.IsActive).FirstAsync()));
    }

    [MySqlFact]
    public async Task GovernmentFeeScreens_Configuration_Assessment_Collection_Payment_AndDocuments_WorkThroughTheUi()
    {
        var maker = await NewAdminAsync(); var checker = await NewAdminAsync();
        var c = maker.Client;
        var (_, agreementId, number) = await OverdueAgreementAsync(c, checker.Client, backdate: false);

        // configuration: standard types listed, a percentage type is created, validation shown
        var types = await GetOkAsync(c, "/GovernmentFeeTypes");
        foreach (var t in new[] { "Stamp duty", "Registration fee", "Transfer fee", "Applicable tax", "NOC fee", "Development charges", "Other government fee", "Standard" }) Dbg.Has(types, t);
        Dbg.Has(await GetOkAsync(c, "/GovernmentFeeTypes/Create"), "New government fee type");
        var code = "PCT" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var invalid = await PostAsync(c, "/GovernmentFeeTypes/Create", "/GovernmentFeeTypes/Create", ("Code", code), ("Name", "Stamp 2%"), ("Kind", "1"), ("Method", "3"), ("DefaultBearer", "1"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Dbg.Has(await BodyAsync(invalid), "Enter a rate");
        var typeOk = await PostAsync(c, "/GovernmentFeeTypes/Create", "/GovernmentFeeTypes/Create", ("Code", code), ("Name", "Stamp 2%"), ("Kind", "1"), ("Method", "3"), ("Rate", "2"), ("DefaultBearer", "1"), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, typeOk.StatusCode);
        var typeId = await DbAsync(async db => await db.GovernmentFeeTypes.Where(t => t.Code == code).Select(t => t.Id).FirstAsync());
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFeeTypes/Edit/{typeId}"), "Edit government fee type");
        var manual = await DbAsync(async db => await db.GovernmentFeeTypes.Where(t => t.Code == "REGISTRATION").Select(t => t.Id).FirstAsync());

        // assessment: agreement picker, form, validation, success
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFees/Create?term={number}"), number);
        var form = await GetOkAsync(c, $"/GovernmentFees/Create?agreementId={agreementId}");
        foreach (var t in new[] { "Assess a government fee", number, "Stamp 2%", "Company-borne part" }) Dbg.Has(form, t);
        var bad = await PostAsync(c, $"/GovernmentFees/Create?agreementId={agreementId}", "/GovernmentFees/Create", ("AgreementId", agreementId.ToString()), ("FeeTypeId", typeId.ToString()), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Dbg.Has(await BodyAsync(bad), "Enter the amount the percentage applies to");
        var created = await PostAsync(c, $"/GovernmentFees/Create?agreementId={agreementId}", "/GovernmentFees/Create", ("AgreementId", agreementId.ToString()), ("FeeTypeId", typeId.ToString()), ("BasisAmount", "5000000"),
            ("CompanyBorneAmount", "20000"), ("Reference", "Challan 77"), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = IdFrom(created, @"/GovernmentFees/Details/(\d+)");
        var d = await GetOkAsync(c, $"/GovernmentFees/Details/{id}");
        foreach (var t in new[] { "GFA-", "Customer liability", "Customer collection", "Government payable (assessed)", "Actual payment to government", "Company-borne expense", "80,000.00", "100,000.00", "20,000.00", "Challan 77" }) Dbg.Has(d, t);
        var second = await PostAsync(c, $"/GovernmentFees/Create?agreementId={agreementId}", "/GovernmentFees/Create", ("AgreementId", agreementId.ToString()), ("FeeTypeId", manual.ToString()), ("Amount", "5000"), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        var secondId = IdFrom(second, @"/GovernmentFees/Details/(\d+)");

        // customer collection, payment to government, caps
        var cash = await MethodIdAsync("CASH");
        var col = await PostAsync(c, $"/GovernmentFees/Details/{id}", $"/GovernmentFees/Collect/{id}", ("amount", "30000"), ("paymentMethodId", cash.ToString()), ("collectionDate", Today), ("idempotencyKey", Guid.NewGuid().ToString("N")), ("rowVersion", Rv(d, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, col.StatusCode);
        d = await GetOkAsync(c, $"/GovernmentFees/Details/{id}");
        Dbg.Has(d, "GFC-"); Dbg.Has(d, "50,000.00");
        var tooMuch = await PostAsync(c, $"/GovernmentFees/Details/{id}", $"/GovernmentFees/Collect/{id}", ("amount", "50000.01"), ("paymentMethodId", cash.ToString()), ("collectionDate", Today), ("idempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, tooMuch.StatusCode);
        Assert.Equal(30000m, await DbAsync(async db => await db.GovernmentFeeAssessments.Where(a => a.Id == id).Select(a => a.CollectedAmount).FirstAsync()));
        var payForm = await GetOkAsync(c, $"/GovernmentPayments/Create?assessmentId={id}");
        Dbg.Has(payForm, "Record payment for GFA-"); Dbg.Has(payForm, "100,000.00");
        Dbg.Has(await GetOkAsync(c, "/GovernmentPayments/Create"), "Record payment to government");
        var overPay = await PostAsync(c, $"/GovernmentPayments/Create?assessmentId={id}", "/GovernmentPayments/Create", ("AssessmentId", id.ToString()), ("Amount", "100000.01"), ("PaymentMethodId", cash.ToString()), ("PaymentDate", Today),
            ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, overPay.StatusCode);
        Dbg.Has(await BodyAsync(overPay), "exceeds the government payable");
        var pay = await PostAsync(c, $"/GovernmentPayments/Create?assessmentId={id}", "/GovernmentPayments/Create", ("AssessmentId", id.ToString()), ("Amount", "100000"), ("PaymentMethodId", cash.ToString()), ("PaymentDate", Today),
            ("Reference", "CH-5555"), ("IdempotencyKey", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Redirect, pay.StatusCode);
        d = await GetOkAsync(c, $"/GovernmentFees/Details/{id}");
        Dbg.Has(d, "GFP-"); Dbg.Has(d, "CH-5555");
        var gp = await GetOkAsync(c, $"/GovernmentPayments?projectId={await DbAsync(async db => await db.GovernmentFeeAssessments.Where(a => a.Id == id).Select(a => a.ProjectId).FirstAsync())}");
        Dbg.Has(gp, "GFP-"); Dbg.Has(gp, "CH-5555"); Dbg.Has(gp, "GFA-");

        // documents
        var up = await UploadAsync(c, $"/GovernmentFees/Details/{id}", $"/GovernmentFees/UploadDocument/{id}", "challan.pdf", ("type", "Challan"), ("title", "Paid challan"));
        Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
        d = await GetOkAsync(c, $"/GovernmentFees/Details/{id}");
        Dbg.Has(d, "Paid challan"); Dbg.Has(d, "challan.pdf");
        var docId = await DbAsync(async db => await db.GovernmentFeeDocuments.Where(x => x.AssessmentId == id).Select(x => x.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/GovernmentFees/DownloadDocument/{id}?documentId={docId}")).StatusCode);
        var del = await PostAsync(c, $"/GovernmentFees/Details/{id}", $"/GovernmentFees/DeleteDocument/{id}", ("documentId", docId.ToString()), ("reason", "Uploaded to the wrong fee"));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.DoesNotContain("challan.pdf", await GetOkAsync(c, $"/GovernmentFees/Details/{id}"));

        // reversal of the collection, cancellation of the untouched assessment, lists and filters
        var colId = await DbAsync(async db => await db.GovernmentFeeCollections.Where(x => x.AssessmentId == id).Select(x => x.Id).FirstAsync());
        var rev = await PostAsync(c, $"/GovernmentFees/Details/{id}", $"/GovernmentFees/ReverseCollection/{id}", ("collectionId", colId.ToString()), ("reason", "Cheque bounced"));
        Assert.Equal(HttpStatusCode.Redirect, rev.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFees/Details/{id}"), "Cheque bounced");
        var secondDetails = await GetOkAsync(c, $"/GovernmentFees/Details/{secondId}");
        var cancel = await PostAsync(c, $"/GovernmentFees/Details/{secondId}", $"/GovernmentFees/Cancel/{secondId}", ("reason", "Entered on the wrong agreement"), ("rowVersion", Rv(secondDetails, "rowVersion")));
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFees/Details/{secondId}"), "Cancelled");
        var list = await GetOkAsync(c, $"/GovernmentFees?search={number}");
        foreach (var t in new[] { "Government fees", "Customer liability", "Collected from customers", "Payable to government", "Company-borne expense", "GFA-" }) Dbg.Has(list, t);
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFees?search={number}&collectionPending=true"), "GFA-");
        Dbg.Has(await GetOkAsync(c, $"/GovernmentFees?search={number}&status=Cancelled"), "Cancelled");
        Assert.Equal(1, await DbAsync(async db => await db.AccountingPostingRequests.CountAsync(a => a.EventType == AccountingEventType.GovFeeAssessed && a.SourceId == id)));
    }

    [MySqlFact]
    public async Task ExpenseAndFeeScreens_RequirePermissions_AndAntiForgery()
    {
        var anon = f.NewClient();
        var r0 = await anon.GetAsync("/Expenses/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, r0.StatusCode);
        Assert.Contains("Login", r0.Headers.Location!.ToString());

        var plain = await f.LoggedInAsync(administrator: false);
        foreach (var url in new[] { "/Expenses/Dashboard", "/Expenses", "/Expenses/Create", "/Expenses/Approvals", "/ExpenseCategories", "/Vendors", "/Vendors/Create", "/VendorInvoices", "/VendorPayments",
                     "/GovernmentFees", "/GovernmentFees/Create", "/GovernmentFeeTypes", "/GovernmentPayments", "/GovernmentPayments/Create" })
        {
            var r = await plain.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        }
        // a project manager registers expenses but can neither approve nor pay them
        var pm = await f.LoggedInAsRoleAsync(RoleNames.ProjectManager);
        Assert.Equal(HttpStatusCode.OK, (await pm.GetAsync("/Expenses/Create")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pm.GetAsync("/Expenses/Dashboard")).StatusCode);
        foreach (var url in new[] { "/Expenses/Approvals", "/VendorPayments/Create", "/GovernmentPayments/Create", "/GovernmentFees/Create" })
            Assert.Contains("AccessDenied", (await pm.GetAsync(url)).Headers.Location!.ToString());
        // the accountant approves and pays but cannot register expenses
        var acc = await f.LoggedInAsRoleAsync(RoleNames.Accountant);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/Expenses/Approvals")).StatusCode);
        Assert.Contains("AccessDenied", (await acc.GetAsync("/Expenses/Create")).Headers.Location!.ToString());

        // state-changing posts without an anti-forgery token are rejected
        var admin = await NewAdminAsync();
        foreach (var url in new[] { "/Expenses/Create", "/Expenses/Submit/1", "/Expenses/Approve/1", "/Expenses/Pay/1", "/Expenses/ReversePayment/1", "/Vendors/Create", "/VendorInvoices/Create",
                     "/VendorPayments/Create", "/GovernmentFees/Create", "/GovernmentFees/Collect/1", "/GovernmentFees/Pay/1", "/GovernmentPayments/Create", "/GovernmentFeeTypes/Create", "/ExpenseCategories/Save" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        // unknown records are not found for administrators
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/Expenses/Details/999999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/GovernmentFees/Details/999999999")).StatusCode);
    }
}
