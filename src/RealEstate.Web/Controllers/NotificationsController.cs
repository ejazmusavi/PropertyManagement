using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Jobs;
using RealEstate.Application.Notifications;
using RealEstate.Application.Projects;
using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Identity;
using RealEstate.Web.Filters;
using RealEstate.Web.ViewModels;

namespace RealEstate.Web.Controllers;

/// <summary>Notification history and delivery logs, manual sending, templates, reminder settings and a customer's channel choices.</summary>
public class NotificationsController(NotificationService notifications, NotificationPreferenceService preferences, CustomerService customers, ProjectService projects) : InstallmentControllerBase
{
    [HasPermission(Permissions.NotificationsView)]
    public async Task<IActionResult> Index(NotificationListRequest filter, CancellationToken ct)
    {
        filter.PageSize = 20;
        return View(new NotificationListViewModel { Filter = filter, Result = await notifications.ListAsync(filter, ct), Summary = await notifications.SummaryAsync(ct) });
    }

    [HasPermission(Permissions.NotificationsView)]
    public async Task<IActionResult> Details(long id, CancellationToken ct) => View(new NotificationDetailsViewModel { Detail = await notifications.GetAsync(id, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> Retry(long deliveryId, long id, CancellationToken ct)
    {
        try { await notifications.RetryDeliveryAsync(deliveryId, ct); TempData["Success"] = "The delivery was queued for another attempt."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> Cancel(long id, string? reason, string? rowVersion, CancellationToken ct)
    {
        try { await notifications.CancelAsync(id, new CancelNotificationRequest(reason ?? "", rowVersion), ct); TempData["Success"] = "The notification was cancelled."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Details), new { id });
    }

    // ----- send -----
    [HttpGet, HasPermission(Permissions.NotificationsSend)]
    public async Task<IActionResult> Send(CancellationToken ct) => View(new SendNotificationViewModel { Projects = await ProjectListAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsSend)]
    public async Task<IActionResult> Send(SendNotificationViewModel m, CancellationToken ct)
    {
        if (m.Audience == "customer" && m.CustomerId is null) ModelState.AddModelError(nameof(m.CustomerId), "Enter the customer id.");
        if (m.Audience == "project" && m.ProjectId is null) ModelState.AddModelError(nameof(m.ProjectId), "Choose a project.");
        if (m.Audience == "property" && m.PropertyId is null) ModelState.AddModelError(nameof(m.PropertyId), "Enter the property id.");
        if (ModelState.IsValid)
        {
            try
            {
                var r = await notifications.SendAsync(new SendNotificationRequest(m.Type, m.Audience == "customer" ? m.CustomerId : null, m.Audience == "project" ? m.ProjectId : null,
                    m.Audience == "property" ? m.PropertyId : null, m.Title ?? "", m.Message ?? "", m.IdempotencyKey), ct);
                TempData["Success"] = $"Queued for {r.Queued} of {r.Recipients} recipient(s)" + (r.Duplicates > 0 ? $" ({r.Duplicates} already sent)." : ".");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        m.Projects = await ProjectListAsync(ct);
        return View(m);
    }

    // ----- templates -----
    [HasPermission(Permissions.NotificationsView)]
    public async Task<IActionResult> Templates(CancellationToken ct) => View(new TemplateListViewModel { Templates = await notifications.ListTemplatesAsync(ct) });

    [HttpGet, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> TemplateForm(long id, CancellationToken ct)
    {
        var t = await notifications.GetTemplateAsync(id, ct);
        return View(new TemplateFormViewModel { Type = t.Type, Channel = t.Channel, Language = t.Language, Subject = t.Subject, Body = t.Body, IsActive = t.IsActive, RowVersion = t.RowVersion });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> TemplateForm(long id, TemplateFormViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await notifications.SaveTemplateAsync(new SaveTemplateRequest(m.Type, m.Channel, m.Language, m.Subject ?? "", m.Body ?? "", m.IsActive, m.RowVersion), ct);
                TempData["Success"] = "The template was saved.";
                return RedirectToAction(nameof(Templates));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(m);
    }

    // ----- settings -----
    [HttpGet, HasPermission(Permissions.NotificationsView)]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var s = await notifications.GetSettingsAsync(ct);
        return View(new ReminderSettingsViewModel { UpcomingDaysBefore = s.UpcomingDaysBefore, OverdueRepeatDays = s.OverdueRepeatDays, EventLookbackDays = s.EventLookbackDays });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> Settings(ReminderSettingsViewModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await notifications.SaveSettingsAsync(new ReminderSettingsDto(m.UpcomingDaysBefore, m.OverdueRepeatDays, m.EventLookbackDays), ct);
                TempData["Success"] = "The reminder settings were saved.";
                return RedirectToAction(nameof(Settings));
            }
            catch (Exception ex) when (ModelState.TryAddError(ex)) { }
        }
        return View(m);
    }

    // ----- a customer's channel choices -----
    [HttpGet, HasPermission(Permissions.NotificationsView)]
    public async Task<IActionResult> CustomerPreferences(long id, CancellationToken ct)
    {
        var c = await customers.GetAsync(id, ct);
        return View(new PreferencesViewModel { Title = $"Notification channels - {c.FullName}", Subtitle = c.CustomerCode, CustomerId = id, FormAction = nameof(CustomerPreferences), Cells = await preferences.GetForCustomerAsync(id, ct) });
    }

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.NotificationsManage)]
    public async Task<IActionResult> CustomerPreferences(long id, string[]? enabled, CancellationToken ct)
    {
        try
        {
            var current = await preferences.GetForCustomerAsync(id, ct);
            await preferences.SaveForCustomerAsync(id, PreferenceForm.ToRequest(current, enabled), ct);
            TempData["Success"] = "The notification channels were saved.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(CustomerPreferences), new { id });
    }

    private async Task<IReadOnlyList<ProjectListItemDto>> ProjectListAsync(CancellationToken ct) => (await projects.ListAsync(new ProjectListRequest { PageSize = 100 }, ct)).Items;
}

/// <summary>The signed-in staff user's own notifications and daily-summary subscription.</summary>
public class MyNotificationsController(NotificationService notifications, NotificationPreferenceService preferences) : InstallmentControllerBase
{
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default) =>
        View(new MyNotificationsViewModel { Inbox = await notifications.InboxAsync(new PagedRequest { Page = page, PageSize = 20 }, ct), Preferences = await preferences.GetMineAsStaffAsync(ct) });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(long id, CancellationToken ct)
    {
        try { await notifications.MarkInboxReadAsync(id, ct); }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePreferences(string[]? enabled, CancellationToken ct)
    {
        try
        {
            var current = await preferences.GetMineAsStaffAsync(ct);
            await preferences.SaveMineAsStaffAsync(PreferenceForm.ToRequest(current, enabled), ct);
            TempData["Success"] = "Your daily summary choices were saved.";
        }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Background jobs: catalogue, last runs, run now, history.</summary>
public class JobsController(ScheduledJobService jobs, IBackgroundJobGateway scheduler) : InstallmentControllerBase
{
    [HasPermission(Permissions.JobsView)]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new JobsViewModel { Jobs = await jobs.ListAsync(ct), SchedulerEnabled = scheduler.Enabled });

    [HasPermission(Permissions.JobsView)]
    public async Task<IActionResult> Runs(string? key, int page = 1, CancellationToken ct = default) =>
        View(new JobRunsViewModel { Key = key, Runs = await jobs.RunsAsync(key, new PagedRequest { Page = page, PageSize = 25 }, ct) });

    [HttpPost, ValidateAntiForgeryToken, HasPermission(Permissions.JobsManage)]
    public async Task<IActionResult> Run(string key, CancellationToken ct)
    {
        try { await jobs.RunNowAsync(key, ct); TempData["Success"] = "The job was queued; see the run history for the result."; }
        catch (Exception ex) when (Soft(ex)) { TempData["Error"] = Message(ex); }
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Maps the checkbox grid of the preference screens to a save request (checked cells = enabled; every listed cell is saved).</summary>
public static class PreferenceForm
{
    public static string CellKey(PreferenceDto c) => $"{c.Channel}:{(c.Type is { } t ? (int)t : 0)}";

    public static SavePreferencesRequest ToRequest(IReadOnlyList<PreferenceDto> cells, string[]? enabled)
    {
        var on = (enabled ?? []).ToHashSet();
        return new SavePreferencesRequest([.. cells.Where(c => !c.Locked).Select(c => new SavePreferenceRequest(c.Channel, c.Type, on.Contains(CellKey(c))))]);
    }
}
