using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Installments;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Installment plan templates and their components.</summary>
[ApiController, Authorize]
[Route("api/v1/installment-plans")]
public class InstallmentPlansController(InstallmentPlanService plans) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<InstallmentPlanListItemDto>>> List([FromQuery] InstallmentPlanListRequest request, CancellationToken ct) => await plans.ListAsync(request, ct);

    /// <summary>Creates a Draft plan (optionally with its components). Activate it to make it applicable to agreements.</summary>
    [HttpPost, HasPermission(Permissions.InstallmentsManage)]
    [ProducesResponseType<InstallmentPlanDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateInstallmentPlanRequest request, CancellationToken ct)
    {
        var created = await plans.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> Get(long id, CancellationToken ct) => await plans.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> Update(long id, UpdateInstallmentPlanRequest request, CancellationToken ct) => await plans.UpdateAsync(id, request, ct);

    [HttpPost("{id:long}/components"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> AddComponent(long id, PlanComponentRequest request, CancellationToken ct) => await plans.AddComponentAsync(id, request, ct);

    [HttpPut("{id:long}/components/{componentId:long}"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> UpdateComponent(long id, long componentId, PlanComponentRequest request, CancellationToken ct) =>
        await plans.UpdateComponentAsync(id, componentId, request, ct);

    [HttpDelete("{id:long}/components/{componentId:long}"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> RemoveComponent(long id, long componentId, [FromQuery] string? rowVersion, CancellationToken ct) =>
        await plans.RemoveComponentAsync(id, componentId, new PlanRowVersionRequest(rowVersion), ct);

    [HttpPost("{id:long}/activate"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> Activate(long id, PlanRowVersionRequest request, CancellationToken ct) => await plans.ActivateAsync(id, request, ct);

    [HttpPost("{id:long}/retire"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> Retire(long id, RetirePlanRequest request, CancellationToken ct) => await plans.RetireAsync(id, request, ct);

    [HttpPost("{id:long}/copy"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<InstallmentPlanDetailDto>> Copy(long id, CopyPlanRequest request, CancellationToken ct) => await plans.CopyAsync(id, request, ct);

    /// <summary>Resolves the plan for a sample price and returns the schedule it would generate (nothing is stored).</summary>
    [HttpPost("{id:long}/preview"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PlanPreviewDto>> Preview(long id, PlanPreviewRequest request, CancellationToken ct) => await plans.PreviewAsync(id, request, ct);
}

/// <summary>Charge type catalog and late fee rules.</summary>
[ApiController, Authorize]
[Route("api/v1")]
public class InstallmentSetupController(InstallmentSetupService setup) : ControllerBase
{
    [HttpGet("charge-types"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<IReadOnlyList<ChargeTypeDto>>> ChargeTypes([FromQuery] bool activeOnly, CancellationToken ct) => Ok(await setup.ListChargeTypesAsync(activeOnly, ct));

    [HttpPost("charge-types"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<ChargeTypeDto>> CreateChargeType(SaveChargeTypeRequest request, CancellationToken ct) => await setup.CreateChargeTypeAsync(request, ct);

    [HttpPut("charge-types/{id:long}"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<ChargeTypeDto>> UpdateChargeType(long id, SaveChargeTypeRequest request, CancellationToken ct) => await setup.UpdateChargeTypeAsync(id, request, ct);

    [HttpGet("late-fee-rules"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<LateFeeRuleDto>>> LateFeeRules([FromQuery] LateFeeRuleListRequest request, CancellationToken ct) => await setup.ListLateFeeRulesAsync(request, ct);

    [HttpGet("late-fee-rules/{id:long}"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<LateFeeRuleDto>> LateFeeRule(long id, CancellationToken ct) => await setup.GetLateFeeRuleAsync(id, ct);

    [HttpPost("late-fee-rules"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<LateFeeRuleDto>> CreateLateFeeRule(SaveLateFeeRuleRequest request, CancellationToken ct) => await setup.CreateLateFeeRuleAsync(request, ct);

    [HttpPut("late-fee-rules/{id:long}"), HasPermission(Permissions.InstallmentsManage)]
    public async Task<ActionResult<LateFeeRuleDto>> UpdateLateFeeRule(long id, SaveLateFeeRuleRequest request, CancellationToken ct) => await setup.UpdateLateFeeRuleAsync(id, request, ct);

    [HttpPost("late-fee-rules/preview"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<LateFeePreviewDto>> PreviewLateFee(LateFeePreviewRequest request, CancellationToken ct) => await setup.PreviewLateFeeAsync(request, ct);
}

/// <summary>Agreement installment structure, payment schedules, amendments and history. {id} is the customer agreement id.</summary>
[ApiController, Authorize]
[Route("api/v1/agreements")]
public class AgreementsController(AgreementScheduleService schedules, AgreementAmendmentService amendments, InstallmentPlanService plans) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<InstallmentAgreementListItemDto>>> List([FromQuery] InstallmentAgreementListRequest request, CancellationToken ct) => await schedules.ListAsync(request, ct);

    [HttpGet("{id:long}"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<AgreementInstallmentDto>> Get(long id, CancellationToken ct) => await schedules.GetAsync(id, ct);

    [HttpGet("{id:long}/applicable-plans"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<IReadOnlyList<InstallmentPlanListItemDto>>> ApplicablePlans(long id, CancellationToken ct) => Ok(await plans.ApplicablePlansAsync(id, ct));

    /// <summary>Replaces the agreement's (not yet scheduled) charges with the resolved components of an active plan.</summary>
    [HttpPost("{id:long}/apply-plan"), HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<ActionResult<AgreementInstallmentDto>> ApplyPlan(long id, ApplyPlanRequest request, CancellationToken ct) => await schedules.ApplyPlanAsync(id, request, ct);

    [HttpPost("{id:long}/charges"), HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<ActionResult<AgreementInstallmentDto>> AddCharge(long id, SaveAgreementChargeRequest request, CancellationToken ct) => await schedules.AddChargeAsync(id, request, ct);

    [HttpPut("{id:long}/charges/{chargeId:long}"), HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<ActionResult<AgreementInstallmentDto>> UpdateCharge(long id, long chargeId, SaveAgreementChargeRequest request, CancellationToken ct) => await schedules.UpdateChargeAsync(id, chargeId, request, ct);

    [HttpDelete("{id:long}/charges/{chargeId:long}"), HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<ActionResult<AgreementInstallmentDto>> RemoveCharge(long id, long chargeId, [FromQuery] string? rowVersion, CancellationToken ct) =>
        await schedules.RemoveChargeAsync(id, chargeId, new AgreementRowVersionRequest(rowVersion), ct);

    [HttpGet("{id:long}/schedule-preview"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<SchedulePreviewDto>> SchedulePreview(long id, CancellationToken ct) => await schedules.PreviewAsync(id, ct);

    /// <summary>Generates payment schedule version 1 from the agreement charges. The schedule must reconcile with the agreement total.</summary>
    [HttpPost("{id:long}/generate-schedule"), HasPermission(Permissions.InstallmentsSchedule)]
    public async Task<ActionResult<PaymentScheduleDto>> GenerateSchedule(long id, GenerateScheduleRequest request, CancellationToken ct) => await schedules.GenerateAsync(id, request, ct);

    /// <summary>The active schedule, or a specific version (?version=n) for history.</summary>
    [HttpGet("{id:long}/schedule"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PaymentScheduleDto>> Schedule(long id, [FromQuery] int? version, CancellationToken ct) => await schedules.GetScheduleAsync(id, version, ct);

    [HttpGet("{id:long}/history"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<AgreementHistoryDto>> History(long id, CancellationToken ct) => await schedules.GetHistoryAsync(id, ct);

    [HttpGet("{id:long}/amendments"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<AmendmentListItemDto>>> Amendments(long id, [FromQuery] AmendmentListRequest request, CancellationToken ct)
    {
        request.AgreementId = id;
        return await amendments.ListAsync(request, ct);
    }

    /// <summary>Requests an amendment (Pending). It is applied only when another user approves it.</summary>
    [HttpPost("{id:long}/amendments"), HasPermission(Permissions.AgreementsAmend)]
    [ProducesResponseType<AmendmentDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAmendment(long id, CreateAmendmentRequest request, CancellationToken ct)
    {
        var created = await amendments.CreateAsync(id, request, ct);
        return CreatedAtAction(nameof(Amendment), new { id, amendmentId = created.Id }, created);
    }

    [HttpGet("{id:long}/amendments/{amendmentId:long}"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<AmendmentDetailDto>> Amendment(long id, long amendmentId, CancellationToken ct) => await amendments.GetAsync(id, amendmentId, ct);

    [HttpPost("{id:long}/amendments/{amendmentId:long}/approve"), HasPermission(Permissions.AgreementsAmendApprove)]
    public async Task<ActionResult<AmendmentDetailDto>> ApproveAmendment(long id, long amendmentId, DecideAmendmentRequest request, CancellationToken ct) =>
        await amendments.ApproveAsync(id, amendmentId, request, ct);

    [HttpPost("{id:long}/amendments/{amendmentId:long}/reject"), HasPermission(Permissions.AgreementsAmendApprove)]
    public async Task<ActionResult<AmendmentDetailDto>> RejectAmendment(long id, long amendmentId, RejectAmendmentRequest request, CancellationToken ct) =>
        await amendments.RejectAsync(id, amendmentId, request, ct);

    [HttpPost("{id:long}/amendments/{amendmentId:long}/cancel"), HasPermission(Permissions.AgreementsAmend)]
    public async Task<ActionResult<AmendmentDetailDto>> CancelAmendment(long id, long amendmentId, DecideAmendmentRequest request, CancellationToken ct) =>
        await amendments.CancelAsync(id, amendmentId, request, ct);
}

/// <summary>Cross-agreement amendment and waiver queues (approvals).</summary>
[ApiController, Authorize]
[Route("api/v1")]
public class AmendmentQueueController(AgreementAmendmentService amendments) : ControllerBase
{
    [HttpGet("amendments"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<AmendmentListItemDto>>> Amendments([FromQuery] AmendmentListRequest request, CancellationToken ct) => await amendments.ListAsync(request, ct);

    [HttpGet("waivers"), HasPermission(Permissions.InstallmentsView)]
    public async Task<ActionResult<PagedResult<WaiverDto>>> Waivers([FromQuery] WaiverStatus? status, [FromQuery] long? projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        await amendments.ListWaiversAsync(status, projectId, page, pageSize, ct);
}
