using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Gis;
using RealEstate.Application.Jobs;
using RealEstate.Application.Notifications;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

// ----- notifications -----
public class NotificationListViewModel
{
    public NotificationListRequest Filter { get; set; } = new();
    public PagedResult<NotificationListItemDto> Result { get; set; } = new([], 1, 20, 0);
    public NotificationSummaryDto Summary { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
}

public class NotificationDetailsViewModel
{
    public NotificationDetailDto Detail { get; set; } = default!;
}

public class SendNotificationViewModel
{
    [Required] public NotificationType Type { get; set; } = NotificationType.CompanyAnnouncement;
    [Display(Name = "Audience")] public string Audience { get; set; } = "all";
    [Display(Name = "Customer id")] public long? CustomerId { get; set; }
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [Display(Name = "Property id")] public long? PropertyId { get; set; }
    [Required, StringLength(200)] public string? Title { get; set; }
    [Required, StringLength(2000)] public string? Message { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class TemplateListViewModel
{
    public IReadOnlyList<TemplateDto> Templates { get; set; } = [];
}

public class TemplateFormViewModel
{
    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; }
    public string Language { get; set; } = "en";
    [Required, StringLength(200)] public string? Subject { get; set; }
    [Required, StringLength(2000)] public string? Body { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

public class ReminderSettingsViewModel
{
    [Range(0, 60), Display(Name = "Remind this many days before the due date")] public int UpcomingDaysBefore { get; set; } = 3;
    [Range(1, 60), Display(Name = "Repeat overdue reminders every (days)")] public int OverdueRepeatDays { get; set; } = 7;
    [Range(1, 60), Display(Name = "Event scans look back (days)")] public int EventLookbackDays { get; set; } = 7;
}

public class PreferencesViewModel
{
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string FormAction { get; set; } = "";
    public long? CustomerId { get; set; }
    public bool Staff { get; set; }
    public bool CanEdit { get; set; } = true;
    public IReadOnlyList<PreferenceDto> Cells { get; set; } = [];
}

public class MyNotificationsViewModel
{
    public PagedResult<StaffInboxItemDto> Inbox { get; set; } = new([], 1, 20, 0);
    public IReadOnlyList<PreferenceDto> Preferences { get; set; } = [];
}

// ----- jobs -----
public class JobsViewModel
{
    public IReadOnlyList<JobDto> Jobs { get; set; } = [];
    public bool SchedulerEnabled { get; set; }
}

public class JobRunsViewModel
{
    public string? Key { get; set; }
    public PagedResult<JobRunDto> Runs { get; set; } = new([], 1, 20, 0);
}

// ----- GIS -----
public class GisIndexViewModel
{
    public IReadOnlyList<GisSummaryDto> Projects { get; set; } = [];
}

public class GisProjectViewModel
{
    public ProjectDetailDto Project { get; set; } = default!;
    public IReadOnlyList<BoundaryDto> Boundaries { get; set; } = [];
    public IReadOnlyList<MapLayerDto> Layers { get; set; } = [];
    public IReadOnlyList<MapDocumentDto> Documents { get; set; } = [];
    public IReadOnlyList<SrsDto> Systems { get; set; } = [];
}

public class BoundaryFormViewModel
{
    public long ProjectId { get; set; }
    public long? PropertyId { get; set; }
    public string? ProjectCode { get; set; }
    public string? PropertyCode { get; set; }
    [Required, Display(Name = "Coordinate system")] public long SrsId { get; set; }
    [Required, Display(Name = "Geometry (GeoJSON Polygon or MultiPolygon)")] public string? GeometryJson { get; set; }
    [Required, StringLength(100), Display(Name = "Survey reference")] public string? SurveyReference { get; set; }
    [Required, StringLength(200), Display(Name = "Surveyed by")] public string? SurveyedBy { get; set; }
    [Required, Display(Name = "Survey date")] public DateOnly? SurveyDate { get; set; }
    [Display(Name = "Source document")] public long? SourceDocumentId { get; set; }
    [StringLength(1000)] public string? Remarks { get; set; }
    public IReadOnlyList<SrsDto> Systems { get; set; } = [];
    public IReadOnlyList<MapDocumentDto> Documents { get; set; } = [];
}

public class BoundaryDetailsViewModel
{
    public BoundaryDto Boundary { get; set; } = default!;
    public IReadOnlyList<BoundaryDto> History { get; set; } = [];
}

public class PropertyBoundariesViewModel
{
    public long PropertyId { get; set; }
    public string PropertyCode { get; set; } = "";
    public long ProjectId { get; set; }
    public IReadOnlyList<BoundaryDto> Boundaries { get; set; } = [];
}

public class LayerFormViewModel
{
    public long ProjectId { get; set; }
    public string? ProjectCode { get; set; }
    public long? Id { get; set; }
    [Required, StringLength(40)] public string? Code { get; set; }
    [Required, StringLength(200)] public string? Name { get; set; }
    public MapLayerKind Kind { get; set; } = MapLayerKind.Plots;
    [Required, Display(Name = "Coordinate system")] public long SrsId { get; set; }
    [Range(0, 10000), Display(Name = "Sort order")] public int SortOrder { get; set; }
    [Display(Name = "Visible by default")] public bool IsVisibleByDefault { get; set; } = true;
    [StringLength(2000), Display(Name = "Style (JSON object)")] public string? StyleJson { get; set; }
    [Display(Name = "Source document")] public long? SourceDocumentId { get; set; }
    [StringLength(1000)] public string? Description { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
    public IReadOnlyList<SrsDto> Systems { get; set; } = [];
    public IReadOnlyList<MapDocumentDto> Documents { get; set; } = [];
}

public class SrsListViewModel
{
    public IReadOnlyList<SrsDto> Systems { get; set; } = [];
}

public class SrsFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(30)] public string? Code { get; set; }
    [Range(1, 999999)] public int Srid { get; set; }
    [Required, StringLength(200)] public string? Name { get; set; }
    public SpatialCoordinateKind Kind { get; set; } = SpatialCoordinateKind.Projected;
    [Required, StringLength(30)] public string? Unit { get; set; } = "metre";
    [StringLength(1000)] public string? Description { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public string? RowVersion { get; set; }
}
