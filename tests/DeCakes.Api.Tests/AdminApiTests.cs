using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeCakes.Application.Admin;
using DeCakes.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public sealed class AdminApiTests
{
    private const string Password = "Admin-test-password!";
    private static readonly string[] Endpoints = ["dashboard", "users", "users/1", "bakeries", "bakeries/1"];

    private static TestApp App(InMemoryAdminStore store) => new(configureServices: services =>
    {
        services.RemoveAll<IAdminStore>();
        services.AddSingleton<IAdminStore>(store);
    });

    private static InMemoryAdminStore Fixture()
    {
        var store = new InMemoryAdminStore();
        var now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
        store.Users.AddRange([
            new(2, "bakery@example.test", "Tiệm demo", null, "bakery", "disabled", true, now, now),
            new(1, "literal%_!\\@example.test", "Nguyễn O'An", "0123", "customer", "active", false, now, now),
            new(3, "admin@example.test", "Admin", null, "admin", "active", false, now, now)]);
        store.Bakeries.AddRange([
            new(2, "Demo", "demo", null, null, null, null, "suspended", true, now, now),
            new(1, "Tiệm %_!\\", "literal-slug", "Mô tả", "Địa chỉ", "Huế", "0123", "draft", false, now, now)]);
        return store;
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("customer", 403)]
    [InlineData("bakery", 403)]
    [InlineData("admin", 200)]
    public async Task All_endpoints_require_existing_admin_cookie(string? role, int expected)
    {
        var store = Fixture();
        using var app = App(store);
        using var client = app.CreateApiClient();
        if (role is not null)
        {
            app.AddUser("actor@example.test", Password, role);
            Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("actor@example.test", Password)).StatusCode);
        }

        foreach (var endpoint in Endpoints)
            Assert.Equal(expected, (int)(await client.GetAsync("/api/admin/" + endpoint)).StatusCode);
        Assert.Equal(expected == 200 ? 5 : 0, store.Calls);
    }

    [Theory]
    [InlineData("disabled", "admin")]
    [InlineData("active", "customer")]
    [InlineData("active", "bakery")]
    public async Task Previously_issued_admin_cookie_is_rejected_after_account_change(string status, string role)
    {
        var store = Fixture();
        using var app = App(store);
        var id = app.AddUser("actor@example.test", Password, "admin");
        using var client = app.CreateApiClient();
        (await client.LoginAsync("actor@example.test", Password)).EnsureSuccessStatusCode();
        app.Store.Update(id, u => u with { Status = status, Role = role });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/dashboard")).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("users?page=0")]
    [InlineData("users?pageSize=101")]
    [InlineData("users?pageSize=0")]
    [InlineData("users?page=abc")]
    [InlineData("users?role=owner")]
    [InlineData("users?status=blocked")]
    [InlineData("users?isDemo=1")]
    [InlineData("users/0")]
    [InlineData("users/-1")]
    [InlineData("users/nope")]
    [InlineData("users/9223372036854775808")]
    [InlineData("bakeries/0")]
    [InlineData("bakeries/nope")]
    [InlineData("bakeries?profileStatus=published")]
    [InlineData("bakeries?isDemo=bad")]
    [InlineData("dashboard?isDemo=bad")]
    [InlineData("users?search=%0Afoo")]
    public async Task Invalid_parameters_return_400_without_store_calls(string query)
    {
        var store = Fixture();
        using var app = App(store);
        app.AddUser("actor@example.test", Password, "admin");
        using var client = app.CreateApiClient();
        (await client.LoginAsync("actor@example.test", Password)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/" + query)).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Lists_filter_before_paging_return_total_and_preserve_all_data_on_get()
    {
        var store = Fixture();
        var before = JsonSerializer.Serialize(new { store.Users, store.Bakeries });
        using var app = App(store);
        app.AddUser("actor@example.test", Password, "admin");
        using var client = app.CreateApiClient();
        (await client.LoginAsync("actor@example.test", Password)).EnsureSuccessStatusCode();

        var all = await Read<AdminPage<AdminUserDto>>(client, "users");
        Assert.Equal((1, 20, 3L), (all.Page, all.PageSize, all.TotalCount));
        Assert.Equal(new long[] { 1, 2, 3 }, all.Items.Select(u => u.Id));
        var page = await Read<AdminPage<AdminUserDto>>(client, "users?page=2&pageSize=1");
        Assert.Equal(2L, Assert.Single(page.Items).Id);
        Assert.Equal(3L, page.TotalCount);
        Assert.Empty((await Read<AdminPage<AdminUserDto>>(client, "users?page=2147483647&pageSize=100")).Items);
        Assert.Equal(2L, Assert.Single((await Read<AdminPage<AdminUserDto>>(client, "users?role=bakery&status=disabled&isDemo=true")).Items).Id);
        Assert.Equal(2, (await Read<AdminPage<AdminUserDto>>(client, "users?isDemo=false")).TotalCount);
        foreach (var term in new[] { "%_!\\", "O'An", "literal" })
            Assert.Equal(1L, Assert.Single((await Read<AdminPage<AdminUserDto>>(client, "users?search=" + Uri.EscapeDataString(term))).Items).Id);
        Assert.Equal(1L, Assert.Single((await Read<AdminPage<AdminBakeryDto>>(client, "bakeries?search=literal-slug&profileStatus=draft&isDemo=false")).Items).Id);
        Assert.Equal(2L, Assert.Single((await Read<AdminPage<AdminBakeryDto>>(client, "bakeries?profileStatus=suspended&isDemo=true")).Items).Id);
        Assert.Equal(2, (await Read<AdminPage<AdminBakeryDto>>(client, "bakeries")).TotalCount);
        Assert.Equal("disabled", (await Read<AdminUserDto>(client, "users/2")).Status);
        Assert.Equal("draft", (await Read<AdminBakeryDto>(client, "bakeries/1")).ProfileStatus);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/users/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/bakeries/999")).StatusCode);
        var dashboard = await Read<AdminDashboardDto>(client, "dashboard?isDemo=true");
        Assert.True(dashboard.IsDemo);
        Assert.Equal(1L, dashboard.UsersByRole["bakery"]);
        Assert.Equal(1L, dashboard.BakeriesByProfileStatus["suspended"]);
        Assert.Equal(before, JsonSerializer.Serialize(new { store.Users, store.Bakeries }));
    }

    [Fact]
    public async Task User_json_has_exact_safe_fields_and_errors_hide_details()
    {
        var store = Fixture();
        using var app = App(store);
        app.AddUser("actor@example.test", Password, "admin");
        using var client = app.CreateApiClient();
        (await client.LoginAsync("actor@example.test", Password)).EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await (await client.GetAsync("/api/admin/users/1")).Content.ReadAsStringAsync());
        Assert.Equal(new[] { "createdAt", "email", "fullName", "id", "isDemo", "phone", "role", "status", "updatedAt" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        using var list = JsonDocument.Parse(await (await client.GetAsync("/api/admin/users")).Content.ReadAsStringAsync());
        Assert.Equal(json.RootElement.EnumerateObject().Select(p => p.Name).Order(),
            list.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order());
        store.Fail = true;
        foreach (var endpoint in Endpoints)
        {
            var response = await client.GetAsync("/api/admin/" + endpoint);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("ADMIN-SECRET", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Development_openapi_contains_only_read_admin_routes_and_bootstrap_is_not_registered_in_http()
    {
        using var app = new TestApp(environment: "Development");
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/admin/", StringComparison.Ordinal)).ToArray();
        // 5 GET của Admin giai đoạn 1 + GET lịch sử review + POST review hồ sơ tiệm (đợt onboarding); không có ghi Admin nào khác.
        Assert.Equal(7, paths.Length);
        foreach (var path in paths.Where(p => p.Name != "/api/admin/bakeries/{bakeryId}/review"))
            Assert.Equal("get", Assert.Single(path.Value.EnumerateObject()).Name);
        Assert.Equal("post", Assert.Single(paths.Single(p => p.Name == "/api/admin/bakeries/{bakeryId}/review").Value.EnumerateObject()).Name);
        Assert.Null(app.Services.GetService<IAdminProvisioningStore>());
        Assert.Null(app.Services.GetService<AdminBootstrapService>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/bootstrap")).StatusCode);
    }

    private static async Task<T> Read<T>(ApiClient client, string path)
    {
        var response = await client.GetAsync("/api/admin/" + path);
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
