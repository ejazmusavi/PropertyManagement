using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Documents;
using RealEstate.Application.Expenses;
using RealEstate.Application.GovernmentFees;
using RealEstate.Application.Payments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Government fee configuration (fee types).</summary>
[HasPermission(Permissions.GovFeesView)]
public class GovernmentFeeTypesController(GovernmentFeeService fees, ExpenseService expenses) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(new GovFeeTypeListViewModel { Types = await fees.ListTypesAsync(false, null, ct) });

    [HttpGet, HasPermission(Permissions.GovFeesSetup)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", new GovFeeTypeFormViewModel { Projects = await expenses.ProjectOptionsAsync(false, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesSetup)]
    public async Task<IActionResult> Create(GovFeeTypeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await fees.CreateTypeAsync(ToRequest(m), ct);
                TempData["Success"] = "Government fee type created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Projects = await expenses.ProjectOptionsAsync(false, ct);
        return View("Form", m);
    }

    [HttpGet, HasPermission(Permissions.GovFeesSetup)]
    public async Task<IActionResult> Edit(long id, CancellationToken ct)
    {
        var t = await fees.GetTypeAsync(id, ct);
        return View("Form", new GovFeeTypeFormViewModel
        {
            Id = t.Id, ProjectId = t.ProjectId, Code = t.Code, Name = t.Name, Kind = t.Kind, Method = t.Method, FixedAmount = t.FixedAmount, Rate = t.Rate, DefaultBearer = t.DefaultBearer,
            Description = t.Description, IsActive = t.IsActive, IsSystem = t.IsSystem, RowVersion = t.RowVersion, Projects = await expenses.ProjectOptionsAsync(false, ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesSetup)]
    public async Task<IActionResult> Edit(long id, GovFeeTypeFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await fees.UpdateTypeAsync(id, ToRequest(m), ct);
                TempData["Success"] = "Government fee type updated. New terms apply to new assessments only.";
                return RedirectToAction(nameof(Index));
            }
            catch (ConcurrencyException) { ModelState.AddModelError(string.Empty, "The fee type was changed by another user. Reload and try again."); }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Id = id;
        m.Projects = await expenses.ProjectOptionsAsync(false, ct);
        return View("Form", m);
    }

    private static SaveGovernmentFeeTypeRequest ToRequest(GovFeeTypeFormViewModel m) =>
        new(m.ProjectId, m.Code, m.Name, m.Kind, m.Method, m.FixedAmount, m.Rate, m.DefaultBearer, m.Description, m.IsActive, m.RowVersion);
}

/// <summary>Customer fee assessments, customer collections, payments to government and fee documents.</summary>
[HasPermission(Permissions.GovFeesView)]
public class GovernmentFeesController(GovernmentFeeService fees, ExpenseService expenses) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(GovernmentFeeListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new GovFeeListViewModel
        {
            Filter = filter, Fees = await fees.ListAsync(filter, ct), Summary = await fees.SummaryAsync(filter.ProjectId, ct), Projects = await expenses.ProjectOptionsAsync(false, ct),
            Types = await fees.ListTypesAsync(false, null, ct),
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken ct) =>
        View(new GovFeeDetailsViewModel { Fee = await fees.GetAsync(id, ct), Methods = await OrAsync(async () => await expenses.MethodsAsync(ct), []) });

    // ----- assess -----
    [HttpGet, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> Create(long? agreementId, string? term, CancellationToken ct)
    {
        if (agreementId is not { } id)
            return View("~/Views/Payments/PickAgreement.cshtml", new AgreementPickerViewModel { Term = term, Results = await fees.AgreementLookupAsync(term, ct), Controller = "GovernmentFees", Title = "Assess a government fee" });
        return View(await LoadAsync(new GovFeeAssessFormViewModel { AgreementId = id }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> Create(GovFeeAssessFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var a = await fees.AssessAsync(new CreateGovernmentFeeAssessmentRequest(m.AgreementId, m.FeeTypeId, m.BasisAmount, m.Amount, m.CompanyBorneAmount, m.DueDate, m.Reference, m.Remarks, m.IdempotencyKey), ct);
                TempData["Success"] = $"Fee {a.AssessmentNumber} assessed: customer liability {a.CustomerLiability:N2}, company-borne {a.CompanyBorne:N2}.";
                return RedirectToAction(nameof(Details), new { id = a.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await LoadAsync(m, ct));
    }

    private async Task<GovFeeAssessFormViewModel> LoadAsync(GovFeeAssessFormViewModel m, CancellationToken ct)
    {
        m.Agreement = await fees.GetAgreementAsync(m.AgreementId, ct);
        m.Types = await fees.ListTypesAsync(true, null, ct);
        return m;
    }

    // ----- workflow -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> Cancel(long id, string reason, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => fees.CancelAsync(id, new CancelGovernmentFeeAssessmentRequest(reason ?? "", rowVersion), ct), "Assessment cancelled.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> Collect(long id, decimal amount, long paymentMethodId, DateOnly collectionDate, string? reference, string? remarks, string idempotencyKey, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => fees.CollectAsync(id, new FeeCollectionRequest(amount, paymentMethodId, collectionDate, reference, remarks, idempotencyKey ?? "", rowVersion), ct), "Customer collection recorded.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesPay)]
    public async Task<IActionResult> Pay(long id, GovPaymentFormViewModel m, CancellationToken ct) =>
        await Act(id, () => fees.PayAsync(id, new GovernmentPaymentRequest(m.Amount, m.PaymentMethodId, m.PaymentDate, m.Reference, m.Remarks, m.IdempotencyKey ?? "", null), ct), "Payment to government recorded.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesPay)]
    public async Task<IActionResult> ReverseCollection(long id, long collectionId, string reason, CancellationToken ct) =>
        await Act(id, () => fees.ReverseCollectionAsync(collectionId, new ReverseFeeEntryRequest(reason ?? ""), ct), "Collection reversed.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesPay)]
    public async Task<IActionResult> ReversePayment(long id, long paymentId, string reason, CancellationToken ct) =>
        await Act(id, () => fees.ReversePaymentAsync(paymentId, new ReverseFeeEntryRequest(reason ?? ""), ct), "Payment reversed.");

    // ----- documents -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> UploadDocument(long id, IFormFile? file, GovernmentFeeDocumentType type, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { TempData["Error"] = "Choose a file to upload."; return RedirectToAction(nameof(Details), new { id }); }
        try
        {
            await using var s = file.OpenReadStream();
            await fees.UploadDocumentAsync(id, type, title, new FileUpload(file.FileName, file.ContentType, file.Length, s), ct);
            TempData["Success"] = "Document uploaded.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> DownloadDocument(long id, long documentId, CancellationToken ct)
    {
        var f = await fees.DownloadDocumentAsync(id, documentId, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesAssess)]
    public async Task<IActionResult> DeleteDocument(long id, long documentId, string reason, CancellationToken ct)
    {
        try { await fees.DeleteDocumentAsync(id, documentId, reason ?? "", ct); TempData["Success"] = "Document removed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> Act(long id, Func<Task<GovernmentFeeDetailDto>> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Payments to government: list and record a payment against an assessed fee.</summary>
[HasPermission(Permissions.GovFeesView)]
public class GovernmentPaymentsController(GovernmentFeeService fees, ExpenseService expenses) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(GovernmentPaymentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new GovPaymentListViewModel { Filter = filter, Payments = await fees.ListPaymentsAsync(filter, ct), Projects = await expenses.ProjectOptionsAsync(false, ct) });
    }

    [HttpGet, HasPermission(Permissions.GovFeesPay)]
    public async Task<IActionResult> Create(long? assessmentId, string? search, CancellationToken ct)
    {
        if (assessmentId is not { } id)
        {
            var filter = new GovernmentFeeListRequest { PageSize = 20, PaymentPending = true, Search = search };
            return View("Pick", new GovPaymentPickerViewModel { Search = search, Payable = await fees.ListAsync(filter, ct) });
        }
        var f = await fees.GetAsync(id, ct);
        return View(new GovPaymentFormViewModel { AssessmentId = id, Amount = f.GovernmentPayable, Fee = f, Methods = await expenses.MethodsAsync(ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.GovFeesPay)]
    public async Task<IActionResult> Create(GovPaymentFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await fees.PayAsync(m.AssessmentId, new GovernmentPaymentRequest(m.Amount, m.PaymentMethodId, m.PaymentDate, m.Reference, m.Remarks, m.IdempotencyKey, null), ct);
                TempData["Success"] = "Payment to government recorded.";
                return RedirectToAction("Details", "GovernmentFees", new { id = m.AssessmentId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Fee = await fees.GetAsync(m.AssessmentId, ct);
        m.Methods = await expenses.MethodsAsync(ct);
        return View(m);
    }
}
