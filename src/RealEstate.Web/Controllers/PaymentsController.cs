using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Payment collection, verification, approval, allocation and reversal (thin: bind, call PaymentService, map).</summary>
public class PaymentsController(PaymentService payments, ProjectService projects, ICurrentUser current) : InstallmentControllerBase
{
    // ----- lists (verification and approval queues are status filters) -----
    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Index(PaymentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new PaymentListViewModel
        {
            Filter = filter, Payments = await payments.ListAsync(filter, ct), Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items,
            Title = filter.Status switch { PaymentStatus.PendingVerification => "Payment verification", PaymentStatus.PendingApproval => "Payment approval", _ => "Payments" },
        });
    }

    [HasPermission(Permissions.PaymentsVerify)]
    public IActionResult Verification() => RedirectToAction(nameof(Index), new { status = PaymentStatus.PendingVerification });

    [HasPermission(Permissions.PaymentsApprove)]
    public IActionResult Approvals() => RedirectToAction(nameof(Index), new { status = PaymentStatus.PendingApproval });

    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var p = await payments.GetAsync(id, ct);
        return View(new PaymentDetailsViewModel { Payment = p, IsSubmitter = string.Equals(p.SubmittedBy, current.UserName, StringComparison.OrdinalIgnoreCase) });
    }

    // ----- collection -----
    [HttpGet, HasPermission(Permissions.PaymentsCollect)]
    public async Task<IActionResult> Create(long? agreementId, string? term, CancellationToken ct)
    {
        if (agreementId is not { } id)
            return View("PickAgreement", new AgreementPickerViewModel { Term = term, Results = await payments.AgreementLookupAsync(term, ct), Title = "Collect a payment" });
        return View(await FillAsync(new PaymentFormViewModel { AgreementId = id }, ct, initial: true));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsCollect)]
    public async Task<IActionResult> Create(PaymentFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var p = await payments.CreateAsync(m.ToRequest(), ct);
                TempData["Success"] = p.Status == PaymentStatus.PendingVerification
                    ? $"Payment {p.PaymentNumber} submitted. It must be verified and approved before it is posted."
                    : $"Payment {p.PaymentNumber} submitted. Another user must approve it to post it and issue the receipt.";
                return RedirectToAction(nameof(Details), new { id = p.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await FillAsync(m, ct, initial: false));
    }

    private async Task<PaymentFormViewModel> FillAsync(PaymentFormViewModel m, CancellationToken ct, bool initial)
    {
        m.Context = await payments.CollectionContextAsync(m.AgreementId, ct);
        m.Methods = await payments.MethodsAsync(true, ct);
        if (initial)
        {
            m.PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow);
            m.CustomerId = m.Context.Parties.FirstOrDefault(p => p.IsPrimary)?.CustomerId;
            m.PaymentMethodId = m.Methods.FirstOrDefault(x => x.Kind == PaymentMethodKind.Cash)?.Id ?? 0;
        }
        var existing = m.Lines.ToDictionary(l => l.ItemId, l => l.Amount);
        m.Lines = m.Context.OpenLines.Select(l => new AllocationLineViewModel { ItemId = l.ItemId, Amount = existing.GetValueOrDefault(l.ItemId) }).ToList();
        return m;
    }

    // ----- verification -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsVerify)]
    public async Task<IActionResult> Verify(long id, bool verified, string notes, string? bankReference, DateOnly? clearedOn, string? rowVersion, CancellationToken ct)
    {
        try
        {
            var p = await payments.VerifyAsync(id, new VerifyPaymentRequest(verified, notes ?? "", bankReference, clearedOn, rowVersion), ct);
            TempData["Success"] = verified ? $"Payment {p.PaymentNumber} verified; it now waits for approval." : $"Payment {p.PaymentNumber} rejected at verification.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- approval (posting) -----
    [HttpGet, HasPermission(Permissions.PaymentsApprove)]
    public async Task<IActionResult> Approve(long id, CancellationToken ct) => View(await FillAllocationAsync(new AllocationFormViewModel { PaymentId = id }, ct, initial: true));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsApprove)]
    public async Task<IActionResult> Approve(long id, AllocationFormViewModel m, CancellationToken ct)
    {
        try
        {
            var p = await payments.ApproveAsync(id, new ApprovePaymentRequest(m.Comments, m.OverrideAllocation ? m.Mode : null, m.OverrideAllocation && m.Mode == AllocationMode.ChargeSpecific ? m.ChargeId : null,
                m.OverrideAllocation ? m.ToLines() : null, m.OverrideAllocation ? m.RemainderToAdvance : null, m.RowVersion), ct);
            TempData["Success"] = $"Payment {p.PaymentNumber} approved and posted. Receipt {p.Receipt?.ReceiptNumber} issued.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        m.PaymentId = id;
        return View(await FillAllocationAsync(m, ct, initial: false));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Reject(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try
        {
            var p = await payments.RejectAsync(id, new RejectPaymentRequest(reason ?? "", rowVersion), ct);
            TempData["Success"] = $"Payment {p.PaymentNumber} rejected.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- allocation of posted payments -----
    [HttpGet, HasPermission(Permissions.PaymentsAllocate)]
    public async Task<IActionResult> Allocate(long id, CancellationToken ct) => View(await FillAllocationAsync(new AllocationFormViewModel { PaymentId = id }, ct, initial: true));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsAllocate)]
    public async Task<IActionResult> Allocate(long id, AllocationFormViewModel m, CancellationToken ct)
    {
        try
        {
            await payments.AllocateAsync(id, new AllocatePaymentRequest(m.Mode, m.Mode == AllocationMode.ChargeSpecific ? m.ChargeId : null, m.ToLines(), m.IncludeAdvance, m.RemainderToAdvance, m.RowVersion), ct);
            TempData["Success"] = "Allocation saved.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        m.PaymentId = id;
        return View(await FillAllocationAsync(m, ct, initial: false));
    }

    private async Task<AllocationFormViewModel> FillAllocationAsync(AllocationFormViewModel m, CancellationToken ct, bool initial)
    {
        m.Payment = await payments.GetAsync(m.PaymentId, ct);
        m.Options = await payments.AllocationOptionsAsync(m.PaymentId, ct);
        if (initial)
        {
            m.RowVersion = m.Payment.RowVersion;
            if (m.Payment.Status == PaymentStatus.PendingApproval)
            {
                m.Mode = m.Payment.RequestedAllocationMode; m.ChargeId = m.Payment.RequestedChargeId; m.RemainderToAdvance = m.Payment.RequestedRemainderToAdvance;
                m.Lines = m.Payment.RequestedLines.Select(l => new AllocationLineViewModel { ItemId = l.ItemId, Amount = l.Amount }).ToList();
            }
        }
        var existing = m.Lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        m.Lines = m.Options.Lines.Select(l => new AllocationLineViewModel { ItemId = l.ItemId, Amount = existing.GetValueOrDefault(l.ItemId) }).ToList();
        return m;
    }

    // ----- reversal -----
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsReverse)]
    public async Task<IActionResult> Reverse(long id, string reason, string? rowVersion, CancellationToken ct)
    {
        try
        {
            var p = await payments.ReverseAsync(id, new ReversePaymentRequest(reason ?? "", rowVersion), ct);
            TempData["Success"] = $"Payment {p.PaymentNumber} reversed. Its allocations were undone and offsetting ledger entries were posted.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Customer ledger, agreement ledger and property statement.</summary>
public class LedgersController(LedgerService ledgers) : InstallmentControllerBase
{
    [HasPermission(Permissions.LedgersView)]
    public async Task<IActionResult> Agreement(long id, int page = 1, CancellationToken ct = default) =>
        View(new AgreementLedgerViewModel { Ledger = await ledgers.GetAgreementLedgerAsync(id, new LedgerPageRequest { Page = page }, ct) });

    [HasPermission(Permissions.LedgersView)]
    public async Task<IActionResult> Customer(long id, int page = 1, CancellationToken ct = default) =>
        View(new CustomerLedgerViewModel { Ledger = await ledgers.GetCustomerLedgerAsync(id, new LedgerPageRequest { Page = page }, ct) });

    [HasPermission(Permissions.LedgersView)]
    public async Task<IActionResult> Property(long id, CancellationToken ct) =>
        View(new PropertyStatementViewModel { Statement = await ledgers.GetPropertyStatementAsync(id, ct) });
}

/// <summary>Refund processing: request, approval (maker-checker), payout, cancellation.</summary>
public class RefundsController(RefundService refunds, PaymentService payments, ProjectService projects, ICurrentUser current) : InstallmentControllerBase
{
    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Index(RefundListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new RefundListViewModel { Filter = filter, Refunds = await refunds.ListAsync(filter, ct), Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items });
    }

    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var r = await refunds.GetAsync(id, ct);
        return View(new RefundDetailsViewModel { Refund = r, Methods = await payments.MethodsAsync(true, ct), IsRequester = string.Equals(r.RequestedBy, current.UserName, StringComparison.OrdinalIgnoreCase) });
    }

    [HttpGet, HasPermission(Permissions.RefundsRequest)]
    public async Task<IActionResult> Create(long? agreementId, string? term, CancellationToken ct)
    {
        if (agreementId is not { } id)
            return View("~/Views/Payments/PickAgreement.cshtml", new AgreementPickerViewModel { Term = term, Results = await payments.AgreementLookupAsync(term, ct), Controller = "Refunds", Title = "Request a refund" });
        return View(new RefundFormViewModel { AgreementId = id, Refundable = await refunds.RefundableAsync(id, ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RefundsRequest)]
    public async Task<IActionResult> Create(RefundFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var r = await refunds.CreateAsync(new CreateRefundRequest(m.AgreementId, m.Amount, m.Reason, m.BookingRefundId, m.IdempotencyKey), ct);
                TempData["Success"] = $"Refund {r.RefundNumber} requested. Another user must approve it before it can be paid.";
                return RedirectToAction(nameof(Details), new { id = r.Id });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Refundable = await refunds.RefundableAsync(m.AgreementId, ct);
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RefundsApprove)]
    public async Task<IActionResult> Approve(long id, string? comments, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => refunds.ApproveAsync(id, new ApproveRefundRequest(comments, rowVersion), ct), "Refund approved; it can now be paid.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RefundsApprove)]
    public async Task<IActionResult> Reject(long id, string comments, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => refunds.RejectAsync(id, new RejectRefundRequest(comments ?? "", rowVersion), ct), "Refund rejected.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RefundsRequest)]
    public async Task<IActionResult> Cancel(long id, string reason, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => refunds.CancelAsync(id, new CancelRefundRequest(reason ?? "", rowVersion), ct), "Refund cancelled.");

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RefundsPay)]
    public async Task<IActionResult> Pay(long id, long paymentMethodId, DateOnly paidOn, string? reference, string? rowVersion, CancellationToken ct) =>
        await Act(id, () => refunds.PayAsync(id, new PayRefundRequest(paymentMethodId, paidOn, reference, rowVersion), ct), "Refund payout recorded and posted to the ledger.");

    private async Task<IActionResult> Act(long id, Func<Task<RefundDetailDto>> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }
}
