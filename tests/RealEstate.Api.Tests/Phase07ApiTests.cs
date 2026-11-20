using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RealEstate.Application.Auth;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Pricing;
using RealEstate.Application.Projects;
using RealEstate.Application.Sales;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Tests.Support;

namespace RealEstate.Api.Tests;

[Collection("api")]
public class Phase07ApiTests(ApiFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    private async Task<HttpClient> ClientAsync(string? role = null)
    {
        var email = role is null ? await factory.CreateUserAsync(administrator: true) : await factory.CreateUserInRoleAsync(role);
        var c = factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ApiFactory.Password));
        r.EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        return c;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<long> ProjectAsync(HttpClient c) => (await Ok<ProjectDetailDto>(await c.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("X" + Guid.NewGuid().ToString("N")[..10],
        "Expenses API", null, "Murree", null, null, null, 80m, MeasurementUnit.Kanal, null, null)))).Id;

    private static async Task<long> CategoryAsync(HttpClient c, string code) => (await Ok<List<ExpenseCategoryDto>>(await c.GetAsync("/api/v1/expense-categories"))).Single(x => x.Code == code).Id;
    private static async Task<long> CashAsync(HttpClient c) => (await Ok<List<PaymentMethodDto>>(await c.GetAsync("/api/v1/expenses/payment-methods"))).Single(m => m.Code == "CASH").Id;

    private static async Task<ExpenseDetailDto> ApprovedExpenseAsync(HttpClient maker, HttpClient checker, long projectId, decimal amount, long? vendorId = null)
    {
        var cat = await CategoryAsync(maker, "OFFICE_RENT");
        var e = await Ok<ExpenseDetailDto>(await maker.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, cat, vendorId, "API expense", Today, amount, null, null, null, Guid.NewGuid().ToString("N"))));
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3, 4, 5]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "bill.pdf" }, { new StringContent("Invoice"), "type" } };
        await Ok<ExpenseAttachmentDto>(await maker.PostAsync($"/api/v1/expenses/{e.Id}/attachments", form));
        e = await Ok<ExpenseDetailDto>(await maker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/submit", new SubmitExpenseRequest(null)));
        return await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/approve", new ApproveExpenseRequest("ok", e.RowVersion)));
    }

    [MySqlFact]
    public async Task Phase7Endpoints_RequireAuthentication()
    {
        var c = factory.CreateClient();
        foreach (var url in new[] { "/api/v1/expenses", "/api/v1/expenses/1", "/api/v1/expenses/dashboard", "/api/v1/expenses/payments", "/api/v1/expense-categories", "/api/v1/vendors", "/api/v1/vendors/1",
                     "/api/v1/vendor-invoices", "/api/v1/government-fees", "/api/v1/government-fees/types", "/api/v1/government-fees/summary", "/api/v1/government-fees/payments" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        foreach (var url in new[] { "/api/v1/expenses", "/api/v1/expenses/1/approve", "/api/v1/expenses/1/pay", "/api/v1/vendors", "/api/v1/vendor-invoices", "/api/v1/government-fees/assessments", "/api/v1/government-fees/1/collections", "/api/v1/government-fees/1/payments" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, new { })).StatusCode);
    }

    [MySqlFact]
    public async Task Expense_Create_Document_Approve_Pay_Reverse_EndToEnd()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        var projectId = await ProjectAsync(maker);
        var cat = await CategoryAsync(maker, "UTILITIES");
        var cash = await CashAsync(maker);

        // validation (400) and creation (201)
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, cat, null, "x", Today, 0m, null, null, null, null))).StatusCode);
        var key = Guid.NewGuid().ToString("N");
        var r = await maker.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, cat, null, "Electricity bill", Today, 80_000m, null, "REF-1", null, key));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var e = (await r.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
        Assert.StartsWith("EXP-", e.ExpenseNumber);
        Assert.Equal(e.Id, (await Ok<ExpenseDetailDto>(await maker.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, cat, null, "Electricity bill", Today, 80_000m, null, "REF-1", null, key)))).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, cat, null, "Electricity bill", Today, 80_001m, null, "REF-1", null, key))).StatusCode);

        // no document -> cannot submit (422); nothing payable before approval (422)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/submit", new SubmitExpenseRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(1m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3, 4, 5]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "bill.pdf" }, { new StringContent("Invoice"), "type" }, { new StringContent("Bill"), "title" } };
        var doc = await Ok<ExpenseAttachmentDto>(await maker.PostAsync($"/api/v1/expenses/{e.Id}/attachments", form));
        Assert.Equal("Bill", doc.Title);
        Assert.Single(await Ok<List<ExpenseAttachmentDto>>(await maker.GetAsync($"/api/v1/expenses/{e.Id}/attachments")));
        var dl = await maker.GetAsync($"/api/v1/expenses/{e.Id}/attachments/{doc.Id}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(5, (await dl.Content.ReadAsByteArrayAsync()).Length);
        e = await Ok<ExpenseDetailDto>(await maker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/submit", new SubmitExpenseRequest(null)));
        Assert.Equal(ExpenseStatus.PendingApproval, e.Status);

        // maker-checker: the submitter cannot approve (422); rejection needs a comment (400); a different user approves
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/approve", new ApproveExpenseRequest(null, e.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/reject", new RejectExpenseRequest("", e.RowVersion))).StatusCode);
        e = await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/approve", new ApproveExpenseRequest("ok", e.RowVersion)));
        Assert.Equal(ExpenseStatus.Approved, e.Status);
        Assert.Equal(ExpensePaymentStatus.Unpaid, e.PaymentStatus);

        // payments: partial, idempotent retry, over-payment refused, remainder, reversal
        var pk = Guid.NewGuid().ToString("N");
        e = await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(30_000m, cash, Today, null, null, pk)));
        Assert.Equal(ExpensePaymentStatus.PartiallyPaid, e.PaymentStatus);
        e = await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(30_000m, cash, Today, null, null, pk)));
        Assert.Single(e.Payments);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(30_001m, cash, Today, null, null, pk))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(50_000.01m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(1m, cash, Today.AddDays(2), null, null, Guid.NewGuid().ToString("N")))).StatusCode);
        e = await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(50_000m, cash, Today, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(ExpensePaymentStatus.Paid, e.PaymentStatus);
        var list = await Ok<PagedResult<ExpenseListItemDto>>(await maker.GetAsync($"/api/v1/expenses?projectId={projectId}&paymentStatus=Paid"));
        Assert.Contains(list.Items, x => x.Id == e.Id);
        var payments = await Ok<PagedResult<VendorPaymentDto>>(await maker.GetAsync($"/api/v1/expenses/payments?expenseId={e.Id}"));
        Assert.Equal(2, payments.TotalCount);
        var rev = await checker.PostAsJsonAsync($"/api/v1/expenses/payments/{payments.Items[0].Id}/reverse", new ReverseVendorPaymentRequest("Duplicate transfer"));
        e = await Ok<ExpenseDetailDto>(rev);
        Assert.Equal(ExpensePaymentStatus.PartiallyPaid, e.PaymentStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await checker.PostAsJsonAsync($"/api/v1/expenses/payments/{payments.Items[0].Id}/reverse", new ReverseVendorPaymentRequest("Again please"))).StatusCode);

        // dashboard, get, update of a non-editable expense (422), unknown id (404)
        var dash = await Ok<ExpenseDashboardDto>(await maker.GetAsync($"/api/v1/expenses/dashboard?projectId={projectId}"));
        Assert.Equal(80_000m, dash.TotalApproved);
        Assert.Equal(e.Outstanding, dash.PayablesOutstanding);
        Assert.Equal(e.Id, (await Ok<ExpenseDetailDto>(await maker.GetAsync($"/api/v1/expenses/{e.Id}"))).Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PutAsJsonAsync($"/api/v1/expenses/{e.Id}", new UpdateExpenseRequest(cat, null, "Edit", Today, 1m, null, null, null, e.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/api/v1/expenses/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Vendors_Invoices_AndCategories_EndToEnd()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        var projectId = await ProjectAsync(maker);
        var name = "Vendor " + Guid.NewGuid().ToString("N")[..8];
        var r = await maker.PostAsJsonAsync("/api/v1/vendors", new SaveVendorRequest(name, "Contact", "0300", null, null, "NTN", null));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var detail = (await r.Content.ReadFromJsonAsync<VendorDetailDto>())!;
        var vendor = detail.Vendor;
        Assert.StartsWith("VEN-", vendor.Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/vendors", new SaveVendorRequest(name, null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/vendors", new SaveVendorRequest("", null, null, null, null, null, null))).StatusCode);
        var upd = await Ok<VendorDto>(await maker.PutAsJsonAsync($"/api/v1/vendors/{vendor.Id}", new SaveVendorRequest(name, "New contact", "0300", null, null, "NTN", null, true, vendor.RowVersion)));
        Assert.Equal("New contact", upd.ContactPerson);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PutAsJsonAsync($"/api/v1/vendors/{vendor.Id}", new SaveVendorRequest(name, "Stale", "0300", null, null, "NTN", null, true, vendor.RowVersion))).StatusCode);
        Assert.Contains((await Ok<PagedResult<VendorListItemDto>>(await maker.GetAsync($"/api/v1/vendors?search={name}"))).Items, x => x.Id == vendor.Id);

        // invoice: 201, idempotent retry, duplicate number 400, appears in the list; pay through the expense; vendor payable tracked
        var cat = await CategoryAsync(maker, "CONSTRUCTION");
        var key = Guid.NewGuid().ToString("N");
        var req = new CreateVendorInvoiceRequest(projectId, vendor.Id, "INV-77", cat, Today, Today.AddDays(30), 40_000m, null, null, key);
        var ir = await maker.PostAsJsonAsync("/api/v1/vendor-invoices", req);
        Assert.Equal(HttpStatusCode.Created, ir.StatusCode);
        var inv = (await ir.Content.ReadFromJsonAsync<VendorInvoiceListItemDto>())!;
        Assert.Equal(inv.Id, (await Ok<VendorInvoiceListItemDto>(await maker.PostAsJsonAsync("/api/v1/vendor-invoices", req))).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/vendor-invoices", req with { IdempotencyKey = null, InvoiceNumber = "inv-77" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/vendor-invoices", req with { IdempotencyKey = null, InvoiceNumber = "INV-78", DueDate = Today.AddDays(-1) })).StatusCode);
        Assert.Contains((await Ok<PagedResult<VendorInvoiceListItemDto>>(await maker.GetAsync($"/api/v1/vendor-invoices?vendorId={vendor.Id}"))).Items, x => x.Id == inv.Id);
        Assert.Equal(inv.ExpenseId, (await Ok<VendorInvoiceListItemDto>(await maker.GetAsync($"/api/v1/vendor-invoices/{inv.Id}"))).ExpenseId);
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "inv.pdf" } };
        await Ok<ExpenseAttachmentDto>(await maker.PostAsync($"/api/v1/expenses/{inv.ExpenseId}/attachments", form));
        var ex = await Ok<ExpenseDetailDto>(await maker.PostAsJsonAsync($"/api/v1/expenses/{inv.ExpenseId}/submit", new SubmitExpenseRequest(null)));
        await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{inv.ExpenseId}/approve", new ApproveExpenseRequest(null, ex.RowVersion)));
        await Ok<ExpenseDetailDto>(await checker.PostAsJsonAsync($"/api/v1/expenses/{inv.ExpenseId}/pay", new PayExpenseRequest(15_000m, await CashAsync(maker), Today, null, null, Guid.NewGuid().ToString("N"))));
        var vd = await Ok<VendorDetailDto>(await maker.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        Assert.Equal(25_000m, vd.Payable);
        Assert.Equal(15_000m, vd.Paid);
        Assert.Equal(ExpensePaymentStatus.PartiallyPaid, (await Ok<VendorInvoiceListItemDto>(await maker.GetAsync($"/api/v1/vendor-invoices/{inv.Id}"))).PaymentStatus);

        // categories: standard list, custom create (201), duplicate (400)
        var code = "API_" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        Assert.Equal(HttpStatusCode.Created, (await maker.PostAsJsonAsync("/api/v1/expense-categories", new SaveExpenseCategoryRequest(code, "API " + code, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/expense-categories", new SaveExpenseCategoryRequest(code, "Other", null))).StatusCode);
        Assert.True((await Ok<List<ExpenseCategoryDto>>(await maker.GetAsync("/api/v1/expense-categories?activeOnly=true"))).Count >= 12);
    }

    private static async Task<(long AgreementId, long ProjectId)> AgreementAsync(HttpClient maker, HttpClient checker)
    {
        var project = await Ok<ProjectDetailDto>(await maker.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Y" + Guid.NewGuid().ToString("N")[..10], "Fees API", null, "Murree",
            null, null, null, 80m, MeasurementUnit.Kanal, null, null)));
        (await maker.PostAsJsonAsync("/api/v1/pricing/rules", new CreatePricingRuleRequest(project.Id, "BASE", "Base rate", null, PricingRuleKind.BaseRate, PricingFactor.Size, PricingScope.Project,
            null, null, null, null, null, null, PricingMethod.PerUnit, 1_000_000m, MeasurementUnit.Marla, 0, Today, null, null))).EnsureSuccessStatusCode();
        var types = (await maker.GetFromJsonAsync<List<PropertyTypeDto>>("/api/v1/property-types"))!;
        var property = await Ok<PropertyDetailDto>(await maker.PostAsJsonAsync("/api/v1/properties", new CreatePropertyRequest(project.Id, "P" + Guid.NewGuid().ToString("N")[..10], "1", null, null, null,
            types.Single(t => t.Code == "RES-PLOT").Id, 5m, MeasurementUnit.Marla, null, null, false, false, false, DevelopmentStatus.Developed, null, null, null)));
        var customer = await Ok<CustomerDetailDto>(await maker.PostAsJsonAsync("/api/v1/customers", new CreateCustomerRequest("Fee Payer", IdentityDocumentType.Cnic,
            Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(), "0300-1234567", null, "Murree", null, [new AddressDto(AddressType.Home, "H1", "Murree", null, true)], null, null)));
        var booking = await Ok<BookingDetailDto>(await maker.PostAsJsonAsync("/api/v1/bookings", new CreateBookingRequest(project.Id, null, PaymentMode.Installment,
            [new BookingPropertyInput(property.Id, 0m, null)], [new OwnerInput(customer.Id, 100m)], null, 500_000m, false, null, null, true, null)));
        booking = await Ok<BookingDetailDto>(await checker.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/approve", new ApproveBookingRequest("ok", booking.RowVersion)));
        return (booking.AgreementId!.Value, project.Id);
    }

    [MySqlFact]
    public async Task GovernmentFees_Types_Assessment_Collection_Payment_Reversal_EndToEnd()
    {
        var maker = await ClientAsync(); var checker = await ClientAsync();
        var (agreementId, projectId) = await AgreementAsync(maker, checker);
        var cash = await CashAsync(maker);

        // fee types: seeded standard types, a project-specific percentage type
        var types = await Ok<List<GovernmentFeeTypeDto>>(await maker.GetAsync("/api/v1/government-fees/types?activeOnly=true"));
        foreach (var k in new[] { GovernmentFeeKind.StampDuty, GovernmentFeeKind.Registration, GovernmentFeeKind.TransferFee, GovernmentFeeKind.Tax, GovernmentFeeKind.NocFee, GovernmentFeeKind.DevelopmentCharge, GovernmentFeeKind.Other })
            Assert.Contains(types, t => t.Kind == k);
        var code = "API" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/government-fees/types", new SaveGovernmentFeeTypeRequest(projectId, code, "Bad percent", GovernmentFeeKind.StampDuty, FeeCalculationMethod.PercentOfBasis, null, null, FeeBearer.Customer, null))).StatusCode);
        var tr = await maker.PostAsJsonAsync("/api/v1/government-fees/types", new SaveGovernmentFeeTypeRequest(projectId, code, "Stamp 1%", GovernmentFeeKind.StampDuty, FeeCalculationMethod.PercentOfBasis, null, 1m, FeeBearer.Customer, null));
        Assert.Equal(HttpStatusCode.Created, tr.StatusCode);
        var type = (await tr.Content.ReadFromJsonAsync<GovernmentFeeTypeDto>())!;
        Assert.Equal(type.Id, (await Ok<GovernmentFeeTypeDto>(await maker.GetAsync($"/api/v1/government-fees/types/{type.Id}"))).Id);
        var updated = await Ok<GovernmentFeeTypeDto>(await maker.PutAsJsonAsync($"/api/v1/government-fees/types/{type.Id}", new SaveGovernmentFeeTypeRequest(projectId, code, "Stamp 1% (revised)", GovernmentFeeKind.StampDuty,
            FeeCalculationMethod.PercentOfBasis, null, 1m, FeeBearer.Customer, null, true, type.RowVersion)));
        Assert.Equal("Stamp 1% (revised)", updated.Name);

        // assessment: 1% of 5,000,000 = 50,000, 10,000 borne by the company
        var key = Guid.NewGuid().ToString("N");
        var req = new CreateGovernmentFeeAssessmentRequest(agreementId, type.Id, 5_000_000m, null, 10_000m, null, "Challan 1", null, key);
        var ar = await maker.PostAsJsonAsync("/api/v1/government-fees/assessments", req);
        Assert.Equal(HttpStatusCode.Created, ar.StatusCode);
        var a = (await ar.Content.ReadFromJsonAsync<GovernmentFeeDetailDto>())!;
        Assert.Equal(50_000m, a.Amount); Assert.Equal(40_000m, a.CustomerLiability); Assert.Equal(10_000m, a.CompanyBorne);
        Assert.Equal(a.Id, (await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync("/api/v1/government-fees/assessments", req))).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/government-fees/assessments", req with { BasisAmount = 6_000_000m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/api/v1/government-fees/assessments", req with { IdempotencyKey = Guid.NewGuid().ToString("N"), BasisAmount = null })).StatusCode);

        // collection and payment with their separate caps
        a = await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/collections", new FeeCollectionRequest(25_000m, cash, Today, null, null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(15_000m, a.CustomerOutstanding);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/collections", new FeeCollectionRequest(15_000.01m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode);
        a = await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/payments", new GovernmentPaymentRequest(50_000m, cash, Today, "CH-1", null, Guid.NewGuid().ToString("N"))));
        Assert.Equal(0m, a.GovernmentPayable);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/payments", new GovernmentPaymentRequest(0.01m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode);
        var sum = await Ok<GovernmentFeeSummaryDto>(await maker.GetAsync($"/api/v1/government-fees/summary?projectId={projectId}"));
        Assert.Equal(50_000m, sum.TotalAssessed); Assert.Equal(40_000m, sum.CustomerLiability); Assert.Equal(25_000m, sum.CustomerCollected); Assert.Equal(10_000m, sum.CompanyBorne); Assert.Equal(50_000m, sum.PaidToGovernment);

        // lists, payments list, documents, reversal, cancellation
        var list = await Ok<PagedResult<GovernmentFeeListItemDto>>(await maker.GetAsync($"/api/v1/government-fees?projectId={projectId}&collectionPending=true"));
        Assert.Contains(list.Items, x => x.Id == a.Id);
        Assert.Single((await Ok<PagedResult<GovernmentFeePaymentDto>>(await maker.GetAsync($"/api/v1/government-fees/payments?assessmentId={a.Id}"))).Items);
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3, 4]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "challan.pdf" }, { new StringContent("Challan"), "type" } };
        var doc = await Ok<GovernmentFeeDocumentDto>(await maker.PostAsync($"/api/v1/government-fees/{a.Id}/documents", form));
        Assert.Equal(HttpStatusCode.OK, (await maker.GetAsync($"/api/v1/government-fees/{a.Id}/documents/{doc.Id}/download")).StatusCode);
        Assert.Single(await Ok<List<GovernmentFeeDocumentDto>>(await maker.GetAsync($"/api/v1/government-fees/{a.Id}/documents")));
        Assert.Equal(HttpStatusCode.NoContent, (await maker.DeleteAsync($"/api/v1/government-fees/{a.Id}/documents/{doc.Id}?reason=Wrong%20file%20uploaded")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/cancel", new CancelGovernmentFeeAssessmentRequest("Cannot cancel with money", a.RowVersion))).StatusCode);
        a = await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync($"/api/v1/government-fees/payments/{a.Payments[0].Id}/reverse", new ReverseFeeEntryRequest("Challan rejected")));
        Assert.Equal(0m, a.PaidToGovernment);
        a = await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync($"/api/v1/government-fees/collections/{a.Collections[0].Id}/reverse", new ReverseFeeEntryRequest("Cheque bounced")));
        Assert.Equal(0m, a.Collected);
        a = await Ok<GovernmentFeeDetailDto>(await maker.PostAsJsonAsync($"/api/v1/government-fees/{a.Id}/cancel", new CancelGovernmentFeeAssessmentRequest("Assessed twice", a.RowVersion)));
        Assert.Equal(GovernmentFeeAssessmentStatus.Cancelled, a.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.GetAsync("/api/v1/government-fees/999999999")).StatusCode);
    }

    [MySqlFact]
    public async Task Authorization_IsEnforced_ByPermissionAndProject()
    {
        var admin = await ClientAsync(); var checker = await ClientAsync();
        var projectId = await ProjectAsync(admin);
        var e = await ApprovedExpenseAsync(admin, checker, projectId, 1_000m);
        var cash = await CashAsync(admin);

        // a cashier has no expense or vendor permissions at all
        var cashier = await ClientAsync(RoleNames.Cashier);
        foreach (var url in new[] { "/api/v1/expenses", $"/api/v1/expenses/{e.Id}", "/api/v1/expenses/dashboard", "/api/v1/vendors", "/api/v1/vendor-invoices", "/api/v1/government-fees", "/api/v1/government-fees/types" })
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, 1, null, "Nope", Today, 5m, null, null, null, null))).StatusCode);

        // a project manager (not assigned to this project) is blocked at project level; as an assigned manager they can register but not approve or pay
        var pm = await ClientAsync(RoleNames.ProjectManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.GetAsync($"/api/v1/expenses/{e.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, e.CategoryId, null, "Unassigned", Today, 5m, null, null, null, null))).StatusCode);
        Assert.DoesNotContain((await Ok<PagedResult<ExpenseListItemDto>>(await pm.GetAsync("/api/v1/expenses?pageSize=100"))).Items, x => x.Id == e.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.GetAsync($"/api/v1/expenses/dashboard?projectId={projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(1m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode);

        // an accountant may approve and pay but not register expenses, and is still limited to assigned projects
        var acc = await ClientAsync(RoleNames.Accountant);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PostAsJsonAsync("/api/v1/expenses", new CreateExpenseRequest(projectId, e.CategoryId, null, "No manage permission", Today, 5m, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PostAsJsonAsync($"/api/v1/expenses/{e.Id}/pay", new PayExpenseRequest(1m, cash, Today, null, null, Guid.NewGuid().ToString("N")))).StatusCode); // not assigned to the project
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.GetAsync($"/api/v1/expenses/{e.Id}")).StatusCode);
    }
}
