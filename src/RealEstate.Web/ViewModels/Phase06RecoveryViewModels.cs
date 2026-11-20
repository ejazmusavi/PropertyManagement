using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Domain.Enums;

namespace RealEstate.Web.ViewModels;

// ===================================================================================================
// receipts
// ===================================================================================================
public class ReceiptListViewModel
{
    public PagedResult<ReceiptDto> Receipts { get; set; } = default!;
    public ReceiptListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class ReceiptDetailsViewModel
{
    public ReceiptDto Receipt { get; set; } = default!;
}

// ===================================================================================================
// recovery dashboard and reports
// ===================================================================================================
public class RecoveryDashboardViewModel
{
    public RecoveryDashboardDto Dashboard { get; set; } = default!;
    public long? ProjectId { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class OverdueViewModel
{
    public DueListDto Result { get; set; } = default!;
    public DueListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<OfficerOptionDto> Officers { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class UpcomingViewModel
{
    public DueListDto Result { get; set; } = default!;
    public DueListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class DefaultersViewModel
{
    public PagedResult<DefaulterDto> Defaulters { get; set; } = default!;
    public DefaulterListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class PerformanceViewModel
{
    public IReadOnlyList<OfficerPerformanceDto> Officers { get; set; } = [];
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public long? ProjectId { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

// ===================================================================================================
// assignments
// ===================================================================================================
public class AssignmentsViewModel
{
    public PagedResult<AssignmentDto> Assignments { get; set; } = default!;
    public AssignmentListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<OfficerOptionDto> Officers { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class AssignFormViewModel
{
    public long AgreementId { get; set; }
    [Display(Name = "Recovery officer")] public Guid OfficerUserId { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    public string? Term { get; set; }
    public AgreementLookupDto? Agreement { get; set; }
    public IReadOnlyList<AgreementLookupDto> Matches { get; set; } = [];
    public IReadOnlyList<OfficerOptionDto> Officers { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
    public decimal Overdue { get; set; }
    public bool Searched { get; set; }
}

// ===================================================================================================
// follow-ups
// ===================================================================================================
public class FollowUpsViewModel
{
    public PagedResult<FollowUpDto> FollowUps { get; set; } = default!;
    public FollowUpListRequest Filter { get; set; } = new();
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
    public IReadOnlyList<OfficerOptionDto> Officers { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
}

public class FollowUpFormViewModel
{
    public long AgreementId { get; set; }
    [Display(Name = "Follow-up date")] public DateOnly FollowUpDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public FollowUpMethod Method { get; set; } = FollowUpMethod.Call;
    public FollowUpOutcome Outcome { get; set; } = FollowUpOutcome.Contacted;
    [Display(Name = "Promised amount")] public decimal? PromisedAmount { get; set; }
    [Display(Name = "Promised date")] public DateOnly? PromisedDate { get; set; }
    [Display(Name = "Next follow-up")] public DateOnly? NextFollowUpDate { get; set; }
    [Required, StringLength(2000)] public string Notes { get; set; } = "";
    public string? Term { get; set; }
    public AgreementLookupDto? Agreement { get; set; }
    public IReadOnlyList<AgreementLookupDto> Matches { get; set; } = [];
    public string CurrencySymbol { get; set; } = "";
    public decimal Overdue { get; set; }
    public bool Searched { get; set; }

    public CreateFollowUpRequest ToRequest() => new(AgreementId, FollowUpDate, Method, Outcome, PromisedAmount, PromisedDate, NextFollowUpDate, Notes);
}
