using DeCakes.Application.Admin;

namespace DeCakes.Api.Tests.Infrastructure;

public sealed class InMemoryAdminStore : IAdminStore
{
    public List<AdminUserDto> Users { get; } = [];
    public List<AdminBakeryDto> Bakeries { get; } = [];
    public int Calls { get; private set; }
    public bool Fail { get; set; }

    public Task<AdminPage<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var rows = Users.Where(u => (filter.Role is null || u.Role == filter.Role) && (filter.Status is null || u.Status == filter.Status)
            && (filter.IsDemo is null || u.IsDemo == filter.IsDemo)
            && (filter.Search is null || Contains(u.Email, filter.Search) || Contains(u.FullName, filter.Search))).OrderBy(u => u.Id).ToArray();
        return Task.FromResult(Page(rows, page, pageSize));
    }

    public Task<AdminUserDto?> FindUserAsync(long id, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        return Task.FromResult(Users.Find(u => u.Id == id));
    }

    public Task<AdminPage<AdminBakeryDto>> ListBakeriesAsync(AdminBakeryFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var rows = Bakeries.Where(b => (filter.ProfileStatus is null || b.ProfileStatus == filter.ProfileStatus)
            && (filter.IsDemo is null || b.IsDemo == filter.IsDemo)
            && (filter.Search is null || Contains(b.Name, filter.Search) || Contains(b.Slug, filter.Search))).OrderBy(b => b.Id).ToArray();
        return Task.FromResult(Page(rows, page, pageSize));
    }

    public Task<AdminBakeryDto?> FindBakeryAsync(long id, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        return Task.FromResult(Bakeries.Find(b => b.Id == id));
    }

    public Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var users = Users.Where(u => isDemo is null || u.IsDemo == isDemo).ToArray();
        return Task.FromResult(new AdminDashboardDto(isDemo,
            users.GroupBy(u => u.Role).ToDictionary(g => g.Key, g => g.LongCount()),
            users.GroupBy(u => u.Status).ToDictionary(g => g.Key, g => g.LongCount()),
            Bakeries.Where(b => isDemo is null || b.IsDemo == isDemo).GroupBy(b => b.ProfileStatus).ToDictionary(g => g.Key, g => g.LongCount()),
            new Dictionary<string, long>(), new Dictionary<string, long>()));
    }

    private static bool Contains(string value, string search) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static AdminPage<T> Page<T>(T[] rows, int page, int size) => new(
        rows.Skip((int)Math.Min((long)(page - 1) * size, int.MaxValue)).Take(size).ToArray(), page, size, rows.LongLength);

    private void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Fail) throw new InvalidOperationException("ADMIN-SECRET-DB-DETAIL");
    }
}
