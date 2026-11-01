using DeCakes.Application.Common;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Admin;

public sealed class AdminService(IAdminStore store) : IAdminService
{
    public async Task<AdminResult<AdminPage<AdminUserDto>>> ListUsersAsync(
        AdminUserFilter filter, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var search = Search(filter.Search, errors);
        var role = Normalize(filter.Role);
        var status = Normalize(filter.Status);
        if (role is not null && !SystemRoles.IsKnown(role)) errors["role"] = ["role phải là customer, bakery hoặc admin."];
        if (status is not null && status is not ("active" or "disabled")) errors["status"] = ["status phải là active hoặc disabled."];
        var (number, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0) return new AdminResult<AdminPage<AdminUserDto>>.Invalid(errors);
        return new AdminResult<AdminPage<AdminUserDto>>.Ok(await store.ListUsersAsync(
            new(search, role, status, filter.IsDemo), number, size, cancellationToken));
    }

    public async Task<AdminResult<AdminPage<AdminBakeryDto>>> ListBakeriesAsync(
        AdminBakeryFilter filter, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var search = Search(filter.Search, errors);
        var status = Normalize(filter.ProfileStatus);
        // rejected: migrations/V004 (admin từ chối hồ sơ).
        if (status is not null && status is not ("draft" or "pending_review" or "active" or "suspended" or "rejected"))
            errors["profileStatus"] = ["profileStatus phải là draft, pending_review, active, suspended hoặc rejected."];
        var (number, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0) return new AdminResult<AdminPage<AdminBakeryDto>>.Invalid(errors);
        return new AdminResult<AdminPage<AdminBakeryDto>>.Ok(await store.ListBakeriesAsync(
            new(search, status, filter.IsDemo), number, size, cancellationToken));
    }

    public async Task<AdminResult<AdminUserDto>> GetUserAsync(long id, CancellationToken cancellationToken)
    {
        if (id <= 0) return new AdminResult<AdminUserDto>.Invalid(IdError());
        var user = await store.FindUserAsync(id, cancellationToken);
        return user is null ? new AdminResult<AdminUserDto>.NotFound() : new AdminResult<AdminUserDto>.Ok(user);
    }

    public async Task<AdminResult<AdminBakeryDto>> GetBakeryAsync(long id, CancellationToken cancellationToken)
    {
        if (id <= 0) return new AdminResult<AdminBakeryDto>.Invalid(IdError());
        var bakery = await store.FindBakeryAsync(id, cancellationToken);
        return bakery is null ? new AdminResult<AdminBakeryDto>.NotFound() : new AdminResult<AdminBakeryDto>.Ok(bakery);
    }

    public Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken) =>
        store.DashboardAsync(isDemo, cancellationToken);

    private static Dictionary<string, string[]> IdError() => new() { ["id"] = ["id phải lớn hơn 0."] };
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Search(string? value, IDictionary<string, string[]> errors)
    {
        var search = Normalize(value);
        if (value is not null && (search?.Length > 255 || value.Any(char.IsControl)))
            errors["search"] = ["search tối đa 255 ký tự, không chứa ký tự điều khiển."];
        return search;
    }
}
