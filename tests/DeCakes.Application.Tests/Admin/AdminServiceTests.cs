using DeCakes.Application.Admin;

namespace DeCakes.Application.Tests.Admin;

public sealed class AdminServiceTests
{
    private readonly Store _store = new();
    private AdminService Service => new(_store);
    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(0, 20, null, null, "page")]
    [InlineData(1, 101, null, null, "pageSize")]
    [InlineData(1, -1, null, null, "pageSize")]
    [InlineData(1, 20, "Admin", null, "role")]
    [InlineData(1, 20, null, "draft", "status")]
    public async Task Users_validate_before_query(int page, int size, string? role, string? status, string field)
    {
        var invalid = Assert.IsType<AdminResult<AdminPage<AdminUserDto>>.Invalid>(
            await Service.ListUsersAsync(new(null, role, status, null), page, size, Ct));
        Assert.Contains(field, invalid.Errors.Keys);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task Defaults_normalization_literal_input_and_demo_are_forwarded_with_cancellation()
    {
        using var cts = new CancellationTokenSource();
        Assert.IsType<AdminResult<AdminPage<AdminUserDto>>.Ok>(await Service.ListUsersAsync(
            new(" %_!\\' ", " admin ", " active ", null), null, null, cts.Token));
        Assert.Equal(new AdminUserFilter("%_!\\'", "admin", "active", null), _store.Users);
        Assert.Equal((1, 20), _store.Paging);
        Assert.Equal(cts.Token, _store.Token);
        await Service.ListBakeriesAsync(new(" slug ", " suspended ", false), int.MaxValue, 100, Ct);
        Assert.Equal(new AdminBakeryFilter("slug", "suspended", false), _store.Bakeries);
        Assert.Equal((int.MaxValue, 100), _store.Paging);
        await Service.ListBakeriesAsync(new(" ", " ", true), null, null, Ct);
        Assert.Equal(new AdminBakeryFilter(null, null, true), _store.Bakeries);
    }

    [Theory]
    [InlineData("bad\nsearch")]
    [InlineData("bad\tsearch")]
    public async Task Control_characters_in_search_fail_for_both_lists(string search)
    {
        Assert.IsType<AdminResult<AdminPage<AdminUserDto>>.Invalid>(await Service.ListUsersAsync(new(search, null, null, null), null, null, Ct));
        Assert.IsType<AdminResult<AdminPage<AdminBakeryDto>>.Invalid>(await Service.ListBakeriesAsync(new(search, null, null), null, null, Ct));
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task Search_limit_and_profile_status_are_validated()
    {
        Assert.IsType<AdminResult<AdminPage<AdminUserDto>>.Invalid>(await Service.ListUsersAsync(new(new string('a', 256), null, null, null), null, null, Ct));
        Assert.IsType<AdminResult<AdminPage<AdminBakeryDto>>.Invalid>(await Service.ListBakeriesAsync(new(new string('a', 256), "hidden", null), 0, 101, Ct));
        Assert.Equal(0, _store.Calls);
        foreach (var status in new[] { "draft", "pending_review", "active", "suspended" })
            Assert.IsType<AdminResult<AdminPage<AdminBakeryDto>>.Ok>(await Service.ListBakeriesAsync(new(null, status, null), 1, 100, Ct));
        Assert.IsType<AdminResult<AdminPage<AdminUserDto>>.Ok>(await Service.ListUsersAsync(new(new string('a', 255), "customer", "disabled", true), 1, 100, Ct));
    }

    [Fact]
    public async Task Invalid_ids_do_not_query_and_missing_ids_return_not_found()
    {
        Assert.IsType<AdminResult<AdminUserDto>.Invalid>(await Service.GetUserAsync(0, Ct));
        Assert.IsType<AdminResult<AdminBakeryDto>.Invalid>(await Service.GetBakeryAsync(-1, Ct));
        Assert.Equal(0, _store.Calls);
        Assert.IsType<AdminResult<AdminUserDto>.NotFound>(await Service.GetUserAsync(9, Ct));
        Assert.IsType<AdminResult<AdminBakeryDto>.NotFound>(await Service.GetBakeryAsync(9, Ct));
    }

    [Fact]
    public async Task Store_errors_and_cancellation_propagate()
    {
        _store.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.GetUserAsync(1, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.GetBakeryAsync(1, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.DashboardAsync(null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ListUsersAsync(new(null, null, null, null), null, null, Ct));
        _store.Fail = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ListBakeriesAsync(new(null, null, null), null, null, cts.Token));
    }

    private sealed class Store : IAdminStore
    {
        public int Calls;
        public bool Fail;
        public AdminUserFilter? Users;
        public AdminBakeryFilter? Bakeries;
        public (int, int) Paging;
        public CancellationToken Token;
        private void Check(CancellationToken ct) { Calls++; Token = ct; ct.ThrowIfCancellationRequested(); if (Fail) throw new InvalidOperationException(); }
        public Task<AdminPage<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, int page, int pageSize, CancellationToken cancellationToken)
        { Check(cancellationToken); Users = filter; Paging = (page, pageSize); return Task.FromResult(new AdminPage<AdminUserDto>([], page, pageSize, 0)); }
        public Task<AdminPage<AdminBakeryDto>> ListBakeriesAsync(AdminBakeryFilter filter, int page, int pageSize, CancellationToken cancellationToken)
        { Check(cancellationToken); Bakeries = filter; Paging = (page, pageSize); return Task.FromResult(new AdminPage<AdminBakeryDto>([], page, pageSize, 0)); }
        public Task<AdminUserDto?> FindUserAsync(long id, CancellationToken cancellationToken) { Check(cancellationToken); return Task.FromResult<AdminUserDto?>(null); }
        public Task<AdminBakeryDto?> FindBakeryAsync(long id, CancellationToken cancellationToken) { Check(cancellationToken); return Task.FromResult<AdminBakeryDto?>(null); }
        public Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken) { Check(cancellationToken); return Task.FromResult(new AdminDashboardDto(isDemo, new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<string, long>())); }
    }
}
