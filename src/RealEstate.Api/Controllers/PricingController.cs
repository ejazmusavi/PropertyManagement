using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Pricing;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Pricing rules, discounts, rate cards, calculation, published property prices, price history and pricing approvals.</summary>
[ApiController, Authorize]
[Route("api/v1")]
public class PricingController(PricingRuleService rules, PricingCalculationService calculator, PropertyPricingService prices, PricingApprovalService approvals) : ControllerBase
{
    // ----- rules -----
    [HttpGet("pricing/rules"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PagedResult<PricingRuleDto>>> Rules([FromQuery] PricingRuleListRequest request, CancellationToken ct) => await rules.ListRulesAsync(request, ct);

    /// <summary>Creates a pricing rule. Customer-scope rules start as PendingApproval and an approval request is created automatically.</summary>
    [HttpPost("pricing/rules"), HasPermission(Permissions.PricingManage)]
    [ProducesResponseType<PricingRuleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRule(CreatePricingRuleRequest request, CancellationToken ct)
    {
        var created = await rules.CreateRuleAsync(request, ct);
        return CreatedAtAction(nameof(Rule), new { id = created.Id }, created);
    }

    [HttpGet("pricing/rules/{id:long}"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PricingRuleDto>> Rule(long id, CancellationToken ct) => await rules.GetRuleAsync(id, ct);

    [HttpPut("pricing/rules/{id:long}"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<PricingRuleDto>> UpdateRule(long id, UpdateRuleDetailsRequest request, CancellationToken ct) => await rules.UpdateRuleDetailsAsync(id, request, ct);

    [HttpPost("pricing/rules/{id:long}/retire"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<PricingRuleDto>> RetireRule(long id, RetireRequest request, CancellationToken ct) => await rules.RetireRuleAsync(id, request, ct);

    [HttpGet("pricing/rate-cards/{projectId:long}"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<RateCardDto>> RateCard(long projectId, [FromQuery] DateOnly? asOf, CancellationToken ct) => await rules.GetRateCardAsync(projectId, asOf, ct);

    // ----- discounts -----
    [HttpGet("pricing/discounts"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PagedResult<DiscountRuleDto>>> Discounts([FromQuery] DiscountListRequest request, CancellationToken ct) => await rules.ListDiscountsAsync(request, ct);

    [HttpPost("pricing/discounts"), HasPermission(Permissions.PricingManage)]
    [ProducesResponseType<DiscountRuleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateDiscount(CreateDiscountRuleRequest request, CancellationToken ct)
    {
        var created = await rules.CreateDiscountAsync(request, ct);
        return CreatedAtAction(nameof(Discount), new { id = created.Id }, created);
    }

    [HttpGet("pricing/discounts/{id:long}"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<DiscountRuleDto>> Discount(long id, CancellationToken ct) => await rules.GetDiscountAsync(id, ct);

    [HttpPut("pricing/discounts/{id:long}"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<DiscountRuleDto>> UpdateDiscount(long id, UpdateRuleDetailsRequest request, CancellationToken ct) => await rules.UpdateDiscountDetailsAsync(id, request, ct);

    [HttpPost("pricing/discounts/{id:long}/retire"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<DiscountRuleDto>> RetireDiscount(long id, RetireRequest request, CancellationToken ct) => await rules.RetireDiscountAsync(id, request, ct);

    // ----- calculation and property prices -----
    /// <summary>Calculates a property's price (base, size-based, premiums, discounts, charges, final, applied rules). Pure read: nothing is stored.</summary>
    [HttpPost("pricing/calculate"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PriceCalculationDto>> Calculate(CalculatePriceRequest request, CancellationToken ct) => await calculator.CalculateAsync(request, ct);

    /// <summary>Calculation with canonical JSON and SHA-256 hash: the immutable snapshot a booking stores.</summary>
    [HttpPost("pricing/snapshot"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PriceSnapshotDto>> Snapshot(CalculatePriceRequest request, CancellationToken ct) => await calculator.CreateSnapshotAsync(request, ct);

    [HttpGet("properties/{id:long}/price"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PropertyPriceViewDto>> Price(long id, [FromQuery] long? customerId, [FromQuery] DateOnly? asOf, CancellationToken ct) => await prices.GetPriceAsync(id, customerId, asOf, ct);

    [HttpPost("properties/{id:long}/price/publish"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<PublishOutcomeDto>> Publish(long id, PublishPriceRequest request, CancellationToken ct) => await prices.PublishAsync(id, request, ct);

    [HttpPost("projects/{projectId:long}/pricing/publish"), HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<PublishProjectResultDto>> PublishProject(long projectId, PublishPriceRequest request, CancellationToken ct) => await prices.PublishProjectAsync(projectId, request, ct);

    [HttpGet("pricing/prices"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PagedResult<PropertyPriceListItemDto>>> PropertyPrices([FromQuery] PropertyPriceListRequest request, CancellationToken ct) => await prices.ListPropertyPricesAsync(request, ct);

    [HttpGet("pricing/history"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PagedResult<PriceHistoryDto>>> History([FromQuery] PriceHistoryRequest request, CancellationToken ct) => await prices.ListHistoryAsync(request, ct);

    [HttpGet("pricing/history/{id:long}/snapshot"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PriceSnapshotDto>> HistorySnapshot(long id, CancellationToken ct) => await prices.GetHistorySnapshotAsync(id, ct);

    [HttpGet("properties/{id:long}/price-adjustments"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<IReadOnlyList<AdjustmentDto>>> Adjustments(long id, CancellationToken ct) => Ok(await prices.ListAdjustmentsAsync(id, ct));

    [HttpPost("pricing/adjustments/{id:long}/revoke"), HasPermission(Permissions.PricingApprove)]
    public async Task<ActionResult<AdjustmentDto>> RevokeAdjustment(long id, RetireRequest request, CancellationToken ct) => await prices.RevokeAdjustmentAsync(id, request, ct);

    // ----- approvals -----
    [HttpGet("pricing/approvals"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<PagedResult<ApprovalDto>>> Approvals([FromQuery] ApprovalListRequest request, CancellationToken ct) => await approvals.ListAsync(request, ct);

    [HttpPost("pricing/approvals"), HasPermission(Permissions.PricingRequest)]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RequestApproval(RequestPricingApprovalRequest request, CancellationToken ct)
    {
        var created = await approvals.RequestAsync(request, ct);
        return CreatedAtAction(nameof(Approval), new { id = created.Id }, created);
    }

    [HttpGet("pricing/approvals/{id:long}"), HasPermission(Permissions.PricingView)]
    public async Task<ActionResult<ApprovalDto>> Approval(long id, CancellationToken ct) => await approvals.GetAsync(id, ct);

    /// <summary>Approve or reject. The approver must differ from the requester and hold pricing.approve.</summary>
    [HttpPost("pricing/approvals/{id:long}/decision"), HasPermission(Permissions.PricingApprove)]
    public async Task<ActionResult<ApprovalDto>> Decide(long id, DecidePricingApprovalRequest request, CancellationToken ct) => await approvals.DecideAsync(id, request, ct);

    [HttpPost("pricing/approvals/{id:long}/cancel")]
    public async Task<ActionResult<ApprovalDto>> Cancel(long id, CancelPricingApprovalRequest request, CancellationToken ct) => await approvals.CancelAsync(id, request, ct);
}
