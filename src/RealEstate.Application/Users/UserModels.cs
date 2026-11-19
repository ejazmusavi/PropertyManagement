namespace RealEstate.Application.Users;

public sealed record UserListItemDto(Guid Id, string Email, string FullName, bool IsActive, IReadOnlyList<string> Roles, DateTime? LastLoginAtUtc);

public sealed record DashboardSummaryDto(string? CompanyName, int ActiveUsers, int Roles, int AssignedProjects, DateTime? LastLoginAtUtc);

public interface IUserQueryService
{
    Task<Common.PagedResult<UserListItemDto>> ListAsync(Common.PagedRequest request, CancellationToken ct = default);
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(Guid userId, CancellationToken ct = default);
}
