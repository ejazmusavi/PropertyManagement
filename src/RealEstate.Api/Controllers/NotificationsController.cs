using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Jobs;
using RealEstate.Application.Notifications;
using RealEstate.Domain.Common;
using RealEstate.Infrastructure.Identity;

namespace RealEstate.Api.Controllers;

/// <summary>Notification history and delivery logs, templates, reminder settings, manual sending.</summary>
[ApiController, Authorize]
[Route("api/v1/notifications")]
public class NotificationsController(NotificationService notifications) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<PagedResult<NotificationListItemDto>>> List([FromQuery] NotificationListRequest request, CancellationToken ct) => await notifications.ListAsync(request, ct);

    [HttpGet("summary"), HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<NotificationSummaryDto>> Summary(CancellationToken ct) => await notifications.SummaryAsync(ct);

    [HttpGet("{id:long}"), HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<NotificationDetailDto>> Get(long id, CancellationToken ct) => await notifications.GetAsync(id, ct);

    /// <summary>Sends an announcement, possession notice or message to a customer, a project's owners, a property's owners or all customers.</summary>
    [HttpPost("send"), HasPermission(Permissions.NotificationsSend)]
    public async Task<ActionResult<SendNotificationResultDto>> Send(SendNotificationRequest request, CancellationToken ct) => await notifications.SendAsync(request, ct);

    [HttpPost("{id:long}/cancel"), HasPermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<NotificationDetailDto>> Cancel(long id, CancelNotificationRequest request, CancellationToken ct) => await notifications.CancelAsync(id, request, ct);

    [HttpPost("deliveries/{deliveryId:long}/retry"), HasPermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<NotificationDetailDto>> Retry(long deliveryId, CancellationToken ct) => await notifications.RetryDeliveryAsync(deliveryId, ct);

    // ----- templates -----
    [HttpGet("templates"), HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<IReadOnlyList<TemplateDto>>> Templates(CancellationToken ct) => Ok(await notifications.ListTemplatesAsync(ct));

    [HttpGet("templates/{id:long}"), HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<TemplateDto>> Template(long id, CancellationToken ct) => await notifications.GetTemplateAsync(id, ct);

    [HttpPut("templates"), HasPermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<TemplateDto>> SaveTemplate(SaveTemplateRequest request, CancellationToken ct) => await notifications.SaveTemplateAsync(request, ct);

    // ----- settings -----
    [HttpGet("settings"), HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<ReminderSettingsDto>> Settings(CancellationToken ct) => await notifications.GetSettingsAsync(ct);

    [HttpPut("settings"), HasPermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<ReminderSettingsDto>> SaveSettings(ReminderSettingsDto request, CancellationToken ct) => await notifications.SaveSettingsAsync(request, ct);
}

/// <summary>Notifications addressed to the signed-in staff user (the daily summary) and their own channel choices.</summary>
[ApiController, Authorize]
[Route("api/v1/me/notifications")]
public class MyNotificationsController(NotificationService notifications, NotificationPreferenceService preferences) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<StaffInboxItemDto>>> Inbox([FromQuery] PagedRequest request, CancellationToken ct) => await notifications.InboxAsync(request, ct);

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken ct)
    {
        await notifications.MarkInboxReadAsync(id, ct);
        return NoContent();
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<IReadOnlyList<PreferenceDto>>> Preferences(CancellationToken ct) => Ok(await preferences.GetMineAsStaffAsync(ct));

    [HttpPut("preferences")]
    public async Task<ActionResult<IReadOnlyList<PreferenceDto>>> SavePreferences(SavePreferencesRequest request, CancellationToken ct) => Ok(await preferences.SaveMineAsStaffAsync(request, ct));
}

/// <summary>Office view of a customer's channel choices (and edits on the customer's behalf).</summary>
[ApiController, Authorize]
[Route("api/v1/customers/{customerId:long}/notification-preferences")]
public class CustomerNotificationPreferencesController(NotificationPreferenceService preferences) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.NotificationsView)]
    public async Task<ActionResult<IReadOnlyList<PreferenceDto>>> Get(long customerId, CancellationToken ct) => Ok(await preferences.GetForCustomerAsync(customerId, ct));

    [HttpPut, HasPermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<IReadOnlyList<PreferenceDto>>> Save(long customerId, SavePreferencesRequest request, CancellationToken ct) => Ok(await preferences.SaveForCustomerAsync(customerId, request, ct));
}

/// <summary>Background jobs: catalogue with last run, run history, run now.</summary>
[ApiController, Authorize]
[Route("api/v1/jobs")]
public class JobsController(ScheduledJobService jobs) : ControllerBase
{
    [HttpGet, HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<IReadOnlyList<JobDto>>> List(CancellationToken ct) => Ok(await jobs.ListAsync(ct));

    [HttpGet("runs"), HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<PagedResult<JobRunDto>>> Runs([FromQuery] string? key, [FromQuery] PagedRequest request, CancellationToken ct) => await jobs.RunsAsync(key, request, ct);

    /// <summary>Queues one immediate run (with the scheduler's retry policy). Returns 202 with the scheduler's job id.</summary>
    [HttpPost("{key}/run"), HasPermission(Permissions.JobsManage)]
    public async Task<IActionResult> Run(string key, CancellationToken ct) => Accepted(new { jobId = await jobs.RunNowAsync(key, ct) });
}
