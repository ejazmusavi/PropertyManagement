namespace RealEstate.Application.Users;

public sealed record UserListItemDto(Guid Id, string Email, string FullName, bool IsActive, IReadOnlyList<string> Roles, DateTime? LastLoginAtUtc);

public sealed record UserLookupDto(Guid Id, string FullName, string Email, bool IsActive);

public sealed record DashboardSummaryDto(string? CompanyName, int ActiveUsers, int Roles, int AssignedProjects, DateTime? LastLoginAtUtc);

public interface IUserQueryService
{
    Task<Common.PagedResult<UserListItemDto>> ListAsync(Common.PagedRequest request, CancellationToken ct = default);
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserLookupDto>> LookupAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>Active users matching the search term (name/email), limited to <paramref name="take"/> rows.</summary>
    Task<IReadOnlyList<UserLookupDto>> SearchActiveAsync(string? search, int take, CancellationToken ct = default);
}
