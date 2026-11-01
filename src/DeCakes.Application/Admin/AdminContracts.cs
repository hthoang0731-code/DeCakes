namespace DeCakes.Application.Admin;

public sealed record AdminPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

public sealed record AdminUserDto(
    long Id, string Email, string FullName, string? Phone, string Role, string Status,
    bool IsDemo, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record AdminBakeryDto(
    long Id, string Name, string Slug, string? Description, string? AddressLine, string? City,
    string? Phone, string ProfileStatus, bool IsDemo, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record AdminUserFilter(string? Search, string? Role, string? Status, bool? IsDemo);

public sealed record AdminBakeryFilter(string? Search, string? ProfileStatus, bool? IsDemo);

/// <summary>Orders inherit the demo classification of their source design_request, not their bakery/customer.</summary>
public sealed record AdminDashboardDto(
    bool? IsDemo,
    IReadOnlyDictionary<string, long> UsersByRole,
    IReadOnlyDictionary<string, long> UsersByStatus,
    IReadOnlyDictionary<string, long> BakeriesByProfileStatus,
    IReadOnlyDictionary<string, long> DesignRequestsByStatus,
    IReadOnlyDictionary<string, long> OrdersByStatus);

public abstract record AdminResult<T>
{
    public sealed record Ok(T Value) : AdminResult<T>;
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AdminResult<T>;
    public sealed record NotFound : AdminResult<T>;
}

public interface IAdminStore
{
    Task<AdminPage<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminUserDto?> FindUserAsync(long id, CancellationToken cancellationToken);
    Task<AdminPage<AdminBakeryDto>> ListBakeriesAsync(AdminBakeryFilter filter, int page, int pageSize, CancellationToken cancellationToken);
    Task<AdminBakeryDto?> FindBakeryAsync(long id, CancellationToken cancellationToken);
    Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken);
}

public interface IAdminService
{
    Task<AdminResult<AdminPage<AdminUserDto>>> ListUsersAsync(AdminUserFilter filter, int? page, int? pageSize, CancellationToken cancellationToken);
    Task<AdminResult<AdminUserDto>> GetUserAsync(long id, CancellationToken cancellationToken);
    Task<AdminResult<AdminPage<AdminBakeryDto>>> ListBakeriesAsync(AdminBakeryFilter filter, int? page, int? pageSize, CancellationToken cancellationToken);
    Task<AdminResult<AdminBakeryDto>> GetBakeryAsync(long id, CancellationToken cancellationToken);
    Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken);
}
