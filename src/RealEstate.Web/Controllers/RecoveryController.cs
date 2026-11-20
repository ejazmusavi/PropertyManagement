using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Application.Recovery;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

public class RecoveryController(RecoveryService recovery, PaymentService payments, PaymentSupport support, ProjectService projects) : InstallmentControllerBase
{
    // ----- dashboard and reports -----
    [HasPermission(Permissions.RecoveryView)]
    public async Task<IActionResult> Index(long? projectId, CancellationToken ct) =>
        View(new RecoveryDashboardViewModel { ProjectId = projectId, Dashboard = await recovery.DashboardAsync(projectId, ct), Projects = await ProjectsAsync(ct) });

    [Authorize]
    public async Task<IActionResult> Overdue(DueListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        var result = await recovery.OverdueAsync(filter, ct);
        return View(new OverdueViewModel { Filter = filter, Result = result, Projects = await ProjectsAsync(ct), Officers = await OfficersFilterAsync(ct), CurrencySymbol = await SymbolAsync(ct) });
    }

    [Authorize]
    public async Task<IActionResult> Upcoming(DueListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new UpcomingViewModel { Filter = filter, Result = await recovery.UpcomingAsync(filter, ct), Projects = await ProjectsAsync(ct), CurrencySymbol = await SymbolAsync(ct) });
    }

    [Authorize]
    public async Task<IActionResult> Defaulters(DefaulterListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new DefaultersViewModel { Filter = filter, Defaulters = await recovery.DefaultersAsync(filter, ct), Projects = await ProjectsAsync(ct), CurrencySymbol = await SymbolAsync(ct) });
    }

    [HasPermission(Permissions.RecoveryView)]
    public async Task<IActionResult> Performance(DateOnly? from, DateOnly? to, long? projectId, CancellationToken ct) =>
        View(new PerformanceViewModel
        {
            From = from, To = to, ProjectId = projectId, Officers = await recovery.OfficerPerformanceAsync(new OfficerPerformanceRequest(from, to, projectId), ct),
            Projects = await ProjectsAsync(ct), CurrencySymbol = await SymbolAsync(ct),
        });

    // ----- assignments -----
    [Authorize]
    public async Task<IActionResult> Assignments(AssignmentListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new AssignmentsViewModel
        {
            Filter = filter, Assignments = await recovery.ListAssignmentsAsync(filter, ct), Projects = await ProjectsAsync(ct), Officers = await OfficersFilterAsync(ct), CurrencySymbol = await SymbolAsync(ct),
        });
    }

    [HttpGet, HasPermission(Permissions.RecoveryAssign)]
    public async Task<IActionResult> Assign(long? agreementId, string? term, CancellationToken ct)
    {
        var m = new AssignFormViewModel { AgreementId = agreementId ?? 0, Term = term };
        return View(await FillAssignAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RecoveryAssign)]
    public async Task<IActionResult> Assign(AssignFormViewModel m, CancellationToken ct)
    {
        if (m.AgreementId <= 0) ModelState.AddModelError(nameof(m.AgreementId), "Select an agreement first.");
        if (m.OfficerUserId == Guid.Empty) ModelState.AddModelError(nameof(m.OfficerUserId), "Select the recovery officer.");
        if (ModelState.IsValid)
        {
            try
            {
                var a = await recovery.AssignAsync(new CreateAssignmentRequest(m.AgreementId, m.OfficerUserId, m.Notes), ct);
                TempData["Success"] = $"Agreement {a.AgreementNumber} assigned to {a.OfficerName}.";
                return RedirectToAction(nameof(Assignments));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await FillAssignAsync(m, ct));
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RecoveryAssign)]
    public async Task<IActionResult> Close(long id, string? reason, string? rowVersion, CancellationToken ct)
    {
        try { await recovery.CloseAssignmentAsync(id, new CloseAssignmentRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "Assignment closed."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Assignments));
    }

    // ----- follow-ups -----
    [Authorize]
    public async Task<IActionResult> FollowUps(FollowUpListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new FollowUpsViewModel
        {
            Filter = filter, FollowUps = await recovery.ListFollowUpsAsync(filter, ct), Projects = await ProjectsAsync(ct), Officers = await OfficersFilterAsync(ct), CurrencySymbol = await SymbolAsync(ct),
        });
    }

    [HttpGet, HasPermission(Permissions.RecoveryFollowUp)]
    public async Task<IActionResult> RecordFollowUp(long? agreementId, string? term, CancellationToken ct) =>
        View(await FillFollowUpAsync(new FollowUpFormViewModel { AgreementId = agreementId ?? 0, Term = term }, ct));

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.RecoveryFollowUp)]
    public async Task<IActionResult> RecordFollowUp(FollowUpFormViewModel m, CancellationToken ct)
    {
        if (m.AgreementId <= 0) ModelState.AddModelError(nameof(m.AgreementId), "Select an agreement first.");
        if (m.Outcome != FollowUpOutcome.PromiseToPay) { m.PromisedAmount = null; m.PromisedDate = null; foreach (var k in new[] { "PromisedAmount", "PromisedDate" }) ModelState.Remove(k); }
        if (ModelState.IsValid)
        {
            try
            {
                var f = await recovery.CreateFollowUpAsync(m.ToRequest(), ct);
                TempData["Success"] = $"Follow-up recorded for agreement {f.AgreementNumber}.";
                return RedirectToAction(nameof(FollowUps), new { agreementId = f.AgreementId });
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(await FillFollowUpAsync(m, ct));
    }

    // ----- helpers -----
    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectsAsync(CancellationToken ct) =>
        await OrAsync<IReadOnlyList<ProjectListItemDto>>(async () => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items, []);

    private Task<string> SymbolAsync(CancellationToken ct) => support.CurrencySymbolAsync(ct);

    /// <summary>Officers who currently hold active assignments (used by the officer filters).</summary>
    private async Task<IReadOnlyList<OfficerOptionDto>> OfficersFilterAsync(CancellationToken ct)
    {
        var active = await OrAsync(async () => await recovery.ListAssignmentsAsync(new AssignmentListRequest { Status = RecoveryAssignmentStatus.Active, PageSize = 100 }, ct), null);
        return active?.Items.GroupBy(a => a.OfficerUserId).Select(g => new OfficerOptionDto(g.Key, g.First().OfficerName)).OrderBy(o => o.FullName).ToList() ?? [];
    }

    private async Task<(AgreementLookupDto? Agreement, IReadOnlyList<AgreementLookupDto> Matches, string Symbol, decimal Overdue)> PickAgreementAsync(long agreementId, string? term, CancellationToken ct)
    {
        if (agreementId > 0)
        {
            var ctx = await payments.CollectionContextAsync(agreementId, ct);
            var primary = ctx.Parties.FirstOrDefault(p => p.IsPrimary) ?? ctx.Parties.FirstOrDefault();
            return (new AgreementLookupDto(ctx.AgreementId, ctx.AgreementNumber, ctx.BookingNumber, ctx.ProjectCode, primary?.CustomerId ?? 0, primary?.FullName ?? "", primary?.CustomerCode ?? ""), [], ctx.CurrencySymbol, ctx.Overdue);
        }
        return (null, string.IsNullOrWhiteSpace(term) ? [] : await OrAsync<IReadOnlyList<AgreementLookupDto>>(async () => await payments.AgreementLookupAsync(term, ct), []), "", 0m);
    }

    private async Task<AssignFormViewModel> FillAssignAsync(AssignFormViewModel m, CancellationToken ct)
    {
        var (agreement, matches, symbol, overdue) = await PickAgreementAsync(m.AgreementId, m.Term, ct);
        m.Agreement = agreement; m.Matches = matches; m.CurrencySymbol = symbol; m.Overdue = overdue; m.Searched = !string.IsNullOrWhiteSpace(m.Term);
        if (agreement is not null) m.Officers = await recovery.OfficersAsync(agreement.AgreementId, ct);
        return m;
    }

    private async Task<FollowUpFormViewModel> FillFollowUpAsync(FollowUpFormViewModel m, CancellationToken ct)
    {
        var (agreement, matches, symbol, overdue) = await PickAgreementAsync(m.AgreementId, m.Term, ct);
        m.Agreement = agreement; m.Matches = matches; m.CurrencySymbol = symbol; m.Overdue = overdue; m.Searched = !string.IsNullOrWhiteSpace(m.Term);
        return m;
    }
}
