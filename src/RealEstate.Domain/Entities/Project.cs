using RealEstate.Domain.Common;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class Project : AuditableEntity<long>
{
    public const decimal MaxArea = 999_999_999m;

    public string ProjectCode { get; private set; } = default!;
    public string ProjectName { get; private set; } = default!;
    public string? Description { get; private set; }
    public string City { get; private set; } = default!;
    public string? Address { get; private set; }
    public DateOnly? StartDate { get; private set; }
    public DateOnly? ExpectedCompletionDate { get; private set; }
    public decimal InitialArea { get; private set; }
    public decimal CurrentArea { get; private set; }
    public MeasurementUnit MeasurementUnit { get; private set; }
    public ProjectStatus Status { get; private set; }
    public Guid? ProjectManagerId { get; private set; }
    public byte[] RowVersion { get; set; } = [];

    public ProjectAddress? AddressDetails { get; set; }

    private Project() { }

    public static Project Create(string code, string name, string? description, string city, string? address,
        DateOnly? start, DateOnly? expected, decimal initialArea, MeasurementUnit unit, Guid? managerId)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("Project code is required.");
        ValidateArea(initialArea);
        var p = new Project
        {
            ProjectCode = code.Trim().ToUpperInvariant(),
            InitialArea = initialArea,
            CurrentArea = initialArea,
            MeasurementUnit = unit,
            Status = ProjectStatus.Planning,
            ProjectManagerId = managerId,
        };
        p.UpdateDetails(name, description, city, address, start, expected);
        return p;
    }

    public void UpdateDetails(string name, string? description, string city, string? address, DateOnly? start, DateOnly? expected)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Project name is required.");
        if (string.IsNullOrWhiteSpace(city)) throw new DomainException("City is required.");
        if (start is not null && expected is not null && expected < start)
            throw new DomainException("Expected completion date cannot be before the start date.");
        ProjectName = name.Trim();
        Description = Clean(description);
        City = city.Trim();
        Address = Clean(address);
        StartDate = start;
        ExpectedCompletionDate = expected;
    }

    public void SetManager(Guid? managerId) => ProjectManagerId = managerId;

    public static bool CanTransition(ProjectStatus from, ProjectStatus to) => (from, to) switch
    {
        (ProjectStatus.Planning, ProjectStatus.Active) => true,
        (ProjectStatus.Planning, ProjectStatus.OnHold) => true,
        (ProjectStatus.Planning, ProjectStatus.Archived) => true,
        (ProjectStatus.Active, ProjectStatus.OnHold) => true,
        (ProjectStatus.Active, ProjectStatus.Completed) => true,
        (ProjectStatus.OnHold, ProjectStatus.Active) => true,
        (ProjectStatus.OnHold, ProjectStatus.Archived) => true,
        (ProjectStatus.Completed, ProjectStatus.Archived) => true,
        _ => false,
    };

    public ProjectStatusHistory ChangeStatus(ProjectStatus to, string reason, DateTime nowUtc, Guid? userId, string? userName)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to change the project status.");
        if (!CanTransition(Status, to)) throw new DomainException($"A project cannot move from {Status} to {to}.");
        var history = new ProjectStatusHistory
        {
            Project = this, FromStatus = Status, ToStatus = to, Reason = reason.Trim(),
            ChangedAtUtc = nowUtc, ChangedByUserId = userId, ChangedBy = userName,
        };
        Status = to;
        return history;
    }

    /// <summary>True when land and area changes are still permitted.</summary>
    public bool AllowsLandChanges => Status is not (ProjectStatus.Completed or ProjectStatus.Archived);

    public bool IsReadOnly => Status == ProjectStatus.Archived;

    /// <summary>Applies a signed area change. Only callable through the land services, which also write the history record.</summary>
    public void ApplyAreaChange(decimal delta)
    {
        if (!AllowsLandChanges) throw new DomainException($"Land cannot be changed while the project is {Status}.");
        var updated = CurrentArea + delta;
        ValidateArea(updated);
        CurrentArea = updated;
    }

    public static void ValidateArea(decimal area)
    {
        if (area <= 0) throw new DomainException("Area must be greater than zero.");
        if (area > MaxArea) throw new DomainException("Area is too large.");
        if (decimal.Round(area, 4) != area) throw new DomainException("Area supports at most 4 decimal places.");
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public class ProjectAddress : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public string? AddressLine { get; set; }
    public string? Locality { get; set; }
    public string? Province { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? Landmark { get; set; }
}

public class ProjectDocument : StoredDocument
{
    public long ProjectId { get; set; }
    public ProjectDocumentType DocumentType { get; set; }
    public string? Description { get; set; }
}

public class ProjectManagerAssignment : AuditableEntity<long>
{
    public long ProjectId { get; set; }
    public Guid UserId { get; set; }
    public DateTime AssignedFromUtc { get; set; }
    public DateTime? AssignedToUtc { get; set; }
    public string? AssignedBy { get; set; }
    public string? Reason { get; set; }
}

public class ProjectStatusHistory : Entity<long>
{
    public long ProjectId { get; set; }
    public Project? Project { get; set; }
    public ProjectStatus? FromStatus { get; set; }
    public ProjectStatus ToStatus { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime ChangedAtUtc { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
}

/// <summary>Every change of the project's current area is recorded here; areas are never edited without a record.</summary>
public class ProjectAreaHistory : Entity<long>
{
    public long ProjectId { get; set; }
    public Project? Project { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal PreviousArea { get; set; }
    public decimal Change { get; set; }
    public decimal NewArea { get; set; }
    public AreaChangeSource Source { get; set; }
    public string Reason { get; set; } = default!;
    public LandAcquisition? Acquisition { get; set; }
    public LandAdjustment? Adjustment { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangedBy { get; set; }
}
