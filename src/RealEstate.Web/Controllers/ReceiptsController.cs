using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class ReceiptsController(ReceiptService receipts, ProjectService projects) : InstallmentControllerBase
{
    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Index(ReceiptListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new ReceiptListViewModel
        {
            Filter = filter, Receipts = await receipts.ListAsync(filter, ct),
            Projects = (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items,
        });
    }

    [HasPermission(Permissions.PaymentsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(new ReceiptDetailsViewModel { Receipt = await receipts.GetAsync(id, ct) });

    /// <summary>The first download is the original; every later one is a numbered duplicate copy. Each download is counted and audited.</summary>
    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.PaymentsReceipts)]
    public async Task<IActionResult> Pdf(long id, CancellationToken ct)
    {
        var f = await receipts.PdfAsync(id, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }
}
