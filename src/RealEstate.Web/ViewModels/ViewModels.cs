using System.ComponentModel.DataAnnotations;
using RealEstate.Application.Common;
using RealEstate.Application.Users;

namespace RealEstate.Web.ViewModels;

public class LoginViewModel
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password), StringLength(256)] public string Password { get; set; } = "";
    [Display(Name = "Remember me")] public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class DashboardViewModel
{
    public required DashboardSummaryDto Summary { get; init; }
    public required string FullName { get; init; }
}

public class ProfileViewModel
{
    public string Email { get; set; } = "";
    public IReadOnlyList<string> Roles { get; set; } = [];
    public DateTime? LastLoginAtUtc { get; set; }
    [Required, Display(Name = "Full name"), StringLength(200)] public string FullName { get; set; } = "";
    [Display(Name = "Phone number"), StringLength(30)] public string? PhoneNumber { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")] public string CurrentPassword { get; set; } = "";
    [Required, DataType(DataType.Password), Display(Name = "New password"), StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
    [Required, DataType(DataType.Password), Display(Name = "Confirm new password"), Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")] public string ConfirmNewPassword { get; set; } = "";
}

public class UserListViewModel
{
    public required PagedResult<UserListItemDto> Users { get; init; }
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool Descending { get; init; }
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
}
