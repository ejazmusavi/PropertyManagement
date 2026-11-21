using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Gateway;
using RealEstate.Application.Portal;
using RealEstate.Application.Projects;

namespace RealEstate.Web.ViewModels;

public class PortalAccountFormViewModel
{
    [EmailAddress, StringLength(256), Display(Name = "Login e-mail")]
    public string? Email { get; set; }

    [DataType(DataType.Password), StringLength(128, MinimumLength = 12), Display(Name = "Initial password")]
    public string? Password { get; set; }
}

public class PortalAccountPageViewModel
{
    public CustomerDetailDto Customer { get; set; } = default!;
    public CustomerAccountDto? Account { get; set; }
    public PortalAccountFormViewModel Form { get; set; } = new();
}

public class AnnouncementFormViewModel
{
    public long? Id { get; set; }
    [Required, StringLength(200)] public string? Title { get; set; }
    [Required, StringLength(4000)] public string? Body { get; set; }
    [Display(Name = "Project")] public long? ProjectId { get; set; }
    [Display(Name = "Publish from (UTC)")] public DateTime? PublishFrom { get; set; }
    [Display(Name = "Expires (UTC)")] public DateTime? ExpiresAt { get; set; }
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
    public IReadOnlyList<ProjectListItemDto> Projects { get; set; } = [];
}

public class InstructionsViewModel
{
    [StringLength(2000), Display(Name = "Payment instructions shown to customers")]
    public string? Note { get; set; }
}

public class SupportListViewModel
{
    public SupportListRequest Filter { get; set; } = new();
    public PagedResult<SupportListItemDto> Result { get; set; } = default!;
}

public class SupportDetailsViewModel
{
    public SupportDetailDto Detail { get; set; } = default!;
}

public class GatewayListViewModel
{
    public GatewayListRequest Filter { get; set; } = new();
    public PagedResult<GatewayTransactionDto> Result { get; set; } = default!;
}

public class WebhookListViewModel
{
    public WebhookLogListRequest Filter { get; set; } = new();
    public PagedResult<WebhookLogDto> Result { get; set; } = default!;
}
